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
        public int NumaNodeId { get; set; } = 0;
        public ushort ProcessorGroup { get; set; }
        public ObservableCollection<CpuThreadInfo> Threads { get; set; } = new();

        /// <summary>
        /// "P" (performance core) or "E" (efficiency core). Computed by the VM
        /// after topology detection: on hybrid Intel CPUs EfficiencyClass &gt; 0 is a
        /// P-core and 0 is an E-core; on homogeneous AMD/older Intel CPUs every
        /// core reports EfficiencyClass 0 and is treated as a performance core.
        /// </summary>
        public string CoreKind { get; set; } = "P";

        /// <summary>Lowest logical processor index in this physical core's thread set.</summary>
        public int FirstLogicalProcessor =>
            FullCoreMask == 0 ? 0 : (int)System.Numerics.BitOperations.TrailingZeroCount(FullCoreMask);

        /// <summary>True when this is a performance core (CoreKind != "E").</summary>
        public bool IsPCore => CoreKind != "E";

        /// <summary>True when this is an efficiency core (CoreKind == "E").</summary>
        public bool IsECore => CoreKind == "E";
    }

    /// <summary>
    /// A named cluster of physical cores shown in the topology summary —
    /// "P-Cores · CCD 0", "E-Cores", or simply "Performance Cores" on a
    /// homogeneous CPU with a single L3 domain.
    /// </summary>
    public sealed class CpuCoreGroup
    {
        public string Kind { get; init; } = "P";          // "P" or "E"
        public int CcdId { get; init; } = 0;              // L3 cache domain index, or 0 when single-CCD
        public bool HasMultipleCcds { get; init; }        // true when the CPU exposes more than one L3 domain
        public IReadOnlyList<CpuCoreInfo> Cores { get; init; } = Array.Empty<CpuCoreInfo>();

        public int ThreadCount => Cores.Sum(c => c.Threads.Count);

        public string DisplayName => Kind switch
        {
            "E" => HasMultipleCcds ? $"E-Cores · CCD {CcdId}" : "E-Cores",
            _   => HasMultipleCcds ? $"P-Cores · CCD {CcdId}" : "P-Cores",
        };

        /// <summary>Short chip label, e.g. "P ×8" or "E ×4".</summary>
        public string ChipLabel => $"{Kind} ×{Cores.Count}";
    }

    /// <summary>
    /// Immutable snapshot of the CPU topology shown on the Per-CPU Scheduling
    /// page: CPU model, physical core / logical thread counts, hybrid P/E split,
    /// CCD (L3 domain) count, and whether SMT is active.
    /// </summary>
    public sealed class CpuTopologySummary
    {
        public string CpuName { get; init; } = "Unknown CPU";
        public int PhysicalCores { get; init; }
        public int LogicalProcessors { get; init; }
        public int PCoreCount { get; init; }
        public int ECoreCount { get; init; }
        public int CcdCount { get; init; }
        public bool IsHybrid { get; init; }
        public bool SmtEnabled { get; init; }

        /// <summary>True when at least one E-core is present (Intel 12th gen+ hybrid).</summary>
        public bool HasEcores => ECoreCount > 0;

        public IReadOnlyList<CpuCoreGroup> Groups { get; init; } = Array.Empty<CpuCoreGroup>();

        /// <summary>One-line card caption, e.g. "16 cores · 32 threads · SMT on · 2 CCDs".</summary>
        public string Describe()
        {
            var parts = new List<string>
            {
                $"{PhysicalCores} cores",
                $"{LogicalProcessors} threads",
                SmtEnabled ? "SMT on" : "SMT off",
            };
            if (IsHybrid) parts.Add($"{PCoreCount} P · {ECoreCount} E");
            if (CcdCount > 1) parts.Add($"{CcdCount} CCDs");
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// Builds the summary + named core groups from the raw topology. Cores are
        /// classified P/E from EfficiencyClass (hybrid) or all-P (homogeneous),
        /// then clustered per L3 cache domain (CCD) — the AMD X3D split case.
        /// </summary>
        public static CpuTopologySummary Build(string cpuName, IReadOnlyList<CpuCoreInfo> cores)
        {
            bool hasP = cores.Any(c => c.EfficiencyClass > 0);
            foreach (var c in cores)
            {
                c.CoreKind = hasP && c.EfficiencyClass == 0 ? "E" : "P";
            }

            var ccdIds = cores.Select(c => c.L3CacheId).Distinct().OrderBy(id => id).ToList();
            bool multiCcd = ccdIds.Count > 1;

            var groups = new List<CpuCoreGroup>();
            foreach (var kind in new[] { "P", "E" })
            {
                var kindCores = cores.Where(c => c.CoreKind == kind).ToList();
                if (kindCores.Count == 0) continue;

                foreach (var ccd in kindCores.Select(c => c.L3CacheId).Distinct().OrderBy(id => id))
                {
                    groups.Add(new CpuCoreGroup
                    {
                        Kind = kind,
                        CcdId = ccd,
                        HasMultipleCcds = multiCcd,
                        Cores = kindCores.Where(c => c.L3CacheId == ccd)
                                         .OrderBy(c => c.FirstLogicalProcessor)
                                         .ToList(),
                    });
                }
            }

            return new CpuTopologySummary
            {
                CpuName = string.IsNullOrWhiteSpace(cpuName) ? "Unknown CPU" : cpuName,
                PhysicalCores = cores.Count,
                LogicalProcessors = cores.Sum(c => c.Threads.Count),
                PCoreCount = cores.Count(c => c.CoreKind == "P"),
                ECoreCount = cores.Count(c => c.CoreKind == "E"),
                CcdCount = ccdIds.Count,
                IsHybrid = hasP && cores.Any(c => c.EfficiencyClass == 0),
                SmtEnabled = cores.Any(c => c.Threads.Count > 1),
                Groups = groups,
            };
        }
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

        /// <summary>Compact badge label for the table's Limit column.</summary>
        public string MsiLimitShort => MsiLimit == "Auto" ? "Auto" : $"×{MsiLimit}";

        [ObservableProperty]
        private string _maxMsiLimit = "1";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DevicePolicyShort))]
        private string _devicePolicy = "IrqPolicyMachineDefault";

        /// <summary>Compact display label for the registry DevicePolicy value.</summary>
        public string DevicePolicyShort => DevicePolicy switch
        {
            "IrqPolicyMachineDefault" => "Machine default",
            "IrqPolicyAllCloseProcessors" => "All close",
            "IrqPolicyOneCloseProcessor" => "One close",
            "IrqPolicyAllProcessorsInMachine" => "All processors",
            "IrqPolicySpecifiedProcessors" => "Specified proc",
            "IrqPolicySpreadMessagesAcrossAllProcessors" => "Spread",
            _ => DevicePolicy,
        };

        [ObservableProperty]
        private string _devicePriority = "Undefined";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CoresAssignedDisplay), nameof(AssignmentSubtitle), nameof(AssignedCoresShort))]
        private string _specifiedProc = string.Empty;

        /// <summary>Short badge label for the table's Assigned column ("—" or "N cores").</summary>
        public string AssignedCoresShort
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
                    1 => "1 core",
                    _ => $"{count} cores",
                };
            }
        }

        /// <summary>Subtitle under the device name: the device policy, plus the threads it's pinned to.</summary>
        public string AssignmentSubtitle =>
            string.IsNullOrEmpty(SpecifiedProc) ? DevicePolicyShort : $"{DevicePolicyShort} · Threads: {SpecifiedProc}";

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
