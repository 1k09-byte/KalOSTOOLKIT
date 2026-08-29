using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Models.Bios;
using KalOS.Services;
using KalOS.Services.Bios;

namespace KalOS.ViewModels;

public partial class BiosViewModel : ObservableObject
{
    private readonly BiosProviderFactory _factory;
    private readonly ScewinService _scewin;
    private readonly BiosUpdateService _updateService;
    private readonly LoggingService _log;

    private IBiosProvider? _provider;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _updateCts;
    private IReadOnlyList<BiosSetting>? _allSettings;
    private BiosSystemInfo? _currentSystemInfo;

    public BiosViewModel(BiosProviderFactory factory, ScewinService scewin, BiosUpdateService updateService, LoggingService log)
    {
        _factory = factory;
        _scewin = scewin;
        _updateService = updateService;
        _log = log;

        _scewinPath = _scewin.BinaryPath ?? "";
    }

    // ── Collections ────────────────────────────────────────────────────

    public ObservableCollection<BiosSettingViewModel> Settings { get; } = new();
    public ObservableCollection<BiosSettingViewModel> FilteredSettings { get; } = new();
    public ObservableCollection<BiosDiffItem> PendingChanges { get; } = new();
    public ObservableCollection<string> Backups { get; } = new();

    // ── Observable state ───────────────────────────────────────────────

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _vendorStatus = "No BIOS backend detected — SCEWIN_64.exe was not found";
    [ObservableProperty] private string _hardwareLine = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _biosLocked;
    [ObservableProperty] private string _lockMessage = "";
    [ObservableProperty] private string _scewinPath;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showChangesOnly;

    // ── BIOS Version & Update State ─────────────────────────────────────

    [ObservableProperty] private string _biosVersion = "Unknown";
    [ObservableProperty] private string _biosReleaseDate = "Unknown";
    [ObservableProperty] private string _motherboard = "Unknown";
    [ObservableProperty] private string _firmwareVendor = "Unknown";
    [ObservableProperty] private string _smbiosVersion = "Unknown";
    [ObservableProperty] private BiosUpdateStatus _biosUpdateStatus = BiosUpdateStatus.Unknown;
    [ObservableProperty] private string _biosUpdateStatusText = "Click 'Check for updates' to verify.";
    [ObservableProperty] private string _biosLatestVersion = "";
    [ObservableProperty] private bool _isCheckingBiosUpdate;
    [ObservableProperty] private string _biosVersionDescription = "Loading firmware details…";
    [ObservableProperty] private string _biosUpdateNotes = "";

    public bool IsConfigured => _scewin.IsBinaryConfigured;
    public bool IsElevated => _scewin.IsElevated;
    public bool RefreshEnabled => !IsBusy && IsConfigured;
    public bool ApplyEnabled => !IsBusy && PendingChanges.Count > 0;
    public int SettingsCount => FilteredSettings.Count;
    public int TotalCount => Settings.Count;
    public bool HasPendingChanges => PendingChanges.Count > 0;

    public bool IsBiosUpToDate => BiosUpdateStatus == BiosUpdateStatus.UpToDate;
    public bool HasBiosUpdate => BiosUpdateStatus == BiosUpdateStatus.UpdateAvailable;
    public bool CanCheckBiosUpdate => !IsCheckingBiosUpdate;

    public string BiosButtonGlyph => BiosUpdateStatus switch
    {
        BiosUpdateStatus.UpToDate => "\uE73E",       // Checkmark
        BiosUpdateStatus.UpdateAvailable => "\uE896", // Download / Arrow
        BiosUpdateStatus.Error => "\uE783",           // Warning triangle
        _ => "\uE895",                                // Sync / Check
    };

    public string BiosButtonText => IsCheckingBiosUpdate ? "Checking…" : BiosUpdateStatus switch
    {
        BiosUpdateStatus.UpToDate => "Up to date",
        BiosUpdateStatus.UpdateAvailable => $"Update available: {BiosLatestVersion}",
        BiosUpdateStatus.Error => "Check failed · Retry",
        _ => "Check for updates",
    };

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(RefreshEnabled));
        OnPropertyChanged(nameof(ApplyEnabled));
    }

    partial void OnIsCheckingBiosUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckBiosUpdate));
        OnPropertyChanged(nameof(BiosButtonText));
    }

    partial void OnBiosUpdateStatusChanged(BiosUpdateStatus value)
    {
        OnPropertyChanged(nameof(IsBiosUpToDate));
        OnPropertyChanged(nameof(HasBiosUpdate));
        OnPropertyChanged(nameof(BiosButtonGlyph));
        OnPropertyChanged(nameof(BiosButtonText));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowChangesOnlyChanged(bool value) => ApplyFilter();

    partial void OnScewinPathChanged(string value)
    {
        _scewin.BinaryPath = value;
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(RefreshEnabled));

        if (IsConfigured)
        {
            var missing = _scewin.MissingDriverFiles();
            if (missing.Count > 0)
                SetError($"Missing driver files next to SCEWIN: {string.Join(", ", missing)}");
            else
                ClearError();
        }
    }

    // ── Commands ───────────────────────────────────────────────────────

    [RelayCommand]
    public async Task CheckBiosUpdateAsync()
    {
        if (IsCheckingBiosUpdate) return;

        _updateCts?.Dispose();
        var cts = _updateCts = new CancellationTokenSource();
        var ct = cts.Token;

        IsCheckingBiosUpdate = true;
        BiosUpdateStatus = BiosUpdateStatus.Checking;
        BiosUpdateStatusText = "Checking for BIOS updates…";

        try
        {
            var systemInfo = _currentSystemInfo ?? await _factory.GetSystemInfoAsync();
            _currentSystemInfo = systemInfo;

            UpdateSystemInfoProperties(systemInfo);

            var result = await _updateService.CheckBiosVersionAsync(systemInfo, ct);
            BiosUpdateStatus = result.Status;
            BiosLatestVersion = result.LatestVersion ?? systemInfo.BiosVersion;
            BiosUpdateStatusText = result.StatusMessage ?? "Check complete.";
            BiosUpdateNotes = result.Notes ?? "";

            _log.Info($"BIOS update check completed: Status={BiosUpdateStatus}, Installed={systemInfo.BiosVersion}, Latest={BiosLatestVersion}");
        }
        catch (OperationCanceledException)
        {
            BiosUpdateStatusText = "Check cancelled.";
            BiosUpdateStatus = BiosUpdateStatus.Unknown;
        }
        catch (Exception ex)
        {
            _log.Warn($"BIOS update check failed: {ex.Message}");
            BiosUpdateStatus = BiosUpdateStatus.Error;
            BiosUpdateStatusText = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingBiosUpdate = false;
        }
    }

    public async Task InitializeSystemInfoAsync()
    {
        try
        {
            var systemInfo = await _factory.GetSystemInfoAsync();
            _currentSystemInfo = systemInfo;
            UpdateSystemInfoProperties(systemInfo);
            await CheckBiosUpdateAsync();
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to initialize BIOS system info: {ex.Message}");
        }
    }

    private void UpdateSystemInfoProperties(BiosSystemInfo systemInfo)
    {
        HardwareLine = $"{systemInfo.Manufacturer} {systemInfo.Model} · BIOS {systemInfo.BiosVersion}";
        BiosVersion = systemInfo.BiosVersion;
        BiosReleaseDate = systemInfo.BiosReleaseDate;
        Motherboard = $"{systemInfo.BaseBoardManufacturer} {systemInfo.BaseBoardProduct}".Trim();
        FirmwareVendor = systemInfo.FirmwareVendor;
        SmbiosVersion = systemInfo.SmbiosVersion;
        BiosVersionDescription = $"Installed: {systemInfo.BiosVersion} (Released: {systemInfo.BiosReleaseDate}) · Motherboard: {Motherboard}";
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        IsBusy = true;
        ClearError();
        StatusMessage = "Exporting BIOS settings via SCEWIN...";
        try
        {
            var systemInfo = await _factory.GetSystemInfoAsync();
            _currentSystemInfo = systemInfo;
            UpdateSystemInfoProperties(systemInfo);
            _ = CheckBiosUpdateAsync();

            _provider ??= await _factory.CreateAsync();
            VendorStatus = _provider.DisplayName;

            var settings = await _provider.GetSettingsAsync(ct);
            _allSettings = settings;

            // An empty export is not a valid BIOS result. Treat it as an
            // explicit failure instead of presenting a blank page as success.
            ClearLock();
            if (settings.Count == 0)
                throw new InvalidOperationException("SCEWIN returned no recognizable BIOS settings. Check the SCEWIN version and its companion files.");

            Settings.Clear();
            FilteredSettings.Clear();
            PendingChanges.Clear();

            foreach (var s in settings.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var vm = new BiosSettingViewModel(s);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(BiosSettingViewModel.IsDirty))
                    {
                        RefreshPendingChanges();
                        if (ShowChangesOnly)
                            ApplyFilter();
                    }
                };
                Settings.Add(vm);
            }

            ApplyFilter();

            StatusMessage = $"Loaded {Settings.Count} BIOS settings.";
            OnPropertyChanged(nameof(SettingsCount));
            OnPropertyChanged(nameof(TotalCount));
            _log.Success($"Parsed {Settings.Count} BIOS settings from SCEWIN export.");

            RefreshBackups();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Export cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"SCEWIN export failed: {ex}");
            if (LooksLikeLockError(ex.Message))
                SetLocked(ex.Message);
            SetError($"BIOS export failed: {ex.Message}");
            StatusMessage = "Export failed — no BIOS settings were loaded.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ApplyAsync()
    {
        if (_provider is null || PendingChanges.Count == 0) return;

        IsBusy = true;
        StatusMessage = "Applying changes via SCEWIN...";
        ClearError();

        try
        {
            var changes = PendingChanges
                .Where(d => d.IsIncluded)
                .Select(d => new BiosSettingChange(d.Name, d.ProposedValue))
                .ToList();

            if (changes.Count == 0)
            {
                StatusMessage = "No changes selected to apply.";
                return;
            }

            var result = await _provider.ApplySettingsAsync(changes, null);

            if (result.Success)
            {
                StatusMessage = $"Successfully applied {changes.Count} settings. A reboot may be required.";
                foreach (var s in Settings.Where(s => s.IsDirty)) s.MarkApplied();
                PendingChanges.Clear();
                OnPropertyChanged(nameof(HasPendingChanges));
                OnPropertyChanged(nameof(ApplyEnabled));
                RefreshBackups();
                ApplyFilter();
            }
            else
            {
                SetError(string.Join("\n", result.Errors));
            }
        }
        catch (Exception ex)
        {
            SetError($"Apply failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ResetAll()
    {
        foreach (var s in Settings.Where(s => s.IsDirty))
            s.ResetToFirmware();
        PendingChanges.Clear();
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(ApplyEnabled));
        ApplyFilter();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        FilteredSettings.Clear();
        var query = SearchText?.Trim() ?? "";
        foreach (var s in Settings)
        {
            if (ShowChangesOnly && !s.IsDirty)
                continue;

            if (string.IsNullOrEmpty(query) ||
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (s.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            {
                FilteredSettings.Add(s);
            }
        }
        OnPropertyChanged(nameof(SettingsCount));
    }

    private void RefreshPendingChanges()
    {
        PendingChanges.Clear();
        foreach (var s in Settings.Where(s => s.IsDirty))
        {
            PendingChanges.Add(new BiosDiffItem(
                s.Name,
                s.CurrentValue,
                s.OutputValue,
                s.IsEditable,
                s.IsSensitive));
        }
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(ApplyEnabled));
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var f in _scewin.GetBackupFiles().OrderByDescending(f => f))
            Backups.Add(Path.GetFileName(f));
    }

    private void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
    }

    private void ClearError()
    {
        HasError = false;
        ErrorMessage = "";
    }

    // ── Lock detection helpers ─────────────────────────────────────────

    private void SetLocked(string? message = null)
    {
        BiosLocked = true;
        LockMessage = message ?? "The BIOS appears to be locked. A supervisor/power-on password may be required to read or change firmware settings.";
        IsBusy = false;
    }

    private void ClearLock()
    {
        BiosLocked = false;
        LockMessage = "";
    }

    private static bool LooksLikeLockError(string message)
    {
        var upper = message.ToUpperInvariant();
        return upper.Contains("BIOS MAY BE LOCKED") ||
               upper.Contains("LOCKED") ||
               upper.Contains("SUPERVISOR PASSWORD") ||
               upper.Contains("ACCESS DENIED") ||
               upper.Contains("HII DATABASE");
    }
}