using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KalOS.ViewModels
{
    public partial class DeviceAffinityViewModel : ObservableObject
    {
        public PciDeviceItem Device { get; }
        
        public string DeviceName => Device.Name;
        public string DeviceId => Device.DeviceId;
        public string DeviceCategory => Device.Category;
        
        public ObservableCollection<CpuCoreInfo> Cores { get; }
        public ObservableCollection<CpuCoreGroup> Groups { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsMsiLimitEnabled))]
        private bool _msiEnabled;

        public bool IsMsiLimitEnabled => MsiEnabled;

        [ObservableProperty] private int _msiLimit;
        [ObservableProperty] private string _devicePriority = "Undefined";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCoreSelectionEnabled))]
        private string _devicePolicy = "IrqPolicyMachineDefault";

        public bool IsCoreSelectionEnabled => DevicePolicy == "IrqPolicySpecifiedProcessors";

        [ObservableProperty] private string _policyDescription = "";

        public int MaxMsiLimit { get; }
        public string MaxMsiLimitText => $"(Max: {MaxMsiLimit})";
        public string MsiLimitDescription { get; }

        public List<string> Priorities { get; } = new() { "Undefined", "Low", "Normal", "High" };
        public List<string> Policies { get; } = new() 
        { 
            "IrqPolicyMachineDefault", 
            "IrqPolicyAllCloseProcessors", 
            "IrqPolicyOneCloseProcessor", 
            "IrqPolicyAllProcessorsInMachine", 
            "IrqPolicySpecifiedProcessors", 
            "IrqPolicySpreadMessagesAcrossAllProcessors" 
        };

        public DeviceAffinityViewModel(PciDeviceItem device, IEnumerable<CpuCoreInfo> cores)
        {
            Device = device;
            
            var procStrings = (device.SpecifiedProc ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                          .Select(s => s.Trim())
                                                          .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var dialogCores = new List<CpuCoreInfo>();
            foreach (var core in cores)
            {
                var c = new CpuCoreInfo
                {
                    CoreId = core.CoreId,
                    CoreTypeLabel = core.CoreTypeLabel,
                    IsPCore = core.IsPCore,
                    IsECore = core.IsECore,
                    EfficiencyClass = core.EfficiencyClass,
                    L3CacheId = core.L3CacheId,
                    NumaNodeId = core.NumaNodeId,
                    ProcessorGroup = core.ProcessorGroup,
                    LogicalProcessorMask = core.LogicalProcessorMask,
                    FullCoreMask = core.FullCoreMask
                };

                foreach (var thread in core.Threads)
                {
                    bool selected = procStrings.Contains(thread.ThreadId.ToString());
                    var tInfo = new CpuThreadInfo
                    {
                        ThreadId = thread.ThreadId,
                        IsSelected = selected
                    };
                    tInfo.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(CpuThreadInfo.IsSelected))
                        {
                            OnPropertyChanged(nameof(SelectedThreadsSummary));
                            OnPropertyChanged(nameof(CalculatedMaskHex));
                        }
                    };
                    c.Threads.Add(tInfo);
                }
                dialogCores.Add(c);
            }
            Cores = new ObservableCollection<CpuCoreInfo>(dialogCores);

            // Group cores for UI rendering
            BuildGroups(dialogCores);
            
            MsiEnabled = device.MsiEnabled;
            int maxLimit = int.TryParse(device.MaxMsiLimit, out int m) ? m : 8;
            MaxMsiLimit = maxLimit;
            
            if (device.Category == "Graphics Cards" || device.Category == "Audio Controllers")
            {
                MsiLimitDescription = "The maximum MSI limit for Graphics and Audio controllers is 1 to prevent DPC latency spikes and ensure stability.";
            }
            else
            {
                MsiLimitDescription = "Specifies the maximum number of MSI messages the device can allocate.";
            }
            
            int parsedLimit = int.TryParse(device.MsiLimit, out int l) && l > 0 ? l : 1;
            MsiLimit = Math.Min(parsedLimit, MaxMsiLimit);
            
            DevicePriority = string.IsNullOrEmpty(device.DevicePriority) ? "Undefined" : device.DevicePriority;
            DevicePolicy = string.IsNullOrEmpty(device.DevicePolicy) ? "IrqPolicyMachineDefault" : device.DevicePolicy;
            UpdatePolicyDescription(DevicePolicy);
        }

        private void BuildGroups(List<CpuCoreInfo> cores)
        {
            Groups.Clear();

            bool isHybrid = cores.Any(c => c.IsECore) && cores.Any(c => c.IsPCore);
            bool isMultiCcd = cores.Select(c => c.L3CacheId).Distinct().Count() > 1 && !isHybrid;

            if (isHybrid)
            {
                var pCores = cores.Where(c => c.IsPCore).ToList();
                var eCores = cores.Where(c => c.IsECore).ToList();

                if (pCores.Count > 0)
                {
                    var pGrp = new CpuCoreGroup
                    {
                        Name = "Performance Cores (P-Cores)",
                        ShortName = "P-Cores",
                        Description = "High performance cores with HyperThreading",
                        RecommendedColumns = Math.Clamp(pCores.Count, 2, 4)
                    };
                    foreach (var c in pCores) pGrp.Cores.Add(c);
                    Groups.Add(pGrp);
                }

                if (eCores.Count > 0)
                {
                    var eGrp = new CpuCoreGroup
                    {
                        Name = "Efficiency Cores (E-Cores)",
                        ShortName = "E-Cores",
                        Description = "Low-power background cores",
                        RecommendedColumns = Math.Clamp(eCores.Count, 2, 4)
                    };
                    foreach (var c in eCores) eGrp.Cores.Add(c);
                    Groups.Add(eGrp);
                }
            }
            else if (isMultiCcd)
            {
                var ccds = cores.GroupBy(c => c.L3CacheId).OrderBy(g => g.Key);
                foreach (var ccd in ccds)
                {
                    var grp = new CpuCoreGroup
                    {
                        Name = $"CCD {ccd.Key} Cores",
                        ShortName = $"CCD {ccd.Key}",
                        Description = ccd.Key == 0 ? "Primary Core Complex" : "Secondary Core Complex",
                        RecommendedColumns = Math.Clamp(ccd.Count(), 2, 4)
                    };
                    foreach (var c in ccd) grp.Cores.Add(c);
                    Groups.Add(grp);
                }
            }
            else
            {
                var allGrp = new CpuCoreGroup
                {
                    Name = "CPU Cores",
                    ShortName = "All Cores",
                    Description = "Standard processor cores",
                    RecommendedColumns = Math.Clamp(cores.Count, 2, 4)
                };
                foreach (var c in cores) allGrp.Cores.Add(c);
                Groups.Add(allGrp);
            }
        }

        partial void OnDevicePolicyChanged(string value)
        {
            UpdatePolicyDescription(value);
        }

        private void UpdatePolicyDescription(string policy)
        {
            PolicyDescription = policy switch
            {
                "IrqPolicyMachineDefault" => "Assigns interrupts according to the default behavior of the OS.",
                "IrqPolicyAllCloseProcessors" => "Assigns interrupts to all processors close to the device.",
                "IrqPolicyOneCloseProcessor" => "Assigns interrupts to a single processor close to the device.",
                "IrqPolicyAllProcessorsInMachine" => "Assigns interrupts to all logical processors in the system.",
                "IrqPolicySpecifiedProcessors" => "Assigns interrupts only to the specific processors selected in the mask below.",
                "IrqPolicySpreadMessagesAcrossAllProcessors" => "Spreads multiple MSI messages across all available processors.",
                _ => ""
            };
        }

        public string SelectedThreadsSummary
        {
            get
            {
                var selected = Cores.SelectMany(c => c.Threads).Where(t => t.IsSelected).Select(t => t.ThreadId).ToList();
                if (selected.Count == 0) return "None (Clear)";
                return string.Join(", ", selected);
            }
        }

        public string CalculatedMaskHex => $"0x{GetCalculatedMask():X16}";

        [RelayCommand]
        public void SelectAll()
        {
            foreach (var core in Cores)
            {
                core.SetAllThreads(true);
            }
            OnPropertyChanged(nameof(SelectedThreadsSummary));
            OnPropertyChanged(nameof(CalculatedMaskHex));
        }

        [RelayCommand]
        public void ClearAll()
        {
            foreach (var core in Cores)
            {
                core.SetAllThreads(false);
            }
            OnPropertyChanged(nameof(SelectedThreadsSummary));
            OnPropertyChanged(nameof(CalculatedMaskHex));
        }

        [RelayCommand]
        public void SelectPCores()
        {
            foreach (var core in Cores)
            {
                core.SetAllThreads(core.IsPCore);
            }
            OnPropertyChanged(nameof(SelectedThreadsSummary));
            OnPropertyChanged(nameof(CalculatedMaskHex));
        }

        [RelayCommand]
        public void SelectECores()
        {
            foreach (var core in Cores)
            {
                core.SetAllThreads(core.IsECore);
            }
            OnPropertyChanged(nameof(SelectedThreadsSummary));
            OnPropertyChanged(nameof(CalculatedMaskHex));
        }

        public void ToggleCore(CpuCoreInfo core)
        {
            core.ToggleAllThreads();
            OnPropertyChanged(nameof(SelectedThreadsSummary));
            OnPropertyChanged(nameof(CalculatedMaskHex));
        }

        public ulong GetCalculatedMask()
        {
            ulong mask = 0;
            foreach (var core in Cores)
            {
                foreach (var thread in core.Threads)
                {
                    if (thread.IsSelected)
                    {
                        mask |= (1UL << thread.ThreadId);
                    }
                }
            }
            return mask;
        }
    }
}

