using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace KalOS.ViewModels
{
    public partial class CpuThreadInfo : ObservableObject
    {
        public int ThreadId { get; set; }
        public string DisplayName => $"Thread {ThreadId}";
        [ObservableProperty] private bool _isSelected;
    }

    public class CpuCoreInfo
    {
        public int CoreId { get; set; }
        public string DisplayName => $"Core {CoreId}";
        public ulong LogicalProcessorMask { get; set; }
        public ulong FullCoreMask { get; set; }
        public int EfficiencyClass { get; set; }
        public int L3CacheId { get; set; }
        public ushort ProcessorGroup { get; set; }
        public ObservableCollection<CpuThreadInfo> Threads { get; set; } = new();
    }

    public partial class PciDeviceItem : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;

        [ObservableProperty]
        private bool _msiSupported;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MsiEnabledText))]
        private bool _msiEnabled;

        public string MsiEnabledText => MsiEnabled ? "Enabled" : "Disabled";

        [ObservableProperty]
        private string _msiLimit = "Auto";

        [ObservableProperty]
        private string _maxMsiLimit = "1";

        [ObservableProperty]
        private string _devicePolicy = "IrqPolicyMachineDefault";

        [ObservableProperty]
        private string _devicePriority = "Undefined";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CoresAssignedDisplay))]
        private string _specifiedProc = string.Empty;

        public string CoresAssignedDisplay
        {
            get
            {
                if (string.IsNullOrEmpty(SpecifiedProc)) return "—";
                int count = SpecifiedProc
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Length;
                return count switch
                {
                    0 => "—",
                    1 => "1 logical processor",
                    _ => $"{count} logical processors"
                };
            }
        }

        public bool IsSupported => MsiSupported;
    }

    public class PciDeviceGroup : ObservableCollection<PciDeviceItem>
    {
        public string Key { get; }
        public PciDeviceGroup(string key, IEnumerable<PciDeviceItem> items) : base(items)
        {
            Key = key;
        }

        public string DisplayHeader => Key;
    }
}
