using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaliteKit.Models;
using KaliteKit.Services;

namespace KaliteKit.ViewModels
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

        public string Name
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Gpu.Name) || Gpu.Name.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsAmd) return "AMD Radeon Graphics (VEN_1002)";
                    if (IsNvidia) return "NVIDIA Graphics Adapter";
                }
                return Gpu.Name;
            }
        }

        public string Vendor => Gpu.Vendor;
        public bool IsNvidia => Gpu.IsNvidia;
        public bool IsAmd => Gpu.IsAmd;

        /// <summary>True when this adapter is on a laptop/notebook (chassis detection or a mobile model name).</summary>
        public bool IsLaptop => Gpu.IsMobileGpu;

        /// <summary>"Laptop GPU" caption next to the adapter name on the GPU Drivers page.</summary>
        public string LaptopBadgeText => IsLaptop ? "Laptop GPU" : "";

        /// <summary>Small laptop chip shown only for notebook adapters.</summary>
        public bool ShowLaptopBadge => IsLaptop;

        /// <summary>"Installed driver: 26.8.1" — exactly what AMD/NVIDIA reported.</summary>
        public string InstalledText => Gpu.DriverVersion.StartsWith("10.0.")
            ? "Generic Windows Driver (No vendor driver installed)"
            : $"Installed driver: {Gpu.DriverVersion}";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusGlyph))]
        private bool _isPending = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
        [NotifyPropertyChangedFor(nameof(PrimaryActionGlyph))]
        [NotifyPropertyChangedFor(nameof(InstallButtonText))]
        [NotifyPropertyChangedFor(nameof(StatusGlyph))]
        [NotifyPropertyChangedFor(nameof(CanAutoInstall))]
        private DriverStatus _status = DriverStatus.Unknown;


        [ObservableProperty]
        private DriverInfo? _latest;

        [ObservableProperty]
        private string? _errorText;

        [ObservableProperty]
        private string _statusText = "Waiting for check…";

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
            : $"Latest available: {Latest.DisplayString ?? Latest.Version}"
              + (Latest.ReleaseDate.HasValue ? $" ({Latest.ReleaseDate.Value:d MMMM yyyy})" : "");

        public bool HasLatestText => !string.IsNullOrEmpty(LatestText);

        /// <summary>We can drive this update ourselves (silent NVIDIA or AMD pipeline).</summary>
        public bool CanAutoInstall => (IsNvidia || IsAmd);

        public string PrimaryActionText => Status switch
        {
            DriverStatus.UpToDate => "Debloat System",
            DriverStatus.UpdateAvailable => (IsAmd ? "Update & Debloat" : "Update Driver"),
            _ => (IsAmd ? "Install & Debloat (Radeon Slimmer)" : "Install Driver")
        };

        public string PrimaryActionGlyph => Status switch
        {
            DriverStatus.UpToDate => "\uE946",
            _ => "\uE896"
        };

        public string InstallButtonText => PrimaryActionText;

        /// <summary>Offer the vendor page.</summary>
        public bool ShowOpenPage => !IsBusy;

        public string StatusGlyph => IsPending ? "\uE895" : Status switch
        {
            DriverStatus.UpToDate => "\uE73E",
            DriverStatus.UpdateAvailable => "\uE896",
            DriverStatus.Error => "\uE783",
            DriverStatus.Unsupported => "\uE946",
            _ => (Gpu.DriverVersion.StartsWith("10.0.") ? "\uE896" : "\uE73E"),
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
                DriverStatus.Unsupported => "Unmanaged device",
                DriverStatus.Error => "Check failed",
                _ => (Gpu.DriverVersion.StartsWith("10.0.") ? "Driver not installed" : "Up to date"),
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
        private readonly DriverDownloadService _downloadService;
        private readonly DriverInstallService _installService;
        private readonly DriverCleanupService _cleanupService;
        private readonly RadeonSlimmerService _slimmerService;
        private readonly AmdAutoDetectService _autoDetectService;
        private readonly RadeonPackageSlimmer _packageSlimmer;
        private readonly LoggingService _log;

        private CancellationTokenSource? _cts;

        public ObservableCollection<GpuDriverItem> Gpus { get; } = new();

        public RadeonPackageSlimmer PackageSlimmer => _packageSlimmer;

        [ObservableProperty]
        private bool _isChecking;

        [ObservableProperty]
        private bool _isInstalling;

        [ObservableProperty]
        private bool _isCleaning;

        [ObservableProperty]
        private bool _isSlimming;

        [ObservableProperty]
        private bool _isAutoDetecting;

        [ObservableProperty]
        private string _statusText = "Ready.";

        [ObservableProperty]
        private bool _hasError;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private bool _hasNoGpus;

        [ObservableProperty]
        private bool _isGpuAudioEnabled = true;

        [ObservableProperty]
        private bool _isTogglingAudio;

        public bool IsWorking => IsChecking || IsInstalling || IsCleaning || IsSlimming || IsAutoDetecting;

        public bool CanCheck => !IsWorking;

        public bool HasAmdGpu => Gpus.Any(g => g.IsAmd);
        public bool HasNvidiaGpu => Gpus.Any(g => g.IsNvidia);

        /// <summary>True once the first full check finished — used by the page to auto-check on first visit only.</summary>
        public bool HasBeenChecked { get; private set; }

        public GpuDriversViewModel(
            GpuDetectionService detection,
            DriverService driverService,
            DriverDownloadService downloadService,
            DriverInstallService installService,
            DriverCleanupService cleanupService,
            RadeonSlimmerService slimmerService,
            AmdAutoDetectService autoDetectService,
            RadeonPackageSlimmer packageSlimmer,
            LoggingService log)
        {
            _detection = detection;
            _driverService = driverService;
            _downloadService = downloadService;
            _installService = installService;
            _cleanupService = cleanupService;
            _slimmerService = slimmerService;
            _autoDetectService = autoDetectService;
            _packageSlimmer = packageSlimmer;
            _log = log;

            Gpus.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasAmdGpu));
                OnPropertyChanged(nameof(HasNvidiaGpu));
            };

            // Reclaim space from interrupted driver installs (crashed runs, power
            // loss, or older builds without cleanup). Safe here: no install can
            // be running yet, and the sweep skips recently-modified files anyway.
            _driverService.CleanStaleDownloads();
        }


        partial void OnIsCheckingChanged(bool value) => NotifyWorkingChanged();
        partial void OnIsInstallingChanged(bool value) => NotifyWorkingChanged();
        partial void OnIsCleaningChanged(bool value) => NotifyWorkingChanged();
        partial void OnIsSlimmingChanged(bool value) => NotifyWorkingChanged();
        partial void OnIsAutoDetectingChanged(bool value) => NotifyWorkingChanged();


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
                OnPropertyChanged(nameof(HasAmdGpu));
                OnPropertyChanged(nameof(HasNvidiaGpu));

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

                try
                {
                    IsGpuAudioEnabled = await _packageSlimmer.IsGpuAudioEnabledAsync();
                }
                catch { }

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
        /// <summary>
        /// NVIDIA version history (newest first) for the "Manually select a
        /// driver version" option in the install dialog. Empty when the API is
        /// unreachable — the dialog then disables that option.
        /// </summary>
        public async Task<IReadOnlyList<DriverInfo>> GetNvidiaVersionHistoryAsync(GpuDriverItem item, CancellationToken ct = default)
        {
            try
            {
                return await _driverService.GetVersionHistoryAsync(item.Gpu, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"NVIDIA version history failed for {item.Name}: {ex.Message}");
                return Array.Empty<DriverInfo>();
            }
        }

        /// <summary>
        /// AMD only: downloads the notebook (desktop+notebook "combined" INF)
        /// Adrenalin package for this GPU and runs it through the standard
        /// silent extract → strip → pnputil pipeline. The variant laptop
        /// iGPUs/dGPUs need when the desktop package's INF rejects them.
        /// </summary>
        [RelayCommand]
        public async Task InstallAmdNotebookDriverAsync(GpuDriverItem? item)
        {
            if (item is null || !item.IsAmd || IsWorking) return;

            _cts?.Dispose();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;

            IsInstalling = true;
            item.BeginBusy("Resolving the AMD notebook (combined) package…");
            StatusText = $"{item.Name}: resolving AMD notebook package…";

            try
            {
                var driver = await _driverService.GetAmdNotebookDriverAsync(item.Gpu, ct);
                if (driver is null || string.IsNullOrWhiteSpace(driver.DownloadUrl))
                {
                    StatusText = $"{item.Name}: no AMD notebook package available.";
                    HasError = true;
                    ErrorMessage = "Could not resolve the AMD notebook (combined) driver package. Open the vendor page instead.";
                    return;
                }

                item.StatusText = $"Notebook package: {driver.DisplayString}";
                StatusText = $"{item.Name}: downloading {driver.DisplayString}…";

                var progress = new Progress<DriverUpdateProgress>(p =>
                {
                    item.Report(p);
                    StatusText = $"{item.Name}: {p.Message}";
                });

                bool ok = await _driverService.UpdateAsync(item.Gpu, driver, progress, ct);
                if (ok)
                {
                    _log.Success($"AMD notebook driver install completed for {item.Name}");
                    StatusText = $"{item.Name}: notebook package installed.";
                    await RecheckSingleAsync(item, ct);
                }
                else
                {
                    StatusText = $"{item.Name}: notebook package install did not complete.";
                    HasError = true;
                    ErrorMessage = "The combined package could not be downloaded or its display INF is missing. " +
                                   "It may not cover this GPU — try the desktop package or the vendor page.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Install cancelled.";
            }
            catch (Exception ex)
            {
                _log.Error($"AMD notebook install failed for {item.Name}: {ex.Message}");
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = $"{item.Name}: notebook install failed.";
            }
            finally
            {
                item.EndBusy();
                IsInstalling = false;
            }
        }

        public async Task InstallAsync(GpuDriverItem? item, NvidiaInstallComponents? nvidiaComponents = null, string? onDiskDriverPath = null, DriverInfo? driverOverride = null, NvInstallTweaks? nvidiaTweaks = null)
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

                var driver = driverOverride ?? item.Latest!;
                bool ok = await _driverService.UpdateAsync(item.Gpu, driver, progress, ct, nvidiaComponents, onDiskDriverPath, nvidiaTweaks);

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

        /// <summary>
        /// Downloads the driver installer and immediately launches Radeon Software Slimmer
        /// with the downloaded installer staged for custom component slimming.
        /// </summary>
        public async Task DownloadAndOpenInSlimmerAsync(GpuDriverItem? item)
        {
            if (item?.Latest is null || IsWorking) return;

            _cts?.Dispose();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;

            IsInstalling = true;
            item.BeginBusy("Downloading driver for Radeon Software Slimmer…");
            StatusText = $"{item.Name}: downloading driver package…";

            try
            {
                string workDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KaliteKit", "drivers");
                Directory.CreateDirectory(workDir);
                string filename = "amd-driver-" + item.Latest.Version + ".exe";
                try
                {
                    var uri = new Uri(item.Latest.DownloadUrl);
                    string lastSeg = Path.GetFileName(uri.LocalPath);
                    if (!string.IsNullOrWhiteSpace(lastSeg) && lastSeg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        filename = lastSeg;
                    }
                }
                catch { }

                string destPath = Path.Combine(workDir, filename);

                var downloadProgress = new Progress<double>(pct =>
                {
                    item.ProgressValue = pct;
                    StatusText = $"{item.Name}: Downloading driver… {pct:F0}%";
                });

                var downloadService = new DriverDownloadService(_log);
                await downloadService.DownloadAsync(item.Latest.DownloadUrl, destPath, downloadProgress, ct);

                StatusText = "Launching Radeon Software Slimmer…";
                var statusProgress = new Progress<string>(s => StatusText = s);
                bool launched = await _slimmerService.LaunchOrDownloadAsync(_log, statusProgress, destPath, ct);

                if (launched)
                {
                    StatusText = "Radeon Software Slimmer opened with downloaded driver.";
                    _log.Success("Radeon Software Slimmer launched successfully with driver installer staged.");
                }
                else
                {
                    StatusText = "Could not launch Radeon Software Slimmer.";
                    HasError = true;
                    ErrorMessage = "Unable to start Radeon Software Slimmer. Please check if the tool is accessible.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Download cancelled.";
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to download driver for Slimmer: {ex.Message}");
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = "Download failed.";
            }
            finally
            {
                item.EndBusy();
                IsInstalling = false;
            }
        }

        /// <summary>
        /// Toggles GPU HDMI/DisplayPort audio device in Device Manager cleanly via pnputil.
        /// </summary>
        [RelayCommand]
        public async Task ToggleGpuAudioAsync()
        {
            if (IsTogglingAudio) return;
            IsTogglingAudio = true;
            try
            {
                bool target = !IsGpuAudioEnabled;
                _log.Info($"[Slimmer] Toggling GPU HDMI/DP audio to enabled={target}...");
                StatusText = target ? "Enabling GPU HDMI/DP audio controller…" : "Disabling GPU HDMI/DP audio controller (DPC latency optimization)…";
                await _packageSlimmer.SetGpuAudioEnabledAsync(target);
                await Task.Delay(500);
                IsGpuAudioEnabled = await _packageSlimmer.IsGpuAudioEnabledAsync();
                StatusText = IsGpuAudioEnabled ? "GPU HDMI/DP audio enabled." : "GPU HDMI/DP audio disabled (DPC optimization active).";
            }
            catch (Exception ex)
            {
                _log.Warn($"[Slimmer] Failed to toggle GPU audio: {ex.Message}");
            }
            finally
            {
                IsTogglingAudio = false;
            }
        }

        /// <summary>
        /// Launches the official AMD Cleanup Utility.
        /// </summary>
        [RelayCommand]
        public async Task RunAmdCleanupAsync()
        {
            if (IsWorking) return;

            IsCleaning = true;
            StatusText = "Launching AMD Cleanup Utility…";
            _log.Info("[ViewModel] User triggered AMD Cleanup Utility");

            try
            {
                var progress = new Progress<string>(s => StatusText = s);
                bool launched = await _cleanupService.LaunchAmdCleanupUtilityAsync(progress);
                if (launched)
                {
                    StatusText = "AMD Cleanup Utility launched.";
                    _log.Success("AMD Cleanup Utility launched successfully.");
                }
                else
                {
                    StatusText = "Failed to launch AMD Cleanup Utility.";
                    HasError = true;
                    ErrorMessage = "Unable to download or launch AMD Cleanup Utility.";
                }
            }
            catch (Exception ex)
            {
                _log.Error($"AMD Cleanup failed: {ex.Message}");
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = "Échec d'AMD Cleanup.";
            }
            finally
            {
                IsCleaning = false;
            }
        }



        /// <summary>
        /// Downloads and opens Radeon Software Slimmer for interactive package debloating or post-install trimming.
        /// </summary>
        [RelayCommand]
        public async Task LaunchRadeonSlimmerAsync()
        {
            if (IsWorking) return;

            IsSlimming = true;
            StatusText = "Preparing Radeon Software Slimmer…";

            try
            {
                var progress = new Progress<string>(s => StatusText = s);
                bool launched = await _slimmerService.LaunchOrDownloadAsync(_log, progress);
                if (launched)
                {
                    StatusText = "Radeon Software Slimmer launched.";
                    _log.Success("Radeon Software Slimmer launched successfully.");
                }
                else
                {
                    StatusText = "Could not launch Radeon Software Slimmer.";
                    HasError = true;
                    ErrorMessage = "Unable to download or start Radeon Software Slimmer.";
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Radeon Slimmer failed: {ex.Message}");
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = "Radeon Slimmer failed.";
            }
            finally
            {
                IsSlimming = false;
            }
        }

        /// <summary>
        /// Downloads the official AMD Auto-Detect and Install tool, validates its Authenticode signature,
        /// and launches it as administrator. If untrusted, immediately removes the file.
        /// </summary>
        [RelayCommand]
        public async Task UpdateAmdOfficialAsync()
        {
            if (IsWorking) return;


            _cts?.Dispose();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;

            IsAutoDetecting = true;
            StatusText = "Downloading official AMD Auto-Detect tool…";

            try
            {
                var downloadProgress = new Progress<double>(pct =>
                {
                    StatusText = $"Downloading official AMD Auto-Detect tool… {pct:F0}%";
                });

                var statusProgress = new Progress<string>(s =>
                {
                    StatusText = s;
                });

                var (success, message) = await _autoDetectService.DownloadAndLaunchAutoDetectAsync(
                    downloadProgress, statusProgress, ct);

                if (success)
                {
                    StatusText = "Official AMD Auto-Detect tool launched.";
                }
                else
                {
                    StatusText = "AMD Auto-Detect failed.";
                    HasError = true;
                    ErrorMessage = message;
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Operation cancelled.";
            }
            catch (Exception ex)
            {
                _log.Error($"AMD Auto-Detect updater error: {ex.Message}");
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = "AMD Auto-Detect failed.";
            }
            finally
            {
                IsAutoDetecting = false;
            }
        }

        /// <summary>
        /// Runs full post-install debloat for AMD Radeon systems: disables telemetry & crash services,
        /// removes scheduled tasks, and cleans residual caches.
        /// </summary>
        [RelayCommand]
        public async Task RunAmdPostInstallDebloatAsync()
        {
            if (IsWorking) return;

            IsSlimming = true;
            StatusText = "Running AMD Debloat…";

            try
            {
                var progress = new Progress<string>(s => StatusText = s);
                bool ok = await _packageSlimmer.PostInstallDebloatAsync(progress);
                StatusText = ok ? "AMD Debloat completed." : "AMD Debloat failed.";
            }
            catch (Exception ex)
            {
                _log.Error($"AMD Debloat failed: {ex.Message}");
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = "AMD Debloat failed.";
            }
            finally
            {
                IsSlimming = false;
            }
        }

        /// <summary>
        /// Exact Radeon Software Slimmer Workflow:
        /// 1. Download official AMD driver package (with progress).
        /// 2. Extract package silently (with progress).
        /// 3. Discover real extracted packages, scheduled tasks, and INF components.
        /// 4. Open RadeonSlimmerDialog for user customization.
        /// 5. If approved, strip unselected, install display driver via pnputil / Setup.exe, and run post-install debloat.
        /// </summary>
        public async Task PrepareAndOpenAmdSlimmerAsync(GpuDriverItem item, Microsoft.UI.Xaml.XamlRoot xamlRoot)
        {
            if (item.Latest == null || IsWorking) return;

            _cts?.Dispose();
            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;

            IsInstalling = true;
            item.BeginBusy("Preparing AMD driver…");
            StatusText = $"{item.Name}: Verifying AMD package…";

            string workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KaliteKit", "drivers");
            string exePath = Path.Combine(workDir, $"amd-driver-{item.Latest.Version}.exe");
            string extractDir = Path.Combine(workDir, "extracted");

            try
            {
                Directory.CreateDirectory(workDir);

                // 1. Download if missing
                if (!File.Exists(exePath) || new FileInfo(exePath).Length < 10_000_000)
                {
                    _log.Info($"[Slimmer] Downloading AMD driver from {item.Latest.DownloadUrl}...");
                    var downloadProgress = new Progress<double>(pct =>
                    {
                        item.ProgressValue = pct;
                        StatusText = $"{item.Name}: Downloading AMD Adrenalin driver… {pct:F0}%";
                    });

                    await _downloadService.DownloadAsync(item.Latest.DownloadUrl, exePath, downloadProgress, ct);
                }

                // 2. Extract if not already extracted with primary display drivers and manifest
                bool hasExtractedFiles = Directory.Exists(extractDir) &&
                    Directory.EnumerateFiles(extractDir, "u0*.inf", SearchOption.AllDirectories).Any() &&
                    File.Exists(Path.Combine(extractDir, "Config", "InstallManifest.json"));

                if (!hasExtractedFiles)
                {
                    if (Directory.Exists(extractDir))
                    {
                        try { Directory.Delete(extractDir, true); } catch { }
                    }

                    _log.Info($"[Slimmer] Extracting AMD package {exePath} to {extractDir}...");
                    StatusText = $"{item.Name}: Extracting AMD Adrenalin packages…";
                    var extractStatus = new Progress<string>(s => StatusText = $"{item.Name}: {s}");
                    bool extracted = await _installService.ExtractAmdInstallerAsync(exePath, extractDir, extractStatus, ct);
                    if (!extracted)
                    {
                        throw new InvalidOperationException("Unable to extract official AMD Adrenalin package.");
                    }
                }

                // 3. Discover real extracted components
                var packages = _packageSlimmer.DiscoverPackages(extractDir);
                var tasks = _packageSlimmer.DiscoverScheduledTasks(extractDir);
                var displayComponents = _packageSlimmer.DiscoverDisplayComponents(extractDir);

                item.EndBusy();
                IsInstalling = false;

                // 4. Show Radeon Software Slimmer Dialog
                var slimmerDialog = new Views.RadeonSlimmerDialog(packages, tasks, displayComponents, _packageSlimmer)
                {
                    XamlRoot = xamlRoot
                };

                var result = await slimmerDialog.ShowAsync();
                if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    StatusText = "Custom installation cancelled.";
                    return;
                }

                // 5. Install phase
                IsInstalling = true;
                item.BeginBusy("Customizing and installing…");
                item.BusyText = "Stripping unselected packages…";
                StatusText = $"{item.Name}: Stripping unselected packages…";

                _packageSlimmer.StripUnselected(
                    extractDir,
                    slimmerDialog.Packages,
                    slimmerDialog.ScheduledTasks,
                    slimmerDialog.DisplayComponents);

                item.BusyText = "Installing WHQL graphics driver in background… (display may flicker)";
                StatusText = $"{item.Name}: Installing WHQL graphics driver…";
                var infPaths = DriverInstallService.FindAllAmdDisplayInfs(extractDir);
                _log.Info($"[Slimmer] Discovered {infPaths.Count} display INF(s) in '{extractDir}': {string.Join(", ", infPaths)}");

                if (infPaths.Count > 0)
                {
                    foreach (var inf in infPaths)
                    {
                        _log.Info($"[Slimmer] Installing display driver via pnputil: {inf}");
                        int pnpCode = await new ProcessManager(_log).RunAsync("pnputil", $"/add-driver \"{inf}\" /install", TimeSpan.FromMinutes(5));
                        _log.Info($"[Slimmer] pnputil returned exit code {pnpCode} for {Path.GetFileName(inf)}");
                    }

                    // Rescan devices to force PnP binding
                    await new ProcessManager(_log).RunAsync("pnputil", "/scan-devices", TimeSpan.FromSeconds(30));
                }

                // Install AMD Software: Adrenalin Edition application if present
                string? setupExe = Directory.Exists(extractDir)
                    ? Directory.EnumerateFiles(extractDir, "Setup.exe", SearchOption.AllDirectories).FirstOrDefault()
                    : null;

                if (setupExe != null)
                {
                    _log.Info($"[Slimmer] Installing AMD Software Adrenalin UI via Setup.exe: {setupExe}");
                    item.BusyText = "Installing AMD Software: Adrenalin Edition application…";
                    StatusText = $"{item.Name}: Installing AMD Software Adrenalin UI…";
                    int setupCode = await new ProcessManager(_log).RunAsync(setupExe, "-install", TimeSpan.FromMinutes(10));
                    _log.Info($"[Slimmer] AMD Setup.exe returned exit code {setupCode}");

                    // Wait for all spawned AMD installer child processes (AtiSetup, InstallManagerApp, etc.)
                    await WaitForAmdInstallProcessesAsync(item, ct);
                }

                // Register AMD Software MSIX Package if present
                string msixPath = @"C:\Program Files\AMD\CNext\CNext\RSXPackage.msix";
                if (File.Exists(msixPath))
                {
                    item.BusyText = "Registering control panel in Windows…";
                    StatusText = $"{item.Name}: Registering control panel…";
                    await new ProcessManager(_log).RunAsync("powershell", $"-NoProfile -Command \"Add-AppxPackage -Path '{msixPath}' -ErrorAction SilentlyContinue\"", TimeSpan.FromMinutes(2));
                }

                // Launch Radeon Software to confirm presence
                string radeonExe = @"C:\Program Files\AMD\CNext\CNext\RadeonSoftware.exe";
                if (File.Exists(radeonExe))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(radeonExe) { UseShellExecute = true });
                    }
                    catch { }
                }

                item.BusyText = "Disabling AMD telemetry background tasks…";
                StatusText = $"{item.Name}: Disabling AMD telemetry…";
                await _packageSlimmer.PostInstallDebloatAsync();

                _log.Success($"[Slimmer] AMD custom installation finished for {item.Name}");
                item.BusyText = "AMD driver and software installed successfully!";
                StatusText = $"{item.Name}: AMD driver and software installed successfully!";

                // Allow Windows PnP and DirectX display stack to settle
                await Task.Delay(2000, ct);
                await CheckForUpdatesAsync();
            }
            catch (OperationCanceledException)
            {
                StatusText = "Operation cancelled.";
            }
            catch (Exception ex)
            {
                _log.Error($"[Slimmer] Failed: {ex.Message}");
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = "Driver installation failed.";
            }
            finally
            {
                item.EndBusy();
                IsInstalling = false;
            }
        }

    private async Task WaitForAmdInstallProcessesAsync(GpuDriverItem item, CancellationToken ct = default)
        {
            var targetNames = new[] { "AtiSetup", "InstallManagerApp", "AMDSoftwareInstaller", "ATISetup", "Setup" };
            // Give 5 seconds for installer sub-processes to launch
            await Task.Delay(5000, ct);

            int maxWaitSeconds = 600;
            int elapsed = 5;

            while (elapsed < maxWaitSeconds && !ct.IsCancellationRequested)
            {
                var running = Process.GetProcesses()
                    .Where(p => targetNames.Any(t => p.ProcessName.Equals(t, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (running.Count == 0)
                {
                    _log.Info("[Slimmer] All AMD installer processes finished.");
                    break;
                }

                item.BusyText = $"Installation d'AMD Software en cours… ({running.Count} processus actifs, {elapsed}s)";
                await Task.Delay(2000, ct);
                elapsed += 2;
            }
        }
    }
}



