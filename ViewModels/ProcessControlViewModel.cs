using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using KalOS.Models.ProcessControl;
using KalOS.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace KalOS.ViewModels;

/// <summary>One row in the live process list.</summary>
public partial class ProcessItem : ObservableObject
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private long _workingSetBytes;
    [ObservableProperty] private string _priorityText = "—";
    [ObservableProperty] private string _affinityText = "—";
    [ObservableProperty] private bool _managed;
    [ObservableProperty] private string _managedBy = string.Empty;

    public string MemText
    {
        get => WorkingSetBytes >= 1024 * 1024
            ? $"{WorkingSetBytes / 1024.0 / 1024.0:0.0} MB"
            : $"{WorkingSetBytes / 1024.0:0} KB";
    }

    public string CpuPercentText => $"{CpuPercent:0.0}%";

    public Microsoft.UI.Xaml.Visibility ManagedVisibility
        => Managed ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnWorkingSetBytesChanged(long value) => OnPropertyChanged(nameof(MemText));

    partial void OnCpuPercentChanged(double value) => OnPropertyChanged(nameof(CpuPercentText));

    partial void OnManagedChanged(bool value) => OnPropertyChanged(nameof(ManagedVisibility));
}

/// <summary>One point in the live monitoring history.</summary>
public sealed class MonitorSample
{
    public double[] Cores { get; init; } = Array.Empty<double>();
    public double TotalCpu { get; init; }
    public double MemUsedMb { get; init; }
    public double DiskMbPerSec { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// View model for the Process Control page. The engine lives in
/// <see cref="ProcessControlService"/>; this VM renders it and drives the
/// live monitor sampler.
/// </summary>
public partial class ProcessControlViewModel : ObservableObject
{
    private readonly ProcessControlService _service;
    private readonly DispatcherQueue _dispatcher;
    private ProcessControlNative.PdhSampler? _pdh;
    private DispatcherTimer? _monitorTimer;
    private bool _disposed;

    /// <summary>
    /// When ON, closing the window (X) hides KalOS to the system tray
    /// instead of exiting — rules keep applying and the tray icon restores
    /// the window. Persisted in app-behavior.json.
    /// </summary>
    public bool RunInBackground
    {
        get => App.TrayService?.RunInBackground ?? false;
        set => SetRunInBackground(value);
    }

    public void SetRunInBackground(bool enabled)
    {
        var tray = App.TrayService;
        if (tray is null) return;
        _ = tray.SetRunInBackgroundAsync(enabled);
        OnPropertyChanged(nameof(RunInBackground));
    }

    public ProcessControlViewModel(ProcessControlService service)
    {
        _service = service;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _service.ProcessesChanged += OnServiceProcessesChanged;
        _service.RulesChanged += OnRulesChanged;
        _service.EngineStateChanged += OnEngineStateChanged;
        _service.ActionLogged += OnActionLogged;

        var topo = service.GetTopology();
        CpuName = topo.CpuName;
        LogicalCount = topo.LogicalCount;
        PhysicalCount = topo.PhysicalCount;
        HasEcPreset = topo.HasHybridCores;
        HasCcdPreset = topo.L3GroupCount >= 2;
        CcdEstimated = topo.CcdEstimated;

        RefreshRules();
        RefreshEngineState();
        RefreshActions();

        _pdh = new ProcessControlNative.PdhSampler(Math.Max(1, topo.LogicalCount));
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitorTimer.Tick += (_, _) => SampleMonitor();
        _monitorTimer.Start();
    }

    // ── Collections ──────────────────────────────────────────────────────

    public ObservableCollection<ProcessItem> Processes { get; } = new();
    public ObservableCollection<ProcessRule> Rules { get; } = new();
    public ObservableCollection<ActionLogEntry> Actions { get; } = new();
    public ObservableCollection<MonitorSample> Samples { get; } = new();

    // ── Topology ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _cpuName = "—";
    [ObservableProperty] private int _logicalCount;
    [ObservableProperty] private int _physicalCount;
    [ObservableProperty] private bool _hasEcPreset;
    [ObservableProperty] private bool _hasCcdPreset;
    [ObservableProperty] private bool _ccdEstimated;

    // ── Selection ────────────────────────────────────────────────────────

    [ObservableProperty] private ProcessItem? _selectedProcess;
    [ObservableProperty] private ProcessRule? _selectedRule;
    [ObservableProperty] private ProcessRule? _editingRule;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private int _managedCount;

    // ── Engine / feature toggles ─────────────────────────────────────────

    [ObservableProperty] private bool _engineEnabled;
    [ObservableProperty] private bool _boostActive;
    [ObservableProperty] private bool _autoBalanceEnabled;
    [ObservableProperty] private int _autoBalanceThreshold = 60;
    [ObservableProperty] private int _autoBalanceSustain = 15;
    [ObservableProperty] private int _autoBalanceRecover = 30;
    [ObservableProperty] private string _autoBalanceExclusions = string.Empty;
    [ObservableProperty] private bool _foregroundBoostEnabled;
    [ObservableProperty] private string _rulesAutostartText = string.Empty;

    public string ManagedCountText => ManagedCount == 0
        ? "No processes under rule control"
        : $"{ManagedCount} process(es) under rule control";

    partial void OnManagedCountChanged(int value) => OnPropertyChanged(nameof(ManagedCountText));

    public string CcdEstimatedLabel => CcdEstimated
        ? "CCD split estimated (CPU exposes a single L3 group)"
        : (HasCcdPreset ? "CCD-aware presets available" : string.Empty);
    public string TotalCpuText => $"{TotalCpu:0.0}%";
    public string MemText => $"{MemUsedMb:0} MB";
    public string DiskText => $"{DiskMbPerSec:0.0} MB/s";

    /// <summary>True when a Core Isolation preset applies to the current CPU.</summary>
    public bool PresetAvailable(CoreIsolationPreset preset) => _service.PresetCpuSetIds(preset) != null;

    /// <summary>Refreshes the login-autostart status line (called on page load and after repair).</summary>
    public void RefreshRulesAutostart()
    {
        RulesAutostartText = ProcessControlService.IsRulesAutostartRegistered()
            ? "Sticky rules also enforce from login in a hidden background session (--rules). Autostart: registered."
            : "Sticky rules enforce while KalOS is open. Register the hidden login session below for always-on enforcement.";
    }

    /// <summary>Applies the editor's numeric fields (instance index, max instances) to the rule being edited.</summary>
    public void SetEditorNumerics(string instanceIndex, string maxInstances)
    {
        var rule = EditingRule;
        if (rule == null) return;
        rule.InstanceIndex = int.TryParse(instanceIndex, out int ii) && ii > 0 ? ii : null;
        rule.MaxInstances = int.TryParse(maxInstances, out int mi) && mi > 0 ? mi : null;
    }

    /// <summary>Quick enable/disable from the rules list toggle.</summary>
    public void UpdateRuleQuick(ProcessRule rule)
    {
        _service.UpdateRule(rule);
        StatusText = $"Rule '{rule.DisplayName}' {(rule.Enabled ? "enabled" : "disabled")}."
            + (rule.Enabled ? " New settings were pushed to its running processes." : " Its processes were restored to Windows defaults.");
    }

    // ── Monitor (latest) ─────────────────────────────────────────────────

    [ObservableProperty] private double _totalCpu;
    [ObservableProperty] private double _memUsedMb;
    [ObservableProperty] private double _diskMbPerSec;

    // ── Service event marshaling ─────────────────────────────────────────

    private void OnServiceProcessesChanged() => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        RefreshProcesses();
    });

    private void OnRulesChanged() => _dispatcher.TryEnqueue(RefreshRules);

    private void OnEngineStateChanged() => _dispatcher.TryEnqueue(RefreshEngineState);

    private void OnActionLogged(ActionLogEntry entry) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        Actions.Insert(0, entry);
        while (Actions.Count > 300) Actions.RemoveAt(Actions.Count - 1);
    });

    // ── Process list ─────────────────────────────────────────────────────

    public void RefreshProcesses()
    {
        var snapshots = _service.GetProcessSnapshots();
        var query = SearchText?.Trim();

        // Merge into the existing collection to keep selection stable.
        var byPid = Processes.ToDictionary(p => p.Pid);
        var seen = new HashSet<int>();
        foreach (var snap in snapshots)
        {
            if (!string.IsNullOrEmpty(query) &&
                !snap.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            seen.Add(snap.Pid);
            if (byPid.TryGetValue(snap.Pid, out var item))
            {
                item.CpuPercent = snap.CpuPercent;
                item.WorkingSetBytes = snap.WorkingSetBytes;
                item.PriorityText = snap.PriorityText;
                item.AffinityText = snap.AffinityText;
                item.Managed = snap.Managed;
                item.ManagedBy = snap.ManagedBy;
            }
            else
            {
                Processes.Add(new ProcessItem
                {
                    Pid = snap.Pid,
                    Name = snap.Name,
                    CpuPercent = snap.CpuPercent,
                    WorkingSetBytes = snap.WorkingSetBytes,
                    PriorityText = snap.PriorityText,
                    AffinityText = snap.AffinityText,
                    Managed = snap.Managed,
                    ManagedBy = snap.ManagedBy,
                });
            }
        }
        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Processes[i].Pid)) Processes.RemoveAt(i);
        }

        ManagedCount = Processes.Count(p => p.Managed);
    }

    partial void OnSearchTextChanged(string value) => RefreshProcesses();

    // ── Rules ────────────────────────────────────────────────────────────

    private void RefreshRules()
    {
        var rules = _service.Rules;
        for (int i = 0; i < Rules.Count; i++)
        {
            if (!rules.Any(r => r.Id == Rules[i].Id)) { Rules.RemoveAt(i); i--; }
        }
        foreach (var rule in rules)
        {
            var existing = Rules.FirstOrDefault(r => r.Id == rule.Id);
            if (existing == null)
            {
                Rules.Add(rule);
            }
            else
            {
                int index = Rules.IndexOf(existing);
                Rules[index] = rule;
            }
        }
    }

    private void RefreshEngineState()
    {
        var config = _service.Config;
        EngineEnabled = config.EngineEnabled;
        BoostActive = _service.BoostModeActive;
        AutoBalanceEnabled = config.AutoBalanceEnabled;
        AutoBalanceThreshold = config.AutoBalanceCpuPercentThreshold;
        AutoBalanceSustain = config.AutoBalanceSustainSeconds;
        AutoBalanceRecover = config.AutoBalanceRecoverSeconds;
        AutoBalanceExclusions = string.Join(", ", config.AutoBalanceExclusions);
        ForegroundBoostEnabled = config.ForegroundBoostEnabled;
    }

    private void RefreshActions()
    {
        Actions.Clear();
        foreach (var entry in _service.Actions.TakeLast(300).Reverse())
        {
            Actions.Add(entry);
        }
    }

    // ── Rule editor ──────────────────────────────────────────────────────

    /// <summary>Opens the rule editor for a new rule, optionally prefilled from a process.</summary>
    public void NewRule(ProcessItem? fromProcess = null)
    {
        EditingRule = new ProcessRule
        {
            Name = fromProcess != null ? $"Tune {fromProcess.Name}" : string.Empty,
            ProcessName = fromProcess?.Name ?? string.Empty,
            MatchMode = RuleMatchMode.Name,
        };
    }

    public void EditRule(ProcessRule rule)
    {
        EditingRule = CloneRule(rule);
    }

    /// <summary>Validates and persists the rule being edited. True when saved (the caller closes the editor).</summary>
    public async Task<bool> SaveRuleAsync()
    {
        var rule = EditingRule;
        if (rule == null) return false;
        if (string.IsNullOrWhiteSpace(rule.ProcessName))
        {
            StatusText = "Rule needs a process name, path, or command-line fragment.";
            return false;
        }

        // Safety rail: Realtime priority on anything system-critical requires explicit confirmation.
        if (rule.CpuPriority == CpuPriorityLevel.Realtime && _service.IsSystemCritical(rule.ProcessName))
        {
            bool ok = await ConfirmAsync("Realtime priority on a system process",
                $"'{rule.ProcessName}' looks like a system-critical process. Realtime priority can hang the system. Apply anyway?");
            if (!ok) return false;
        }

        bool isNew = !Rules.Any(r => r.Id == rule.Id);
        if (isNew) _service.AddRule(rule);
        else _service.UpdateRule(rule); // edits push live onto running processes — no restart needed
        EditingRule = null;
        StatusText = isNew
            ? $"Rule '{DisplayName(rule)}' created and applied to any running {rule.ProcessName}."
            : $"Rule '{DisplayName(rule)}' saved — new settings pushed to its running processes instantly."
            + " Use Restart on the Processes tab for a completely fresh instance.";
        return true;
    }

    public void CancelRuleEdit() => EditingRule = null;

    public void DeleteSelectedRule()
    {
        var rule = SelectedRule ?? EditingRule;
        if (rule == null) return;
        _service.DeleteRule(rule.Id);
        EditingRule = null;
        StatusText = $"Rule '{DisplayName(rule)}' deleted.";
    }

    /// <summary>Applies a Core Isolation preset to the rule being edited (sets its CPU-set pin).</summary>
    public string ApplyPresetToRule(CoreIsolationPreset preset)
    {
        var rule = EditingRule;
        if (rule == null) return string.Empty;
        var ids = _service.PresetCpuSetIds(preset);
        if (ids == null) return "This preset does not apply to the current CPU.";
        rule.CpuSetIds = ids;
        rule.AffinityMask = 0;
        StatusText = $"{preset}: rule pinned to {ids.Count} CPU set(s).";
        return string.Empty;
    }

    public void ClearRulePin()
    {
        if (EditingRule == null) return;
        EditingRule.CpuSetIds = new List<uint>();
        EditingRule.AffinityMask = 0;
    }

    /// <summary>Applies a preset immediately to the selected process.</summary>
    public string ApplyPresetToSelectedProcess(CoreIsolationPreset preset)
    {
        if (SelectedProcess == null) return "Select a process first.";
        if (!_service.ApplyPresetToProcess(preset, SelectedProcess.Pid))
            return "Preset could not be applied (does not apply to this CPU, or access denied).";
        return string.Empty;
    }

    private static string DisplayName(ProcessRule rule)
        => string.IsNullOrEmpty(rule.Name) ? rule.ProcessName : rule.Name;

    private static ProcessRule CloneRule(ProcessRule source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        ProcessName = source.ProcessName,
        MatchMode = source.MatchMode,
        InstanceIndex = source.InstanceIndex,
        CpuPriority = source.CpuPriority,
        IoPriority = source.IoPriority,
        MemoryPriority = source.MemoryPriority,
        CpuSetIds = source.CpuSetIds.ToList(),
        AffinityMask = source.AffinityMask,
        EnableCoreCap = source.EnableCoreCap,
        MaxCores = source.MaxCores,
        MaxCpuPercent = source.MaxCpuPercent,
        HardThrottle = source.HardThrottle,
        SpreadInstances = source.SpreadInstances,
        Blocklist = source.Blocklist,
        Revive = source.Revive,
        MaxInstances = source.MaxInstances,
        PreventSleep = source.PreventSleep,
        KeepRunning = source.KeepRunning,
        Enabled = source.Enabled,
    };

    // ── Process actions ──────────────────────────────────────────────────

    public async Task KillSelectedAsync()
    {
        var item = SelectedProcess;
        if (item == null) return;
        if (_service.IsSystemCritical(item.Name))
        {
            bool ok = await ConfirmAsync("Terminate system process?",
                $"'{item.Name}' is considered system-critical. Terminating it can crash Windows. Continue?");
            if (!ok) return;
        }
        if (_service.Kill(item.Pid, out var error))
        {
            StatusText = $"Terminated {item.Name} (pid {item.Pid}).";
        }
        else
        {
            StatusText = error ?? "Terminate failed.";
        }
    }

    public void RestoreSelected()
    {
        if (SelectedProcess == null) return;
        _service.RestoreProcess(SelectedProcess.Pid);
        StatusText = $"Restored {SelectedProcess.Name} to Windows defaults.";
    }

    public void RestoreAll()
    {
        int count = _service.RestoreAllManaged();
        StatusText = count == 0 ? "Nothing managed to restore." : $"Restored {count} managed process(es).";
    }

    public void ApplyRuleToSelected(string ruleId)
    {
        if (SelectedProcess == null) return;
        _service.ApplyRuleToProcess(ruleId, SelectedProcess.Pid);
        StatusText = $"Rule applied to {SelectedProcess.Name} — it now shows under rule control.";
    }

    public void AllowCloseSelected()
    {
        if (SelectedProcess == null) return;
        _service.SetKeepRunningOverride(SelectedProcess.Name);
        StatusText = $"Keep Running overridden for {SelectedProcess.Name} (10 minutes).";
    }

    /// <summary>Restarts the selected process (close + relaunch) so an edited rule starts from a clean slate.</summary>
    public async Task RestartSelectedAsync()
    {
        var item = SelectedProcess;
        if (item == null) return;
        StatusText = $"Restarting {item.Name}…";
        var (ok, message) = await _service.RestartProcessAsync(item.Pid);
        StatusText = message;
        if (!ok) await ConfirmAsync("Restart failed", message); // surface the reason, not just the status bar
    }

    // ── Engine settings ──────────────────────────────────────────────────

    public void ToggleEngine()
    {
        var config = _service.Config;
        config.EngineEnabled = EngineEnabled;
        _service.UpdateConfig(config);
        StatusText = EngineEnabled ? "Engine enabled — rules enforce in real time." : "Engine paused.";
    }

    public void ToggleBoost()
    {
        bool active = _service.ToggleBoostMode();
        BoostActive = active;
        StatusText = active ? "Boost Mode on: core parking off, max frequency forced." : "Boost Mode off: power settings restored.";
    }

    public void SaveAutoBalanceSettings()
    {
        var config = _service.Config;
        config.AutoBalanceEnabled = AutoBalanceEnabled;
        config.AutoBalanceCpuPercentThreshold = Math.Clamp(AutoBalanceThreshold, 10, 99);
        config.AutoBalanceSustainSeconds = Math.Max(3, AutoBalanceSustain);
        config.AutoBalanceRecoverSeconds = Math.Max(5, AutoBalanceRecover);
        config.AutoBalanceExclusions = AutoBalanceExclusions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        config.ForegroundBoostEnabled = ForegroundBoostEnabled;
        _service.UpdateConfig(config);
        RefreshEngineState();
        StatusText = "Engine settings saved.";
    }

    // ── Export / import ──────────────────────────────────────────────────

    public async Task ExportRulesAsync()
    {
        var file = await PickSaveFileAsync("kalos-rules.json");
        if (file == null) return;
        await System.IO.File.WriteAllTextAsync(file.Path, _service.ExportRulesJson());
        StatusText = $"Rules exported to {file.Path}.";
    }

    public async Task ImportRulesAsync()
    {
        var file = await PickOpenFileAsync();
        if (file == null) return;
        string json = await System.IO.File.ReadAllTextAsync(file.Path);
        var (ok, message) = _service.ImportRulesJson(json);
        StatusText = message;
    }

    public async Task ExportActionsAsync()
    {
        var file = await PickSaveFileAsync("kalos-action-log.json");
        if (file == null) return;
        await System.IO.File.WriteAllTextAsync(file.Path, _service.ExportActionsJson());
        StatusText = $"Action log exported to {file.Path}.";
    }

    private static async Task<Windows.Storage.StorageFile?> PickSaveFileAsync(string suggestedName)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            return await picker.PickSaveFileAsync();
        }
        catch { return null; }
    }

    private static async Task<Windows.Storage.StorageFile?> PickOpenFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            return await picker.PickSingleFileAsync();
        }
        catch { return null; }
    }

    // ── Monitor ──────────────────────────────────────────────────────────

    private void SampleMonitor()
    {
        if (_disposed) return;
        double total;
        double[]? cores = null;
        double disk = 0;

        var sample = _pdh?.Sample();
        if (sample is { } s)
        {
            cores = s.Cores ?? Array.Empty<double>();
            disk = s.DiskBytesPerSec / (1024.0 * 1024.0);
            total = cores.Length > 0 ? cores.Average() : 0;
        }
        else
        {
            total = ProcessControlNative.GetSystemCpuPercent();
        }

        var mem = ProcessControlNative.GetMemoryStatus();
        double used = mem.TotalMb > 0 ? mem.TotalMb - mem.AvailMb : 0;

        TotalCpu = Math.Round(total, 1);
        MemUsedMb = Math.Round(used, 0);
        DiskMbPerSec = Math.Round(disk, 1);
        OnPropertyChanged(nameof(TotalCpuText));
        OnPropertyChanged(nameof(MemText));
        OnPropertyChanged(nameof(DiskText));

        Samples.Add(new MonitorSample
        {
            Cores = cores ?? Array.Empty<double>(),
            TotalCpu = total,
            MemUsedMb = used,
            DiskMbPerSec = disk,
        });
        while (Samples.Count > 90) Samples.RemoveAt(0);
    }

    // ── Safety-rail confirmations ────────────────────────────────────────

    private static async Task<bool> ConfirmAsync(string title, string message)
    {
        try
        {
            var root = (App)App.Current;
            var xamlRoot = root.MainWindow?.Content?.XamlRoot;
            if (xamlRoot == null) return true;
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxWidth = 420 },
                PrimaryButtonText = "Yes",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };
            App.TrackDialog(dialog);
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _service.ProcessesChanged -= OnServiceProcessesChanged;
        _service.RulesChanged -= OnRulesChanged;
        _service.EngineStateChanged -= OnEngineStateChanged;
        _service.ActionLogged -= OnActionLogged;
        _monitorTimer?.Stop();
        _pdh?.Dispose();
        _pdh = null;
    }
}