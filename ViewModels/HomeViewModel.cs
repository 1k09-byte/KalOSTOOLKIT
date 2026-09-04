using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using KalOS.Services;
using Microsoft.UI.Dispatching;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KalOS.ViewModels
{
    public class RestorePointItem
    {
        public string Description { get; set; } = string.Empty;
        public string CreationTime { get; set; } = string.Empty;
        public uint SequenceNumber { get; set; }
    }

    public partial class HomeViewModel : ObservableObject
    {
        private readonly LoggingService _logging;
        private readonly HardwareMonitorService _hardwareMonitor;
        private readonly ProcessControlService _processControl;

        [ObservableProperty]
        private string _restorePointStatus = string.Empty;

        [ObservableProperty]
        private bool _isLoading = false;

        [ObservableProperty]
        private bool _metricsLive = false;

        [ObservableProperty]
        private bool _gamingModeActive;

        [ObservableProperty]
        private bool _gamingModeBusy;

        [ObservableProperty]
        private double _cpuValue;

        [ObservableProperty]
        private double _gpuValue;

        [ObservableProperty]
        private double _ramValue;

        [ObservableProperty]
        private string _cpuTempText = "N/A";

        [ObservableProperty]
        private string _gpuTempText = "N/A";

        [ObservableProperty]
        private string _ramDetailText = "N/A";

        [ObservableProperty]
        private string _diskText = "N/A";

        [ObservableProperty]
        private string _metricsStatusText = string.Empty;

        [ObservableProperty]
        private string _gamingModeHeadline = "Gaming mode";

        [ObservableProperty]
        private string _gamingModeDescription = "Boost the foreground app: unparks all cores, locks max frequency, and boosts the active window. Everything restores automatically when you switch it off.";

        [ObservableProperty]
        private string _modeText = string.Empty;

        public string SystemInfo { get; } =
            System.Runtime.InteropServices.RuntimeInformation.OSDescription.Replace("Microsoft ", string.Empty)
            + " \u00B7 "
            + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();

        public string AppVersion { get; } =
            typeof(HomeViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

        public int RestorePointCount => RestorePoints.Count;

        public ObservableCollection<RestorePointItem> RestorePoints { get; } = new();

        public HomeViewModel(LoggingService logging, HardwareMonitorService hardwareMonitor, ProcessControlService processControl)
        {
            _logging = logging;
            _hardwareMonitor = hardwareMonitor;
            _processControl = processControl;
            _gamingModeActive = processControl.BoostModeActive;
            _modeText = _gamingModeActive ? "On" : "Off";
            _ = LoadRestorePointsAsync();
        }

        public async Task LoadRestorePointsAsync()
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            IsLoading = true;
            RestorePoints.Clear();

            var list = new System.Collections.Generic.List<RestorePointItem>();

            await Task.Run(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var rp = new RestorePointItem
                        {
                            Description = mo["Description"]?.ToString() ?? "Unknown",
                            SequenceNumber = (uint)(mo["SequenceNumber"] ?? 0)
                        };

                        var creationTimeStr = mo["CreationTime"]?.ToString();
                        if (!string.IsNullOrEmpty(creationTimeStr))
                        {
                            try
                            {
                                var dt = ManagementDateTimeConverter.ToDateTime(creationTimeStr);
                                rp.CreationTime = dt.ToString("g");
                            }
                            catch
                            {
                                rp.CreationTime = creationTimeStr;
                            }
                        }

                        list.Add(rp);
                    }
                }
                catch (Exception ex)
                {
                    string reason = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                    _logging.Warn($"Restore points unavailable — System Restore appears to be disabled ({reason}).");
                    dispatcher?.TryEnqueue(() =>
                        RestorePointStatus = "Restore points unavailable — System Restore appears to be disabled on this PC.");
                }
            });

            list.Reverse();

            foreach (var item in list)
                RestorePoints.Add(item);

            OnPropertyChanged(nameof(RestorePointCount));
            IsLoading = false;
        }

        /// <summary>Start the live metrics loop (CPU/GPU/RAM/Disk). Started from the page;
        /// stopped when the page unloads so it adds no resident overhead at rest.</summary>
        public void StartMetricsLoop()
        {
            if (_metricsCts != null) return;
            _metricsCts = new CancellationTokenSource();
            var token = _metricsCts.Token;
            _ = Task.Run(async () =>
            {
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _hardwareMonitor.ScanAsync(token);
                        var cpu = result.Readings.FirstOrDefault(r => r.Category == "CPU" && r.SensorType == "Load")?.NumericValue ?? double.NaN;
                        var gpu = result.Readings.FirstOrDefault(r => r.Category == "GPU" && r.SensorType == "Load")?.NumericValue ?? double.NaN;
                        var ram = result.Readings.FirstOrDefault(r => r.Category == "Memory" && r.SensorType == "Load")?.NumericValue ?? double.NaN;
                        var cpuTemp = result.Readings.FirstOrDefault(r => r.Category == "CPU" && r.SensorType == "Temperature")?.Value;
                        var gpuTemp = result.Readings.FirstOrDefault(r => r.Category == "GPU" && r.SensorType == "Temperature")?.Value;
                        var disk = HardwareMonitorService.GetDiskUsage();
                        bool anyLive = !double.IsNaN(cpu) || !double.IsNaN(gpu) || !double.IsNaN(ram);
                        dispatcher?.TryEnqueue(() =>
                        {
                            CpuValue = cpu;
                            GpuValue = gpu;
                            RamValue = ram;
                            RamDetailText = double.IsNaN(ram) ? "N/A" : $"{ram:0}% in use";
                            CpuTempText = string.IsNullOrEmpty(cpuTemp) ? "N/A" : cpuTemp;
                            GpuTempText = string.IsNullOrEmpty(gpuTemp) ? "N/A" : gpuTemp;
                            DiskText = disk.TotalBytes <= 0 ? "N/A" : $"{disk.UsedText} / {disk.TotalText}";
                            if (anyLive != MetricsLive)
                            {
                                MetricsLive = anyLive;
                                MetricsStatusText = anyLive ? "Live" : "No sensors available";
                            }
                        });
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                    try { await Task.Delay(2500, token); } catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        public void StopMetricsLoop()
        {
            _metricsCts?.Cancel();
            _metricsCts = null;
        }

        private CancellationTokenSource? _metricsCts;

        /// <summary>Switch Gaming Mode on/off. Reads back the real engine state so the UI
        /// never lies about whether the mode actually applied.</summary>
        public async Task SetGamingModeAsync(bool on)
        {
            GamingModeBusy = true;
            try
            {
                bool result = _processControl.ToggleBoostMode();
                // ToggleBoostMode flips the current state, so if the caller asked for 'on'
                // but the toggle landed us in 'off', flip it back. In practice this only matters
                // if the engine refused to apply the requested state.
                if (on && !result)
                {
                    // Engine refused to enable — try once more and accept whatever it settles on.
                    result = _processControl.ToggleBoostMode();
                }
                if (!on && result)
                {
                    // Engine didn't disable — flip once more.
                    _processControl.ToggleBoostMode();
                }

                GamingModeActive = _processControl.BoostModeActive;
                ModeText = GamingModeActive ? "On" : "Off";
            }
            finally
            {
                GamingModeBusy = false;
            }
            await Task.CompletedTask;
        }

        /// <summary>Forwarded from the page so the theme can refresh the hero card look
        /// (e.g. surface/border choice) without the VM owning the theme subscription.</summary>
        public void OnThemeChanged()
        {
            // No-op here: the hero card brushes are computed from app-wide resources,
            // which already recolor with the theme. Kept so the page hook compiles.
        }

        [RelayCommand]
        private void OpenTaskManager()
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
            catch (Exception ex) { _logging.Warn($"Failed to open Task Manager: {ex.Message}"); }
        }

        [RelayCommand]
        private void OpenDeviceManager()
        {
            try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); }
            catch (Exception ex) { _logging.Warn($"Failed to open Device Manager: {ex.Message}"); }
        }

        [RelayCommand]
        private void OpenWindowsSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); }
            catch (Exception ex) { _logging.Warn($"Failed to open Windows Settings: {ex.Message}"); }
        }

        /// <summary>Computed brushes for the hero card. Calm surface so the page reads as
        /// a WinUI 3 content page, not a gradient/washed dashboard.</summary>
        public Brush HeroBrush => Application.Current.Resources[string.Equals(MetricsLive ? "AppSuccessBrush" : "HeroSurfaceBrush", "AppSuccessBrush") ? "AppSuccessBrush" : "HeroSurfaceBrush"] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        public Brush HeroBorder => Application.Current.Resources["HeroBorderBrush"] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        [RelayCommand]
        private async Task CreateRestorePointAsync()
        {
            await CreateRestorePointWithDescriptionAsync("KalOS App Restore Point");
        }

        public async Task<bool> CreateRestorePointWithDescriptionAsync(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) description = "KalOS App Restore Point";
            var safeDesc = description.Replace("'", "''");
            RestorePointStatus = $"Creating restore point '{safeDesc}'. Approve the UAC prompt to continue...";
            bool success = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Enable-ComputerRestore -Drive 'C:\\\\'; Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'MODIFY_SETTINGS'\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                try
                {
                    using var process = Process.Start(psi);
                    if (process == null) return false;
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    dispatcher?.TryEnqueue(() =>
                        RestorePointStatus = "Restore point creation was cancelled at the UAC prompt. Click Create Restore Point again to retry.");
                    return false;
                }
                catch (Exception ex)
                {
                    _logging.Error($"Failed to create restore point: {ex.Message}");
                    return false;
                }
            });

            if (success)
            {
                RestorePointStatus = $"Restore point '{description}' created successfully.";
                _ = LoadRestorePointsAsync();
                return true;
            }
            else
            {
                if (string.IsNullOrEmpty(RestorePointStatus) || !RestorePointStatus.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                    RestorePointStatus = "Failed to create restore point (You may need to enable System Restore for C: or approve the UAC prompt).";
                return false;
            }
        }

        public async Task<bool> DeleteRestorePointAsync(uint sequenceNumber)
        {
            RestorePointStatus = $"Deleting restore point {sequenceNumber}. Approve the UAC prompt to continue...";
            bool success = await Task.Run(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\default", $"SELECT * FROM SystemRestore WHERE SequenceNumber = {sequenceNumber}");
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Get-ComputerRestorePoint | Where-Object {{ $_.SequenceNumber -eq {sequenceNumber} }} | ForEach-Object {{ $_.Delete() }}; vssadmin delete shadows /for=C: /oldest /quiet 2>$null; exit 0\"",
                            UseShellExecute = true,
                            Verb = "runas",
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        using var proc = Process.Start(psi);
                        if (proc == null) return false;
                        proc.WaitForExit();
                        return proc.ExitCode == 0;
                    }
                    using var s2 = new ManagementObjectSearcher(@"root\default", $"SELECT * FROM SystemRestore WHERE SequenceNumber = {sequenceNumber}");
                    foreach (ManagementObject mo2 in s2.Get())
                    {
                        try { mo2.Delete(); return true; } catch { }
                    }
                    return false;
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    dispatcher?.TryEnqueue(() => RestorePointStatus = "Delete cancelled at the UAC prompt.");
                    return false;
                }
                catch (Exception ex)
                {
                    _logging.Error($"Failed to delete restore point {sequenceNumber}: {ex.Message}");
                    return false;
                }
            });

            if (success)
            {
                RestorePointStatus = $"Restore point {sequenceNumber} deleted.";
                _ = LoadRestorePointsAsync();
            }
            else
            {
                if (!RestorePointStatus.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                    RestorePointStatus = $"Failed to delete restore point {sequenceNumber}. Try running as administrator.";
            }
            return success;
        }

        public void RestoreSystem(uint sequenceNumber)
        {
            RestorePointStatus = "Initiating system restore. Approve the UAC prompt to continue — your computer will restart automatically...";
            Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Restore-Computer -RestorePoint {sequenceNumber} -Confirm:$false\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                try
                {
                    var process = Process.Start(psi);
                    if (process == null)
                    {
                        var dispatcher = DispatcherQueue.GetForCurrentThread();
                        dispatcher?.TryEnqueue(() =>
                            RestorePointStatus = "Failed to initiate system restore (could not start the elevated process).");
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    dispatcher?.TryEnqueue(() =>
                        RestorePointStatus = "System restore was cancelled at the UAC prompt. Click Restore again to retry.");
                }
                catch (Exception ex)
                {
                    _logging.Error($"System restore failed: {ex.Message}");
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    dispatcher?.TryEnqueue(() =>
                        RestorePointStatus = $"Failed to initiate system restore: {ex.Message}");
                }
            });
        }
    }
}
