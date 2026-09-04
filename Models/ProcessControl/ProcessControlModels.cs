using System.Collections.Generic;
using System.Linq;

namespace KalOS.Models.ProcessControl;

/// <summary>
/// Single lookup table mapping internal feature identifiers to display names.
/// Change a name here once and every page/action/log uses the new label —
/// display strings are never hardcoded in feature code.
/// </summary>
public static class FeatureNames
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["AutoBalance"] = "AutoBalance",
        ["StickyRules"] = "Sticky Rules",
        ["BoostMode"] = "Boost Mode",
        ["CoreCap"] = "Core Cap",
        ["HardThrottle"] = "Hard Throttle",
        ["SpreadBalancer"] = "Spread Balancer",
        ["MemoryPriority"] = "Memory Priority",
        ["Blocklist"] = "Blocklist",
        ["AutoRevive"] = "AutoRevive",
        ["PowerAutomation"] = "Power Automation",
        ["ForegroundBoost"] = "Foreground Boost",
        ["CoreIsolation"] = "Core Isolation",
        ["InstanceLimit"] = "Instance Limit",
        ["PreventSleep"] = "Prevent Sleep",
        ["KeepRunning"] = "Keep Running",
        ["ProcessorGroups"] = "Processor Groups",
    };

    /// <summary>Resolves an internal id to its display name (falls back to the id itself).</summary>
    public static string Display(string id) => Names.TryGetValue(id, out var name) ? name : id;

    public static IReadOnlyList<string> All => Names.Keys.OrderBy(k => k).ToList();
}

/// <summary>CPU priority class levels as exposed by the rule editor (0=Idle … 5=Realtime).</summary>
public enum CpuPriorityLevel
{
    Idle = 0,
    BelowNormal = 1,
    Normal = 2,
    AboveNormal = 3,
    High = 4,
    Realtime = 5,
}

/// <summary>I/O priority levels (maps to PROCESS_IO_PRIORITY_CLASS_INFORMATION 0–3).</summary>
public enum IoPriorityLevel
{
    VeryLow = 0,
    Low = 1,
    Normal = 2,
    High = 3,
}

/// <summary>Memory priority 1–5 (5 is Windows' default for normal processes).</summary>
public enum MemoryPriorityLevel
{
    Lowest = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    Highest = 5,
}

/// <summary>How a rule decides which processes it matches.</summary>
public enum RuleMatchMode
{
    /// <summary>Match by image name (e.g. "chrome.exe"). All instances match.</summary>
    Name = 0,

    /// <summary>Match by full executable path (distinguishes same-named processes).</summary>
    Path = 1,

    /// <summary>Match by command line substring.</summary>
    CommandLine = 2,
}

/// <summary>
/// One persistent process-control rule; enabled rules apply always.
/// </summary>
public sealed class ProcessRule
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

    /// <summary>Display name of the rule (defaults to the process name when empty).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Process image name, path, or command-line fragment — see <see cref="MatchMode"/>.</summary>
    public string ProcessName { get; set; } = string.Empty;

    public RuleMatchMode MatchMode { get; set; } = RuleMatchMode.Name;

    /// <summary>1-based instance index this rule targets (null = every instance). 2 = "2nd instance of X".</summary>
    public int? InstanceIndex { get; set; }

    /// <summary>Optional CPU priority (null = don't touch).</summary>
    public CpuPriorityLevel? CpuPriority { get; set; }

    /// <summary>Optional I/O priority (null = don't touch).</summary>
    public IoPriorityLevel? IoPriority { get; set; }

    /// <summary>Optional memory priority (null = don't touch).</summary>
    public MemoryPriorityLevel? MemoryPriority { get; set; }

    /// <summary>CPU set ids to pin the process to (empty = all cores). Preferred over the mask.</summary>
    public List<uint> CpuSetIds { get; set; } = new();

    /// <summary>Legacy group-0 affinity mask fallback (used when CPU Sets are unavailable).</summary>
    public ulong AffinityMask { get; set; }

    /// <summary>Dynamic core cap: re-evaluate the core budget from live CPU load.</summary>
    public bool EnableCoreCap { get; set; }

    /// <summary>Hard ceiling on simultaneously usable cores (1..N).</summary>
    public int MaxCores { get; set; }

    /// <summary>Soft CPU ceiling in percent (0 = off). Core budget shrinks above it.</summary>
    public int MaxCpuPercent { get; set; }

    /// <summary>Hard throttle: enforce MaxCpuPercent by suspending threads on a duty cycle.</summary>
    public bool HardThrottle { get; set; }

    /// <summary>Spread every running instance across distinct core groups.</summary>
    public bool SpreadInstances { get; set; }

    /// <summary>Auto-terminate matching processes the moment they launch.</summary>
    public bool Blocklist { get; set; }

    /// <summary>Auto-relaunch matching processes when they exit unexpectedly.</summary>
    public bool Revive { get; set; }

    /// <summary>Maximum simultaneous instances (new launches past the cap are terminated).</summary>
    public int? MaxInstances { get; set; }

    /// <summary>Block the system from sleeping while a matching process runs.</summary>
    public bool PreventSleep { get; set; }

    /// <summary>Prevent the process from being closed (auto-restart + UI guard with override).</summary>
    public bool KeepRunning { get; set; }

    /// <summary>When set, this rule activates the named profile while a matching process is foreground/fullscreen.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Display name shown in lists (falls back to the process match).</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? ProcessName : Name;

    /// <summary>Match target shown in the rules list.</summary>
    public string MatchTarget => MatchMode switch
    {
        RuleMatchMode.Path => $"path: {ProcessName}",
        RuleMatchMode.CommandLine => $"cmd: {ProcessName}",
        _ => ProcessName + (InstanceIndex is { } i ? $" (instance {i})" : string.Empty),
    };

    /// <summary>Human-readable description auto-built for the rules list.</summary>
    public string Summary =>
        string.Join(", ", new[]
        {
            CpuPriority is { } cp ? $"CPU: {CpuPriorityText(cp)}" : null,
            IoPriority is { } io ? $"I/O: {io}" : null,
            MemoryPriority is { } mp ? $"Mem: {MemoryPriorityText(mp)}" : null,
            CpuSetIds.Count > 0 ? $"Pinned: {CpuSetIds.Count} CPU set(s)" : (AffinityMask != 0 ? $"Pinned: {AffinityMask}" : null),
            EnableCoreCap ? $"Core cap: {MaxCores} core(s)" : null,
            MaxCpuPercent > 0 ? $"CPU ≤ {MaxCpuPercent}%" : null,
            SpreadInstances ? "Spread instances" : null,
            Blocklist ? "Blocklist" : null,
            Revive ? "AutoRevive" : null,
            MaxInstances is { } mi ? $"Max {mi} instance(s)" : null,
            PreventSleep ? "Prevent sleep" : null,
            KeepRunning ? "Keep running" : null,
        }.Where(s => s != null));

    private static string CpuPriorityText(CpuPriorityLevel p) => p switch
    {
        CpuPriorityLevel.Idle => "Idle",
        CpuPriorityLevel.BelowNormal => "Below Normal",
        CpuPriorityLevel.Normal => "Normal",
        CpuPriorityLevel.AboveNormal => "Above Normal",
        CpuPriorityLevel.High => "High",
        CpuPriorityLevel.Realtime => "Realtime",
        _ => p.ToString(),
    };

    private static string MemoryPriorityText(MemoryPriorityLevel p) => p switch
    {
        MemoryPriorityLevel.Lowest => "1 (Lowest)",
        MemoryPriorityLevel.Low => "2 (Low)",
        MemoryPriorityLevel.Medium => "3 (Medium)",
        MemoryPriorityLevel.High => "4 (High)",
        _ => "5 (Highest, default)",
    };
}

// Rule profiles (named rule sets with power-plan automation) and Focus Mode
// (fullscreen-triggered profile switching) removed by user request.

/// <summary>Engine-wide configuration (thresholds, toggles).</summary>
public sealed class EngineConfig
{
    public bool EngineEnabled { get; set; } = true;

    // AutoBalance (ProBalance equivalent)
    public bool AutoBalanceEnabled { get; set; } = true;
    public int AutoBalanceCpuPercentThreshold { get; set; } = 60;
    public int AutoBalanceSustainSeconds { get; set; } = 15;
    public int AutoBalanceRecoverSeconds { get; set; } = 30;

    // Foreground Boost (off by default)
    public bool ForegroundBoostEnabled { get; set; }

    /// <summary>Processes excluded from AutoBalance (image names).</summary>
    public List<string> AutoBalanceExclusions { get; set; } = new();

    /// <summary>Global Boost Mode state (core parking + frequency scaling disabled).</summary>
    public bool BoostModeActive { get; set; }
}

/// <summary>One entry in the human-readable action log.</summary>
public sealed class ActionLogEntry
{
    public System.DateTimeOffset Timestamp { get; set; } = System.DateTimeOffset.Now;
    public string Feature { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Process { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;

    public string Display => $"[{Timestamp:HH:mm:ss}] {Feature}: {Action} — {Process} {Detail}".TrimEnd();
}

/// <summary>Snapshot of one running process for the live process list.</summary>
public sealed class ProcessSnapshot
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public double CpuPercent { get; set; }
    public long WorkingSetBytes { get; set; }
    public string PriorityText { get; set; } = "—";
    public string AffinityText { get; set; } = "—";
    public bool Managed { get; set; }
    public string ManagedBy { get; set; } = string.Empty;
}

/// <summary>CPU topology facts used by the Core Isolation presets and monitoring view.</summary>
public sealed class CpuTopologyInfo
{
    public string CpuName { get; set; } = "Unknown Processor";
    public int LogicalCount { get; set; }
    public int PhysicalCount { get; set; }
    public bool HasHybridCores { get; set; }

    /// <summary>Distinct L3 cache groups (CCD/CCX on AMD, core clusters on Intel).</summary>
    public int L3GroupCount { get; set; }

    /// <summary>True when CCD count was estimated with the ≥16-logical heuristic rather than detected.</summary>
    public bool CcdEstimated { get; set; }

    /// <summary>Per-core info: cpu set id, group, logical index, core index, L3 index, efficiency class.</summary>
    public List<CpuSetInfo> CpuSets { get; set; } = new();
}

public sealed class CpuSetInfo
{
    public uint Id { get; set; }
    public ushort Group { get; set; }
    public byte LogicalProcessorIndex { get; set; }
    public byte CoreIndex { get; set; }
    public byte LastLevelCacheIndex { get; set; }
    public byte EfficiencyClass { get; set; }

    /// <summary>True for P-cores on hybrid CPUs (EfficiencyClass ≥ 1).</summary>
    public bool IsPerformance => EfficiencyClass >= 1;

    public bool IsEfficiency => EfficiencyClass == 0;
}

/// <summary>One Windows power plan (from powercfg /list).</summary>
public sealed record PowerPlan(string Guid, string Name, bool Active);

/// <summary>Well-known presets for one-click Core Isolation changes.</summary>
public enum CoreIsolationPreset
{
    ECoresOff,
    PCoresOff,
    Ccd0Off,
    Ccd1Off,
    SmtOff,
}