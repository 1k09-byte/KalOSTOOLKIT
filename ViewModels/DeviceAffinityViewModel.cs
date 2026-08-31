using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Linq;
using System.Collections.Generic;

namespace KalOS.ViewModels
{
    public partial class DeviceAffinityViewModel : ObservableObject
    {
        public PciDeviceItem Device { get; }
        
        public string DeviceName => Device.Name;
        public string DeviceId => Device.DeviceId;
        
        public ObservableCollection<CpuCoreInfo> Cores { get; }

        /// <summary>Total threads in the mask (across all cores).</summary>
        public int TotalThreadCount => Cores.Sum(c => c.Threads.Count);

        /// <summary>How many threads are currently selected in the mask.</summary>
        public int SelectedThreadCount => Cores.Sum(c => c.Threads.Count(t => t.IsSelected));

        /// <summary>"N of M selected" line shown in the mask header.</summary>
        public string SelectedSummary => $"{SelectedThreadCount} of {TotalThreadCount} selected";

        [ObservableProperty] private bool _msiEnabled;
        [ObservableProperty] private int _msiLimit;
        [ObservableProperty] private string _devicePriority = "Undefined";
        [ObservableProperty] private string _devicePolicy = "IrqPolicyMachineDefault";
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
        // No longer using ComboBox Limits, using an integer Max limit of 8 for the NumberBox

        public DeviceAffinityViewModel(PciDeviceItem device, IEnumerable<CpuCoreInfo> cores)
        {
            Device = device;
            
            var procStrings = (device.SpecifiedProc ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                                                          .Select(s => s.Trim())
                                                          .ToList();

            var dialogCores = new List<CpuCoreInfo>();
            foreach(var core in cores) {
                var c = new CpuCoreInfo { CoreId = core.CoreId };
                foreach(var thread in core.Threads) {
                    bool selected = procStrings.Contains(thread.ThreadId.ToString());
                    c.Threads.Add(new CpuThreadInfo { ThreadId = thread.ThreadId, IsSelected = selected });
                }
                dialogCores.Add(c);
            }
            Cores = new ObservableCollection<CpuCoreInfo>(dialogCores);
            
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

            // Keep the "N of M selected" summary live as checkboxes change.
            foreach (var core in Cores)
            {
                foreach (var thread in core.Threads)
                {
                    thread.PropertyChanged += (_, _) =>
                    {
                        OnPropertyChanged(nameof(SelectedThreadCount));
                        OnPropertyChanged(nameof(SelectedSummary));
                    };
                }
            }
        }

        /// <summary>Selects or clears every thread in the mask at once.</summary>
        public void SetAllThreads(bool selected)
        {
            foreach (var core in Cores)
            {
                foreach (var thread in core.Threads)
                {
                    thread.IsSelected = selected;
                }
            }
            OnPropertyChanged(nameof(SelectedThreadCount));
            OnPropertyChanged(nameof(SelectedSummary));
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
                "IrqPolicySpecifiedProcessors" => "Assigns interrupts only to the specific processors selected in the mask.",
                "IrqPolicySpreadMessagesAcrossAllProcessors" => "Spreads multiple MSI messages across all available processors.",
                _ => ""
            };
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
