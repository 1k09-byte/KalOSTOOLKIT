using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Services;

namespace KalOS.ViewModels;

public partial class SystemOverviewViewModel : ObservableObject
{
    private readonly HardwareMonitorService _hardwareMonitor;
    private readonly LoggingService _log;
    private readonly SystemRefreshService _refreshService;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    public IReadOnlyList<RefreshRateOption> RefreshRates => SystemRefreshService.RefreshRates;

    public ObservableCollection<SystemSensorReading> Readings { get; } = new();
    public ObservableCollection<OverviewMetric> Metrics { get; } = new();
    public ObservableCollection<OverviewDetail> Details { get; } = new();
    public OverviewDetail CpuTemperatureDetail { get; private set; } = OverviewDetail.Unavailable("CPU temperature");
    public OverviewDetail GpuTemperatureDetail { get; private set; } = OverviewDetail.Unavailable("GPU temperature");
    public OverviewDetail RamUsedDetail { get; private set; } = OverviewDetail.Unavailable("RAM used");
    public OverviewDetail DiskUsageDetail { get; private set; } = OverviewDetail.Unavailable("Disk usage");
    public OverviewDetail GpuFanDetail { get; private set; } = OverviewDetail.Unavailable("GPU fan");
    public OverviewDetail RamTemperatureDetail { get; private set; } = OverviewDetail.Unavailable("RAM temperature");

    public OverviewMetric CpuMetric { get; private set; } = OverviewMetric.Empty("CPU usage", "Overall processor activity");
    public OverviewMetric GpuMetric { get; private set; } = OverviewMetric.Empty("GPU usage", "Graphics processor activity");
    public OverviewMetric RamMetric { get; private set; } = OverviewMetric.Empty("RAM usage", "Memory currently in use");

    [ObservableProperty]
    private RefreshRateOption _selectedRefreshRate;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installationProgress;

    [ObservableProperty]
    private bool _installationProgressIndeterminate;

    [ObservableProperty]
    private string _installationProgressText = string.Empty;

    [ObservableProperty]
    private string _statusText = "LibreHardwareMonitor is required to read system details.";

    [ObservableProperty]
    private string? _errorText;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    [ObservableProperty]
    private int _hardwareCount;

    [ObservableProperty]
    private int _sensorCount;

    public bool IsBusy => IsChecking || IsInstalling;
    public bool CanScan => IsInstalled && !IsBusy;
    public bool CanUninstall => IsInstalled && !IsInstalling;
    public int RefreshIntervalSeconds => SelectedRefreshRate.Seconds;

    // Only the very first scan shows the skeleton — refresh loops keep the dashboard visible
    public bool IsInitialLoading => IsInstalled && IsChecking && SensorCount == 0;
    public bool IsDashboardReady => IsInstalled && !IsInitialLoading;

    public SystemOverviewViewModel(HardwareMonitorService hardwareMonitor, LoggingService log, SystemRefreshService refreshService)
    {
        _hardwareMonitor = hardwareMonitor;
        _log = log;
        _refreshService = refreshService;
        _selectedRefreshRate = _refreshService.SelectedRate;
        _refreshService.RefreshRateChanged += OnSharedRefreshRateChanged;
    }

    private void OnSharedRefreshRateChanged(object? sender, EventArgs e)
    {
        SelectedRefreshRate = _refreshService.SelectedRate;
    }

    public void LoadDiskOnly()
    {
        IsInstalled = true;
        ErrorText = null;
        SetDetails(
            OverviewDetail.Unavailable("CPU temperature"),
            OverviewDetail.Unavailable("GPU temperature"),
            OverviewDetail.Unavailable("RAM used"),
            CreateDiskDetail(),
            OverviewDetail.Unavailable("GPU fan"),
            OverviewDetail.Unavailable("RAM temperature"));
        OnPropertyChanged(nameof(CpuMetric));
        OnPropertyChanged(nameof(GpuMetric));
        OnPropertyChanged(nameof(RamMetric));
        StartSafeLiveUpdates();
    }

    private void StartSafeLiveUpdates()
    {
        StopLiveUpdates();
        _liveCts = new CancellationTokenSource();
        _liveTask = SafeRefreshLoopAsync(_liveCts.Token);
    }

    private async Task SafeRefreshLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(RefreshIntervalSeconds, 1, 30)), token);
                var disk = HardwareMonitorService.GetDiskUsage();
                DiskUsageDetail = new OverviewDetail("Disk usage", disk.UsedText, "C: drive")
                {
                    SecondaryValue = disk.TotalText,
                    Progress = disk.UsagePercent
                };
                OnPropertyChanged(nameof(DiskUsageDetail));
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Checks for the prerequisite and scans immediately when it is present.</summary>
    public async Task LoadAsync()
    {
        if (IsBusy) return;

        IsChecking = true;
        ErrorText = null;
        StatusText = "Checking for LibreHardwareMonitor...";

        try
        {
            IsInstalled = await _hardwareMonitor.IsInstalledAsync();
            if (IsInstalled)
            {
                await ScanAsync(CancellationToken.None);
            }
            else
            {
                Details.Clear();
                Details.Add(OverviewDetail.Unavailable("CPU temperature"));
                Details.Add(OverviewDetail.Unavailable("GPU temperature"));
                Details.Add(OverviewDetail.Unavailable("RAM used"));
                Details.Add(CreateDiskDetail());
                Details.Add(OverviewDetail.Unavailable("GPU fan"));
                Details.Add(OverviewDetail.Unavailable("RAM temperature"));
                OnPropertyChanged(nameof(CpuTemperatureDetail));
                OnPropertyChanged(nameof(GpuTemperatureDetail));
                OnPropertyChanged(nameof(RamUsedDetail));
                OnPropertyChanged(nameof(DiskUsageDetail));
                OnPropertyChanged(nameof(GpuFanDetail));
                OnPropertyChanged(nameof(RamTemperatureDetail));
                StatusText = "LibreHardwareMonitor is required before system details can be scanned.";
                ErrorText = null;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"System overview prerequisite check failed: {ex.Message}");
            ErrorText = ex.Message;
            StatusText = "Could not check the hardware monitor prerequisite.";
        }
        finally
        {
            IsChecking = false;
        }
    }

    public async Task InstallAndScanAsync()
    {
        if (IsBusy) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsInstalling = true;
        ErrorText = null;
        StatusText = "Installing LibreHardwareMonitor via winget...";
        InstallationProgress = 0;
        InstallationProgressIndeterminate = true;
        InstallationProgressText = "Preparing installation...";

        var progress = new Progress<HardwareInstallProgress>(update =>
        {
            InstallationProgress = update.Percent;
            InstallationProgressIndeterminate = update.IsIndeterminate;
            InstallationProgressText = update.Message;
            StatusText = update.Message;
        });

        try
        {
            IsInstalled = await _hardwareMonitor.InstallAsync(_cts.Token, progress);
            if (!IsInstalled)
            {
                InstallationProgressIndeterminate = false;
                StatusText = "LibreHardwareMonitor could not be installed.";
                ErrorText = "winget did not confirm the installation. Check that winget is available and try again.";
                return;
            }

            InstallationProgress = 100;
            InstallationProgressIndeterminate = false;
            InstallationProgressText = "Installation complete. Reading system details...";
            StatusText = InstallationProgressText;
            IsInstalling = false;
            await ScanAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Installation cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"LibreHardwareMonitor installation failed: {ex.Message}");
            ErrorText = ex.Message;
            StatusText = "Installation failed.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    public async Task UninstallAsync()
    {
        if (IsBusy) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsInstalling = true;
        ErrorText = null;
        StatusText = "Uninstalling LibreHardwareMonitor via winget...";
        InstallationProgress = 0;
        InstallationProgressIndeterminate = true;
        InstallationProgressText = "Preparing uninstallation...";

        var progress = new Progress<HardwareInstallProgress>(update =>
        {
            InstallationProgress = update.Percent;
            InstallationProgressIndeterminate = update.IsIndeterminate;
            InstallationProgressText = update.Message;
            StatusText = update.Message;
        });

        try
        {
            bool success = await _hardwareMonitor.UninstallAsync(_cts.Token, progress);
            if (!success)
            {
                StatusText = "Uninstall could not be verified.";
                ErrorText = "winget did not confirm the uninstallation. It may have been installed manually — check Programs & Features or remove the executable manually.";
                return;
            }

            IsInstalled = false;
            Readings.Clear();
            Details.Clear();
            Details.Add(OverviewDetail.Unavailable("CPU temperature"));
            Details.Add(OverviewDetail.Unavailable("GPU temperature"));
            Details.Add(OverviewDetail.Unavailable("RAM used"));
            Details.Add(CreateDiskDetail());
            Details.Add(OverviewDetail.Unavailable("GPU fan"));
            Details.Add(OverviewDetail.Unavailable("RAM temperature"));
            OnPropertyChanged(nameof(CpuTemperatureDetail));
            OnPropertyChanged(nameof(GpuTemperatureDetail));
            OnPropertyChanged(nameof(RamUsedDetail));
            OnPropertyChanged(nameof(DiskUsageDetail));
            OnPropertyChanged(nameof(GpuFanDetail));
            OnPropertyChanged(nameof(RamTemperatureDetail));
            StopLiveUpdates();
            InstallationProgress = 100;
            InstallationProgressIndeterminate = false;
            InstallationProgressText = "Uninstalled. System overview will show limited data.";
            StatusText = InstallationProgressText;
            ErrorText = null;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Uninstall cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"Uninstall failed: {ex.Message}");
            ErrorText = ex.Message;
            StatusText = "Uninstall failed.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsBusy) return;
        await ScanAsync(CancellationToken.None);
    }

    /// <summary>Starts refreshing the dashboard at the selected rate while the page is visible.</summary>
    public void StartLiveUpdates()
    {
        if (_liveTask is { IsCompleted: false }) return;

        _liveCts?.Dispose();
        _liveCts = new CancellationTokenSource();
        _liveTask = RefreshLoopAsync(_liveCts.Token);
    }

    /// <summary>Stops live refresh when the user leaves the page.</summary>
    public void StopLiveUpdates()
    {
        _liveCts?.Cancel();
        _liveCts?.Dispose();
        _liveCts = null;
        _liveTask = null;
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(RefreshIntervalSeconds, 1, 30)), cancellationToken);
                if (IsInstalled && !IsBusy)
                {
                    await ScanAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the page is left.
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled || (IsInstalling && !cancellationToken.CanBeCanceled)) return;
        if (!await _scanLock.WaitAsync(0, cancellationToken)) return;

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;
        IsChecking = true;
        ErrorText = null;
        StatusText = "Scanning CPU, GPU, memory, storage, and sensors...";

        try
        {
            HardwareScanResult result = await _hardwareMonitor.ScanAsync(ct);
            Readings.Clear();
            foreach (var reading in result.Readings.OrderBy(r => r.Category).ThenBy(r => r.Hardware).ThenBy(r => r.Sensor))
            {
                Readings.Add(reading);
            }
            UpdateDashboard(result.Readings);

            HardwareCount = result.HardwareCount;
            SensorCount = result.Readings.Count;
            StatusText = result.Readings.Count == 0
                ? "The monitor opened, but no readable sensors were returned."
                : $"Updated {result.Readings.Count} key metrics from {result.HardwareCount} hardware devices.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"System overview scan failed: {ex.Message}");
            ErrorText = ex.Message;
            StatusText = "System scan failed.";
        }
        finally
        {
            IsChecking = false;
            _scanLock.Release();
        }
    }

    private void UpdateDashboard(IReadOnlyList<SystemSensorReading> readings)
    {
        Metrics.Clear();
        CpuMetric = CreateMetric(readings, "CPU", "Load", "CPU usage", "Overall processor activity", "%");
        GpuMetric = CreateMetric(readings, "GPU", "Load", "GPU usage", "Graphics processor activity", "%");
        RamMetric = CreateMetric(readings, "Memory", "Load", "RAM usage", "Memory currently in use", "%");
        Metrics.Add(CpuMetric);
        Metrics.Add(GpuMetric);
        Metrics.Add(RamMetric);
        OnPropertyChanged(nameof(CpuMetric));
        OnPropertyChanged(nameof(GpuMetric));
        OnPropertyChanged(nameof(RamMetric));

        SetDetails(
            CreateDetail(readings, "CPU", "Temperature", "CPU temperature"),
            CreateDetail(readings, "GPU", "Temperature", "GPU temperature"),
            CreateRamUsedDetail(readings),
            CreateDiskDetail(),
            CreateGpuFanDetail(readings),
            CreateRamTemperatureDetail(readings));
    }

    private static OverviewMetric CreateMetric(
        IReadOnlyList<SystemSensorReading> readings,
        string category,
        string sensorType,
        string label,
        string description,
        string unit)
    {
        var reading = FindReading(readings, category, sensorType);
        return new OverviewMetric(
            label,
            description,
            reading?.NumericValue ?? double.NaN,
            reading?.NumericValue ?? 0,
            unit,
            reading?.Hardware ?? "No reading available");
    }

    private void SetDetails(params OverviewDetail[] details)
    {
        CpuTemperatureDetail = details[0];
        GpuTemperatureDetail = details[1];
        RamUsedDetail = details[2];
        DiskUsageDetail = details[3];
        GpuFanDetail = details[4];
        RamTemperatureDetail = details[5];
        Details.Clear();
        foreach (var detail in details) Details.Add(detail);
        OnPropertyChanged(nameof(CpuTemperatureDetail));
        OnPropertyChanged(nameof(GpuTemperatureDetail));
        OnPropertyChanged(nameof(RamUsedDetail));
        OnPropertyChanged(nameof(DiskUsageDetail));
        OnPropertyChanged(nameof(GpuFanDetail));
        OnPropertyChanged(nameof(RamTemperatureDetail));
    }

    private static OverviewDetail CreateRamUsedDetail(IReadOnlyList<SystemSensorReading> readings)
    {
        var memory = readings.Where(r => r.Category.Equals("Memory", StringComparison.OrdinalIgnoreCase) && r.SensorType.Equals("Data", StringComparison.OrdinalIgnoreCase)).ToList();
        var used = memory.Where(r => r.Sensor.Contains("Used", StringComparison.OrdinalIgnoreCase) && !r.Sensor.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Sensor.Contains("Memory", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        var total = memory.Where(r => r.Sensor.Contains("Total", StringComparison.OrdinalIgnoreCase) && !r.Sensor.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Sensor.Contains("Memory", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (used == null || total == null) return new OverviewDetail("RAM used", "N/A", "Unavailable");
        return new OverviewDetail("RAM used", $"{used.NumericValue:0.##} GB of {total.NumericValue:0.##} GB", used.Hardware);
    }

    private static OverviewDetail CreateRamTemperatureDetail(IReadOnlyList<SystemSensorReading> readings)
    {
        var ram = readings.Where(r => r.Category.Equals("Memory", StringComparison.OrdinalIgnoreCase) && r.SensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase) && r.NumericValue >= 0 && r.NumericValue < 100 && !r.Sensor.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        return new OverviewDetail("RAM temperature", ram == null ? "N/A" : $"{ram.NumericValue:0.##} °C", ram?.Hardware ?? "Unavailable");
    }

    private static OverviewDetail CreateGpuFanDetail(IReadOnlyList<SystemSensorReading> readings)
    {
        var fan = FindReading(readings, "GPU", "Fan");
        if (fan == null) return OverviewDetail.Unavailable("GPU fan");
        // 0 RPM often means fan-stop at idle (RTX 40 series) — show as Idle rather than 0
        if (fan.NumericValue == 0) return new OverviewDetail("GPU fan", "Idle", fan.Hardware);
        return new OverviewDetail("GPU fan", fan.Value, fan.Hardware);
    }

    private static OverviewDetail CreateDiskDetail()
    {
        var disk = HardwareMonitorService.GetDiskUsage();
        return new OverviewDetail("Disk usage", disk.UsedText, "All available drives")
        {
            SecondaryValue = disk.TotalText,
            Progress = disk.UsagePercent
        };
    }

    private static OverviewDetail CreateDetail(
        IReadOnlyList<SystemSensorReading> readings,
        string category,
        string sensorType,
        string label,
        string? nameHint = null)
    {
        var reading = FindReading(readings, category, sensorType, nameHint);
        if (string.Equals(category, "Memory", StringComparison.OrdinalIgnoreCase) && string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
        {
            reading = readings.Where(r => r.Category.Equals("Memory", StringComparison.OrdinalIgnoreCase)
                                      && r.SensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.NumericValue < 100)
                .FirstOrDefault();
        }
        if (reading == null && string.Equals(sensorType, "Data", StringComparison.OrdinalIgnoreCase))
        {
            // Some LHM GPU backends report VRAM as SmallData rather than Data.
            reading = FindReading(readings, category, "SmallData", nameHint);
        }
        return new OverviewDetail(label, reading?.Value ?? "N/A", reading?.Hardware ?? "Unavailable");
    }

    private static SystemSensorReading? FindReading(
        IEnumerable<SystemSensorReading> readings,
        string category,
        string sensorType,
        string? nameHint = null)
    {
        var matches = readings.Where(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(r.SensorType, sensorType, StringComparison.OrdinalIgnoreCase));
        return matches
            .OrderByDescending(r => !string.IsNullOrWhiteSpace(nameHint)
                && r.Sensor.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.Sensor.Contains("Used", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.Sensor.Contains("Total", StringComparison.OrdinalIgnoreCase)
                || r.Sensor.Contains("Package", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    partial void OnSelectedRefreshRateChanged(RefreshRateOption value)
    {
        OnPropertyChanged(nameof(RefreshIntervalSeconds));

        // Restart the delay immediately so a newly selected rate takes effect
        // without waiting for the previous, longer interval to expire.
        if (!ReferenceEquals(_refreshService.SelectedRate, value))
        {
            _refreshService.SetRate(value);
        }
        if (_liveCts != null)
        {
            _liveCts.Cancel();
            _liveCts.Dispose();
            _liveCts = new CancellationTokenSource();
            _liveTask = RefreshLoopAsync(_liveCts.Token);
        }
    }

    partial void OnErrorTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(IsDashboardReady));
    }

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(IsDashboardReady));
    }

    partial void OnSensorCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(IsDashboardReady));
    }

    partial void OnHardwareCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(IsDashboardReady));
    }

    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanUninstall));
    }

}

public sealed record OverviewMetric(
    string Label,
    string Description,
    double Value,
    double Progress,
    string Unit,
    string Hardware)
{
    public static OverviewMetric Empty(string label, string description) =>
        new(label, description, double.NaN, 0, "%", "No reading available");
}

public sealed record OverviewDetail(
    string Label,
    string Value,
    string Hardware)
{
    public static OverviewDetail Unavailable(string label) => new(label, "N/A", "Unavailable");
    public string SecondaryValue { get; init; } = string.Empty;
    public double Progress { get; init; }
    public bool IsAvailable => Value != "N/A";
}
