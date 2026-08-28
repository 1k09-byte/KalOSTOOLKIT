using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Services;

namespace KalOS.ViewModels
{
    public partial class SdioViewModel : ObservableObject
    {
        private readonly SdioManagerService _sdioManager;
        private readonly LoggingService _log;

        [ObservableProperty]
        private bool _showNotInstalled = true;

        [ObservableProperty]
        private bool _showNewer = true;

        [ObservableProperty]
        private bool _showCurrent = false;

        [ObservableProperty]
        private bool _showOlder = false;

        [ObservableProperty]
        private bool _showBetterMatch = true;

        [ObservableProperty]
        private bool _showWorseMatch = false;

        [ObservableProperty]
        private bool _createRestorePoint = true;

        [ObservableProperty]
        private bool _autoReboot = false;

        [ObservableProperty]
        private bool _isWorking;

        [ObservableProperty]
        private string _statusText = "Ready for driver scanning.";

        [ObservableProperty]
        private string _consoleOutput = "";

        private CancellationTokenSource? _cts;

        public SdioViewModel(SdioManagerService sdioManager, LoggingService log)
        {
            _sdioManager = sdioManager;
            _log = log;

            // Perform an initial scan for the SDIO payload on page load
            if (_sdioManager.IsSdioInstalled)
            {
                StatusText = "SDIO is fully installed and ready to launch.";
            }
            else
            {
                StatusText = "SDIO will be safely downloaded via WinGet when you open it.";
            }
        }

        [RelayCommand]
        public async Task StartInstallAsync()
        {
            if (IsWorking) return;

            IsWorking = true;
            ConsoleOutput = "";
            StatusText = "Preparing Snappy Driver Installer Origin...";

            _cts?.Dispose();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;

            try
            {
                if (!_sdioManager.IsSdioInstalled)
                {
                    StatusText = "Downloading SDIO backend...";
                    await _sdioManager.DownloadSdioAsync(ct);
                }

                StatusText = "SDIO backend is executing in the background. Please wait...";

                var progress = new Progress<string>(msg =>
                {
                    // Append line safely and truncate if getting too long
                    ConsoleOutput += msg + Environment.NewLine;
                    if (ConsoleOutput.Length > 10000)
                    {
                        ConsoleOutput = ConsoleOutput.Substring(ConsoleOutput.Length - 10000);
                    }
                });

                bool success = await _sdioManager.RunSdioAutoInstallAsync(
                    ShowNotInstalled, ShowNewer, ShowCurrent, ShowOlder,
                    ShowBetterMatch, ShowWorseMatch, CreateRestorePoint, AutoReboot,
                    progress, ct
                );

                if (success)
                {
                    StatusText = "SDIO auto-installation completed successfully.";
                }
                else
                {
                    StatusText = "SDIO auto-installation encountered errors or was missing files.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Operation was cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed: {ex.Message}";
                _log.Error(ex.Message);
            }
            finally
            {
                IsWorking = false;
            }
        }

        [RelayCommand]
        public async Task OpenSdioAsync()
        {
            if (IsWorking) return;

            IsWorking = true;
            _cts?.Dispose();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;

            try
            {
                if (!_sdioManager.IsSdioInstalled)
                {
                    StatusText = "Downloading SDIO backend...";
                    await _sdioManager.DownloadSdioAsync(ct);
                }

                StatusText = "SDIO is currently running. Waiting for you to close it...";
                await _sdioManager.OpenSdioGuiAsync(ct);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Operation cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Launch failed: {ex.Message}";
                _log.Error(ex.Message);
            }
            finally
            {
                IsWorking = false;
                StatusText = _sdioManager.IsSdioInstalled ? "SDIO is fully installed and ready to launch." : "SDIO will be safely downloaded via WinGet when you open it.";
            }
        }
    }
}
