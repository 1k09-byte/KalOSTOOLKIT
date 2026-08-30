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

        public AffinityManagerViewModel(LoggingService logging)
        {
            _logging = logging;
            // Determine elevation once at construction so the UI can show an "Admin required" InfoBar
            // before the user attempts to write anything.
            RefreshIsAdmin();
            // Re-evaluate HasDevices / HasNoDevices and the header stat pills any time the
            // underlying collection changes so x:Bind in the empty-state placeholder, the
            // populated list, and the stat strip stay in sync.
            AllDevices.CollectionChanged += (_, e) =>
            {
                // Per-item listeners keep the "MSI on" / "pinned" pills live when a single
                // device's registry state is re-read (Edit dialog, Optimize, etc.).
                if (e.NewItems != null) foreach (PciDeviceItem d in e.NewItems) d.PropertyChanged += OnDevicePropertyChanged;
                if (e.OldItems != null) foreach (PciDeviceItem d in e.OldItems) d.PropertyChanged -= OnDevicePropertyChanged;

                OnPropertyChanged(nameof(HasDevices));
                OnPropertyChanged(nameof(HasNoDevices));
                RefreshCounts();
            };
        }

        private void OnDevicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PciDeviceItem.MsiEnabled) or nameof(PciDeviceItem.SpecifiedProc))
                RefreshCounts();
        }

        private void RefreshCounts()
        {
            OnPropertyChanged(nameof(TotalDevices));
            OnPropertyChanged(nameof(MsiEnabledCount));
            OnPropertyChanged(nameof(PinnedCount));
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

        public ObservableCollection<PciDeviceItem> AllDevices { get; } = new();
        public ObservableCollection<PciDeviceGroup> GroupedDevices { get; } = new();
        public List<CpuCoreInfo> SystemCores { get; private set; } = new();

        [ObservableProperty]
        private bool _hasHighCoreCount;

        /// <summary>Computed: device list is populated AND initial scan finished.</summary>
        public bool HasDevices => !IsLoading && AllDevices.Count > 0;

        /// <summary>Computed: scan complete AND no devices found — drives the empty-state placeholder.</summary>
        public bool HasNoDevices => !IsLoading && AllDevices.Count == 0;

        // ── Header stat pills (refreshed by RefreshCounts / topology detect) ──

        /// <summary>Total PCI devices shown in the scheduling list.</summary>
        public int TotalDevices => AllDevices.Count;

        /// <summary>Devices currently running in MSI mode.</summary>
        public int MsiEnabledCount => AllDevices.Count(d => d.MsiEnabled);

        /// <summary>Devices pinned to specific cores via AssignmentSetOverride.</summary>
        public int PinnedCount => AllDevices.Count(d => !string.IsNullOrEmpty(d.SpecifiedProc));

        /// <summary>Physical cores available for scheduling (group-0 topology).</summary>
        public int CpuCoreCount => SystemCores.Count;

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
                    // Find disconnected network adapters to exclude
                    var disconnectedNetAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var netSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT PNPDeviceID, NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionStatus IS NOT NULL");
                        foreach (ManagementObject netObj in netSearcher.Get())
                        {
                            string? id = netObj["PNPDeviceID"] as string;
                            if (!string.IsNullOrEmpty(id))
                            {
                                // NetConnectionStatus: 2 = Connected, 4 = Disconnected, 7 = Media Disconnected
                                // If it's not 2 (Connected), we consider it unplugged/unused.
                                int status = Convert.ToInt32(netObj["NetConnectionStatus"]);
                                if (status != 2)
                                {
                                    disconnectedNetAdapters.Add(id);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to get network adapters: {ex.Message}");
                    }

                    var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_PnPEntity");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"] as string ?? "";
                        string deviceId = obj["PNPDeviceID"] as string ?? "";
                        string pnpClass = obj["PNPClass"] as string ?? "";
                        
                        // Check if device is working properly (ConfigManagerErrorCode == 0)
                        // A value other than 0 usually means disabled or not functioning.
                        uint configErrorCode = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;
                        if (configErrorCode != 0) continue;

                        if (string.IsNullOrEmpty(deviceId) || !deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;

                        string category = "";
                        if (pnpClass.Equals("Display", StringComparison.OrdinalIgnoreCase)) category = "Graphics Cards";
                        else if (pnpClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("Audio", StringComparison.OrdinalIgnoreCase) || name.Contains("Sound", StringComparison.OrdinalIgnoreCase)) category = "Audio Controllers";
                        else if (pnpClass.Equals("Net", StringComparison.OrdinalIgnoreCase) || name.Contains("Network", StringComparison.OrdinalIgnoreCase) || name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)) category = "Network Interface Controllers";
                        else if ((pnpClass.Equals("USB", StringComparison.OrdinalIgnoreCase) || name.Contains("USB", StringComparison.OrdinalIgnoreCase)) && (name.Contains("xHCI", StringComparison.OrdinalIgnoreCase) || name.Contains("Extensible", StringComparison.OrdinalIgnoreCase))) category = "XHCI Controllers";
                        // Storage controllers intentionally excluded from the affinity list
                        // per user request. They were previously shown here but have been removed.
                        
                        if (string.IsNullOrEmpty(category)) continue; // skip non-relevant PCI devices
                        
                        // Skip disconnected network adapters
                        if (category == "Network Interface Controllers" && disconnectedNetAdapters.Contains(deviceId))
                        {
                            continue;
                        }

                        var item = new PciDeviceItem
                        {
                            Name = name,
                            DeviceId = deviceId,
                            Category = category
                        };

                        // Best-practice MSI limit defaults per device class:
                        //   Audio:   1  (Realtek ALC drivers break > 1 — causes crackling/popping)
                        //   NVMe:   32  (multi-queue parallelism; high random-IOPS ceiling)
                        //   GPU:     4  (modern GPUs use MSI-X vectors > 1; cap=1 wastes PCIe bandwidth)
                        //   XHCI:    8  (USB 3.0+ controllers benefit from moderate parallelism)
                        //   Network:32  (high parallelism for NIC interrupt moderation)
                        //   SATA:    8  (AHCI single-vector is sufficient for general storage)
if (category == "Network Interface Controllers") item.MaxMsiLimit = "32";
            else if (category == "XHCI Controllers") item.MaxMsiLimit = "8";
            else if (category == "Audio Controllers") item.MaxMsiLimit = "1";
            else if (category == "Graphics Cards") item.MaxMsiLimit = "1";
                        else if (category == "Storage Controllers")
                        {
                            // NVMe needs many vectors for IO queue parallelism; SATA AHCI single-vector is fine.
                            // Match both "NVMe" (marketing spelling) and "NVM Express" (Windows device name).
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

                        var groups = AllDevices.GroupBy(x => x.Category);
                        foreach (var g in groups)
                        {
                            GroupedDevices.Add(new PciDeviceGroup(g.Key, g));
                        }
                        SystemCores = DetectCpuTopology();
                        OnPropertyChanged(nameof(CpuCoreCount));
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
                // Windows only exposes 64 bits per group in MSI AssignmentSetOverride,
                // and we don't have a kernel-mode path to write group-aware affinity.
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
                var fallback = new CpuCoreInfo { CoreId = 0, LogicalProcessorMask = 1, FullCoreMask = 1 };
                fallback.Threads.Add(new CpuThreadInfo { ThreadId = 0 });
                cores.Add(fallback);
            }
            return cores;
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

                // SAFETY: Only update MessageNumberLimit when the device is currently
                // actively running in MSI mode. We deliberately do NOT write MSISupported=1
                // as a forced override \u2014 a previous version of this tool did, and that 0\u21921 flip
                // on Audio devices whose native driver uses line-based IRQs (e.g. Realtek
                // HDAudio on certain chipsets) has been observed to crash in the driver's
                // ISR/DPC setup path on next reboot, surfacing as
                // SYSTEM_THREAD_EXCEPTION_NOT_HANDLED (0x7E).
                //
                // Distinguish the two meanings here:
                //   * MsiSupported == true  : the MSI registry subkey exists for this device.
                //   * MsiEnabled   == true  : the OS is currently using MSI for this device.
                // Only when BOTH are true do we touch the limit \u2014 otherwise leave MSI alone.
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

        /// <summary>
        /// Forces MSI mode ON for audio controllers and sets affinity + MSI limit.
        /// Unlike SetDeviceAffinity, this writes MSISupported=1 even if currently disabled,
        /// which is necessary for audio controllers that ship with MSI off by default.
        /// The restart after writing makes the change take effect immediately.
        /// </summary>
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
            // Backwards-compatible void overload.
            RestartDevice(instanceId, out _);
        }

        /// <summary>
        /// Restarts the PCI device via pnputil. Returns true if pnputil reported success.
        /// pnputil exit code 0 means SUCCESS; non-zero (often 0xE000020B or similar) means
        /// the device driver rejected the hot-restart or no driver supports it \u2014 in which
        /// case the registry change will only take effect on the next cold boot.
        /// </summary>
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
            // Backwards-compatible void overload retained for any future callers that don't care about success/failure.
            // Internally the rich overload (return bool + out error) handles the actual write.
            SetDeviceAffinityManually(item, affinityMask, policy, priority, msiEnabled, msiLimit, out _);
        }

        /// <summary>
        /// Writes the affinity policy + MSI settings to the device's registry key and returns true
        /// if the write succeeded. False indicates the registry write was blocked (e.g. not elevated,
        /// ACL denies access, registry path missing for this device) — callers MUST surface this
        /// to the user instead of proceeding to a restart prompt that will then re-read stale data.
        /// </summary>
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

        /// <summary>True if the current process is elevated to Administrator (required to write MSI / affinity keys).</summary>
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

        /// <summary>
        /// Writes DevicePolicy + DevicePriority + AssignmentSetOverride WITHOUT touching MSI/MSI-X.
        /// Used for XHCI, where a custom core affinity is desired but the driver must keep its own
        /// MessageNumberLimit (overriding that field crashes isochronous USB transfers — mice /
        /// keyboards / USB audio interfaces can stall).
        /// </summary>
        private void SetDeviceAffinityPolicyOnly(PciDeviceItem item, ulong affinityMask, int priority)
        {
            // SAFETY GATE: only write IrqPolicySpecifiedProcessors (4) to devices that are
            // ACTIVELY using MSI. Writing DevicePolicy=4 to a line-IRQ device that hasn't been
            // flipped to MSI can confuse the driver \u2014 the kernel may try to route interrupts
            // through the AssignmentSetOverride but the ISR/DPC path still expects legacy
            // line-based routing, which has been observed to cause 0x7E
            // SYSTEM_THREAD_EXCEPTION_NOT_HANDLED on next reboot. The MSI subkey existing
            // (MsiSupported) is not sufficient \u2014 we need MsiEnabled, which means the OS is
            // currently using MSI for this device.
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

        /// <summary>
        /// Restores a single device's MSI / affinity registry to Windows defaults. Recovery path
        /// for users whose device misbehaves after Optimize. Deletes AssignmentSetOverride,
        /// resets DevicePolicy=0 and DevicePriority=0, and removes MessageNumberLimit. MSISupported
        /// is intentionally left untouched: flipping it from 1→0 mid-session forces the driver to
        /// re-run its line-IRQ init path, which is more crash-prone than leaving MSI on with no
        /// customization.
        /// </summary>
        /// <summary>
        /// Result of <see cref="RestoreDeviceDefaults"/>.
        /// </summary>
        /// <param name="Success">True if the registry call did not throw. False on permission/IO error.</param>
        /// <param name="WasChanged">True if at least one registry value was actually different from
        /// default and was therefore written/deleted. Lets callers distinguish "device already at
        /// default" from "device had stale overrides that we cleared".</param>
        /// <param name="Error">Human-readable error string when <see cref="Success"/> is false.</param>
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
                        // Compare-then-write: only mark wasChanged when the registry was actually
                        // different from the default. Lets the caller accurately report "already at
                        // default" vs. "we cleared stale state".
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
                        // Drop MessageNumberLimit so the driver rebuilds / picks its default.
                        if (msiKey.GetValue("MessageNumberLimit") != null)
                        {
                            msiKey.DeleteValue("MessageNumberLimit", throwOnMissingValue: false);
                            wasChanged = true;
                        }
                        // ALSO drop MSISupported. A previous buggy Optimize run could have flipped
                        // this from 0 \u2192 1 on a device whose driver uses line-based IRQs; that flag
                        // would otherwise persist on the next reboot, forcing the driver into MSI
                        // crash territory (0x7E SYSTEM_THREAD_EXCEPTION_NOT_HANDLED) even after the
                        // rest of the affinity profile was cleared. Drivers that natively use MSI
                        // will re-write the value themselves on the next enumeration \u2014 safe to
                        // delete and let the driver re-assert.
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
        /// Restores every listed device to Windows defaults (same as
        /// <see cref="RestoreDeviceDefaults"/> per device), then re-reads the
        /// registry so the UI and stat pills reflect the cleared state.
        /// </summary>
        public void RestoreAll()
        {
            if (AllDevices.Count == 0) return;
            foreach (var item in AllDevices.ToList())
            {
                RestoreDeviceDefaults(item);
            }
            foreach (var item in AllDevices)
            {
                ReadMsiRegistry(item);
            }
            StatusText = $"Restored {AllDevices.Count} device(s) to Windows defaults.";
            RefreshCounts();
        }

        /// <summary>
        /// Low-latency optimization that pins every device class except Audio to dedicated
        /// physical cores with High priority. Audio is kept at Normal priority with MSI=1 to
        /// avoid crackling/popping on Realtek ALC and similar codecs.
        ///
        ///   Audio    : IrqPolicySpecifiedProcessors (4), FullCoreMask of one non-CPU0 physical
        ///              core, Normal priority (2), MessageNumberLimit=1.
        ///   XHCI     : IrqPolicySpecifiedProcessors (4), FullCoreMask of one non-CPU0 physical
        ///              core (distinct from Audio's), High priority (3). MessageNumberLimit is
        ///              NOT touched — XHCI drivers compute their own vector count.
        ///   Network  : IrqPolicySpecifiedProcessors (4), FullCoreMask of one non-CPU0 physical
        ///              core (distinct from Audio/XHCI's), High priority (3). MessageNumberLimit
        ///              is NOT touched — RSS/RSC depends on multiple vectors.
        ///   GPU      : IrqPolicySpecifiedProcessors (4), up to 4 logical processors on
        ///              dedicated physical cores, High priority (3). MessageNumberLimit is
        ///              NOT touched — NVIDIA/AMD scale MSI-X vectors dynamically.
        ///
        /// Other guarantees:
        ///   * CPU 0 is NOT globally disabled — system threads can still use it. We only avoid
        ///     CPU 0 as a *pinning target* because of its existing interrupt traffic.
        ///   * The GPU is NOT restarted via pnputil. Restarting the primary adapter under a WinUI 3
        ///     app has been observed to TDR-crash dxgkrnl (0x116). Affected Audio/USB/Network
        ///     devices are restarted instead; GPU changes take effect on the next driver reload
        ///     or reboot.
        /// </summary>
        [RelayCommand]
        public async Task OptimizeAffinitiesAsync()
        {
            if (IsLoading) return;

            // Per-category skip counters: when MsiEnabled=false but MsiSupported=true, the device
            // has the MSI subkey but is currently using line-based IRQs. We deliberately skip
            // writing IrqPolicySpecifiedProcessors to those devices (it would confuse the line-IRQ
            // driver path and has been observed to cause 0x7E SYSTEM_THREAD_EXCEPTION_NOT_HANDLED).
            // Track the counts so we can surface them in the final StatusText \u2014 otherwise users
            // would see "nothing to optimize" with no explanation of why their hardware was skipped.
            int xhciSkippedMsiOff = 0;
            int networkSkippedMsiOff = 0;
            int gpuSkippedMsiOff = 0;
            int audioSkippedMsiOff = 0;

            bool audioTouched = AllDevices.Any(d => d.Category == "Audio Controllers" && d.MsiSupported);
            bool xhciTouched = AllDevices.Any(d => d.Category == "XHCI Controllers" && d.MsiSupported);
            bool networkTouched = AllDevices.Any(d => d.Category == "Network Interface Controllers" && d.MsiSupported);
            bool gpuTouched = AllDevices.Any(d => d.Category == "Graphics Cards" && d.MsiSupported);

            string dialogContent =
                "Low-latency profile.\n\n" +
                "• Audio: pinned to a dedicated physical core (not CPU 0). MSI limit = 1 (only when device natively uses MSI; line-IRQ devices left untouched so driver mode is never changed), Normal priority.\n" +
                "• USB (XHCI): pinned to a different dedicated physical core (not CPU 0). High priority; MSI limit left at driver default.\n" +
                "• Network (WiFi / Ethernet): pinned to a different dedicated physical core (not CPU 0). High priority; MSI limit left at driver default.\n" +
                "• GPU: pinned to up to 4 dedicated physical cores (less than the default all-CPU distribution). High priority (preempts other driver DPCs to minimize render latency). MSI limit = 1. The GPU IS restarted — your screen will flicker/go black for a few seconds while the graphics stack re-initializes.\n" +
                "\nCPU 0 stays available for system threads. Audio / USB / Network / GPU devices are restarted in the background after the registry writes.\n\n";

            if (audioTouched || xhciTouched || networkTouched || gpuTouched)
            {
                dialogContent += "Affected device(s) will be restarted briefly — including the GPU (screen flicker expected). ";
            }
            dialogContent += "Proceed?";

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
            StatusText = "Optimizing Audio / USB / Network / Storage / GPU affinities (low-latency profile)...";

            var devicesCopy = AllDevices.ToList();
            bool hasChanges = false;
            bool hasGpuChanges = false;
            bool hasNetworkChanges = false;
            bool gpuSkipped = false;
            // Track per-device restart outcomes so the final StatusText can
            // honestly tell the user whether pnputil's hot-restart actually
            // took effect or whether they need a reboot. pnputil /restart-device
            // can silently fail (device in use, driver doesn't support
            // hot-restart, ACL denies the cycle); the prior code discarded the
            // error so the user had no signal that their affinity change was
            // still pending until the next boot.
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

                // Build the pinning candidate pool. We exclude CPU 0 (LogicalProcessorMask == 1)
                // as a *pinning target* but do NOT black-list it from the system — system threads
                // keep using CPU 0 as normal. Avoiding CPU 0 as a target is purely because that
                // single logical processor already carries HAL/SMI tick traffic, and adding audio
                // DPCs there would saturate it.
                var pinningCandidates = cores.Where(c => c.LogicalProcessorMask != 1UL).ToList();
                if (pinningCandidates.Count == 0)
                {
                    // Edge case: single-core / no-spare systems. Fall back to CPU 0 rather than
                    // leaving the device's policy unchanged — at least we set a low priority.
                    pinningCandidates = cores.Take(1).ToList();
                }

            var audioCore = pinningCandidates.FirstOrDefault();
            var xhciCore = pinningCandidates.Count > 1 ? pinningCandidates[1] : null;
            var networkCore = pinningCandidates.Count > 2 ? pinningCandidates[2] : null;

                // FullCoreMask covers BOTH SMT threads of the physical core. We deliberately allow
                // both threads (rather than forcing a single thread) so that if one SMT sibling is
                // briefly occupied by another thread, the audio/USB DPC has a fallback thread on
                // the same core — preventing the DPC queue stalling the audio engine under load.
            ulong audioMask = audioCore?.FullCoreMask ?? 0UL;
            ulong xhciMask = xhciCore?.FullCoreMask ?? 0UL;
            ulong networkMask = networkCore?.FullCoreMask ?? 0UL;

                // GPU optimization is always applied in the modern flow (no opt-in checkbox). Gated
                // only on having enough non-CPU0 cores to give the GPU at least one core above
                // the Audio + XHCI pinning candidates.
                // beyond Audio and XHCI. We allocate up to MAX_GPU_CORES cores from the candidate
                // pool starting at index 2 (slots 0 and 1 are already taken by Audio and XHCI).
                //
                // We deliberately use a multi-core span (NOT a single thread) because modern GPUs
                // (NVIDIA RTX 30/40, AMD RX 6000/7000) parallelize DPCs across their MSI-X vectors
                // for command list building and completion rings. Less than ~2 physical cores causes
                // the DPC queue to stall, which is what produced the DPC_WATCHDOG_VIOLATION 0x133
                // bug check in the previous version of this tool. 2-4 cores is the established
                // sweet spot. MSI vector limit is left at the driver default for the same reason.
                // Target up to 4 logical processors for the GPU (user-requested lower bound).
                // We cap by *logical processors* (set bits in the FullCoreMask) rather than core
                // count so this works correctly on any SMT layout:
                //   SMT=2 desktop (typical): selects 2 cores  -> 4 SMT threads.
                //   SMT=1 (older / disabled): selects up to 4 cores -> 4 logical processors.
                //   SMT>=4 (rare high-core):   selects 1 core   -> 4 SMT threads.
                // Pin the GPU to between MinGpuLogicalProcessors (2) and MaxGpuLogicalProcessors (4)
                // logical processors. Both bounds are enforced by the loop:
                //   * Below Min: always include cores regardless of nextBits (we need at least Min).
                //   * At/above Min: stop once the next core would exceed Max.
                // On underprovisioned systems (e.g. SMT=1 quad-core where Audio+XHCI take 2 of
                // 3 candidate cores and only 1 LP is left for GPU) the loop ends below Min; we
                // mark gpuMaskUsable = false and skip the GPU branch so we never write a sub-Min
                // mask to the GPU's AssignmentSetOverride. On any system with >=4 non-CPU0
                // cores the user sees a value strictly inside [Min, Max].
                const int MinGpuLogicalProcessors = 2;
                const int MaxGpuLogicalProcessors = 2;
                ulong gpuMask = 0;
                bool gpuMaskUsable = false;
                // GPU starts at idx=3 below because Audio (0), XHCI (1), and Network (2)
                // take the first three candidate cores. The GPU IS added to devicesToRestart
                // (user-requested): the adapter is restarted after the registry writes so the
                // affinity + MSI limit apply immediately — expect a brief screen flicker.
                if (pinningCandidates.Count > 3)
                {
                    int idx = 3;
                    while (idx < pinningCandidates.Count)
                    {
                        ulong coreMask = pinningCandidates[idx].FullCoreMask;
                        int currentBits = System.Numerics.BitOperations.PopCount(gpuMask);
                        int nextBits = currentBits + System.Numerics.BitOperations.PopCount(coreMask);
                        // Stop only once we're at Min AND the next core would exceed Max.
                        if (currentBits >= MinGpuLogicalProcessors && nextBits > MaxGpuLogicalProcessors) break;
                        gpuMask |= coreMask;
                        idx++;
                    }
                    gpuMaskUsable = System.Numerics.BitOperations.PopCount(gpuMask) >= MinGpuLogicalProcessors;
                }
                // True when the user opted INTO GPU optimization but the system couldn't provide
                // enough cores to satisfy Min. The GPU branch in the per-device loop is then
                // skipped so we never write a sub-Min mask, but we'd otherwise give the user no
                // feedback that their intent was heard and rejected. Status text surfaces this state.
                gpuSkipped = pinningCandidates.Count > 3 && !gpuMaskUsable;

                var devicesToRestart = new List<string>();

                foreach (var item in devicesCopy)
                {
                    if (!item.MsiSupported) continue;

                    // Audio controllers are always optimized regardless of MSI state.
                    // Force enable MSI mode and set affinity.
                    if (item.Category == "Audio Controllers" && audioMask != 0)
                    {
                        ForceEnableMsiAndSetAffinity(item, audioMask, priority: 2, msiLimit: 1);
                        devicesToRestart.Add(item.DeviceId);
                        hasChanges = true;
                    }

                    // Per-category skip accounting for the MSI gate. MsiSupported=true but
                    // MsiEnabled=false means the device has the MSI subkey but is currently
                    // using line-based IRQs \u2014 unsafe to write IrqPolicySpecifiedProcessors to.
                    if (!item.MsiEnabled)
                    {
                        switch (item.Category)
                        {
                            case "XHCI Controllers":        xhciSkippedMsiOff++;   break;
                            case "Network Interface Controllers": networkSkippedMsiOff++; break;
                            case "Graphics Cards":          gpuSkippedMsiOff++;     break;
                        }
                        continue;
                    }
                    else if (item.Category == "XHCI Controllers" && xhciMask != 0)
                    {
                        // XHCI uses SetDeviceAffinityPolicyOnly: we set the affinity mask, but we
                        // do NOT touch MessageNumberLimit. The XHCI driver sets its own count
                        // based on enabled ports; overriding it crashes isochronous transfers
                        // (USB mice / keyboards / audio interfaces).
                        SetDeviceAffinityPolicyOnly(item, xhciMask, priority: 3);
                        devicesToRestart.Add(item.DeviceId);
                        hasChanges = true;
                    }
                    else if (item.Category == "Network Interface Controllers" && networkMask != 0)
                    {
                        // Network uses SetDeviceAffinityPolicyOnly for the same reason as XHCI:
                        // NIC drivers scale their vector count based on RSS queues / interrupt
                        // moderation, and force-restricting MessageNumberLimit can stall
                        // multi-queue traffic. pnputil /restart-device is safe for NICs (the
                        // adapter briefly disconnects, then reconnects — apps with retry logic
                        // are unaffected, and Wi-Fi roaming reassociates within seconds).
                        SetDeviceAffinityPolicyOnly(item, networkMask, priority: 3);
                        devicesToRestart.Add(item.DeviceId);
                        hasChanges = true;
                        hasNetworkChanges = true;
                    }
                    else if (item.Category == "Graphics Cards" && gpuMaskUsable)
                    {
                        // GPU optimization. User-requested behavior: MSI MessageNumberLimit
                        // is forced to 1 (single vector) and the adapter IS restarted after
                        // the registry writes so everything applies immediately.
                        // NOTE: restarting the primary graphics adapter makes the screen
                        // flicker/go black for a few seconds while dxgkrnl re-initializes.
                        SetDeviceAffinity(item, gpuMask, priority: 3, msiLimit: 1);
                        devicesToRestart.Add(item.DeviceId);
                        hasGpuChanges = true;
                        hasChanges = true;
                    }
                }

                // Restart affected devices so affinity changes take effect immediately.
                // Use the rich overload so we can capture success/failure per device
                // and surface the actual outcome to the user.
                foreach (var id in devicesToRestart)
                {
                    if (RestartDevice(id, out string? err))
                    {
                        restartSuccessCount++;
                    }
                    else
                    {
                        restartFailCount++;
                        // Capture only the first error — the failure pattern is almost
                        // always the same root cause (e.g. device in use), so showing
                        // the first is more useful than a noisy list.
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
                    // Build the final StatusText BEFORE LoadDevicesAsync. The
                    // rescan overwrites StatusText with "Scanning PCI
                    // devices..." / "Loaded N devices.", so we re-apply our
                    // final message AFTER it completes — otherwise the user
                    // never sees the restart outcome and only sees the
                    // generic "Loaded N devices." line.
                    string finalStatus;
                    if (!hasChanges)
                    {
                        // Build a per-category skip summary so the user understands WHY nothing
                        // was applied \u2014 a device supporting MSI but not actively using it is
                        // skipped for safety (writing IrqPolicySpecifiedProcessors to a line-IRQ
                        // device has been observed to cause 0x7E BSODs).
                        int totalSkipped = audioSkippedMsiOff + xhciSkippedMsiOff + networkSkippedMsiOff + gpuSkippedMsiOff;
                        if (totalSkipped == 0)
                        {
                            finalStatus = "No MSI-capable Audio, USB, Network, or GPU devices found; nothing to optimize.";
                        }
                        else
                        {
                            var parts = new List<string>();
                            if (audioSkippedMsiOff   > 0) parts.Add($"Audio: {audioSkippedMsiOff}");
                            if (xhciSkippedMsiOff    > 0) parts.Add($"XHCI: {xhciSkippedMsiOff}");
                            if (networkSkippedMsiOff > 0) parts.Add($"Network: {networkSkippedMsiOff}");
                            if (gpuSkippedMsiOff     > 0) parts.Add($"GPU: {gpuSkippedMsiOff}");
                            finalStatus = $"No MSI-active devices to optimize (skipped {totalSkipped} device(s) that support MSI but are using line-based IRQs: {string.Join(", ", parts)}). Enable MSI in the per-device dialog to allow optimization, or run the per-device dialog directly.";
                        }
                    }
                    else if (hasGpuChanges)
                    {
                        finalStatus = "Audio / USB / Network / GPU affinities applied.";
                    }
                    else if (hasNetworkChanges)
                    {
                        finalStatus = "Audio / USB / Network affinities applied.";
                    }
                    else if (gpuSkipped)
                    {
                        finalStatus = "Audio / USB / Network affinities applied. GPU optimization was skipped because this CPU could not satisfy the 2-logical-processor minimum (Audio + XHCI + Network took the available candidate cores).";
                    }
                    else
                    {
                        // Reached when hasChanges=true but none of the per-category change
                        // flags are set. In practice this means only Audio/USB were modified
                        // and the GPU branch in the loop was skipped \u2014 either because
                        // gpuMaskUsable=false (insufficient candidate cores) or every GPU
                        // device was skipped by the MsiEnabled gate. Point the user at the
                        // skip summary (or at the per-device dialog) rather than implying
                        // the bulk path intentionally skips GPU.
                        finalStatus = "Audio / USB affinities applied. GPU was skipped in the bulk run \u2014 check the per-device dialog or the skipped-device summary for the reason.";
                    }

                    // Restart outcome suffix — the GPU is now part of the restart
                    // list, so a plain success/fail summary covers everything.
                    if (hasChanges && (restartSuccessCount + restartFailCount) > 0)
                    {
                        if (restartFailCount == 0)
                        {
                            finalStatus += $" {restartSuccessCount} device(s) restarted in the background.";
                        }
                        else if (restartSuccessCount == 0)
                        {
                            finalStatus += $" Device restart failed ({firstRestartError ?? "pnputil rejected"}) \u2014 affinity change takes effect on next reboot.";
                        }
                        else
                        {
                            finalStatus += $" {restartSuccessCount} restarted; {restartFailCount} failed ({firstRestartError ?? "pnputil rejected"}) \u2014 failed ones take effect on next reboot.";
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
