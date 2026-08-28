using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using KalOS.Services;
using Microsoft.UI.Dispatching;

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

        [ObservableProperty]
        private string _restorePointStatus = string.Empty;

        [ObservableProperty]
        private bool _isLoading = false;

        /// <summary>Short OS + architecture line for the stat tile, e.g. "Windows 10.0.26200 · X64".</summary>
        public string SystemInfo { get; } =
            System.Runtime.InteropServices.RuntimeInformation.OSDescription.Replace("Microsoft ", string.Empty)
            + " \u00B7 "
            + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();

        /// <summary>Assembly version for the stat tile.</summary>
        public string AppVersion { get; } =
            typeof(HomeViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

        /// <summary>Number of restore points, kept in sync with the list</summary>
        public int RestorePointCount => RestorePoints.Count;

        public ObservableCollection<RestorePointItem> RestorePoints { get; } = new();

        public HomeViewModel(LoggingService logging)
        {
            _logging = logging;
            _ = LoadRestorePointsAsync();
        }

        public async Task LoadRestorePointsAsync()
        {
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
                    _logging.Error($"Failed to load restore points: {ex.Message}");
                }
            });

            list.Reverse();

            foreach (var item in list)
            {
                RestorePoints.Add(item);
            }

            OnPropertyChanged(nameof(RestorePointCount));
            IsLoading = false;
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

        [RelayCommand]
        private async Task CreateRestorePointAsync()
        {
            RestorePointStatus = "Creating system restore point. Approve the UAC prompt to continue...";
            bool success = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Enable-ComputerRestore -Drive 'C:\'; Checkpoint-Computer -Description 'KalOS App Restore Point' -RestorePointType 'MODIFY_SETTINGS'\"",
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
                    {
                        RestorePointStatus = "Restore point creation was cancelled at the UAC prompt. Click Create Restore Point again to retry.";
                    });
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
                RestorePointStatus = "Restore point created successfully.";
                _ = LoadRestorePointsAsync();
            }
            else
            {
                if (string.IsNullOrEmpty(RestorePointStatus) || !RestorePointStatus.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    RestorePointStatus = "Failed to create restore point (You may need to enable System Restore for C: or approve the UAC prompt).";
                }
            }
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
                        {
                            RestorePointStatus = "Failed to initiate system restore (could not start the elevated process).";
                        });
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    dispatcher?.TryEnqueue(() =>
                    {
                        RestorePointStatus = "System restore was cancelled at the UAC prompt. Click Restore again to retry.";
                    });
                }
                catch (Exception ex)
                {
                    _logging.Error($"System restore failed: {ex.Message}");
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    dispatcher?.TryEnqueue(() =>
                    {
                        RestorePointStatus = $"Failed to initiate system restore: {ex.Message}";
                    });
                }
            });
        }
    }
}
