using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Models;
using KalOS.Services;

namespace KalOS.ViewModels
{
    /// <summary>
    /// One GPU row: the hardware as WMI reported it plus the bindable state for
    /// its check result and any running update. All vendor knowledge stays in
    /// the backend — this row only renders a <see cref="DriverCheckResult"/>.
    /// </summary>
    public partial class GpuDriverItem : ObservableObject
    {
        public GpuDriverItem(GpuInfo gpu)
        {
            Gpu = gpu;
        }

        public GpuInfo Gpu { get; }

        public string Name => Gpu.Name;
        public string Vendor => Gpu.Vendor;
        public bool IsNvidia => Gpu.IsNvidia;
        public bool IsAmd => Gpu.IsAmd;

        /// <summary>"Installed driver 32.0.15.5244" — exactly what WMI saw.</summary>
        public string InstalledText => $"Installed driver {Gpu.DriverVersion}";

        [ObservableProperty]
        private bool _isPending = true;

        [ObservableProperty]
        private DriverStatus _status = DriverStatus.Unknown;

        [ObservableProperty]
        private DriverInfo? _latest;

        [ObservableProperty]
        private string? _errorText;

        [ObservableProperty]
        private string _statusText = "Waiting to check…";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _busyText = "";

        [ObservableProperty]
        private double _progressValue;

        [ObservableProperty]
        private bool _progressIndeterminate;

        /// <summary>Latest-known caption shown under the installed line.</summary>
        public string LatestText => Latest is null
            ? ""
            : $"{Latest.DisplayString ?? $"Latest known: {Latest.Version}"}"
              + (Latest.ReleaseDate.HasValue ? $" \u00B7 released {Latest.ReleaseDate.Value:MMM d, yyyy}" : "");

        public bool HasLatestText => !string.IsNullOrEmpty(LatestText);

        /// <summary>We can drive this update ourselves (silent NVIDIA or AMD pipeline).</summary>
        public bool CanAutoInstall =>
            Status == DriverStatus.UpdateAvailable
            && (IsNvidia || IsAmd)
            && Latest != null
            && Uri.IsWellFormedUriString(Latest.DownloadUrl, UriKind.Absolute);

        /// <summary>No silent path exists (AMD/Intel) — offer the vendor page.</summary>
        public bool ShowOpenPage =>
            !IsBusy
            && Latest != null
            && Uri.IsWellFormedUriString(Latest.DownloadUrl, UriKind.Absolute)
            && Status is DriverStatus.UpdateAvailable or DriverStatus.Unknown;

        public string StatusGlyph => IsPending ? "\uE895" : Status switch
        {
            DriverStatus.UpToDate => "\uE73E",
            DriverStatus.UpdateAvailable => "\uE896",
            DriverStatus.Error => "\uE783",
            DriverStatus.Unsupported => "\uE946",
            _ => "\uE946", // Unknown / manual check
        };

        /// <summary>The status badge yields to the progress UI while busy.</summary>
        public bool ShowStatus => !IsBusy;

        /// <summary>Fills the row from a backend result. The row never talks to vendors itself.</summary>
        public void Apply(DriverCheckResult result)
        {
            IsPending = false;
            ErrorText = result.Error;
            Status = result.Status;
            Latest = result.LatestDriver;
            StatusText = result.Status switch
            {
                DriverStatus.UpToDate => "Up to date",
                DriverStatus.UpdateAvailable => "Update available",
                DriverStatus.Unsupported => "No automated source for this adapter",
                DriverStatus.Error => "Check failed",
                _ => "Download manually",
            };
        }

        public void BeginBusy(string message)
        {
            IsBusy = true;
            BusyText = message;
            ProgressValue = 0;
            ProgressIndeterminate = false;
        }

        public void Report(DriverUpdateProgress progress)
        {
            // Downloading and CleaningUp are step/byte-based, so they render as a
            // real percentage; extraction and install give no reliable numbers.
            ProgressIndeterminate = progress.Phase is not (DriverUpdatePhase.Downloading or DriverUpdatePhase.CleaningUp);
            if (!ProgressIndeterminate)
            {
                ProgressValue = progress.Percent;
            }
            BusyText = progress.Message;
        }

        public void EndBusy()
        {
            IsBusy = false;
            BusyText = "";
            ProgressValue = 0;
            ProgressIndeterminate = false;
        }

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowStatus));

        partial void OnLatestChanged(DriverInfo? value)
        {
            OnPropertyChanged(nameof(LatestText));
            OnPropertyChanged(nameof(HasLatestText));
            OnPropertyChanged(nameof(CanAutoInstall));
            OnPropertyChanged(nameof(ShowOpenPage));
        }

        partial void OnStatusChanged(DriverStatus value)
        {
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(CanAutoInstall));
            OnPropertyChanged(nameof(ShowOpenPage));
        }
    }

    /// <summary>
    /// Drives the GPU Drivers page: detects adapters, checks each against its
    /// vendor source via <see cref="DriverService"/>, and runs approved updates.
    /// The page itself only binds and forwards button clicks.
    /// </summary>
    public partial class GpuDriversViewModel : ObservableObject
    {
        private readonly GpuDetectionService _detection;
        private readonly DriverService _driverService;
        private readonly LoggingService _log;

        private CancellationTokenSource? _cts;

        public ObservableCollection<GpuDriverItem> Gpus { get; } = new();

        [ObservableProperty]
        private bool _isChecking;

        [ObservableProperty]
        private bool _isInstalling;

        [ObservableProperty]
        private string _statusText = "Ready.";

        [ObservableProperty]
        private bool _hasError;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private bool _hasNoGpus;

        public bool IsWorking => IsChecking || IsInstalling;

        public bool CanCheck => !IsWorking;

        /// <summary>True once the first full check finished — used by the page to auto-check on first visit only.</summary>
        public bool HasBeenChecked { get; private set; }

        public GpuDriversViewModel(GpuDetectionService detection, DriverService driverService, LoggingService log)
        {
            _detection = detection;
            _driverService = driverService;
            _log = log;

            // Reclaim space from interrupted driver installs (crashed runs, power
            // loss, or older builds without cleanup). Safe here: no install can
            // be running yet, and the sweep skips recently-modified files anyway.
            _driverService.CleanStaleDownloads();
        }

        partial void OnIsCheckingChanged(bool value) => NotifyWorkingChanged();

        partial void OnIsInstallingChanged(bool value) => NotifyWorkingChanged();

        private void NotifyWorkingChanged()
        {
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(CanCheck));
        }

        /// <summary>Detect GPUs → check each → summarize. Safe to call repeatedly.</summary>
        [RelayCommand]
        public async Task CheckForUpdatesAsync()
        {
            if (IsWorking) return;

            _cts?.Dispose();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;

            IsChecking = true;
            HasError = false;
            ErrorMessage = null;
            StatusText = "Detecting graphics hardware…";

            try
            {
                var gpus = await _detection.GetGpusAsync().WaitAsync(ct);
                ct.ThrowIfCancellationRequested();

                Gpus.Clear();
                HasNoGpus = gpus.Count == 0;
                foreach (var gpu in gpus)
                {
                    Gpus.Add(new GpuDriverItem(gpu));
                }

                if (gpus.Count == 0)
                {
                    StatusText = "No graphics adapters were detected.";
                    HasBeenChecked = true;
                    return;
                }

                StatusText = $"Found {gpus.Count} adapter{(gpus.Count == 1 ? "" : "s")} — checking driver versions…";

                int updates = 0, current = 0, issues = 0;
                foreach (var item in Gpus)
                {
                    var result = await _driverService.CheckForUpdateAsync(item.Gpu, ct);
                    item.Apply(result);

                    switch (result.Status)
                    {
                        case DriverStatus.UpdateAvailable: updates++; break;
                        case DriverStatus.UpToDate: current++; break;
                        default: issues++; break;
                    }
                }

                var parts = new System.Collections.Generic.List<string>();
                if (updates > 0) parts.Add($"{updates} update{(updates == 1 ? "" : "s")} available");
                if (current > 0) parts.Add($"{current} up to date");
                if (issues > 0) parts.Add($"{issues} need manual attention");

                StatusText = parts.Count > 0 ? string.Join(" · ", parts) : "All drivers checked.";
                HasBeenChecked = true;
            }
            catch (OperationCanceledException)
            {
                StatusText = "Check cancelled.";
            }
            catch (Exception ex)
            {
                _log.Error($"GPU driver check failed: {ex.Message}");
                HasError = true;
                ErrorMessage = "Could not check for driver updates: " + ex.Message;
                StatusText = "Check failed.";
            }
            finally
            {
                IsChecking = false;
            }
        }

        /// <summary>
        /// Runs an already-confirmed update for one GPU with per-row progress,
        /// then quietly re-checks that GPU so the row reflects the new state.
        /// </summary>
        public async Task InstallAsync(GpuDriverItem? item, NvidiaInstallComponents? nvidiaComponents = null)
        {
            if (item is null || !item.CanAutoInstall || IsWorking) return;

            _cts?.Dispose();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;

            IsInstalling = true;
            item.BeginBusy("Preparing to install…");
            StatusText = $"{item.Name}: installing update…";

            try
            {
                var progress = new Progress<DriverUpdateProgress>(p =>
                {
                    item.Report(p);
                    StatusText = $"{item.Name}: {p.Message}";
                });

                bool ok = await _driverService.UpdateAsync(item.Gpu, item.Latest!, progress, ct, nvidiaComponents);

                if (ok)
                {
                    _log.Success($"Driver update completed for {item.Name}");
                    StatusText = $"{item.Name}: driver updated.";
                    await RecheckSingleAsync(item, ct);
                }
                else
                {
                    StatusText = $"{item.Name}: update did not complete.";
                    HasError = true;
                    ErrorMessage = "The driver package could not be extracted or installed. " +
                                   "The package may have been corrupted during download, or the silent extractor is unavailable.";
                }
            }
            catch (OperationCanceledException)
            {
                _log.Info($"Driver update cancelled for {item.Name}");
                StatusText = "Update cancelled.";
            }
            catch (Exception ex)
            {
                _log.Error($"Driver update failed for {item.Name}: {ex.Message}");
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = $"{item.Name}: update failed.";
            }
            finally
            {
                item.EndBusy();
                IsInstalling = false;
            }
        }

        private async Task RecheckSingleAsync(GpuDriverItem item, CancellationToken ct)
        {
            try
            {
                // The freshly installed version needs a fresh WMI read.
                var gpus = await _detection.GetGpusAsync().WaitAsync(ct);
                var fresh = gpus.FirstOrDefault(g => string.Equals(g.PnpDeviceId, item.Gpu.PnpDeviceId, StringComparison.OrdinalIgnoreCase)) ?? item.Gpu;
                var result = await _driverService.CheckForUpdateAsync(fresh, ct);
                item.Apply(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"Post-install re-check failed for {item.Name}: {ex.Message}");
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            if (_cts is { IsCancellationRequested: false })
            {
                _cts.Cancel();
                StatusText = "Cancelling…";
            }
        }

        /// <summary>Dismisses the error banner (InfoBar close button).</summary>
        public void ClearError()
        {
            HasError = false;
            ErrorMessage = null;
        }

        /// <summary>AMD/Intel fallback: open the vendor's own download page.</summary>
        public void OpenDownloadPage(GpuDriverItem? item)
        {
            if (item is null) return;
            // Prefer the human-facing vendor support page; fall back to the direct
            // download URL (used by the silent pipeline) when no page is known.
            var url = !string.IsNullOrWhiteSpace(item.Latest?.SupportUrl)
                ? item.Latest!.SupportUrl
                : item.Latest?.DownloadUrl;
            if (!_driverService.OpenInBrowser(url))
            {
                _log.Warn($"No usable download URL for {item.Name}");
            }
        }
    }
}
