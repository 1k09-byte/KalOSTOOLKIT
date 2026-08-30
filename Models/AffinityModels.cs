using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace KalOS.ViewModels
{
    public partial class CpuThreadInfo : ObservableObject
    {
        public int ThreadId { get; set; }
        public ulong BitMask => 1UL << ThreadId;
        public string DisplayName => $"Thread {ThreadId}";
        [ObservableProperty] private bool _isSelected;
    }

    public partial class CpuCoreInfo : ObservableObject
    {
        public int CoreId { get; set; }
        public string DisplayName => string.IsNullOrEmpty(CoreTypeLabel) ? $"Core {CoreId}" : $"Core {CoreId} ({CoreTypeLabel})";
        public string CoreTypeLabel { get; set; } = string.Empty;
        public bool IsPCore { get; set; } = true;
        public bool IsECore { get; set; } = false;
        public ulong LogicalProcessorMask { get; set; }
        public ulong FullCoreMask { get; set; }
        public int EfficiencyClass { get; set; }
        public int L3CacheId { get; set; }
        public int NumaNodeId { get; set; } = 0;
        public ushort ProcessorGroup { get; set; }
        public ObservableCollection<CpuThreadInfo> Threads { get; set; } = new();

        public bool AreAllThreadsSelected => Threads.Count > 0 && Threads.All(t => t.IsSelected);
        public bool IsAnyThreadSelected => Threads.Any(t => t.IsSelected);

        public void SetAllThreads(bool selected)
        {
            foreach (var t in Threads)
            {
                t.IsSelected = selected;
            }
            OnPropertyChanged(nameof(AreAllThreadsSelected));
            OnPropertyChanged(nameof(IsAnyThreadSelected));
        }

        public void ToggleAllThreads()
        {
            bool target = !AreAllThreadsSelected;
            SetAllThreads(target);
        }
    }

    public partial class CpuCoreGroup : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ObservableCollection<CpuCoreInfo> Cores { get; set; } = new();
        public int RecommendedColumns { get; set; } = 4;
        public int TotalThreads => Cores.Sum(c => c.Threads.Count);
    }

    public class CpuTopologySummary
    {
        public string CpuName { get; set; } = "CPU";
        public int PhysicalCoreCount { get; set; }
        public int LogicalProcessorCount { get; set; }
        public int PCoreCount { get; set; }
        public int ECoreCount { get; set; }
        public int CcdCount { get; set; }
        public bool HasHybridArchitecture => ECoreCount > 0 && PCoreCount > 0;
        public bool HasMultiCcd => CcdCount > 1;
        public bool IsSmtEnabled { get; set; }

        public string ArchitectureSummary
        {
            get
            {
                if (HasHybridArchitecture)
                {
                    return $"{LogicalProcessorCount} Logical Processors • {PCoreCount} P-Cores + {ECoreCount} E-Cores {(IsSmtEnabled ? "• SMT/HT Active" : "")}";
                }
                if (HasMultiCcd)
                {
                    return $"{LogicalProcessorCount} Logical Processors • {PhysicalCoreCount} Cores ({CcdCount} CCDs) {(IsSmtEnabled ? "• SMT Active" : "")}";
                }
                return $"{LogicalProcessorCount} Logical Processors • {PhysicalCoreCount} Physical Cores {(IsSmtEnabled ? "• SMT/HT Active" : "")}";
            }
        }

        public string SummaryString => ArchitectureSummary;
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
        [NotifyPropertyChangedFor(nameof(DevicePolicyDisplay))]
        private string _devicePolicy = "IrqPolicyMachineDefault";

        [ObservableProperty]
        private string _devicePriority = "Undefined";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CoresAssignedDisplay), nameof(AssignedCoresDetail))]
        private string _specifiedProc = string.Empty;

        public string DevicePolicyDisplay => DevicePolicy switch
        {
            "IrqPolicySpecifiedProcessors" => "Specified Processors",
            "IrqPolicyMachineDefault" => "Machine Default",
            "IrqPolicyAllCloseProcessors" => "All Close Processors",
            "IrqPolicyOneCloseProcessor" => "One Close Processor",
            "IrqPolicyAllProcessorsInMachine" => "All Processors",
            "IrqPolicySpreadMessagesAcrossAllProcessors" => "Spread Across Processors",
            _ => string.IsNullOrEmpty(DevicePolicy) ? "Machine Default" : DevicePolicy
        };

        public string CoresAssignedDisplay
        {
            get
            {
                if (DevicePolicy != "IrqPolicySpecifiedProcessors" || string.IsNullOrWhiteSpace(SpecifiedProc))
                {
                    return "Default";
                }
                int count = SpecifiedProc
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Length;
                return count switch
                {
                    0 => "Default",
                    1 => "1 thread",
                    _ => $"{count} threads"
                };
            }
        }

        public string AssignedCoresDetail
        {
            get
            {
                if (DevicePolicy != "IrqPolicySpecifiedProcessors" || string.IsNullOrWhiteSpace(SpecifiedProc))
                {
                    return "OS Managed (Machine Default)";
                }
                return $"Threads: {SpecifiedProc}";
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

