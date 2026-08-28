using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Models;
using KalOS.Services;
using Windows.Storage.Pickers;

namespace KalOS.ViewModels;

/// <summary>One selectable mod row in the Windhawk page list.</summary>
public partial class WindhawkModItem : ObservableObject
{
    public WindhawkModItem(WindhawkModEntry entry)
    {
        _entry = entry;
    }

    private WindhawkModEntry _entry;

    /// <summary>
    /// The manifest entry. Reassignable so a reloaded manifest (e.g. after a
    /// version-pin bump) is reflected on existing rows instead of stale data.
    /// </summary>
    public WindhawkModEntry Entry
    {
        get => _entry;
        set
        {
            if (ReferenceEquals(_entry, value)) return;
            _entry = value;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Description));
        }
    }

    public string Id => Entry.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(Entry.DisplayName) ? Entry.Id : Entry.DisplayName;

    public string Description
    {
        get
        {
            string theme = string.IsNullOrWhiteSpace(Entry.Theme) ? string.Empty : $"Theme: {Entry.Theme}";
            return string.IsNullOrWhiteSpace(theme)
                ? Entry.Description
                : $"{Entry.Description}  ·  {theme}";
        }
    }

    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>Whether the mod has a registry entry (i.e. the engine knows it).</summary>
    [ObservableProperty]
    private bool _isDeployed;

    /// <summary>Whether the mod is registered AND enabled (Disabled != 1).</summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>Whether the mod's registry settings (Theme + extras) match the manifest.</summary>
    [ObservableProperty]
    private bool _hasRequiredSettings;

    /// <summary>Latest published version on mods.windhawk.net (empty until checked).</summary>
    [ObservableProperty]
    private string _latestVersion = string.Empty;

    /// <summary>True when the latest published version is newer than the manifest pin.</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    /// <summary>Button label that doubles as the "offer to bump" (e.g. "Update to v1.10").</summary>
    public string UpdateButtonText => HasUpdate ? $"Update to v{LatestVersion}" : "Update";

    public string StateText => !IsDeployed ? "Not installed" : IsEnabled ? "Installed & enabled" : "Installed (disabled)";

    partial void OnIsDeployedChanged(bool value) => OnPropertyChanged(nameof(StateText));

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StateText));

    partial void OnHasUpdateChanged(bool value) => OnPropertyChanged(nameof(UpdateButtonText));

    partial void OnLatestVersionChanged(string value) => OnPropertyChanged(nameof(UpdateButtonText));
}

/// <summary>
/// Drives the Windhawk page: installs Windhawk, deploys the selected mod set,
/// and backs up / restores the whole configuration. All long-running work runs
/// off the UI thread with <see cref="IProgress{T}"/> feedback and cancellation.
/// </summary>
public partial class WindhawkViewModel : ObservableObject
{
    private readonly WindhawkManagerService _service;
    private readonly LogService _log;

    private WindhawkModManifest _manifest = new();
    private CancellationTokenSource? _cts;
    private bool _updateCheckRunning;

    public ObservableCollection<WindhawkModItem> Mods { get; } = new();

    [ObservableProperty]
    private bool _isWindhawkInstalled;

    /// <summary>Badge text for the Windhawk install state ("Installed" / "Not installed").</summary>
    [ObservableProperty]
    private string _installedStateText = "Checking…";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Ready.";

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _deployButtonText = "Install Windhawk & selected mods";

    public int SelectedCount => Mods.Count(m => m.IsSelected);

    /// <summary>Selected mods that are fully in place — registered, enabled, and themed.</summary>
    public int DeployedSelectedCount => Mods.Count(m => m.IsSelected && m.IsDeployed && m.IsEnabled && m.HasRequiredSettings);

    /// <summary>True when every selected mod is already deployed, enabled, and themed.</summary>
    public bool AllSelectedDeployed => SelectedCount > 0 && DeployedSelectedCount >= SelectedCount;

    public WindhawkViewModel(WindhawkManagerService service, LogService log)
    {
        _service = service;
        _log = log;
    }

    /// <summary>Loads the manifest, refreshes installed/enabled state, and checks for mod updates.</summary>
    public async Task LoadAsync()
    {
        try
        {
            _manifest = await _service.LoadManifestAsync();
            RefreshState();
            _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            StatusError($"Could not load the mod manifest: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries mods.windhawk.net for each mod's latest version and marks rows
    /// with an available update. Never blocks the UI and never sets busy state;
    /// failures just leave the row without an update button.
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        if (_updateCheckRunning) return;
        _updateCheckRunning = true;
        try
        {
            foreach (var item in Mods)
            {
                string latest = await _service.GetLatestModVersionAsync(item.Id);
                item.LatestVersion = latest;
                item.HasUpdate = !string.IsNullOrWhiteSpace(latest) &&
                                 WindhawkManagerService.CompareVersions(latest, item.Entry.Version) > 0;
            }
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync("Windhawk", "UpdateCheck", ex.Message, isError: true);
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    /// <summary>Refreshes Windhawk installed state and each mod's installed/enabled badge.</summary>
    public void RefreshState()
    {
        IsWindhawkInstalled = _service.IsInstalled();
        InstalledStateText = IsWindhawkInstalled ? "Installed" : "Not installed";

        foreach (var entry in _manifest.Mods)
        {
            var item = Mods.FirstOrDefault(m => string.Equals(m.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                item = new WindhawkModItem(entry);
                item.PropertyChanged += OnModSelectionChanged;
                Mods.Add(item);
            }
            item.Entry = entry; // pick up manifest changes (e.g. a bumped version pin)

            // "Deployed" means the engine actually has a compiled library for
            // the mod (not just a registry entry); "enabled" adds Disabled=0;
            // "has settings" means the manifest's Theme/extras are in place.
            item.IsDeployed = _service.IsModDeployed(entry.Id);
            item.IsEnabled = _service.IsModReady(entry.Id);
            item.HasRequiredSettings = _service.HasRequiredSettings(entry);
        }

        // Drop manifest entries that are no longer present.
        for (int i = Mods.Count - 1; i >= 0; i--)
        {
            if (!_manifest.Mods.Any(m => string.Equals(m.Id, Mods[i].Id, StringComparison.OrdinalIgnoreCase)))
            {
                Mods[i].PropertyChanged -= OnModSelectionChanged;
                Mods.RemoveAt(i);
            }
        }

        UpdateDeployButton();
    }

    /// <summary>Drives the deploy button label off the detected deployment state.</summary>
    public void UpdateDeployButton()
    {
        DeployButtonText = !IsWindhawkInstalled
            ? "Install Windhawk & selected mods"
            : AllSelectedDeployed
                ? "Mods already installed"
                : "Install selected mods";
    }

    partial void OnIsWindhawkInstalledChanged(bool value) => UpdateDeployButton();

    /// <summary>Re-evaluates the deploy button when the user ticks/untickst a mod.</summary>
    private void OnModSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WindhawkModItem.IsSelected))
        {
            UpdateDeployButton();
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeployAsync()
    {
        if (IsBusy) return;

        var selectedMods = Mods.Where(m => m.IsSelected).ToList();
        if (selectedMods.Count == 0)
        {
            StatusError("Select at least one mod to install.");
            return;
        }

        // Already deployed, enabled, AND themed — nothing to do, and don't
        // touch the engine. Missing themes are NOT skipped (they need a deploy).
        if (selectedMods.All(m => m.IsDeployed && m.IsEnabled && m.HasRequiredSettings))
        {
            StatusText = "All selected mods are already deployed, enabled, and themed — nothing to do.";
            return;
        }

        var selected = selectedMods.Select(m => m.Entry).ToList();

        _cts = new CancellationTokenSource();
        SetBusy(true, "Starting…");
        try
        {
            IProgress<double> progress = new System.Progress<double>(value => ProgressValue = value);
            IProgress<string> status = new System.Progress<string>(value => StatusText = value);

            if (!_service.IsInstalled())
            {
                status.Report("Windhawk is not installed — installing it first…");
                await _service.InstallWindhawkAsync(progress, status, _cts.Token);
            }
            else
            {
                ProgressValue = 5;
            }

            status.Report("Deploying the selected mods…");
            var results = await _service.DeployModsAsync(selected, _manifest, progress, _cts.Token);

            RefreshState();
            int ok = results.Count(r => r.Success);
            HasError = ok < results.Count;
            StatusText = ok == results.Count
                ? $"All done! {ok}/{results.Count} mods deployed and verified."
                : $"{ok}/{results.Count} mods deployed — check the log for failures.";

            foreach (var result in results)
            {
                _ = _log.WriteAsync("Windhawk", "Deploy", result.Summary, isError: !result.Success);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusError($"Failed to deploy: {ex.Message}");
            _ = _log.WriteAsync("Windhawk", "Deploy", ex.Message, isError: true);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ShowProgress = false;
            NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BackupAsync()
    {
        if (IsBusy) return;

        if (!_service.IsInstalled())
        {
            StatusError("Windhawk must be installed before a backup can be created.");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"windhawk-backup-{DateTime.Now:yyyyMMdd-HHmmss}.whbackup",
        };
        picker.FileTypeChoices.Add("Windhawk backup", new[] { ".whbackup" }.ToList());
        Initialize(picker);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return; // user cancelled

        _cts = new CancellationTokenSource();
        SetBusy(true, "Creating backup…");
        try
        {
            IProgress<double> progress = new System.Progress<double>(value => ProgressValue = value);
            string path = await _service.BackupConfigAsync(file.Path, progress, _cts.Token);
            StatusText = $"Backup saved to {path}";
            _ = _log.WriteAsync("Windhawk", "Backup", path);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusError($"Backup failed: {ex.Message}");
            _ = _log.WriteAsync("Windhawk", "Backup", ex.Message, isError: true);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ShowProgress = false;
            NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RestoreAsync()
    {
        if (IsBusy) return;

        if (!_service.IsInstalled())
        {
            StatusError("Windhawk must be installed before a backup can be restored.");
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".whbackup");
        Initialize(picker);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return; // user cancelled

        _cts = new CancellationTokenSource();
        SetBusy(true, "Restoring from backup…");
        try
        {
            IProgress<double> progress = new System.Progress<double>(value => ProgressValue = value);
            await _service.RestoreConfigAsync(file.Path, progress, _cts.Token);
            RefreshState();
            StatusText = $"Configuration restored from {file.Path}";
            _ = _log.WriteAsync("Windhawk", "Restore", file.Path);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusError($"Restore failed: {ex.Message}");
            _ = _log.WriteAsync("Windhawk", "Restore", ex.Message, isError: true);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ShowProgress = false;
            NotifyCanExecuteChanged();
        }
    }

    /// <summary>Bumps a single mod to the latest published version.</summary>
    public async Task UpdateModAsync(WindhawkModItem item)
    {
        if (IsBusy) return;

        _cts = new CancellationTokenSource();
        SetBusy(true, $"Checking {item.DisplayName} for updates…");
        try
        {
            IProgress<double> progress = new System.Progress<double>(value => ProgressValue = value);
            IProgress<string> status = new System.Progress<string>(value => StatusText = value);

            var result = await _service.UpdateModAsync(item.Entry, progress, _cts.Token);
            if (result.Success && !string.IsNullOrWhiteSpace(item.LatestVersion))
            {
                item.Entry.Version = item.LatestVersion;
                item.HasUpdate = false;
                _service.PersistVersionPin(item.Id, item.LatestVersion);
            }

            RefreshState();
            HasError = !result.Success;
            StatusText = result.Detail;
            _ = _log.WriteAsync("Windhawk", "Update", result.Summary, isError: !result.Success);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusError($"Update failed: {ex.Message}");
            _ = _log.WriteAsync("Windhawk", "Update", ex.Message, isError: true);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ShowProgress = false;
            NotifyCanExecuteChanged();
        }
    }

    /// <summary>Removes a single mod (registry entry, source, and compiled library).</summary>
    public async Task UninstallModAsync(WindhawkModItem item)
    {
        if (IsBusy) return;

        _cts = new CancellationTokenSource();
        SetBusy(true, $"Uninstalling {item.DisplayName}…");
        try
        {
            IProgress<double> progress = new System.Progress<double>(value => ProgressValue = value);

            var result = await _service.UninstallModAsync(item.Id, _cts.Token);
            RefreshState();
            HasError = !result.Success;
            StatusText = result.Detail;
            _ = _log.WriteAsync("Windhawk", "Uninstall", result.Summary, isError: !result.Success);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusError($"Uninstall failed: {ex.Message}");
            _ = _log.WriteAsync("Windhawk", "Uninstall", ex.Message, isError: true);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ShowProgress = false;
            NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        HasError = false;
        ShowProgress = busy;
        ProgressValue = 0;
        StatusText = status;
        NotifyCanExecuteChanged();
    }

    private void NotifyCanExecuteChanged()
    {
        DeployCommand.NotifyCanExecuteChanged();
        BackupCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private void StatusError(string message)
    {
        HasError = true;
        StatusText = message;
    }

    private static void Initialize(object picker)
    {
        if (App.Current is App { MainWindow: { } window })
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
    }
}
