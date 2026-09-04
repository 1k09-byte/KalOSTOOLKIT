using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KalOS.Models.ProcessControl;
using KalOS.Services;

namespace KalOS.Tests.Services;

public class ProcessControlServiceTests
{
    // ── Rule matching ────────────────────────────────────────────────────

    [Fact]
    public void RuleMatches_Name_CaseInsensitive()
    {
        var rule = new ProcessRule { ProcessName = "chrome.exe", MatchMode = RuleMatchMode.Name };
        Assert.True(ProcessControlService.RuleMatches(rule, "chrome.exe", null, 1));
        Assert.True(ProcessControlService.RuleMatches(rule, "CHROME.EXE", null, 1));
        Assert.False(ProcessControlService.RuleMatches(rule, "notepad.exe", null, 1));
    }

    [Fact]
    public void RuleMatches_Path_UsesExecutablePath()
    {
        var rule = new ProcessRule { ProcessName = @"C:\Games\game.exe", MatchMode = RuleMatchMode.Path };
        var info = new ProcessControlService.ProcInfo(1, @"C:\Games\game.exe", @"C:\Games\game.exe --fullscreen", string.Empty);
        Assert.True(ProcessControlService.RuleMatches(rule, "game.exe", info, 1));

        var other = new ProcessControlService.ProcInfo(2, @"C:\Other\game.exe", @"C:\Other\game.exe", string.Empty);
        Assert.False(ProcessControlService.RuleMatches(rule, "game.exe", other, 1));
    }

    [Fact]
    public void RuleMatches_CommandLine_Substring()
    {
        var rule = new ProcessRule { ProcessName = "--render-worker", MatchMode = RuleMatchMode.CommandLine };
        var info = new ProcessControlService.ProcInfo(1, @"C:\node\node.exe", @"C:\node\node.exe --render-worker --port 8080", string.Empty);
        Assert.True(ProcessControlService.RuleMatches(rule, "node.exe", info, 1));
    }

    [Fact]
    public void RuleMatches_InstanceIndex_TargetsSpecificCopy()
    {
        var second = new ProcessRule { ProcessName = "node.exe", InstanceIndex = 2 };
        Assert.False(ProcessControlService.RuleMatches(second, "node.exe", null, 1));
        Assert.True(ProcessControlService.RuleMatches(second, "node.exe", null, 2));
        Assert.False(ProcessControlService.RuleMatches(second, "node.exe", null, 3));

        var all = new ProcessRule { ProcessName = "node.exe", InstanceIndex = null };
        Assert.True(ProcessControlService.RuleMatches(all, "node.exe", null, 1));
        Assert.True(ProcessControlService.RuleMatches(all, "node.exe", null, 5));
    }

    // ── Command-line parsing (AutoRevive relaunch) ───────────────────────

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --flag -x 2", @"--flag -x 2")]
    [InlineData(@"C:\app\app.exe --flag", @"--flag")]
    [InlineData(@"C:\app\app.exe", @"")]
    [InlineData(@"  ""C:\a b\c.exe""", @"")]
    [InlineData(@"", @"")]
    public void ParseArguments_ExtractsTail(string commandLine, string expected)
    {
        Assert.Equal(expected, ProcessControlService.ParseArguments(commandLine));
    }

    // ── Core Cap budget ──────────────────────────────────────────────────

    [Fact]
    public void ComputeCoreBudget_MaxCores_IsHardCeiling()
    {
        var rule = new ProcessRule { EnableCoreCap = true, MaxCores = 4 };
        Assert.Equal(4, ProcessControlService.ComputeCoreBudget(rule, 99, 16));
        Assert.Equal(4, ProcessControlService.ComputeCoreBudget(rule, 1, 16));
    }

    [Fact]
    public void ComputeCoreBudget_MaxCores_ClampedToBaseline()
    {
        var rule = new ProcessRule { EnableCoreCap = true, MaxCores = 32 };
        Assert.Equal(8, ProcessControlService.ComputeCoreBudget(rule, 50, 8));
    }

    [Fact]
    public void ComputeCoreBudget_Percent_ScalesWithLoad()
    {
        var rule = new ProcessRule { EnableCoreCap = true, MaxCpuPercent = 50 };
        Assert.Equal(16, ProcessControlService.ComputeCoreBudget(rule, 100, 16)); // full load → full budget
        Assert.Equal(4, ProcessControlService.ComputeCoreBudget(rule, 25, 16));   // low load → 4 cores
        Assert.Equal(1, ProcessControlService.ComputeCoreBudget(rule, 1, 16));    // never below 1
    }

    [Fact]
    public void ComputeCoreBudget_NoCap_KeepsBaseline()
    {
        var rule = new ProcessRule { EnableCoreCap = true };
        Assert.Equal(16, ProcessControlService.ComputeCoreBudget(rule, 100, 16));
    }

    // ── Core Isolation presets (synthetic CPU-set topology) ──────────────

    private static List<CpuSetInfo> HybridTopology()
    {
        // 2 P-cores (EfficiencyClass 1) with SMT, 2 E-cores (EfficiencyClass 0).
        var sets = new List<CpuSetInfo>();
        int id = 0;
        foreach (var (core, eff) in new[] { (0, (byte)1), (1, (byte)1), (2, (byte)0), (3, (byte)0) })
        {
            sets.Add(new CpuSetInfo { Id = (uint)id++, Group = 0, CoreIndex = (byte)core, LogicalProcessorIndex = 0, EfficiencyClass = eff, LastLevelCacheIndex = (byte)(core / 2) });
            sets.Add(new CpuSetInfo { Id = (uint)id++, Group = 0, CoreIndex = (byte)core, LogicalProcessorIndex = 1, EfficiencyClass = eff, LastLevelCacheIndex = (byte)(core / 2) });
        }
        return sets;
    }

    [Fact]
    public void BuildPreset_ECoresOff_KeepsOnlyPerformanceCores()
    {
        var ids = ProcessControlNative.BuildPresetCpuSetIds(HybridTopology(), CoreIsolationPreset.ECoresOff);
        Assert.NotNull(ids);
        Assert.Equal(4, ids!.Count); // 2 P-cores × 2 SMT threads
    }

    [Fact]
    public void BuildPreset_PCoresOff_KeepsOnlyEfficiencyCores()
    {
        var ids = ProcessControlNative.BuildPresetCpuSetIds(HybridTopology(), CoreIsolationPreset.PCoresOff);
        Assert.NotNull(ids);
        Assert.Equal(4, ids!.Count);
    }

    [Fact]
    public void BuildPreset_SmtOff_KeepsOneThreadPerCore()
    {
        var ids = ProcessControlNative.BuildPresetCpuSetIds(HybridTopology(), CoreIsolationPreset.SmtOff);
        Assert.NotNull(ids);
        Assert.Equal(4, ids!.Count); // 4 physical cores, lowest logical index each
        Assert.All(ids, id => Assert.True(id % 2 == 0)); // ids 0,2,4,6
    }

    [Fact]
    public void BuildPreset_Ccd0Off_KeepsOnlySecondCacheGroup()
    {
        var ids = ProcessControlNative.BuildPresetCpuSetIds(HybridTopology(), CoreIsolationPreset.Ccd0Off);
        Assert.NotNull(ids);
        Assert.Equal(4, ids!.Count); // cores 2,3 (L3 index 1)
        Assert.All(ids, id => Assert.True(id >= 4));
    }

    [Fact]
    public void BuildPreset_ECoresOff_ReturnsNull_WhenNoEfficiencyCores()
    {
        var onlyP = HybridTopology().Where(s => s.IsPerformance).ToList();
        Assert.Null(ProcessControlNative.BuildPresetCpuSetIds(onlyP, CoreIsolationPreset.ECoresOff));
    }

    [Fact]
    public void BuildPreset_Ccd0Off_ReturnsNull_WhenSingleCcd()
    {
        var single = new List<CpuSetInfo>
        {
            new() { Id = 0, Group = 0, CoreIndex = 0, LogicalProcessorIndex = 0, EfficiencyClass = 1, LastLevelCacheIndex = 0 },
            new() { Id = 1, Group = 0, CoreIndex = 1, LogicalProcessorIndex = 0, EfficiencyClass = 1, LastLevelCacheIndex = 0 },
        };
        Assert.Null(ProcessControlNative.BuildPresetCpuSetIds(single, CoreIsolationPreset.Ccd0Off));
    }

    // ── Feature names lookup ─────────────────────────────────────────────

    [Fact]
    public void FeatureNames_ResolvesDisplayNames()
    {
        Assert.Equal("AutoBalance", FeatureNames.Display("AutoBalance"));
        Assert.Equal("Sticky Rules", FeatureNames.Display("StickyRules"));
        Assert.Equal("Core Isolation", FeatureNames.Display("CoreIsolation"));
        Assert.Equal("unknown-id", FeatureNames.Display("unknown-id")); // falls back to the id
    }

    // ── JSON round-trip (rules) ─────────────────────────────────────────

    [Fact]
    public void RuleBundle_SerializesAndDeserializes()
    {
        var bundle = new
        {
            Rules = new List<ProcessRule>
            {
                new()
                {
                    Name = "Game", ProcessName = "game.exe",
                    CpuPriority = CpuPriorityLevel.High,
                    IoPriority = IoPriorityLevel.High,
                    MemoryPriority = MemoryPriorityLevel.Highest,
                    CpuSetIds = new List<uint> { 1, 2, 3 },
                    EnableCoreCap = true, MaxCores = 4, MaxCpuPercent = 75,
                    SpreadInstances = true, PreventSleep = true, KeepRunning = true,
                },
            },
        };

        string json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var rules = JsonSerializer.Deserialize<List<ProcessRule>>(parsed!["Rules"].GetRawText());
        Assert.Single(rules!);
        Assert.Equal("game.exe", rules[0].ProcessName);
        Assert.Equal(CpuPriorityLevel.High, rules[0].CpuPriority);
        Assert.Equal(new List<uint> { 1, 2, 3 }, rules[0].CpuSetIds);
        Assert.True(rules[0].PreventSleep);
        Assert.True(rules[0].KeepRunning);
    }

    [Fact]
    public void ProcessRule_Summary_ListsConfiguredActions()
    {
        var rule = new ProcessRule
        {
            Name = "Test", ProcessName = "test.exe",
            CpuPriority = CpuPriorityLevel.High,
            IoPriority = IoPriorityLevel.High,
            MemoryPriority = MemoryPriorityLevel.Low,
            EnableCoreCap = true, MaxCores = 4,
            Blocklist = true,
        };
        string summary = rule.Summary;
        Assert.Contains("CPU: High", summary);
        Assert.Contains("I/O: High", summary);
        Assert.Contains("Mem: 2 (Low)", summary);
        Assert.Contains("Core cap: 4 core(s)", summary);
        Assert.Contains("Blocklist", summary);
    }

    // ── EngineConfig persistence (behaviors must survive restarts) ─────

    [Fact]
    public void EngineConfig_RoundTripsThroughJson()
    {
        var config = new EngineConfig
        {
            EngineEnabled = true,
            AutoBalanceEnabled = false,
            AutoBalanceCpuPercentThreshold = 75,
            AutoBalanceSustainSeconds = 20,
            AutoBalanceRecoverSeconds = 45,
            ForegroundBoostEnabled = true,
            AutoBalanceExclusions = new List<string> { "dwm.exe", "game.exe" },
            BoostModeActive = true,
        };

        string json = JsonSerializer.Serialize(config);
        var parsed = JsonSerializer.Deserialize<EngineConfig>(json);

        Assert.NotNull(parsed);
        Assert.False(parsed!.AutoBalanceEnabled);
        Assert.Equal(75, parsed.AutoBalanceCpuPercentThreshold);
        Assert.Equal(20, parsed.AutoBalanceSustainSeconds);
        Assert.Equal(45, parsed.AutoBalanceRecoverSeconds);
        Assert.True(parsed.ForegroundBoostEnabled);
        Assert.Equal(new List<string> { "dwm.exe", "game.exe" }, parsed.AutoBalanceExclusions);
        Assert.True(parsed.BoostModeActive);
    }

    [Fact]
    public void EngineConfig_DeserializesEmptyObject_WithDefaults()
    {
        var parsed = JsonSerializer.Deserialize<EngineConfig>("{}");
        Assert.NotNull(parsed);
        Assert.True(parsed!.EngineEnabled);
        Assert.True(parsed.AutoBalanceEnabled);
        Assert.Equal(60, parsed.AutoBalanceCpuPercentThreshold);
    }

    [Fact]
    public void EngineConfigPath_LivesInProcessControlFolder()
    {
        Assert.Contains("process-control", ProcessControlService.ConfigPath);
        Assert.EndsWith("engine.json", ProcessControlService.ConfigPath);
    }

    // ── Rule bundle survives a full serialize→deserialize cycle (the
    // "rules disappear after restart" regression) ─────────────────────

    [Fact]
    public void RuleBundle_RoundTripsAllBehaviorFlags()
    {
        var original = new ProcessControlService.RuleBundle
        {
            Rules = new List<ProcessRule>
            {
                new()
                {
                    Name = "Behaviors", ProcessName = "game.exe",
                    PreventSleep = true, KeepRunning = true, Revive = true,
                    Blocklist = false, MaxInstances = 2,
                    CpuPriority = CpuPriorityLevel.AboveNormal,
                    InstanceIndex = null,
                },
            },
        };

        string json = JsonSerializer.Serialize(original);
        var parsed = JsonSerializer.Deserialize<ProcessControlService.RuleBundle>(json);

        var rule = parsed!.Rules.Single();
        Assert.True(rule.PreventSleep);
        Assert.True(rule.KeepRunning);
        Assert.True(rule.Revive);
        Assert.Equal(2, rule.MaxInstances);
        Assert.Equal(CpuPriorityLevel.AboveNormal, rule.CpuPriority);
        Assert.Null(rule.InstanceIndex);
    }

    // ── Core-cap budget math with percent ceilings (the CPU % lock) ─────

    [Fact]
    public void ComputeCoreBudget_HardMaxCores_AlwaysCaps()
    {
        var rule = new ProcessRule { EnableCoreCap = true, MaxCores = 2 };
        Assert.Equal(2, ProcessControlService.ComputeCoreBudget(rule, 100, 16));
        Assert.Equal(2, ProcessControlService.ComputeCoreBudget(rule, 0, 16));
    }

    [Fact]
    public void ComputeCoreBudget_ScalesWithLoad_WhenPercentCeilingSet()
    {
        var rule = new ProcessRule { EnableCoreCap = true, MaxCpuPercent = 50 };
        Assert.Equal(16, ProcessControlService.ComputeCoreBudget(rule, 100, 16)); // ceil(1.0*16)=16
        Assert.Equal(8, ProcessControlService.ComputeCoreBudget(rule, 50, 16));
        Assert.Equal(1, ProcessControlService.ComputeCoreBudget(rule, 1, 16));
    }

    [Fact]
    public void ComputeCoreBudget_NeverExceedsHardCapOrBaseline()
    {
        var rule = new ProcessRule { EnableCoreCap = true, MaxCores = 4, MaxCpuPercent = 100 };
        Assert.Equal(4, ProcessControlService.ComputeCoreBudget(rule, 100, 16)); // hard cap wins over the percent math
        Assert.Equal(3, ProcessControlService.ComputeCoreBudget(rule, 100, 3)); // baseline clamps
    }

    [Fact]
    public void ComputeCoreBudget_NoLimits_ReturnsBaseline()
    {
        var rule = new ProcessRule { EnableCoreCap = true };
        Assert.Equal(16, ProcessControlService.ComputeCoreBudget(rule, 100, 16));
    }

    // ── Hard-throttle primitive: SuspendProcess must actually stall CPU ──

    [Fact]
    public void SuspendProcess_ActuallyStopsCpuProgress()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 30 127.0.0.1 >nul",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var victim = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(victim);
        try
        {
            System.Threading.Thread.Sleep(500); // let it spin a little
            Assert.True(KalOS.Services.ProcessControlNative.SuspendProcess(victim!.Id), "SuspendProcess returned false");

            var before = victim.TotalProcessorTime;
            System.Threading.Thread.Sleep(1200);
            victim.Refresh();
            var drift = victim.TotalProcessorTime - before;

            KalOS.Services.ProcessControlNative.ResumeProcess(victim.Id);

            // A suspended process accumulates ~0 CPU; an unsuspended busy one
            // accumulates hundreds of ms per second.
            Assert.True(drift.TotalMilliseconds < 100,
                $"suspended process still burned {drift.TotalMilliseconds:0} ms CPU over 1.2 s — SuspendProcess is a no-op");
        }
        finally
        {
            try { victim!.Kill(); } catch { }
        }
    }
}