using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Services;
using Microsoft.Win32;

namespace KalOS.ViewModels
{
    public partial class AffinityManagerViewModel : ObservableObject
    {
        private readonly LoggingService _logging;
        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        public AffinityManagerViewModel(LoggingService logging)
        {
            _logging = logging;
            try
            {
                _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            }
            catch { }

            // Determine elevation once at construction so the UI can show an "Admin required" InfoBar
            // before the user attempts to write anything.
            RefreshIsAdmin();
            // Re-evaluate HasDevices / HasNoDevices any time the underlying collection changes so
            // x:Bind in the empty-state placeholder and the populated list stay in sync.
            AllDevices.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasDevices));
                OnPropertyChanged(nameof(HasNoDevices));
                OnPropertyChanged(nameof(TotalDevicesCount));
                OnPropertyChanged(nameof(MsiEnabledCount));
            };
        }

        public void RunOnUIThread(Action action)
        {
            var dispatcher = _dispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(() => action());
            }
            else
            {
                action();
            }
        }


        [ObservableProperty]
        private ObservableCollection<PciDeviceItem> _devices = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDevices), nameof(HasNoDevices))]
        private bool _isLoading;

        [ObservableProperty]
        private bool _isAdmin;

        [ObservableProperty]
        private string _statusText = "Ready";

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private string _selectedCategory = "All";

        public List<string> FilterCategories { get; } = new()
        {
            "All",
            "Graphics Cards",
            "Audio Controllers",
            "Network Interface Controllers",
            "XHCI Controllers"
        };

        public ObservableCollection<PciDeviceItem> AllDevices { get; } = new();
        public ObservableCollection<PciDeviceGroup> GroupedDevices { get; } = new();
        public List<CpuCoreInfo> SystemCores { get; private set; } = new();
        public ObservableCollection<CpuCoreGroup> CpuCoreGroups { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CpuModelName), nameof(CpuSummaryString), nameof(CpuPhysicalCores), nameof(CpuLogicalProcessors))]
        private CpuTopologySummary? _topologySummary;

        public string CpuModelName => TopologySummary?.CpuName ?? "Processor Topology";
        public string CpuSummaryString => TopologySummary?.SummaryString ?? "Detecting CPU architecture and core topology...";
        public int CpuPhysicalCores => TopologySummary?.PhysicalCoreCount ?? 0;
        public int CpuLogicalProcessors => TopologySummary?.LogicalProcessorCount ?? 0;

        [ObservableProperty]
        private bool _hasHighCoreCount;


        /// <summary>Computed: device list is populated AND initial scan finished.</summary>
        public bool HasDevices => !IsLoading && AllDevices.Count > 0;

        /// <summary>Computed: scan complete AND no devices found — drives the empty-state placeholder.</summary>
        public bool HasNoDevices => !IsLoading && AllDevices.Count == 0;

        public int TotalDevicesCount => AllDevices.Count;
        public int MsiEnabledCount => AllDevices.Count(d => d.MsiEnabled);

        partial void OnSearchTextChanged(string value) => ApplyFilter();
        partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

        public void ApplyFilter()
        {
            var filtered = AllDevices.AsEnumerable();

            if (!string.Equals(SelectedCategory, "All", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(d => string.Equals(d.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                string query = SearchText.Trim();
                filtered = filtered.Where(d =>
                    d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    d.DeviceId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    d.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            GroupedDevices.Clear();
            var groups = filtered.GroupBy(x => x.Category).OrderBy(g => g.Key);
            foreach (var g in groups)
            {
                GroupedDevices.Add(new PciDeviceGroup(g.Key, g));
            }
        }

        [RelayCommand]
        public async Task LoadDevicesAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            StatusText = "Scanning PCI devices...";
            AllDevices.Clear();
            GroupedDevices.Clear();

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            await Task.Run(() =>
            {
                var devices = new List<PciDeviceItem>();

                try
                {
                    var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_PnPEntity");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"] as string ?? "";
                        string deviceId = obj["PNPDeviceID"] as string ?? "";
                        string pnpClass = obj["PNPClass"] as string ?? "";
                        
                        // Check if device is working properly (ConfigManagerErrorCode == 0)
                        uint configErrorCode = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;
                        if (configErrorCode != 0) continue;

                        if (string.IsNullOrEmpty(deviceId) || !deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;

                        string category = "";
                        if (pnpClass.Equals("Display", StringComparison.OrdinalIgnoreCase)) category = "Graphics Cards";
                        else if (pnpClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("Audio", StringComparison.OrdinalIgnoreCase) || name.Contains("Sound", StringComparison.OrdinalIgnoreCase)) category = "Audio Controllers";
                        else if (pnpClass.Equals("Net", StringComparison.OrdinalIgnoreCase) 
                                 || name.Contains("Network", StringComparison.OrdinalIgnoreCase) 
                                 || name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) 
                                 || name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) 
                                 || name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) 
                                 || name.Contains("GbE", StringComparison.OrdinalIgnoreCase) 
                                 || name.Contains("LAN", StringComparison.OrdinalIgnoreCase) 
                                 || name.Contains("NIC", StringComparison.OrdinalIgnoreCase) 
                                 || name.Contains("802.11", StringComparison.OrdinalIgnoreCase)) category = "Network Interface Controllers";
                        else if ((pnpClass.Equals("USB", StringComparison.OrdinalIgnoreCase) || name.Contains("USB", StringComparison.OrdinalIgnoreCase)) && (name.Contains("xHCI", StringComparison.OrdinalIgnoreCase) || name.Contains("Extensible", StringComparison.OrdinalIgnoreCase))) category = "XHCI Controllers";
                        
                        if (string.IsNullOrEmpty(category)) continue; // skip non-relevant PCI devices

                        var item = new PciDeviceItem
                        {
                            Name = name,
                            DeviceId = deviceId,
                            Category = category
                        };


                        if (category == "Network Interface Controllers") item.MaxMsiLimit = "32";
                        else if (category == "XHCI Controllers") item.MaxMsiLimit = "8";
                        else if (category == "Audio Controllers") item.MaxMsiLimit = "1";
                        else if (category == "Graphics Cards") item.MaxMsiLimit = "1";
                        else if (category == "Storage Controllers")
                        {
                            bool isNvme = name.Contains("NVMe", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("NVM Express", StringComparison.OrdinalIgnoreCase);
                            item.MaxMsiLimit = isNvme ? "32" : "8";
                        }

                        ReadMsiRegistry(item);
                        devices.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to get devices: {ex.Message}");
                }

                if (dispatcher != null)
                {
                    dispatcher.TryEnqueue(() =>
                    {
                        foreach (var dev in devices)
                        {
                            AllDevices.Add(dev);
                        }

                        SystemCores = DetectCpuTopology();
                        ApplyFilter();
                    });
                }
            });

            StatusText = $"Loaded {AllDevices.Count} devices.";
            IsLoading = false;
        }


        public void ReadMsiRegistry(PciDeviceItem item)
        {
            string regPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management";
            
            using (var baseKey = Registry.LocalMachine.OpenSubKey(regPath))
            {
                if (baseKey != null)
                {
                    // Check MSI Support
                    using (var msiProps = baseKey.OpenSubKey("MessageSignaledInterruptProperties"))
                    {
                        if (msiProps != null)
                        {
                            item.MsiSupported = true;
                            
                            var supported = msiProps.GetValue("MSISupported");
                            item.MsiEnabled = (supported is int s && s == 1);
                            
                            var limit = msiProps.GetValue("MessageNumberLimit");
                            item.MsiLimit = (limit is int l && l > 0) ? l.ToString() : "Auto";
                        }
                    }

                    // Check Affinity
                    using (var affinity = baseKey.OpenSubKey("Affinity Policy"))
                    {
                        if (affinity != null)
                        {
                            var priority = affinity.GetValue("DevicePriority");
                            item.DevicePriority = priority switch
                            {
                                0 => "Undefined",
                                1 => "Low",
                                2 => "Normal",
                                3 => "High",
                                _ => "Undefined"
                            };

                            var policy = affinity.GetValue("DevicePolicy");
                            item.DevicePolicy = policy switch
                            {
                                0 => "IrqPolicyMachineDefault",
                                1 => "IrqPolicyAllCloseProcessors",
                                2 => "IrqPolicyOneCloseProcessor",
                                3 => "IrqPolicyAllProcessorsInMachine",
                                4 => "IrqPolicySpecifiedProcessors",
                                5 => "IrqPolicySpreadMessagesAcrossAllProcessors",
                                _ => "IrqPolicyMachineDefault"
                            };

                            var mask = affinity.GetValue("AssignmentSetOverride") as byte[];
                            if (mask != null)
                            {
                                ulong umask = 0;
                                if (mask.Length >= 8) umask = BitConverter.ToUInt64(mask, 0);
                                else if (mask.Length >= 4) umask = BitConverter.ToUInt32(mask, 0);
                                else if (mask.Length >= 2) umask = BitConverter.ToUInt16(mask, 0);
                                else if (mask.Length >= 1) umask = mask[0];

                                // Always reflect the registry value back to the UI, including umask=0.
                                // Without this, "remove the last thread" would leave the display showing
                                // the old thread list even though AssignmentSetOverride has been cleared.
                                item.SpecifiedProc = umask == 0
                                    ? string.Empty
                                    : MaskToProcessorList(umask);
                            }
                        }
                    }
                }
            }
        }
        
        private string MaskToProcessorList(ulong mask)
        {
            var procs = new List<int>();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0)
                {
                    procs.Add(i);
                }
            }
            return string.Join(", ", procs);
        }

        public List<CpuCoreInfo> DetectCpuTopology()
        {
            var cores = new List<CpuCoreInfo>();
            try
            {
                var topology = KalOS.Helpers.TopologyHelper.GetSystemTopology();
                var physicalGroups = topology.GroupBy(t => t.PhysicalCoreId).OrderBy(g => g.Key).ToList();
                
                int coreIndex = 0;
                foreach (var group in physicalGroups)
                {
                    // Skip physical cores whose threads live in Processor Group > 0.
                    // Windows only exposes 64 bits per group in MSI AssignmentSetOverride.
                    if (group.Any(c => c.ProcessorGroup != 0))
                    {
                        continue;
                    }

                    ulong fullMask = 0;
                    var threads = new ObservableCollection<CpuThreadInfo>();

                    foreach (var logicalCore in group.OrderBy(c => c.LogicalProcessorId))
                    {
                        if (logicalCore.LogicalProcessorId < 64)
                        {
                            fullMask |= (1UL << logicalCore.LogicalProcessorId);
                            threads.Add(new CpuThreadInfo { ThreadId = logicalCore.LogicalProcessorId });
                        }
                    }

                    if (fullMask != 0)
                    {
                        cores.Add(new CpuCoreInfo
                        {
                            CoreId = coreIndex++,
                            LogicalProcessorMask = (1UL << group.First().LogicalProcessorId),
                            FullCoreMask = fullMask,
                            EfficiencyClass = group.First().EfficiencyClass,
                            L3CacheId = group.First().L3CacheId,
                            NumaNodeId = group.First().NumaNodeId,
                            ProcessorGroup = group.First().ProcessorGroup,
                            Threads = threads
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to detect CPU: {ex.Message}");
            }

            if (cores.Count == 0)
            {
                var fallback = new CpuCoreInfo { CoreId = 0, LogicalProcessorMask = 1, FullCoreMask = 1, IsPCore = true, CoreTypeLabel = "Core" };
                fallback.Threads.Add(new CpuThreadInfo { ThreadId = 0 });
                cores.Add(fallback);
            }

            // Classify cores (P-Cores vs E-Cores or CCDs)
            int distinctEffClasses = cores.Select(c => c.EfficiencyClass).Distinct().Count();
            int minEff = cores.Min(c => c.EfficiencyClass);
            int maxEff = cores.Max(c => c.EfficiencyClass);
            bool isHybrid = distinctEffClasses > 1;

            int distinctL3 = cores.Select(c => c.L3CacheId).Distinct().Count();
            bool isMultiCcd = distinctL3 > 1 && !isHybrid;

            foreach (var core in cores)
            {
                if (isHybrid)
                {
                    if (core.EfficiencyClass == maxEff)
                    {
                        core.IsPCore = true;
                        core.IsECore = false;
                        core.CoreTypeLabel = "P-Core";
                    }
                    else
                    {
                        core.IsPCore = false;
                        core.IsECore = true;
                        core.CoreTypeLabel = "E-Core";
                    }
                }
                else if (isMultiCcd)
                {
                    core.IsPCore = true;
                    core.IsECore = false;
                    core.CoreTypeLabel = $"CCD {core.L3CacheId}";
                }
                else
                {
                    core.IsPCore = true;
                    core.IsECore = false;
                    core.CoreTypeLabel = "Core";
                }
            }

            // Build CpuCoreGroups in a local list first
            var groupsList = new List<CpuCoreGroup>();
            if (isHybrid)
            {
                var pCores = cores.Where(c => c.IsPCore).ToList();
                var eCores = cores.Where(c => c.IsECore).ToList();

                if (pCores.Count > 0)
                {
                    var pGroup = new CpuCoreGroup
                    {
                        Name = "Performance Cores (P-Cores)",
                        ShortName = "P-Cores",
                        Description = "High-performance compute cores with HyperThreading",
                        RecommendedColumns = Math.Clamp(pCores.Count, 2, 4)
                    };
                    foreach (var c in pCores) pGroup.Cores.Add(c);
                    groupsList.Add(pGroup);
                }

                if (eCores.Count > 0)
                {
                    var eGroup = new CpuCoreGroup
                    {
                        Name = "Efficiency Cores (E-Cores)",
                        ShortName = "E-Cores",
                        Description = "Low-power background cores (Ideal for Audio interrupts)",
                        RecommendedColumns = Math.Clamp(eCores.Count, 2, 4)
                    };
                    foreach (var c in eCores) eGroup.Cores.Add(c);
                    groupsList.Add(eGroup);
                }
            }
            else if (isMultiCcd)
            {
                var ccdGroups = cores.GroupBy(c => c.L3CacheId).OrderBy(g => g.Key);
                foreach (var ccd in ccdGroups)
                {
                    var grp = new CpuCoreGroup
                    {
                        Name = $"CCD {ccd.Key} (L3 Cache Domain {ccd.Key})",
                        ShortName = $"CCD {ccd.Key}",
                        Description = ccd.Key == 0 ? "Primary Compute Complex / Cache Domain" : "Secondary Compute Complex",
                        RecommendedColumns = Math.Clamp(ccd.Count(), 2, 4)
                    };
                    foreach (var c in ccd) grp.Cores.Add(c);
                    groupsList.Add(grp);
                }
            }
            else
            {
                var allGroup = new CpuCoreGroup
                {
                    Name = "Processor Cores",
                    ShortName = "All Cores",
                    Description = "Uniform CPU core topology",
                    RecommendedColumns = Math.Clamp(cores.Count, 2, 4)
                };
                foreach (var c in cores) allGroup.Cores.Add(c);
                groupsList.Add(allGroup);
            }

            // Build TopologySummary
            var summary = new CpuTopologySummary
            {
                CpuName = KalOS.Helpers.TopologyHelper.GetCpuModelName(),
                PhysicalCoreCount = cores.Count,
                LogicalProcessorCount = cores.Sum(c => c.Threads.Count),
                PCoreCount = cores.Count(c => c.IsPCore),
                ECoreCount = cores.Count(c => c.IsECore),
                CcdCount = distinctL3,
                IsSmtEnabled = cores.Any(c => c.Threads.Count > 1)
            };

            RunOnUIThread(() =>
            {
                CpuCoreGroups.Clear();
                foreach (var g in groupsList) CpuCoreGroups.Add(g);
                TopologySummary = summary;
            });

            return cores;
        }


        private void SetNdisRssProperties(PciDeviceItem item, int coreIndex)
        {
            try
            {
                string enumPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}";
                using var enumKey = Registry.LocalMachine.OpenSubKey(enumPath);
                string? driverRelPath = enumKey?.GetValue("Driver") as string;
                if (!string.IsNullOrEmpty(driverRelPath))
                {
                    string driverClassPath = $@"SYSTEM\CurrentControlSet\Control\Class\{driverRelPath}";
                    using var driverKey = Registry.LocalMachine.OpenSubKey(driverClassPath, writable: true);
                    if (driverKey != null)
                    {
                        driverKey.SetValue("*RssBaseProcNumber", coreIndex.ToString(), RegistryValueKind.String);
                        driverKey.SetValue("*NumRssQueues", "1", RegistryValueKind.String);
                        driverKey.SetValue("*MaxRssProcessors", "1", RegistryValueKind.String);
                        driverKey.SetValue("*RssBaseProcGroup", "0", RegistryValueKind.String);
                        driverKey.SetValue("*NumaNodeId", "0", RegistryValueKind.String);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set NDIS RSS for {item.Name}: {ex.Message}");
            }
        }

        private void SetDeviceAffinity(PciDeviceItem item, ulong affinityMask, int priority = 0, int msiLimit = 1)
        {
            try
            {
                string regPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                using (var key = Registry.LocalMachine.CreateSubKey(regPath, true))
                {
                    key.SetValue("DevicePolicy", 4, RegistryValueKind.DWord); // SpecifiedProcessors
                    key.SetValue("DevicePriority", priority, RegistryValueKind.DWord);

                    byte[] maskBytes = BitConverter.GetBytes(affinityMask);
                    key.SetValue("AssignmentSetOverride", maskBytes, RegistryValueKind.Binary);
                }

                if (item.MsiSupported && item.MsiEnabled)
                {
                    string msiPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                    using (var msiKey = Registry.LocalMachine.CreateSubKey(msiPath, true))
                    {
                        msiKey.SetValue("MessageNumberLimit", msiLimit, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set affinity for {item.Name}: {ex.Message}");
            }
        }

        private void ForceEnableMsiAndSetAffinity(PciDeviceItem item, ulong affinityMask, int priority, int msiLimit)
        {
            try
            {
                string msiPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                using (var msiKey = Registry.LocalMachine.CreateSubKey(msiPath, true))
                {
                    msiKey.SetValue("MSISupported", 1, RegistryValueKind.DWord);
                    msiKey.SetValue("MessageNumberLimit", msiLimit, RegistryValueKind.DWord);
                }

                string regPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                using (var key = Registry.LocalMachine.CreateSubKey(regPath, true))
                {
                    key.SetValue("DevicePolicy", 4, RegistryValueKind.DWord);
                    key.SetValue("DevicePriority", priority, RegistryValueKind.DWord);
                    byte[] maskBytes = BitConverter.GetBytes(affinityMask);
                    key.SetValue("AssignmentSetOverride", maskBytes, RegistryValueKind.Binary);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to force MSI + affinity for {item.Name}: {ex.Message}");
            }
        }

        public void RestartDevice(string instanceId)
        {
            RestartDevice(instanceId, out _);
        }

        public bool RestartDevice(string instanceId, out string? error)
        {
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "pnputil.exe",
                        Arguments = $"/restart-device \"{instanceId}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };
                proc.Start();
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    error = $"pnputil exit {proc.ExitCode}. {(string.IsNullOrEmpty(stderr) ? stdout.Trim() : stderr.Trim())}";
                    Debug.WriteLine($"pnputil restart failed (exit {proc.ExitCode}) for {instanceId}: {stdout} / {stderr}");
                    return false;
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"Failed to restart device {instanceId}: {ex.Message}");
                return false;
            }
        }

        public void SetDeviceAffinityManually(PciDeviceItem item, ulong affinityMask, int policy, int priority, bool msiEnabled, int msiLimit)
        {
            SetDeviceAffinityManually(item, affinityMask, policy, priority, msiEnabled, msiLimit, out _);
        }

        public bool SetDeviceAffinityManually(PciDeviceItem item, ulong affinityMask, int policy, int priority, bool msiEnabled, int msiLimit, out string? error)
        {
            try
            {
                string regPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                using (var key = Registry.LocalMachine.CreateSubKey(regPath, true))
                {
                    key.SetValue("DevicePolicy", policy, RegistryValueKind.DWord);
                    key.SetValue("DevicePriority", priority, RegistryValueKind.DWord);

                    byte[] maskBytes = BitConverter.GetBytes(affinityMask);
                    key.SetValue("AssignmentSetOverride", maskBytes, RegistryValueKind.Binary);
                }

                string msiPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                using (var msiKey = Registry.LocalMachine.CreateSubKey(msiPath, true))
                {
                    msiKey.SetValue("MSISupported", msiEnabled ? 1 : 0, RegistryValueKind.DWord);
                    msiKey.SetValue("MessageNumberLimit", msiLimit, RegistryValueKind.DWord);
                }

                error = null;
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = $"Access denied (run as Administrator): {ex.Message}";
                Debug.WriteLine($"Failed to set affinity for {item.Name}: {ex.Message}");
                return false;
            }
            catch (System.Security.SecurityException ex)
            {
                error = $"Security error: {ex.Message}";
                Debug.WriteLine($"Failed to set affinity for {item.Name}: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"Failed to set affinity for {item.Name}: {ex.Message}");
                return false;
            }
        }

        public void RefreshIsAdmin()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                IsAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to determine elevation: {ex.Message}");
                IsAdmin = false;
            }
        }

        private void SetDeviceAffinityPolicyOnly(PciDeviceItem item, ulong affinityMask, int priority)
        {
            if (!item.MsiEnabled) return;

            try
            {
                string regPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                using (var key = Registry.LocalMachine.CreateSubKey(regPath, true))
                {
                    key.SetValue("DevicePolicy", 4, RegistryValueKind.DWord); // IrqPolicySpecifiedProcessors
                    key.SetValue("DevicePriority", priority, RegistryValueKind.DWord);
                    byte[] maskBytes = BitConverter.GetBytes(affinityMask);
                    key.SetValue("AssignmentSetOverride", maskBytes, RegistryValueKind.Binary);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set affinity for {item.Name}: {ex.Message}");
            }
        }

        public record DeviceRestoreResult(bool Success, bool WasChanged, string? Error);

        public DeviceRestoreResult RestoreDeviceDefaults(PciDeviceItem item)
        {
            bool wasChanged = false;
            try
            {
                string affPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                using (var affKey = Registry.LocalMachine.OpenSubKey(affPath, writable: true))
                {
                    if (affKey != null)
                    {
                        if (affKey.GetValue("AssignmentSetOverride") != null)
                        {
                            affKey.DeleteValue("AssignmentSetOverride", throwOnMissingValue: false);
                            wasChanged = true;
                        }
                        if (affKey.GetValue("DevicePolicy") is int policyInt && policyInt != 0)
                        {
                            affKey.SetValue("DevicePolicy", 0, RegistryValueKind.DWord);
                            wasChanged = true;
                        }
                        if (affKey.GetValue("DevicePriority") is int priorityInt && priorityInt != 0)
                        {
                            affKey.SetValue("DevicePriority", 0, RegistryValueKind.DWord);
                            wasChanged = true;
                        }
                    }
                }

                string msiPath = $@"SYSTEM\CurrentControlSet\Enum\{item.DeviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                using (var msiKey = Registry.LocalMachine.OpenSubKey(msiPath, writable: true))
                {
                    if (msiKey != null)
                    {
                        if (msiKey.GetValue("MessageNumberLimit") != null)
                        {
                            msiKey.DeleteValue("MessageNumberLimit", throwOnMissingValue: false);
                            wasChanged = true;
                        }
                        if (msiKey.GetValue("MSISupported") != null)
                        {
                            msiKey.DeleteValue("MSISupported", throwOnMissingValue: false);
                            wasChanged = true;
                        }
                    }
                }

                return new DeviceRestoreResult(Success: true, WasChanged: wasChanged, Error: null);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Failed to restore defaults for {item.Name}: {ex.Message}");
                return new DeviceRestoreResult(Success: false, WasChanged: false, Error: $"Access denied (run as Administrator): {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore defaults for {item.Name}: {ex.Message}");
                return new DeviceRestoreResult(Success: false, WasChanged: false, Error: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Intelligent low-latency hardware scheduling optimization:
        ///   - Audio: E-Core (if available) or Core 0 / secondary P-Core, MSI=1, Normal priority.
        ///   - USB (XHCI): Dedicated P-Core, High priority, driver default MSI limit.
        ///   - Network (NIC): Dedicated P-Core, High priority, driver default MSI limit, RSS configured.
        ///   - GPU: 2 physical P-Cores on primary CCD, High priority, MSI limit = 1.
        ///   - Fallback: Gracefully shares cores on lower-core systems.
        /// </summary>
        [RelayCommand]
        public async Task OptimizeAffinitiesAsync()
        {
            if (IsLoading) return;

            int xhciSkippedMsiOff = 0;
            int networkSkippedMsiOff = 0;
            int gpuSkippedMsiOff = 0;
            int audioSkippedMsiOff = 0;

            bool audioTouched = AllDevices.Any(d => d.Category == "Audio Controllers" && d.MsiSupported);
            bool xhciTouched = AllDevices.Any(d => d.Category == "XHCI Controllers" && d.MsiSupported);
            bool networkTouched = AllDevices.Any(d => d.Category == "Network Interface Controllers" && d.MsiSupported);
            bool gpuTouched = AllDevices.Any(d => d.Category == "Graphics Cards" && d.MsiSupported);

            string dialogContent =
                "Intelligent Low-Latency Hardware Scheduling Profile:\n\n" +
                "• Audio: Assigned to an Efficiency Core (E-Core) if available, or dedicated/secondary core. Normal priority, MSI limit = 1 to eliminate DPC audio latency & crackling.\n" +
                "• USB (XHCI): Dedicated Performance Core (P-Core). High priority (3) for ultra-low mouse jitter (1000–8000Hz) with driver-native MSI vector scaling.\n" +
                "• Network (Wi-Fi / Ethernet): Dedicated Performance Core (P-Core). High priority (3) with automatic NDIS RSS base processor configuration.\n" +
                "• GPU: Dedicated adjacent Performance Cores (up to 4 logical threads). High priority (3), MSI limit = 1 for consistent frame pacing.\n\n" +
                "CPU 0 remains available for OS/HAL system interrupts. Affected devices will be hot-restarted in the background.\n\nProceed?";

            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "Optimize System Affinities",
                Content = dialogContent,
                PrimaryButtonText = "Optimize Now",
                CloseButtonText = "Cancel",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                XamlRoot = ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.Content?.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                return;
            }

            IsLoading = true;
            StatusText = "Applying intelligent low-latency scheduling profile...";

            var devicesCopy = AllDevices.ToList();
            bool hasChanges = false;
            bool hasGpuChanges = false;
            bool hasNetworkChanges = false;
            bool gpuSkipped = false;
            int restartSuccessCount = 0;
            int restartFailCount = 0;
            string? firstRestartError = null;

            await Task.Run(() =>
            {
                var cores = DetectCpuTopology();

                try
                {
                    uint totalLogical = KalOS.Helpers.TopologyHelper.GetActiveProcessorCount(0xffff);
                    if (totalLogical > 64)
                    {
                        var disp = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                        disp?.TryEnqueue(() => HasHighCoreCount = true);
                    }
                }
                catch (Exception ex) { _logging.Warn($"Failed to read processor topology: {ex.Message}"); }

                // Group cores
                var pCores = cores.Where(c => c.IsPCore && c.ProcessorGroup == 0).OrderBy(c => c.CoreId).ToList();
                var eCores = cores.Where(c => c.IsECore && c.ProcessorGroup == 0).OrderBy(c => c.CoreId).ToList();
                var nonZeroPCores = pCores.Where(c => c.LogicalProcessorMask != 1UL).ToList();
                if (nonZeroPCores.Count == 0) nonZeroPCores = pCores.Take(1).ToList();

                var claimedCoreIds = new HashSet<int>();

                // 1. Audio: E-core if hybrid, else core 0 / dedicated core
                ulong audioMask = 0;
                if (eCores.Count > 0)
                {
                    var freeE = eCores.FirstOrDefault(c => !claimedCoreIds.Contains(c.CoreId)) ?? eCores.First();
                    claimedCoreIds.Add(freeE.CoreId);
                    audioMask = freeE.FullCoreMask;
                }
                else
                {
                    // Non-hybrid (AMD or uniform Intel): assign Core 0 or first available core
                    var targetCore = cores.FirstOrDefault(c => c.ProcessorGroup == 0) ?? cores.First();
                    audioMask = targetCore.FullCoreMask;
                }

                // 2. XHCI: Dedicated P-Core
                ulong xhciMask = 0;
                var freeXhciCore = nonZeroPCores.FirstOrDefault(c => !claimedCoreIds.Contains(c.CoreId)) ?? nonZeroPCores.FirstOrDefault();
                if (freeXhciCore != null)
                {
                    claimedCoreIds.Add(freeXhciCore.CoreId);
                    xhciMask = freeXhciCore.FullCoreMask;
                }

                // 3. Network: Dedicated P-Core
                ulong networkMask = 0;
                int netFirstProc = 0;
                var freeNetCore = nonZeroPCores.FirstOrDefault(c => !claimedCoreIds.Contains(c.CoreId)) ?? nonZeroPCores.LastOrDefault();
                if (freeNetCore != null)
                {
                    claimedCoreIds.Add(freeNetCore.CoreId);
                    networkMask = freeNetCore.FullCoreMask;
                    netFirstProc = freeNetCore.Threads.FirstOrDefault()?.ThreadId ?? freeNetCore.CoreId;
                }

                // 4. GPU: 2 adjacent P-Cores
                ulong gpuMask = 0;
                bool gpuMaskUsable = false;
                var freeGpuCores = nonZeroPCores.Where(c => !claimedCoreIds.Contains(c.CoreId)).Take(2).ToList();
                if (freeGpuCores.Count < 2)
                {
                    freeGpuCores = nonZeroPCores.TakeLast(2).ToList();
                }

                if (freeGpuCores.Count > 0)
                {
                    foreach (var c in freeGpuCores)
                    {
                        gpuMask |= c.FullCoreMask;
                        claimedCoreIds.Add(c.CoreId);
                    }
                    gpuMaskUsable = true;
                }

                gpuSkipped = !gpuMaskUsable && gpuTouched;

                var devicesToRestart = new List<string>();

                foreach (var item in devicesCopy)
                {
                    if (!item.MsiSupported) continue;

                    if (item.Category == "Audio Controllers" && audioMask != 0)
                    {
                        ForceEnableMsiAndSetAffinity(item, audioMask, priority: 2, msiLimit: 1);
                        devicesToRestart.Add(item.DeviceId);
                        hasChanges = true;
                    }
                    else if (!item.MsiEnabled)
                    {
                        switch (item.Category)
                        {
                            case "XHCI Controllers": xhciSkippedMsiOff++; break;
                            case "Network Interface Controllers": networkSkippedMsiOff++; break;
                            case "Graphics Cards": gpuSkippedMsiOff++; break;
                        }
                        continue;
                    }
                    else if (item.Category == "XHCI Controllers" && xhciMask != 0)
                    {
                        SetDeviceAffinityPolicyOnly(item, xhciMask, priority: 3);
                        devicesToRestart.Add(item.DeviceId);
                        hasChanges = true;
                    }
                    else if (item.Category == "Network Interface Controllers" && networkMask != 0)
                    {
                        SetDeviceAffinityPolicyOnly(item, networkMask, priority: 3);
                        SetNdisRssProperties(item, netFirstProc);
                        devicesToRestart.Add(item.DeviceId);
                        hasChanges = true;
                        hasNetworkChanges = true;
                    }
                    else if (item.Category == "Graphics Cards" && gpuMaskUsable)
                    {
                        SetDeviceAffinity(item, gpuMask, priority: 3, msiLimit: 1);
                        devicesToRestart.Add(item.DeviceId);
                        hasGpuChanges = true;
                        hasChanges = true;
                    }
                }

                foreach (var id in devicesToRestart)
                {
                    if (RestartDevice(id, out string? err))
                    {
                        restartSuccessCount++;
                    }
                    else
                    {
                        restartFailCount++;
                        firstRestartError ??= err;
                    }
                }
            });

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(async () =>
                {
                    IsLoading = false;
                    string finalStatus;
                    if (!hasChanges)
                    {
                        int totalSkipped = audioSkippedMsiOff + xhciSkippedMsiOff + networkSkippedMsiOff + gpuSkippedMsiOff;
                        if (totalSkipped == 0)
                        {
                            finalStatus = "No MSI-capable Audio, USB, Network, or GPU devices found; nothing to optimize.";
                        }
                        else
                        {
                            finalStatus = $"Optimization completed with {totalSkipped} device(s) skipped due to line-based IRQ mode. Enable MSI on individual devices to customize.";
                        }
                    }
                    else if (hasGpuChanges)
                    {
                        finalStatus = "Audio, USB, Network, and GPU affinities successfully optimized.";
                    }
                    else if (hasNetworkChanges)
                    {
                        finalStatus = "Audio, USB, and Network affinities successfully optimized.";
                    }
                    else
                    {
                        finalStatus = "Affinities successfully optimized.";
                    }

                    if (hasChanges && (restartSuccessCount + restartFailCount) > 0)
                    {
                        if (restartFailCount == 0)
                        {
                            finalStatus += $" {restartSuccessCount} device(s) restarted in the background.";
                        }
                        else
                        {
                            finalStatus += $" {restartSuccessCount} restarted; {restartFailCount} deferred to next boot.";
                        }
                    }

                    await LoadDevicesAsync();
                    StatusText = finalStatus;
                });
            }
        }


        /// <summary>
        /// Recovery command: clears leftover custom affinity and MSI state on every MSI-capable
        /// device, then restarts the safe-to-restart ones. This is the panic button to run if the
        /// previous aggressive Optimize left your GPU / NIC pinned in a way that's still causing
        /// DPC_WATCHDOG_VIOLATION / SYSTEM_THREAD_EXCEPTION_NOT_HANDLED. The new Optimize itself
        /// only touches Audio + XHCI + Network + GPU, so for *future* runs these are untouched —
        /// but the registry from prior broken optimizes can persist, so we wipe all categories
        /// defensively.
        ///
        /// Restart is only attempted on Audio + XHCI. Restarting GPU / NIC via pnputil can trigger
        /// TDR / dxgkrnl crashes, so for those categories the registry is corrected and the change
        /// takes effect on the next driver reload or reboot.
        /// </summary>
        [RelayCommand]
        public async Task RestoreAllDefaultsAsync()
        {
            if (IsLoading) return;

            bool anyMsiDevices = AllDevices.Any(d => d.MsiSupported);

            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "Restore MSI / Affinity Defaults",
                Content = anyMsiDevices
                    ? "This clears any leftover custom affinity override and MSI message-number limit on every MSI-capable device \u2014 Audio, USB, Graphics, and Network. This is the recovery path if a previous Optimize left your GPU or NIC pinned in a state that was causing blue screens. Audio and USB devices will be restarted; GPU / Network customizations cleared."
                    : "No MSI-capable devices detected, so there is nothing to restore.",
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                XamlRoot = ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.Content?.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                return;
            }

            IsLoading = true;
            StatusText = "Restoring MSI / affinity defaults on every MSI-capable device...";

            var devicesCopy = AllDevices.ToList();
            bool hasChanges = false;
            bool hasRebootRequiredCategories = false;
            var restartList = new List<string>();

            await Task.Run(() =>
            {
                foreach (var item in devicesCopy)
                {
                    if (!item.MsiSupported) continue;

                    DeviceRestoreResult result = RestoreDeviceDefaults(item);
                    if (!result.Success)
                    {
                        // Surface access-denied / IO errors but don't count them as changes.
                        Debug.WriteLine($"Failed to restore defaults for {item.Name}: {result.Error}");
                        continue;
                    }

                    if (!result.WasChanged) continue; // already at default \u2014 nothing to report

                    hasChanges = true;
                    // Only restart Audio + XHCI \u2014 those restart cleanly. Restarting GPU / Storage /
                    // NIC via pnputil can trigger TDR / dxgkrnl crashes, so for those we leave
                    // the registry corrected and rely on the next driver reload / reboot.
                    if (item.Category == "Audio Controllers" || item.Category == "XHCI Controllers")
                    {
                        restartList.Add(item.DeviceId);
                    }
                    else
                    {
                        hasRebootRequiredCategories = true;
                    }
                }

                foreach (var id in restartList)
                {
                    RestartDevice(id);
                }
            });

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(async () =>
                {
                    IsLoading = false;
                    bool anyRestarted = restartList.Count > 0;
                    if (!hasChanges)
                    {
                        StatusText = "No devices had been customized.";
                    }
                    else if (anyRestarted && hasRebootRequiredCategories)
                    {
                        StatusText = "Defaults restored. Audio / USB devices were restarted; GPU / Network customizations cleared.";
                    }
                    else if (anyRestarted)
                    {
                        StatusText = "Defaults restored. Audio / USB devices were restarted; they are now on Machine Default.";
                    }
                    else
                    {
                        // Only GPU / NIC had stale state cleaned \u2014 no restart attempted for them.
                        StatusText = "Defaults restored. GPU / Network customizations cleared.";
                    }
                    await LoadDevicesAsync();
                });
            }
        }
    }
}
