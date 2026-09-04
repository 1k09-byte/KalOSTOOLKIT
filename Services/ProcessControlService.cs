using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Helpers;
using KalOS.Models.ProcessControl;
using Microsoft.Win32;

namespace KalOS.Services;

/// <summary>
/// The Process Control engine: persistent per-process scheduling rules
/// (sticky rules), live enforcement, and the Process Lasso–class feature set.
/// A single heartbeat thread drives sampling, AutoBalance, Core Cap, Focus
/// Mode, Prevent Sleep, and the process watchers; rules keep re-applying to
/// every new instance while the app runs, and the same engine powers the
/// hidden --rules background mode launched at login.
/// </summary>
public sealed class ProcessControlService : IDisposable
{
    private readonly LoggingService _log;

    public event Action? ProcessesChanged;
    public event Action? RulesChanged;
    public event Action? EngineStateChanged;
    public event Action<ActionLogEntry>? ActionLogged;

    private readonly object _sync = new();

    private readonly List<ProcessRule> _rules = new();
    private EngineConfig _config = new();

    /// <summary>pid → managed state (rule id + applied scheduling) for restore.</summary>
    private readonly Dictionary<int, ManagedState> _managed = new();

    /// <summary>pid → last CPU sample (for % computation and the live list).</summary>
    private readonly Dictionary<int, CpuSample> _cpuSamples = new();

    /// <summary>pid → cached WMI path/command line (only fetched when rules need it).</summary>
    private readonly Dictionary<int, ProcInfo> _procInfo = new();

    /// <summary>pid → AutoBalance state.</summary>
    private readonly Dictionary<int, AbState> _abState = new();

    /// <summary>pid → hard-throttle suspension in progress.</summary>
    private readonly Dictionary<int, bool> _throttleSuspended = new();

    private readonly Dictionary<int, long> _lastWorkingSet = new();
    private readonly Dictionary<int, string> _processNames = new();
    private readonly HashSet<int> _knownPids = new();

    private readonly System.Collections.Concurrent.ConcurrentQueue<ProcEvent> _procEvents = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<ActionLogEntry> _pendingActions = new();
    private readonly List<ActionLogEntry> _actions = new();
    private const int MaxActions = 500;

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Timer? _heartbeat;
    private int _tick;
    private int _logicalCount = -1;
    private bool _ownsEngine;

    private int _foregroundBoostedPid;
    private string? _keepRunningOverrideName;
    private DateTimeOffset _keepRunningOverrideUntil;
    private Dictionary<string, string>? _savedBoostValues;
    private bool _sleepRequired;
    private bool _disposed;

    // ── Paths ────────────────────────────────────────────────────────────

    public static string DataFolder => Path.Combine(UpdateService.AppDataFolder, "process-control");
    public static string RulesPath => Path.Combine(DataFolder, "rules.json");
    public static string ActionsPath => Path.Combine(DataFolder, "actions.json");
    public static string BoostSavePath => Path.Combine(DataFolder, "boost-saved.json");

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RulesRunKeyValueName = "KalOSProcessRules";

    // ── Engine ownership (UI app vs hidden --rules session) ─────────────
    // Exactly one process may enforce rules at a time. The UI takes the
    // engine mutex on launch (nudging the background session to exit) and
    // hands it back by spawning a fresh --rules process on close.
    public const string EngineMutexName = @"Local\KalOS.ProcessControlEngine";
    public const string EngineStopEventName = @"Local\KalOS.ProcessControlEngineStop";

    private static Mutex? _engineMutex;

    /// <summary>UI path: waits up to <paramref name="wait"/> for engine ownership, nudging any running background session to exit.</summary>
    public static bool TryBeginEngineSession(TimeSpan wait)
    {
        EventWaitHandle? stop = null;
        try { stop = new EventWaitHandle(false, EventResetMode.AutoReset, EngineStopEventName); } catch { }
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                _engineMutex ??= new Mutex(false, EngineMutexName);
                if (_engineMutex.WaitOne(TimeSpan.Zero)) return true;
            }
            catch (AbandonedMutexException)
            {
                return true; // previous owner died — the OS granted us ownership
            }
            catch { return false; }
            try { stop?.Set(); } catch { } // ask the background session to exit
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(200);
        }
    }

    /// <summary>Background path: owns the engine only if no other session holds it.</summary>
    public static bool TryBeginEngineSessionBackground()
    {
        try
        {
            _engineMutex = new Mutex(true, EngineMutexName, out bool createdNew);
            return createdNew;
        }
        catch { return false; }
    }

    /// <summary>Releases engine ownership (call before spawning the replacement --rules session).</summary>
    public static void EndEngineSession()
    {
        try { _engineMutex?.ReleaseMutex(); } catch { }
        try { _engineMutex?.Dispose(); } catch { }
        _engineMutex = null;
    }

    /// <summary>Blocks until the UI asks the background session to stop, then runs <paramref name="onExit"/>.</summary>
    public static void WaitForEngineStopRequest(Action onExit)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EngineStopEventName);
                evt.WaitOne();
            }
            catch { return; }
            onExit();
        });
    }

    public ProcessControlService(LoggingService log)
    {
        _log = log;
        LoadAll();
    }

    // ── Public state accessors ───────────────────────────────────────────

    public IReadOnlyList<ProcessRule> Rules { get { lock (_sync) return _rules.ToList(); } }
    public EngineConfig Config { get { lock (_sync) return CloneConfig(); } }
    public bool BoostModeActive { get { lock (_sync) return _config.BoostModeActive; } }
    public IReadOnlyList<ActionLogEntry> Actions { get { lock (_sync) return _actions.ToList(); } }

    // ── Lifecycle ────────────────────────────────────────────────────────

    public void Start(bool backgroundSession = false)
    {
        lock (_sync)
        {
            if (_heartbeat != null) return;
        }

        // Engine ownership: the UI waits (and nudges the background session);
        // the background session only proceeds if it can own the engine.
        _ownsEngine = backgroundSession
            ? TryBeginEngineSessionBackground()
            : TryBeginEngineSession(TimeSpan.FromSeconds(3));
        if (!_ownsEngine && backgroundSession) return;

        try
        {
            _startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _startWatcher.EventArrived += (_, e) => EnqueueProcEvent(true, e);
            _startWatcher.Start();
        }
        catch (Exception ex)
        {
            _log.Warn($"ProcessControl: start-trace watcher unavailable ({ex.Message}); polling fallback only.");
        }

        try
        {
            _stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            _stopWatcher.EventArrived += (_, e) => EnqueueProcEvent(false, e);
            _stopWatcher.Start();
        }
        catch (Exception ex)
        {
            _log.Warn($"ProcessControl: stop-trace watcher unavailable ({ex.Message}); polling fallback only.");
        }

        _heartbeat = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        // Apply every active rule to the processes ALREADY running — sticky
        // rules must not require a process restart to take effect.
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000); // let the WMI watchers warm up first
            ApplyRulesToRunningProcesses(null);
            Log("Engine", "initial sweep", "—", "applied active rules to running processes");
            FlushPendingActions();
        });

        // Re-apply Boost Mode that was active when the app closed (saved values persist).
        lock (_sync)
        {
            if (_config.BoostModeActive && _savedBoostValues == null)
            {
                if (ProcessControlNative.ApplyBoostMode(out var saved))
                {
                    _savedBoostValues = saved;
                    SaveBoostValues(saved);
                }
            }
        }

        Log("Engine", "started", "—", "rule enforcement active");
        _log.Info("ProcessControl engine started.");
        EngineStateChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_sync)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            try { _startWatcher?.Stop(); } catch { }
            try { _stopWatcher?.Stop(); } catch { }
            _startWatcher?.Dispose();
            _stopWatcher?.Dispose();
            _startWatcher = null;
            _stopWatcher = null;
        }
    }

    private void EnqueueProcEvent(bool started, EventArrivedEventArgs e)
    {
        try
        {
            var props = e.NewEvent.Properties;
            uint pid = Convert.ToUInt32(props["ProcessID"].Value);
            string name = props["ProcessName"]?.Value as string ?? string.Empty;
            _procEvents.Enqueue(new ProcEvent(started, (int)pid, name));
        }
        catch { /* malformed event — ignore */ }
    }

    // ── Persistence ──────────────────────────────────────────────────────

    private void LoadAll()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            if (File.Exists(RulesPath))
            {
                try
                {
                    var bundle = JsonSerializer.Deserialize<RuleBundle>(File.ReadAllText(RulesPath));
                    if (bundle != null)
                    {
                        _rules.Clear();
                        _rules.AddRange(bundle.Rules);
                    }
                }
                catch (Exception ex)
                {
                    // Corrupt/partial file: keep the .tmp if one exists (an
                    // interrupted atomic save), otherwise start fresh.
                    _log.Warn($"ProcessControl: rules file unreadable ({ex.Message})");
                    try
                    {
                        string tmp = RulesPath + ".tmp";
                        if (File.Exists(tmp))
                        {
                            var bundle = JsonSerializer.Deserialize<RuleBundle>(File.ReadAllText(tmp));
                            if (bundle != null)
                            {
                                _rules.Clear();
                                _rules.AddRange(bundle.Rules);
                                _log.Warn("ProcessControl: recovered rules from interrupted save.");
                            }
                        }
                    }
                    catch { }
                }
            }
            if (File.Exists(BoostSavePath))
            {
                try
                {
                    _savedBoostValues = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BoostSavePath));
                }
                catch { }
            }
            try
            {
                var saved = JsonSerializer.Deserialize<List<ActionLogEntry>>(File.ReadAllText(ActionsPath));
                if (saved != null) _actions.AddRange(saved.TakeLast(MaxActions));
            }
            catch { /* corrupt action log — start fresh */ }
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var saved = JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(ConfigPath));
                    if (saved != null) _config = saved; // behaviors/thresholds survive restarts
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"ProcessControl: config load failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"ProcessControl: rule load failed: {ex.Message}");
        }
    }

    private void SaveRules()
    {
        try
        {
            List<ProcessRule> rules;
            lock (_sync)
            {
                rules = _rules.ToList();
            }
            Directory.CreateDirectory(DataFolder);
            string json = JsonSerializer.Serialize(new RuleBundle { Rules = rules },
                new JsonSerializerOptions { WriteIndented = true });
            // Atomic write: a crash or concurrent writer mid-save must never
            // leave an empty/partial rules file (that's how rules "disappear").
            string tmp = RulesPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(RulesPath)) File.Replace(tmp, RulesPath, null);
            else File.Move(tmp, RulesPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"ProcessControl: rule save failed: {ex.Message}");
        }
    }

    private void SaveBoostValues(Dictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(BoostSavePath, JsonSerializer.Serialize(values));
        }
        catch { }
    }

    private void Log(string feature, string action, string process, string detail)
    {
        var entry = new ActionLogEntry
        {
            Feature = FeatureNames.Display(feature),
            Action = action,
            Process = process,
            Detail = detail,
        };
        lock (_sync)
        {
            _actions.Add(entry);
            if (_actions.Count > MaxActions) _actions.RemoveAt(0);
        }
        _pendingActions.Enqueue(entry);
        try { ActionLogged?.Invoke(entry); } catch { }
    }

    private void FlushPendingActions()
    {
        try
        {
            if (_pendingActions.IsEmpty) return;
            List<ActionLogEntry> snapshot;
            lock (_sync) snapshot = _actions.ToList();
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(ActionsPath, JsonSerializer.Serialize(snapshot));
            while (_pendingActions.TryDequeue(out _)) { }
        }
        catch { }
    }

    // ── Rule / profile editing ───────────────────────────────────────────

    public void AddRule(ProcessRule rule)
    {
        lock (_sync)
        {
            rule.Id = Guid.NewGuid().ToString("N");
            _rules.Add(rule);
            SaveRules();
            _procInfo.Clear(); // force fresh path/command-line lookups
        }
        RulesChanged?.Invoke();
        // Apply immediately to processes that are already running.
        _ = Task.Run(() => ApplyRulesToRunningProcesses(rule));
    }

    public void UpdateRule(ProcessRule rule)
    {
        bool nowInactive;
        lock (_sync)
        {
            int i = _rules.FindIndex(r => r.Id == rule.Id);
            if (i >= 0) _rules[i] = rule;
            SaveRules();
            _procInfo.Clear();
            nowInactive = !RuleIsActive(rule);
        }
        RulesChanged?.Invoke();
        if (nowInactive) RestoreManagedByRule(rule.Id);
        else _ = Task.Run(() => ApplyRulesToRunningProcesses(rule, force: true)); // push edits onto processes this rule already owns
    }

    /// <summary>Restores every process managed by the given rule (rule disabled/deleted/profile deactivated).</summary>
    private void RestoreManagedByRule(string ruleId)
    {
        List<int> pids;
        lock (_sync) pids = _managed.Where(kv => kv.Value.RuleId == ruleId).Select(kv => kv.Key).ToList();
        foreach (int pid in pids) RestoreProcess(pid);
    }

    public void DeleteRule(string ruleId)
    {
        lock (_sync)
        {
            _rules.RemoveAll(r => r.Id == ruleId);
            SaveRules();
        }
        RestoreManagedByRule(ruleId);
        RulesChanged?.Invoke();
    }

    // ── Config ───────────────────────────────────────────────────────────

    public void UpdateConfig(EngineConfig config)
    {
        lock (_sync)
        {
            _config = config;
            SaveConfig();
        }
        EngineStateChanged?.Invoke();
    }

    /// <summary>Engine-wide settings persist separately — never with rule saves (which can't see private state).</summary>
    internal static string ConfigPath => Path.Combine(DataFolder, "engine.json");

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            EngineConfig snapshot;
            lock (_sync) snapshot = CloneConfig();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            _log.Warn($"ProcessControl: config save failed: {ex.Message}");
        }
    }

    private EngineConfig CloneConfig()
        => JsonSerializer.Deserialize<EngineConfig>(JsonSerializer.Serialize(_config)) ?? new EngineConfig();

    // ── Topology & presets ───────────────────────────────────────────────

    private CpuTopologyInfo? _topology;

    public CpuTopologyInfo GetTopology()
    {
        lock (_sync)
        {
            if (_topology != null) return _topology;
            var sets = ProcessControlNative.GetCpuSets();
            var info = new CpuTopologyInfo
            {
                CpuName = TopologyHelper.GetCpuModelName(),
                LogicalCount = sets.Count,
                PhysicalCount = sets.Select(s => (s.Group, s.CoreIndex)).Distinct().Count(),
                HasHybridCores = sets.Any(s => s.IsEfficiency) && sets.Any(s => s.IsPerformance),
                L3GroupCount = sets.Select(s => s.LastLevelCacheIndex).Distinct().Count(),
                CpuSets = sets,
            };
            // Real CCD detection via L3-cache topology; when the CPU exposes a
            // single L3 group but ≥16 logical processors, estimate 2 CCDs and
            // label the result as estimated (ProcessX-style heuristic).
            if (info.L3GroupCount < 2 && info.LogicalCount >= 16)
            {
                info.L3GroupCount = 2;
                info.CcdEstimated = true;
            }
            _topology = info;
            return info;
        }
    }

    /// <summary>CPU-set ids for a preset (null when the preset doesn't apply to this CPU).</summary>
    public List<uint>? PresetCpuSetIds(CoreIsolationPreset preset)
    {
        var sets = GetTopology().CpuSets;
        if (sets.Count == 0) return null;
        return ProcessControlNative.BuildPresetCpuSetIds(sets, preset);
    }

    // ── Engine heartbeat ─────────────────────────────────────────────────

    private sealed record ProcEvent(bool Started, int Pid, string Name);

    private void Heartbeat()
    {
        if (_disposed) return;
        if (!_ownsEngine)
        {
            // Ownership race: retry periodically until the other session exits.
            if (_tick % 5 == 0) _ownsEngine = TryBeginEngineSession(TimeSpan.Zero);
            if (!_ownsEngine) return;
        }
        bool engineEnabled;
        lock (_sync) engineEnabled = _config.EngineEnabled;

        _tick++;
        DrainProcEvents();
        SampleProcesses();
        if (engineEnabled)
        {
            if (_tick % 2 == 0) EvaluateCoreCaps();
            if (_tick % 2 == 0) ApplyHardThrottles();
            if (_tick % 5 == 0) EvaluateAutoBalance();
            if (_tick % 5 == 0) EvaluatePreventSleep();
            if (_tick % 5 == 0) EvaluateForegroundBoost();
            if (_tick % 10 == 0) PollForMissedProcesses();
            if (_tick % 10 == 0) FlushPendingActions();
            if (_tick % 10 == 0) ApplyRulesToRunningProcesses(null); // re-assert — games reset their own priority/affinity after launch
            if (_tick % 3 == 0) PruneStoppedProcesses();
        }
        ProcessesChanged?.Invoke();
    }

    private void DrainProcEvents()
    {
        while (_procEvents.TryDequeue(out var evt))
        {
            if (evt.Started)
            {
                _knownPids.Add(evt.Pid);
                OnProcessStarted(evt.Pid, evt.Name);
            }
            else
            {
                _knownPids.Remove(evt.Pid);
                OnProcessStopped(evt.Pid, evt.Name);
            }
        }
    }

    private void OnProcessStarted(int pid, string name) => ApplyRulesToProcess(pid, name, null);

    private void OnProcessStopped(int pid, string name)
    {
        lock (_sync)
        {
            _cpuSamples.Remove(pid);
            _procInfo.Remove(pid);
            _abState.Remove(pid);
            _throttleSuspended.Remove(pid);
            if (_foregroundBoostedPid == pid) _foregroundBoostedPid = 0;
            _managed.Remove(pid);
        }

        // AutoRevive / Keep Running: relaunch unless overridden.
        CheckRevive(name);
    }

    private void CheckRevive(string name)
    {
        ProcessRule? rule = null;
        lock (_sync)
        {
            rule = _rules.FirstOrDefault(r => r.Enabled && (r.Revive || r.KeepRunning));
        }
        if (rule == null) return;

        // Resolve whether THIS process matched the rule (name, path, or command line).
        bool matched = false;
        if (rule.MatchMode == RuleMatchMode.Name)
        {
            matched = string.Equals(rule.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var info = GetProcInfoByName(name);
            matched = rule.MatchMode == RuleMatchMode.Path
                ? info != null && string.Equals(info.ExecutablePath, rule.ProcessName, StringComparison.OrdinalIgnoreCase)
                : info != null && info.CommandLine.Contains(rule.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        if (!matched) return;

        if (rule.KeepRunning && _keepRunningOverrideName != null &&
            string.Equals(_keepRunningOverrideName, name, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.Now < _keepRunningOverrideUntil)
        {
            return; // user override active
        }

        // Only relaunch when NO instance of the name remains.
        bool anyAlive;
        try { anyAlive = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)).Length > 0; }
        catch { anyAlive = false; }
        if (anyAlive) return;

        var reviveInfo = GetProcInfoByName(name);
        if (reviveInfo == null || string.IsNullOrEmpty(reviveInfo.ExecutablePath)) return;

        Log(rule.KeepRunning ? "KeepRunning" : "AutoRevive", "relaunching", name, reviveInfo.CommandLine);
        _log.Info($"ProcessControl: relaunching {name} after unexpected exit.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = reviveInfo.ExecutablePath,
                    Arguments = reviveInfo.Arguments,
                    WorkingDirectory = Path.GetDirectoryName(reviveInfo.ExecutablePath) ?? string.Empty,
                    UseShellExecute = false,
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _log.Warn($"ProcessControl: relaunch of {name} failed: {ex.Message}");
            }
        });
    }

    private ProcInfo? GetProcInfoByName(string name)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '{name.Replace("'", "''")}'");
            foreach (ManagementObject obj in searcher.Get())
            {
                int pid = Convert.ToInt32(obj["ProcessId"]);
                string? path = obj["ExecutablePath"] as string;
                string? cl = obj["CommandLine"] as string;
                if (string.IsNullOrEmpty(path)) continue;
                var info = new ProcInfo(pid, path!, cl ?? string.Empty, ParseArguments(cl ?? string.Empty));
                lock (_sync) _procInfo[pid] = info;
                return info;
            }
        }
        catch { }
        return null;
    }

    internal static string ParseArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return string.Empty;
        string trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            if (end >= 0) return trimmed[(end + 1)..].Trim();
        }
        int space = trimmed.IndexOf(' ');
        return space >= 0 ? trimmed[(space + 1)..].Trim() : string.Empty;
    }

    internal sealed record ProcInfo(int Pid, string ExecutablePath, string CommandLine, string Arguments);

    // ── Rule application ─────────────────────────────────────────────────

    private bool RuleIsActive(ProcessRule rule)
    {
        return rule.Enabled;
    }

    internal static bool RuleMatches(ProcessRule rule, string name, ProcInfo? info, int instanceIndex)
    {
        if (rule.InstanceIndex is { } idx && idx != instanceIndex) return false;
        return rule.MatchMode switch
        {
            RuleMatchMode.Name => string.Equals(rule.ProcessName, name, StringComparison.OrdinalIgnoreCase),
            RuleMatchMode.Path => info != null && !string.IsNullOrEmpty(rule.ProcessName) &&
                                  string.Equals(info.ExecutablePath, rule.ProcessName, StringComparison.OrdinalIgnoreCase),
            RuleMatchMode.CommandLine => info != null && info.CommandLine.Contains(rule.ProcessName, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private void ApplyRulesToProcess(int pid, string name, ProcessRule? only, bool force = false)
    {
        if (pid == Environment.ProcessId) return; // never manage ourselves
        if (string.IsNullOrEmpty(name)) name = GetNameFromPid(pid);

        List<ProcessRule> active;
        lock (_sync) active = _rules.Where(RuleIsActive).ToList();
        if (active.Count == 0) return;

        bool needsInfo = active.Any(r => r.MatchMode != RuleMatchMode.Name);
        ProcInfo? info = needsInfo ? GetProcInfo(pid) : null;

        int instanceIndex = 1;
        try
        {
            instanceIndex = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)).Count(p => p.Id < pid) + 1;
        }
        catch { }

        foreach (var rule in active)
        {
            if (only != null && rule.Id != only.Id) continue;
            if (!RuleMatches(rule, name, info, instanceIndex)) continue;

            // Blocklist / instance limits terminate first — no managed state.
            // Never terminate system-critical processes, no matter what the rule says.
            if (rule.Blocklist)
            {
                if (IsSystemCritical(name))
                {
                    Log("Blocklist", "skipped system-critical", name, $"pid {pid} — refused");
                    continue;
                }
                Log("Blocklist", "terminated", name, $"pid {pid} (rule '{rule.Name}')");
                _log.Warn($"ProcessControl: blocklist terminated {name} (pid {pid}).");
                ProcessControlNative.Terminate(pid);
                return;
            }
            if (rule.MaxInstances is { } max && instanceIndex > max)
            {
                if (IsSystemCritical(name)) continue;
                Log("InstanceLimit", "terminated", name, $"pid {pid} — instance {instanceIndex} exceeds limit {max}");
                _log.Warn($"ProcessControl: instance limit ({max}) exceeded — terminated {name} pid {pid}.");
                ProcessControlNative.Terminate(pid);
                return;
            }

            ApplyStickyRule(rule, pid, name, instanceIndex, force);
        }
    }

    /// <summary>Applies active rules (optionally just one) to every running process.
    /// <paramref name="force"/> re-applies even to processes the rule already manages.</summary>
    private void ApplyRulesToRunningProcesses(ProcessRule? only, bool force = false)
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try { ApplyRulesToProcess(proc.Id, proc.ProcessName + ".exe", only, force); }
                catch { /* process exited mid-scan */ }
            }
        }
        catch { }
    }

    private string GetNameFromPid(int pid)
    {
        lock (_sync)
        {
            if (_processNames.TryGetValue(pid, out var cached)) return cached;
        }
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName + ".exe";
        }
        catch { return $"pid{pid}"; }
    }

    private ProcInfo? GetProcInfo(int pid)
    {
        lock (_sync)
        {
            if (_procInfo.TryGetValue(pid, out var cached)) return cached;
        }
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                string? path = obj["ExecutablePath"] as string;
                string? cl = obj["CommandLine"] as string;
                if (string.IsNullOrEmpty(path)) return null;
                var info = new ProcInfo(pid, path!, cl ?? string.Empty, ParseArguments(cl ?? string.Empty));
                lock (_sync) _procInfo[pid] = info;
                return info;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Applies one sticky rule to a process, capturing state for restore.
    /// <paramref name="force"/> pushes an edited rule's new settings onto a process
    /// the rule already owns — live re-apply, no process restart needed.</summary>
    private void ApplyStickyRule(ProcessRule rule, int pid, string name, int instanceIndex, bool force = false)
    {
        ManagedState? existing;
        lock (_sync) _managed.TryGetValue(pid, out existing);
        if (existing != null && existing.RuleId != rule.Id)
        {
            return; // owned by another rule — never double-apply
        }

        var state = new ManagedState { RuleId = rule.Id, ProcessName = name };
        bool touched = false;

        // Capture the process's natural values so Restore puts back what was
        // there before us (not blind defaults).
        state.OriginalCpu = ProcessControlNative.GetCpuPriorityLevel(pid);

        // Already owned by this rule? Then this call is a RE-ASSERT: re-apply
        // what the process may have reset (games like Roblox restore their own
        // priority class shortly after launching) and skip baseline capture.
        if (existing != null)
        {
            if (force)
            {
                ReapplyStickyRule(rule, existing, pid, name, instanceIndex);
                return;
            }
            bool changed = false;
            if (rule.CpuPriority is { } wantCpu && existing.AppliedCpu == wantCpu &&
                ProcessControlNative.GetCpuPriorityLevel(pid) != wantCpu &&
                ProcessControlNative.TrySetCpuPriority(pid, wantCpu, out _))
            {
                changed = true;
            }
            if (rule.IoPriority is { } wantIo && existing.AppliedIo == wantIo)
                ProcessControlNative.TrySetIoPriority(pid, wantIo, out _);
            if (rule.MemoryPriority is { } wantMem && existing.AppliedMem == wantMem)
                ProcessControlNative.TrySetMemoryPriority(pid, wantMem, out _);
            if (rule.CpuSetIds.Count > 0)
                ProcessControlNative.TrySetCpuSets(pid, rule.CpuSetIds, out _);
            if (changed) Log("StickyRules", "re-asserted", name, $"pid {pid} — process had reverted its priority");
            return;
        }

        if (rule.CpuPriority is { } cpu)
        {
            if (ProcessControlNative.TrySetCpuPriority(pid, cpu, out var cpuErr)) { state.AppliedCpu = cpu; touched = true; }
            else Log("StickyRules", "failed", name, $"pid {pid} — CPU priority not applied: {cpuErr}");
        }
        if (rule.IoPriority is { } io)
        {
            if (ProcessControlNative.TrySetIoPriority(pid, io, out var ioErr)) { state.AppliedIo = io; touched = true; }
            else Log("StickyRules", "failed", name, $"pid {pid} — I/O priority not applied: {ioErr}");
        }
        if (rule.MemoryPriority is { } mem)
        {
            if (ProcessControlNative.TrySetMemoryPriority(pid, mem, out var memErr)) { state.AppliedMem = mem; touched = true; }
            else Log("StickyRules", "failed", name, $"pid {pid} — memory priority not applied: {memErr}");
        }

        // Spread Balancer: distribute instances across distinct core groups.
        if (rule.SpreadInstances && !rule.EnableCoreCap)
        {
            var groups = SpreadGroups();
            if (groups.Count > 0)
            {
                var target = groups[(instanceIndex - 1) % groups.Count];
                if (TrySetCpuSets(pid, target, state))
                {
                    touched = true;
                    Log("SpreadBalancer", "pinned", name, $"pid {pid} → group {(instanceIndex - 1) % groups.Count + 1}/{groups.Count}");
                }
            }
        }
        else if (rule.CpuSetIds.Count > 0 || rule.AffinityMask != 0)
        {
            // Baseline must exist even for pinned rules so a combined
            // pin + core cap can budget WITHIN the pin later.
            if (rule.EnableCoreCap && state.BaselineCpuSets.Count == 0 && state.BaselineMask == 0)
                CaptureBaseline(pid, state);

            if (rule.CpuSetIds.Count > 0)
            {
                if (TrySetCpuSets(pid, rule.CpuSetIds, state)) touched = true;
                else Log("StickyRules", "failed", name, $"pid {pid} — CPU-set pin not applied (access denied or unsupported)");
            }
            else if (ProcessControlNative.TrySetAffinityMask(pid, rule.AffinityMask, out var maskErr))
            {
                state.AppliedMask = rule.AffinityMask;
                touched = true;
            }
            else Log("StickyRules", "failed", name, $"pid {pid} — affinity not applied: {maskErr}");

            // Apply the cap immediately within the pinned selection.
            if (rule.EnableCoreCap)
            {
                int poolCount = rule.CpuSetIds.Count > 0
                    ? Math.Min(rule.CpuSetIds.Count, Math.Max(1, state.BaselineCpuSets.Count))
                    : Math.Max(1, System.Numerics.BitOperations.PopCount(state.BaselineMask));
                int budget = ComputeCoreBudget(rule, 0, poolCount);
                if (budget < poolCount)
                    ApplyCoreBudget(pid, rule, state, budget, poolCount, rule.CpuSetIds.Count > 0 ? new List<uint>(rule.CpuSetIds) : state.BaselineCpuSets);
            }
        }
        else if (rule.EnableCoreCap)
        {
            CaptureBaseline(pid, state);
            state.CurrentCoreBudget = state.BaselineCpuSets.Count;
            touched = true;
            // Apply the cap immediately — don't wait for the first heartbeat tick.
            int baselineCount = state.BaselineCpuSets.Count > 0
                ? state.BaselineCpuSets.Count
                : Math.Max(1, System.Numerics.BitOperations.PopCount(state.BaselineMask));
            int budget = ComputeCoreBudget(rule, 0, baselineCount);
            if (budget < baselineCount) ApplyCoreBudget(pid, rule, state, budget, baselineCount);
        }

        bool needsTracking = touched || rule.PreventSleep || rule.Revive || rule.KeepRunning;
        if (!needsTracking) return;

        lock (_sync)
        {
            if (_managed.ContainsKey(pid)) return;
            _managed[pid] = state;
        }

        if (rule.PreventSleep)
        {
            Log("PreventSleep", "guarding sleep", name, $"pid {pid}");
        }

        var parts = new List<string>();
        if (state.AppliedCpu is { } ac) parts.Add($"CPU {ac}");
        if (state.AppliedIo is { } ai) parts.Add($"I/O {ai}");
        if (state.AppliedMem is { } am) parts.Add($"mem {(int)am}");
        if (state.AppliedCpuSets is { Count: > 0 } cs) parts.Add($"pinned to {cs.Count} CPU set(s)");
        if (state.AppliedMask != 0) parts.Add($"affinity {state.AppliedMask}");
        if (rule.EnableCoreCap) parts.Add("core cap active");
        Log("StickyRules", "applied", name, $"pid {pid} — {string.Join(", ", parts)}");
    }

    /// <summary>
    /// Pushes an edited rule's new settings onto a process this rule already
    /// owns — live, without waiting for the process to restart. Applies what
    /// the edit changed/added and undoes what it removed.
    /// </summary>
    private void ReapplyStickyRule(ProcessRule rule, ManagedState state, int pid, string name, int instanceIndex)
    {
        bool changed = false;

        // CPU priority: apply the new level, or undo when the edit removed it.
        if (rule.CpuPriority is { } cpu)
        {
            if (state.AppliedCpu != cpu)
            {
                if (ProcessControlNative.TrySetCpuPriority(pid, cpu, out var cpuErr))
                {
                    state.AppliedCpu = cpu;
                    changed = true;
                }
                else Log("StickyRules", "failed", name, $"pid {pid} — CPU priority not updated: {cpuErr}");
            }
        }
        else if (state.AppliedCpu != null &&
                 ProcessControlNative.TrySetCpuPriority(pid, state.OriginalCpu ?? CpuPriorityLevel.Normal, out _))
        {
            state.AppliedCpu = null;
            changed = true;
        }

        // I/O priority.
        if (rule.IoPriority is { } io)
        {
            if (state.AppliedIo != io)
            {
                if (ProcessControlNative.TrySetIoPriority(pid, io, out var ioErr))
                {
                    state.AppliedIo = io;
                    changed = true;
                }
                else Log("StickyRules", "failed", name, $"pid {pid} — I/O priority not updated: {ioErr}");
            }
        }
        else if (state.AppliedIo != null &&
                 ProcessControlNative.TrySetIoPriority(pid, IoPriorityLevel.Normal, out _))
        {
            state.AppliedIo = null;
            changed = true;
        }

        // Memory priority.
        if (rule.MemoryPriority is { } mem)
        {
            if (state.AppliedMem != mem)
            {
                if (ProcessControlNative.TrySetMemoryPriority(pid, mem, out var memErr))
                {
                    state.AppliedMem = mem;
                    changed = true;
                }
                else Log("StickyRules", "failed", name, $"pid {pid} — memory priority not updated: {memErr}");
            }
        }
        else if (state.AppliedMem != null &&
                 ProcessControlNative.TrySetMemoryPriority(pid, MemoryPriorityLevel.Highest, out _))
        {
            state.AppliedMem = null;
            changed = true;
        }

        // Pinning / spread / core cap — mirror the fresh-apply branches.
        if (rule.SpreadInstances && !rule.EnableCoreCap && rule.CpuSetIds.Count == 0 && rule.AffinityMask == 0)
        {
            var groups = SpreadGroups();
            if (groups.Count > 0)
            {
                var target = groups[(instanceIndex - 1) % groups.Count];
                if (TrySetCpuSets(pid, target, state)) changed = true;
            }
        }
        else if (rule.CpuSetIds.Count > 0 || rule.AffinityMask != 0)
        {
            if (rule.CpuSetIds.Count > 0)
            {
                if (state.AppliedCpuSets == null || !state.AppliedCpuSets.SequenceEqual(rule.CpuSetIds))
                {
                    if (TrySetCpuSets(pid, rule.CpuSetIds, state)) changed = true;
                    else Log("StickyRules", "failed", name, $"pid {pid} — CPU-set pin not updated (access denied or unsupported)");
                }
                state.AppliedMask = 0;
            }
            else if (state.AppliedMask != rule.AffinityMask)
            {
                if (ProcessControlNative.TrySetAffinityMask(pid, rule.AffinityMask, out _))
                {
                    state.AppliedMask = rule.AffinityMask;
                    state.AppliedCpuSets = null;
                    changed = true;
                }
            }
        }
        else if (state.AppliedCpuSets is { Count: > 0 } || state.AppliedMask != 0)
        {
            // The edit removed the pin — release it.
            if (state.AppliedCpuSets is { Count: > 0 }) ProcessControlNative.TrySetCpuSets(pid, new List<uint>(), out _);
            if (state.AppliedMask != 0) ProcessControlNative.TrySetAffinityMask(pid, 0, out _);
            state.AppliedCpuSets = null;
            state.AppliedMask = 0;
            changed = true;
        }

        // Core cap newly enabled: capture the baseline now; the heartbeat then
        // drives the budget from live load with the new limits.
        if (rule.EnableCoreCap && state.BaselineCpuSets.Count == 0 && state.BaselineMask == 0)
        {
            CaptureBaseline(pid, state);
            state.CurrentCoreBudget = 0; // let EvaluateCoreCaps re-derive from live load
            changed = true;
        }

        if (!changed) return;
        Log("StickyRules", "updated", name, $"pid {pid} — edit applied live: {rule.Summary}");
    }

    private void CaptureBaseline(int pid, ManagedState state)
    {
        var sets = ProcessControlNative.GetCpuSets();
        state.BaselineCpuSets = sets.Select(s => s.Id).ToList();
        state.BaselineMask = ulong.MaxValue;
        if (state.BaselineCpuSets.Count == 0)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                state.BaselineMask = (ulong)proc.ProcessorAffinity.ToInt64();
            }
            catch { }
        }
    }

    private bool TrySetCpuSets(int pid, IReadOnlyList<uint> ids, ManagedState state)
    {
        if (ProcessControlNative.TrySetCpuSets(pid, ids, out _))
        {
            state.AppliedCpuSets = ids.ToList();
            state.AppliedMask = 0;
            return true;
        }
        return false;
    }

    /// <summary>Physical-core groups (one per physical core) used by Spread Balancer.</summary>
    private List<List<uint>> SpreadGroups()
    {
        var sets = GetTopology().CpuSets;
        return sets.GroupBy(s => (s.Group, s.CoreIndex))
            .Select(g => g.Select(s => s.Id).ToList())
            .ToList();
    }

    // ── Sampling (live list + engine inputs) ─────────────────────────────

    private sealed class CpuSample
    {
        public DateTimeOffset At;
        public TimeSpan Total;
        public double Percent;
    }

    private void SampleProcesses()
    {
        var now = DateTimeOffset.Now;
        if (_logicalCount < 0) _logicalCount = Math.Max(1, GetTopology().LogicalCount);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var total = proc.TotalProcessorTime;
                    long ws = 0;
                    try { ws = proc.WorkingSet64; } catch { }

                    double cpu = 0;
                    CpuSample? prev;
                    lock (_sync)
                    {
                        _cpuSamples.TryGetValue(proc.Id, out prev);
                        if (prev != null)
                        {
                            double elapsed = (now - prev.At).TotalSeconds;
                            double delta = (total - prev.Total).TotalSeconds;
                            if (elapsed > 0.05)
                                cpu = Math.Clamp(delta / elapsed / _logicalCount * 100, 0, 100);
                        }
                        _cpuSamples[proc.Id] = new CpuSample { At = now, Total = total, Percent = cpu };
                        if (ws > 0) _lastWorkingSet[proc.Id] = ws;
                        _processNames[proc.Id] = proc.ProcessName + ".exe";
                    }
                }
                catch { /* process exited mid-enumeration */ }
            }
        }
        catch { /* enumeration failed */ }
    }

    private double LastCpuPercent(int pid)
    {
        lock (_sync)
        {
            return _cpuSamples.TryGetValue(pid, out var s) ? s.Percent : 0;
        }
    }

    /// <summary>Prunes tracker entries for pids that no longer exist — without
    /// this, the process list fills with ghosts and keeps stale priority text.</summary>
    private void PruneStoppedProcesses()
    {
        try
        {
            HashSet<int> alive;
            try { alive = Process.GetProcesses().Select(p => p.Id).ToHashSet(); }
            catch { return; }

            List<int> dead;
            lock (_sync)
            {
                dead = _cpuSamples.Keys.Where(pid => !alive.Contains(pid)).ToList();
            }
            foreach (int pid in dead) OnProcessStopped(pid, GetNameFromPid(pid));
        }
        catch { }
    }

    /// <summary>Live process list for the page (CPU % from the 1s sampler).</summary>
    public List<ProcessSnapshot> GetProcessSnapshots()
    {
        var result = new List<ProcessSnapshot>();
        lock (_sync)
        {
            foreach (var pid in _cpuSamples.Keys)
            {
                double cpu = _cpuSamples[pid].Percent;
                string name = _processNames.TryGetValue(pid, out var n) ? n : GetNameFromPid(pid);
                long ws = _lastWorkingSet.TryGetValue(pid, out var w) ? w : 0;
                bool managed = _managed.ContainsKey(pid);
                string by = managed ? RuleName(_managed[pid].RuleId) : string.Empty;
                result.Add(new ProcessSnapshot
                {
                    Pid = pid,
                    Name = name,
                    CpuPercent = Math.Round(cpu, 1),
                    WorkingSetBytes = ws,
                    PriorityText = ProcessControlNative.GetPriorityText(pid),
                    AffinityText = ProcessControlNative.GetAffinityText(pid),
                    Managed = managed,
                    ManagedBy = by,
                });
            }
        }
        return result.OrderByDescending(p => p.CpuPercent).ToList();
    }

    private string RuleName(string ruleId)
    {
        if (string.IsNullOrEmpty(ruleId)) return "quick pin";
        lock (_sync)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
            return rule == null ? "rule" : (string.IsNullOrEmpty(rule.Name) ? rule.ProcessName : rule.Name);
        }
    }

    // ── AutoBalance (ProBalance equivalent) ──────────────────────────────

    private sealed class AbState
    {
        public double MaxCpu;
        public DateTimeOffset HighSince;
        public CpuPriorityLevel? Applied;
        public bool Restoring;
        public DateTimeOffset RestoreAt;
    }

    private void EvaluateAutoBalance()
    {
        int threshold, sustain, recover;
        bool enabled;
        List<string> exclusions;
        lock (_sync)
        {
            enabled = _config.AutoBalanceEnabled;
            threshold = _config.AutoBalanceCpuPercentThreshold;
            sustain = _config.AutoBalanceSustainSeconds;
            recover = _config.AutoBalanceRecoverSeconds;
            exclusions = _config.AutoBalanceExclusions.ToList();
        }
        if (!enabled) return;

        var now = DateTimeOffset.Now;
        List<(int Pid, string Name, double Cpu)> loads;
        lock (_sync)
        {
            loads = _cpuSamples.Keys
                .Select(pid => (pid, _processNames.TryGetValue(pid, out var n) ? n : string.Empty, _cpuSamples[pid].Percent))
                .ToList();
        }

        foreach (var (pid, name, cpu) in loads)
        {
            if (string.IsNullOrEmpty(name) || exclusions.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (ProcessControlNative.GetForegroundPid() == pid) continue;
            if (IsSystemCritical(name)) continue;
            lock (_sync)
            {
                if (_managed.ContainsKey(pid)) continue; // sticky rules own the process
            }

            AbState? st;
            lock (_sync) _abState.TryGetValue(pid, out st);
            st ??= new AbState { HighSince = now };

            if (cpu > threshold)
            {
                st.MaxCpu = Math.Max(st.MaxCpu, cpu);
                if (st.Applied == null && (now - st.HighSince).TotalSeconds >= sustain)
                {
                    var target = CurrentLevel(pid) > CpuPriorityLevel.BelowNormal
                        ? CpuPriorityLevel.BelowNormal : CpuPriorityLevel.Idle;
                    if (ProcessControlNative.TrySetCpuPriority(pid, target, out _))
                    {
                        st.Applied = target;
                        st.Restoring = false;
                        Log("AutoBalance", "deprioritized", name, $"{cpu:0}% CPU for {sustain}s → {target}");
                        _log.Info($"AutoBalance: {name} ({cpu:0}% CPU) lowered to {target}.");
                    }
                }
            }
            else
            {
                if (st.Applied != null && !st.Restoring)
                {
                    st.Restoring = true;
                    st.RestoreAt = now.AddSeconds(recover);
                }
                if (st.Restoring && st.Applied != null && now >= st.RestoreAt)
                {
                    if (ProcessControlNative.TrySetCpuPriority(pid, CpuPriorityLevel.Normal, out _))
                    {
                        Log("AutoBalance", "restored", name, $"{cpu:0}% CPU — priority back to Normal");
                        _log.Info($"AutoBalance: {name} restored to Normal.");
                    }
                    st.Applied = null;
                    st.Restoring = false;
                    st.HighSince = now;
                    st.MaxCpu = 0;
                }
            }

            lock (_sync) _abState[pid] = st;
        }
    }

    private static CpuPriorityLevel CurrentLevel(int pid)
    {
        string text = ProcessControlNative.GetPriorityText(pid);
        return text switch
        {
            "Below Normal" => CpuPriorityLevel.BelowNormal,
            "Idle" => CpuPriorityLevel.Idle,
            _ => CpuPriorityLevel.Normal,
        };
    }

    /// <summary>
    /// Core budget for the dynamic Core Cap: MaxCores is a hard ceiling;
    /// MaxCpuPercent scales the budget with live load (pure, unit-testable).
    /// A pinned rule (CpuSetIds) caps within its own selection: the budget
    /// never exceeds the number of pinned sets, and the pinned sets are the
    /// pool the budget is drawn from.
    /// </summary>
    internal static int ComputeCoreBudget(ProcessRule rule, double cpuPercent, int baselineCoreCount)
    {
        int pool = rule.CpuSetIds is { Count: > 0 }
            ? Math.Min(rule.CpuSetIds.Count, baselineCoreCount)
            : baselineCoreCount;
        int budget = rule.MaxCores > 0 ? Math.Min(rule.MaxCores, pool) : pool;
        if (rule.MaxCpuPercent > 0)
        {
            int scaled = (int)Math.Ceiling(cpuPercent / 100.0 * baselineCoreCount);
            budget = Math.Clamp(scaled, 1, budget);
        }
        return budget;
    }

    // ── Core Cap (dynamic + hard throttle) ───────────────────────────────

    private void EvaluateCoreCaps()
    {
        List<(int Pid, ProcessRule Rule)> capped;
        lock (_sync)
        {
            capped = _managed
                .Where(kv => !string.IsNullOrEmpty(kv.Value.RuleId))
                .Select(kv => (kv.Key, Rule: _rules.FirstOrDefault(r => r.Id == kv.Value.RuleId)!))
                .Where(x => x.Rule is { Enabled: true, EnableCoreCap: true })
                .ToList();
        }

        foreach (var (pid, rule) in capped)
        {
            double cpu = LastCpuPercent(pid);
            ManagedState? state;
            lock (_sync) _managed.TryGetValue(pid, out state);
            if (state == null) continue;

            // Baseline pool: the rule's pinned sets when it has a pin (the cap
            // then operates WITHIN the pin instead of releasing past it);
            // otherwise every CPU set the process had when it was first
            // managed. Falls back to the legacy affinity mask when CPU Sets
            // are unavailable.
            List<uint> poolIds;
            if (rule.CpuSetIds is { Count: > 0 })
            {
                poolIds = state.BaselineCpuSets.Where(rule.CpuSetIds.Contains).ToList();
                if (poolIds.Count == 0) poolIds = new List<uint>(rule.CpuSetIds); // stale baseline — pin wins
            }
            else
            {
                poolIds = state.BaselineCpuSets;
            }

            int baselineSets = state.BaselineCpuSets.Count;
            int baselineMaskBits = System.Numerics.BitOperations.PopCount(state.BaselineMask);
            int baseline = baselineSets > 0 ? poolIds.Count : baselineMaskBits;
            if (baseline == 0) continue;

            int budget = ComputeCoreBudget(rule, cpu, baseline);

            if (baselineSets > 0)
            {
                ApplyCoreBudgetViaSets(pid, rule, state, budget, baseline, cpu, poolIds);
            }
            else
            {
                ApplyCoreBudgetViaMask(pid, state, budget, baselineMaskBits, cpu);
            }
        }
    }

    /// <summary>One budget decision via the CPU Sets API (preferred path). <paramref name="pool"/> is the full set of ids the budget may use.</summary>
    private void ApplyCoreBudgetViaSets(int pid, ProcessRule rule, ManagedState state,
        int budget, int baselineCount, double cpu, List<uint> pool)
    {
        bool applied = state.AppliedCpuSets != null;
        bool full = applied && state.AppliedCpuSets!.Count >= baselineCount;

        if (budget >= baselineCount)
        {
            if (!full)
            {
                if (ProcessControlNative.TrySetCpuSets(pid, pool, out _))
                {
                    state.AppliedCpuSets = pool.ToList();
                    state.CurrentCoreBudget = budget;
                    Log("CoreCap", "released", state.ProcessName, $"pid {pid} — load {cpu:0}% → full core budget");
                }
            }
            else
            {
                state.CurrentCoreBudget = budget;
            }
            return;
        }

        var target = pool.Take(budget).ToList();
        if (applied && state.AppliedCpuSets!.SequenceEqual(target)) { state.CurrentCoreBudget = budget; return; }
        if (TrySetCpuSets(pid, target, state))
        {
            state.CurrentCoreBudget = budget;
            Log("CoreCap", "capped", state.ProcessName, $"pid {pid} — {cpu:0}% CPU → {budget}/{baselineCount} cores");
        }
    }

    /// <summary>One budget decision via the group-0 affinity mask (fallback when CPU Sets are unavailable).</summary>
    private void ApplyCoreBudgetViaMask(int pid, ManagedState state,
        int budget, int baselineBits, double cpu)
    {
        ulong systemMask = ProcessControlNative.GetSystemAffinityMask();
        if (systemMask == 0) return;

        // Lowest `budget` bits of the system mask.
        ulong targetMask = budget >= baselineBits
            ? systemMask
            : (1UL << budget) - 1;

        if (budget >= baselineBits || targetMask == systemMask)
        {
            if (state.AppliedMask != 0 && state.AppliedMask != systemMask)
            {
                if (ProcessControlNative.TrySetAffinityMask(pid, systemMask, out _))
                {
                    state.AppliedMask = systemMask;
                    state.CurrentCoreBudget = budget;
                    Log("CoreCap", "released", state.ProcessName, $"pid {pid} — load {cpu:0}% → full core budget");
                }
            }
            else
            {
                state.CurrentCoreBudget = budget;
            }
            return;
        }

        if (state.AppliedMask == targetMask) { state.CurrentCoreBudget = budget; return; }
        if (ProcessControlNative.TrySetAffinityMask(pid, targetMask, out _))
        {
            state.AppliedMask = targetMask;
            state.CurrentCoreBudget = budget;
            Log("CoreCap", "capped", state.ProcessName, $"pid {pid} — {cpu:0}% CPU → {budget}/{baselineBits} cores (affinity)");
        }
    }

    /// <summary>Applies a core-cap budget immediately (used at rule application so the cap bites before the first tick).</summary>
    private void ApplyCoreBudget(int pid, ProcessRule rule, ManagedState state, int budget, int baselineCount, List<uint>? pool = null)
    {
        if (state.BaselineCpuSets.Count > 0)
        {
            ApplyCoreBudgetViaSets(pid, rule, state, budget, baselineCount, 0, pool ?? state.BaselineCpuSets);
        }
        else
        {
            ApplyCoreBudgetViaMask(pid, state, budget, baselineCount, 0);
        }
    }

    /// <summary>Hard throttle: suspend threads on a duty cycle when the process exceeds its ceiling.
    /// The duty cycle is closed-loop: every cycle measures the real CPU% over the ACTIVE
    /// window only (a suspended process accumulates no CPU time, so a fixed off-time
    /// can't predict how much it burns when running) and lengthens/shortens the next
    /// suspend until the running-window average converges on the ceiling.</summary>
    private void ApplyHardThrottles()
    {
        List<(int Pid, ProcessRule Rule)> throttled;
        lock (_sync)
        {
            throttled = _managed
                .Where(kv => !string.IsNullOrEmpty(kv.Value.RuleId))
                .Select(kv => (kv.Key, Rule: _rules.FirstOrDefault(r => r.Id == kv.Value.RuleId)!))
                .Where(x => x.Rule is { Enabled: true, HardThrottle: true, MaxCpuPercent: > 0 })
                .ToList();
        }

        foreach (var (pid, rule) in throttled)
        {
            bool suspended;
            ManagedState? state;
            lock (_sync)
            {
                suspended = _throttleSuspended.TryGetValue(pid, out var s) && s;
                _managed.TryGetValue(pid, out state);
            }
            if (suspended || state == null) continue;

            // Measure the process's AWAKE burn rate: CPU consumed since the last
            // resume, divided by how long it has been awake. Sampled CPU% mixes
            // suspended time into the average, which would mask a still-hogging
            // process — the awake rate is the honest trigger.
            double awakeSeconds = 0;
            double coresBusy = 0;
            if (state.LastResumeAt is { } resumeAt)
            {
                awakeSeconds = (DateTimeOffset.Now - resumeAt).TotalSeconds;
                if (awakeSeconds >= 0.3)
                {
                    double burned = Math.Max(0,
                        ProcessControlNative.GetProcessCpuSeconds(pid) - state.ResumeStartCpu);
                    coresBusy = burned / awakeSeconds;
                }
            }

            double sampledCpu = LastCpuPercent(pid);
            // Ceiling expressed in cores (5 % of a 16-thread CPU = 0.8 cores) so
            // it lives in the same units as the measured burn rate.
            if (_logicalCount < 1) _logicalCount = Math.Max(1, GetTopology().LogicalCount);
            double ceilingCores = rule.MaxCpuPercent / 100.0 * _logicalCount;
            bool overCeiling = sampledCpu > rule.MaxCpuPercent || coresBusy > ceilingCores * 1.1;
            if (!overCeiling) continue;

            // Duty cycle needed to hold the ceiling, from the measured burn rate:
            // duty = ceilingCores / coresBusy; suspend = awake * (1/duty − 1).
            // A 1-core burner under a 5 % ceiling on 16 threads: duty = 0.8 →
            // ~2 s awake / ~0.5 s suspended → averages 5.0 %.
            double duty = ceilingCores / Math.Max(0.05, coresBusy);
            duty = Math.Clamp(duty, 0.02, 0.9);
            double awakeForMath = awakeSeconds >= 0.3 ? awakeSeconds : 2.0;
            int offMs = Math.Clamp((int)(awakeForMath * (1.0 / duty - 1.0) * 1000), 100, 30_000);

            lock (_sync) _throttleSuspended[pid] = true;
            string name = state.ProcessName;
            _ = Task.Run(async () =>
            {
                try
                {
                    ProcessControlNative.SuspendProcess(pid);
                    await Task.Delay(offMs);
                    ProcessControlNative.ResumeProcess(pid);
                }
                catch { }
                finally
                {
                    // Stamp the resume point so the next heartbeat measures the
                    // pure AWAKE window (this cycle's suspend is excluded).
                    state.LastResumeAt = DateTimeOffset.Now;
                    state.ResumeStartCpu = ProcessControlNative.GetProcessCpuSeconds(pid);
                    lock (_sync) _throttleSuspended[pid] = false;
                }
            });
            string rateInfo = coresBusy > 0
                ? $" — awake burn {coresBusy:0.0} core(s), suspending {(offMs >= 1000 ? $"{offMs / 1000.0:0.#} s" : $"{offMs} ms")}"
                : $" — suspending {offMs} ms";
            Log("HardThrottle", "throttling", name, $"{sampledCpu:0}% CPU > {rule.MaxCpuPercent}% ceiling{rateInfo}");
        }
    }

    private string stateName(int pid)
    {
        lock (_sync)
        {
            return _managed.TryGetValue(pid, out var s) ? s.ProcessName : pid.ToString();
        }
    }

    private void EvaluateForegroundBoost()
    {
        bool enabled;
        lock (_sync) enabled = _config.ForegroundBoostEnabled;

        int fg = ProcessControlNative.GetForegroundPid();

        if (!enabled)
        {
            int boosted;
            lock (_sync)
            {
                boosted = _foregroundBoostedPid;
                _foregroundBoostedPid = 0;
            }
            if (boosted != 0) ProcessControlNative.TrySetCpuPriority(boosted, CpuPriorityLevel.Normal, out _);
            return;
        }

        if (fg == 0) return;
        lock (_sync)
        {
            if (fg == _foregroundBoostedPid) return;
            if (_foregroundBoostedPid != 0)
            {
                int old = _foregroundBoostedPid;
                _foregroundBoostedPid = 0;
                ProcessControlNative.TrySetCpuPriority(old, CpuPriorityLevel.Normal, out _);
            }
        }
        if (ProcessControlNative.TrySetCpuPriority(fg, CpuPriorityLevel.AboveNormal, out _))
        {
            lock (_sync) _foregroundBoostedPid = fg;
            Log("ForegroundBoost", "boosted", GetNameFromPid(fg), "foreground app → Above Normal");
        }
    }

    // ── Prevent Sleep ────────────────────────────────────────────────────

    private void EvaluatePreventSleep()
    {
        bool any;
        lock (_sync)
        {
            any = _managed.Values.Any(s =>
                !string.IsNullOrEmpty(s.RuleId) &&
                _rules.Any(r => r.Id == s.RuleId && r.Enabled && r.PreventSleep));
        }
        if (any != _sleepRequired)
        {
            _sleepRequired = any;
            // MUST be called on this long-lived heartbeat thread — the assertion
            // is bound to the calling thread and dies with a pooled one.
            ProcessControlNative.SetSleepRequired(any);
            if (any) Log("PreventSleep", "sleep blocked", "system", "a guarded process is running");
        }
        else if (any)
        {
            // Re-assert periodically in case another app cleared the flag.
            ProcessControlNative.SetSleepRequired(true);
        }
    }

    // ── Polling fallback (missed WMI events) ─────────────────────────────

    private void PollForMissedProcesses()
    {
        try
        {
            var current = new HashSet<int>();
            foreach (var proc in Process.GetProcesses()) current.Add(proc.Id);

            List<int> started;
            List<int> stopped;
            lock (_sync)
            {
                started = current.Except(_knownPids).ToList();
                stopped = _knownPids.Except(current).ToList();
                _knownPids.Clear();
                _knownPids.UnionWith(current);
            }
            foreach (int pid in started) OnProcessStarted(pid, GetNameFromPid(pid));
            foreach (int pid in stopped) OnProcessStopped(pid, GetNameFromPid(pid));
        }
        catch { }
    }

    // ── Public management actions ────────────────────────────────────────

    public void ApplyRuleToProcess(string ruleId, int pid)
    {
        ProcessRule? rule;
        lock (_sync) rule = _rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule == null) return;
        string name = GetNameFromPid(pid);
        int instanceIndex = 1;
        try
        {
            instanceIndex = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)).Count(p => p.Id < pid) + 1;
        }
        catch { }
        ApplyStickyRule(rule, pid, name, instanceIndex);
    }

    public bool ApplyPresetToProcess(CoreIsolationPreset preset, int pid)
    {
        var ids = PresetCpuSetIds(preset);
        if (ids == null) return false;
        if (ProcessControlNative.TrySetCpuSets(pid, ids, out _))
        {
            lock (_sync)
            {
                if (!_managed.ContainsKey(pid))
                {
                    _managed[pid] = new ManagedState
                    {
                        RuleId = string.Empty,
                        ProcessName = GetNameFromPid(pid),
                        AppliedCpuSets = ids,
                    };
                }
            }
            Log("CoreIsolation", "applied", GetNameFromPid(pid), $"{preset} → {ids.Count} CPU set(s)");
            return true;
        }
        return false;
    }

    public void RestoreProcess(int pid)
    {
        ManagedState? state;
        lock (_sync)
        {
            if (!_managed.TryGetValue(pid, out state)) return;
            _managed.Remove(pid);
        }

        // Restore exactly what was applied — and to the process's ORIGINAL
        // values where captured, not assumed defaults.
        if (state.AppliedCpuSets is { Count: > 0 }) ProcessControlNative.TrySetCpuSets(pid, new List<uint>(), out _);
        if (state.AppliedMask != 0) ProcessControlNative.TrySetAffinityMask(pid, 0, out _);
        if (state.AppliedCpu is { })
            ProcessControlNative.TrySetCpuPriority(pid, state.OriginalCpu ?? CpuPriorityLevel.Normal, out _);
        if (state.AppliedIo is { }) ProcessControlNative.TrySetIoPriority(pid, IoPriorityLevel.Normal, out _);
        if (state.AppliedMem is { }) ProcessControlNative.TrySetMemoryPriority(pid, MemoryPriorityLevel.Highest, out _);
        lock (_sync) _throttleSuspended.Remove(pid);
        ProcessControlNative.ResumeProcess(pid);

        Log("StickyRules", "restored", state.ProcessName, $"pid {pid}");
    }

    /// <summary>Restores every managed process — the one-click safety net.</summary>
    public int RestoreAllManaged()
    {
        List<int> pids;
        lock (_sync) pids = _managed.Keys.ToList();
        foreach (int pid in pids) RestoreProcess(pid);
        return pids.Count;
    }

    public bool Kill(int pid, out string? error)
    {
        error = null;
        string name = GetNameFromPid(pid);
        ProcessRule? keepRule = null;
        lock (_sync)
        {
            keepRule = _rules.FirstOrDefault(r => r.Enabled && r.KeepRunning &&
                r.MatchMode == RuleMatchMode.Name &&
                string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        if (keepRule != null)
        {
            bool overridden = _keepRunningOverrideName != null &&
                string.Equals(_keepRunningOverrideName, name, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.Now < _keepRunningOverrideUntil;
            if (!overridden)
            {
                error = $"'{name}' is protected by Keep Running. Use Allow Close to override once.";
                return false;
            }
        }
        if (ProcessControlNative.Terminate(pid))
        {
            Log("ProcessControl", "terminated", name, $"pid {pid}");
            return true;
        }
        error = $"TerminateProcess failed for pid {pid} (error {Marshal.GetLastWin32Error()})";
        return false;
    }

    public void SetKeepRunningOverride(string processName, int minutes = 10)
    {
        lock (_sync)
        {
            _keepRunningOverrideName = processName;
            _keepRunningOverrideUntil = DateTimeOffset.Now.AddMinutes(minutes);
        }
        Log("KeepRunning", "override set", processName, $"{minutes} minute(s) — close allowed");
    }

    /// <summary>
    /// Closes a running process and relaunches it with its original command
    /// line — the clean way to fully apply an edited rule (fresh instance,
    /// rules re-apply at launch). Refuses for system-critical processes.
    /// </summary>
    public async Task<(bool Ok, string Message)> RestartProcessAsync(int pid)
    {
        string name = GetNameFromPid(pid);
        if (IsSystemCritical(name))
        {
            return (false, $"'{name}' is system-critical — restart refused.");
        }

        // Snapshot the launch info BEFORE terminating so the relaunch is faithful.
        var info = GetProcInfo(pid);
        string exePath = info?.ExecutablePath ?? string.Empty;
        if (string.IsNullOrEmpty(exePath))
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                exePath = proc.MainModule?.FileName ?? string.Empty;
            }
            catch { }
        }
        if (string.IsNullOrEmpty(exePath))
        {
            return (false, $"Could not read the executable path for {name} — cannot restart it.");
        }
        string arguments = info?.Arguments ?? string.Empty;
        string workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;

        // A matching Keep Running / AutoRevive rule would fight the manual
        // restart with its own relaunch — pause it briefly for this name.
        bool hasReviveRule;
        lock (_sync)
        {
            hasReviveRule = _rules.Any(r => r.Enabled && (r.Revive || r.KeepRunning) &&
                r.MatchMode == RuleMatchMode.Name &&
                string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        if (hasReviveRule) SetKeepRunningOverride(name, minutes: 1);

        Log("ProcessControl", "restarting", name, $"pid {pid} — closing, then relaunching so new rule settings start fresh");
        ProcessControlNative.Terminate(pid);

        // Wait for the pid to actually exit (up to ~5 s) before relaunching.
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) break;
            }
            catch
            {
                break; // already gone
            }
            await Task.Delay(100);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"ProcessControl: relaunch of {name} after manual restart failed: {ex.Message}");
            return (false, $"{name} was closed, but the relaunch failed: {ex.Message}");
        }
        Log("ProcessControl", "relaunched", name, exePath);
        return (true, $"Restarted {name} — its rules apply to the fresh instance.");
    }

    public bool IsSystemCritical(string processName) => SystemCriticalNames.Contains(processName.ToLowerInvariant());

    private static readonly HashSet<string> SystemCriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "wininit.exe", "services.exe", "smss.exe", "lsass.exe", "winlogon.exe",
        "dwm.exe", "explorer.exe", "svchost.exe", "system", "registry", "memory compression",
        "audiodg.exe", "fontdrvhost.exe", "spoolsv.exe", "searchindexer.exe", "MsMpEng.exe",
    };

    // ── Boost Mode ───────────────────────────────────────────────────────

    public bool ToggleBoostMode()
    {
        lock (_sync)
        {
            if (_config.BoostModeActive)
            {
                if (_savedBoostValues != null) ProcessControlNative.RestoreBoostValues(_savedBoostValues);
                _config.BoostModeActive = false;
                _savedBoostValues = null;
                try { File.Delete(BoostSavePath); } catch { }
                Log("BoostMode", "disabled", "system", "core parking / frequency scaling restored");
            }
            else
            {
                if (!ProcessControlNative.ApplyBoostMode(out var saved))
                {
                    Log("BoostMode", "failed", "system", "powercfg could not disable core parking");
                    return false;
                }
                _savedBoostValues = saved;
                SaveBoostValues(saved);
                _config.BoostModeActive = true;
                Log("BoostMode", "enabled", "system", "core parking off, max frequency forced");
            }
            SaveRules();
        }
        EngineStateChanged?.Invoke();
        return BoostModeActive;
    }

    // ── Export / import ──────────────────────────────────────────────────

    public string ExportRulesJson()
    {
        lock (_sync)
        {
            return JsonSerializer.Serialize(new RuleBundle { Rules = _rules },
                new JsonSerializerOptions { WriteIndented = true });
        }
    }

    public (bool Ok, string Message) ImportRulesJson(string json)
    {
        try
        {
            var bundle = JsonSerializer.Deserialize<RuleBundle>(json);
            if (bundle == null) return (false, "Invalid JSON: no rules found.");
            lock (_sync)
            {
                RestoreAllManagedUnlocked();
                _rules.Clear();
                _rules.AddRange(bundle.Rules);
                SaveRules();
            }
            RulesChanged?.Invoke();
            Log("StickyRules", "imported", "—", $"{bundle.Rules.Count} rule(s)");
            return (true, $"Imported {bundle.Rules.Count} rule(s).");
        }
        catch (Exception ex)
        {
            return (false, $"Import failed: {ex.Message}");
        }
    }

    private void RestoreAllManagedUnlocked()
    {
        var pids = _managed.Keys.ToList();
        foreach (int pid in pids)
        {
            ProcessControlNative.TrySetCpuSets(pid, new List<uint>(), out _);
            ProcessControlNative.TrySetAffinityMask(pid, 0, out _);
            ProcessControlNative.TrySetCpuPriority(pid, CpuPriorityLevel.Normal, out _);
            ProcessControlNative.TrySetIoPriority(pid, IoPriorityLevel.Normal, out _);
            ProcessControlNative.TrySetMemoryPriority(pid, MemoryPriorityLevel.Highest, out _);
            ProcessControlNative.ResumeProcess(pid);
        }
        _managed.Clear();
        _throttleSuspended.Clear();
    }

    public string ExportActionsJson() => JsonSerializer.Serialize(Actions, new JsonSerializerOptions { WriteIndented = true });

    // ── Autostart for the hidden background mode ─────────────────────────

    /// <summary>Registers the hidden --rules background mode in the HKCU Run key.</summary>
    public static void EnableRulesAutostart()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(RulesRunKeyValueName, $"\"{exePath}\" --rules");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EnableRulesAutostart failed: {ex.Message}");
        }
    }

    public static bool IsRulesAutostartRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RulesRunKeyValueName) is string value &&
                   value.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    // ── Internal state types ─────────────────────────────────────────────

    private sealed class ManagedState
    {
        public string RuleId = string.Empty;
        public string ProcessName = string.Empty;
        public CpuPriorityLevel? AppliedCpu;
        public IoPriorityLevel? AppliedIo;
        public MemoryPriorityLevel? AppliedMem;
        public List<uint>? AppliedCpuSets;
        public ulong AppliedMask;
        public List<uint> BaselineCpuSets = new();
        public ulong BaselineMask;
        public int CurrentCoreBudget;

        /// <summary>The process's natural priority before we touched it (null = unreadable).</summary>
        public CpuPriorityLevel? OriginalCpu;

        // Hard-throttle duty-cycle state.
        public DateTimeOffset? LastResumeAt;
        public double ResumeStartCpu;
    }

    internal sealed class RuleBundle
    {
        public List<ProcessRule> Rules { get; set; } = new();
    }
}