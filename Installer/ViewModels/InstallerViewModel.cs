using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Models;
using KalOS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KalOS.Setup.ViewModels
{
    /// <summary>
    /// The single wizard state object every page binds to. Holds the user's
    /// selections (which KalOS version to deploy, which GPU/driver, which
    /// software) and the live progress the Progress page renders.
    /// </summary>
    public partial class InstallerViewModel : ObservableObject
    {
        private readonly IServiceProvider _services;

        // ── GPU / driver selection (Drivers page) ─────────────────────────

        [ObservableProperty] private bool _isDetectingGpus;
        [ObservableProperty] private string _gpuStatusText = "Detecting graphics adapters…";
        private List<GpuInfo> _gpus = new();
        public IReadOnlyList<GpuInfo> Gpus => _gpus;
        [ObservableProperty] private GpuInfo? _selectedGpu;

        /// <summary>When true the pipeline also updates every other detected adapter.</summary>
        [ObservableProperty] private bool _updateAllGpus = true;

        /// <summary>
        /// Opt-out for the GPU driver step. When true the wizard skips driver
        /// detection, downloads, and installs entirely — the pipeline only
        /// deploys KalOS and the selected software.
        /// </summary>
        [ObservableProperty] private bool _skipGpuDrivers;

        /// <summary>
        /// The adapters the driver step will update, in order. Empty when the
        /// user opted out of GPU drivers. When <see cref="UpdateAllGpus"/> is
        /// off this is just the selected one.
        /// </summary>
        public IReadOnlyList<GpuInfo> GpusToUpdate
        {
            get
            {
                if (SkipGpuDrivers || SelectedGpu is null) return Array.Empty<GpuInfo>();
                if (!UpdateAllGpus) return new[] { SelectedGpu };
                return _gpus.Where(g => !ReferenceEquals(g, SelectedGpu))
                            .Prepend(SelectedGpu)
                            .ToList();
            }
        }

        [ObservableProperty] private bool _isLoadingVersions;
        private List<DriverInfo> _driverVersions = new();
        public IReadOnlyList<DriverInfo> DriverVersions => _driverVersions;
        [ObservableProperty] private DriverInfo? _selectedDriver;

        /// <summary>True when the selected GPU has a silent install path (NVIDIA/AMD).</summary>
        public bool CanAutoInstallDriver =>
            SelectedGpu is { IsNvidia: true } or { IsAmd: true } && SelectedDriver is not null;

        // ── NVIDIA strip/keep checklist (Drivers page) ───────────────────
        // Mirrors the edit tool's install dialog: everything unchecked is
        // stripped from the package before the display-only pnputil install.
        // Defaults to display driver only — the same stripped install the
        // wizard has always done.
        [ObservableProperty] private bool _keepHdAudio;
        [ObservableProperty] private bool _keepPhysX;
        [ObservableProperty] private bool _keepNvidiaApp;
        [ObservableProperty] private bool _keepUsbc;
        [ObservableProperty] private bool _keepTelemetry;
        [ObservableProperty] private bool _keepMsvcRuntimes;
        [ObservableProperty] private bool _keepFrameViewSdk;
        [ObservableProperty] private bool _keepVirtualAudio;
        [ObservableProperty] private bool _keepNvPlatformControllers;
        [ObservableProperty] private bool _keepDlsr;
        [ObservableProperty] private bool _keepNvContainer;
        [ObservableProperty] private bool _keepShadowPlay;
        [ObservableProperty] private bool _keepNvBackend;
        [ObservableProperty] private bool _keepNvidiaAppMessageBus;

        /// <summary>The user's keep-choices, consumed by the pipeline's driver step.</summary>
        public NvidiaInstallComponents SelectedNvidiaComponents => new()
        {
            KeepHDAudio = KeepHdAudio,
            KeepPhysX = KeepPhysX,
            KeepNvidiaApp = KeepNvidiaApp,
            KeepUSBC = KeepUsbc,
            KeepTelemetry = KeepTelemetry,
            KeepMsvcRuntimes = KeepMsvcRuntimes,
            KeepFrameViewSdk = KeepFrameViewSdk,
            KeepVirtualAudio = KeepVirtualAudio,
            KeepNvPlatformControllers = KeepNvPlatformControllers,
            KeepDlsr = KeepDlsr,
            KeepNvContainer = KeepNvContainer,
            KeepShadowPlay = KeepShadowPlay,
            KeepNvBackend = KeepNvBackend,
            KeepNvidiaAppMessageBus = KeepNvidiaAppMessageBus,
        };

        // ── AMD strip/keep checklist (Drivers page) ──────────────────────
        // Mirrors the edit tool's Radeon Software Slimmer options: everything
        // unchecked is stripped from the Adrenalin package before the
        // display-only pnputil install, and AMD scheduled tasks are removed
        // during debloat unless kept.
        [ObservableProperty] private bool _keepRadeonSoftware;
        [ObservableProperty] private bool _keepAmdAudio;
        [ObservableProperty] private bool _keepAmdTelemetry;
        [ObservableProperty] private bool _keepAmdScheduledTasks;

        /// <summary>The user's AMD keep-choices, consumed by the pipeline's driver step.</summary>
        public AmdInstallComponents SelectedAmdComponents => new()
        {
            KeepRadeonSoftware = KeepRadeonSoftware,
            KeepAudio = KeepAmdAudio,
            KeepTelemetry = KeepAmdTelemetry,
            KeepScheduledTasks = KeepAmdScheduledTasks,
        };

        // ── Software selection (Software page) ────────────────────────────

        /// <summary>Force-install the privacy extensions on every selected browser.</summary>
        [ObservableProperty] private bool _installExtensions = true;

        public List<SoftwarePick> BrowserPicks { get; } = new();
        public List<SoftwarePick> AppPicks { get; } = new();
        public List<SoftwarePick> RuntimePicks { get; } = new();
        public IEnumerable<SoftwarePick> AllPicks =>
            BrowserPicks.Concat(AppPicks).Concat(RuntimePicks);

        // ── Tweaks & cleanup (Tweaks page) ────────────────────────────────
        // Native re-implementation of the privacy.sexy scripts: the catalog is
        // generated from the .bat files by tools/generate_tweaks.py and every
        // tweak runs through TweaksService (registry via Microsoft.Win32, files
        // via System.IO, DISM/schtasks/wevtutil via the built-in tools). All
        // categories default to ON so a default run matches what the scripts
        // did; uncheck any bucket to keep that part untouched.

        /// <summary>Master switch on the Tweaks page — when off the privacy /
        /// cleanup categories below don't run (the Customize page's Windows look
        /// choices are independent of it).</summary>
        [ObservableProperty] private bool _applyTweaks = true;

        [ObservableProperty] private bool _tweakApps = true;
        [ObservableProperty] private bool _tweakFeatures = true;
        [ObservableProperty] private bool _tweakPrivacy = true;
        [ObservableProperty] private bool _tweakServices = true;
        [ObservableProperty] private bool _tweakHistory = true;
        [ObservableProperty] private bool _tweakLogs = true;

        public string TweakAppsLabel => "Remove preinstalled apps";
        public string TweakFeaturesLabel => "Disable features & remove capabilities";
        public string TweakPrivacyLabel => "Privacy & telemetry";
        public string TweakServicesLabel => "Disable services & scheduled tasks";
        public string TweakHistoryLabel => "Clear recent history & activity";
        public string TweakLogsLabel => "Clear logs, temp & shadow copies";

        // A category only shows on the tweaks page once it actually has tweaks,
        // so the page never lists an empty checkbox group. The whole "What to
        // apply" card collapses when nothing is configured yet.
        private bool GroupVisible(params TweakGroup[] groups) =>
            groups.Any(g => TweaksService.All.Any(t => t.Group == g));

        public bool TweakAppsVisible => GroupVisible(TweakGroup.Apps);
        public bool TweakFeaturesVisible => GroupVisible(TweakGroup.Features, TweakGroup.Capabilities);
        public bool TweakPrivacyVisible => GroupVisible(TweakGroup.Privacy);
        public bool TweakServicesVisible => GroupVisible(TweakGroup.Services, TweakGroup.Tasks);
        public bool TweakHistoryVisible => GroupVisible(TweakGroup.History);
        public bool TweakLogsVisible => GroupVisible(TweakGroup.Logs);

        public bool AnyTweakCategoryVisible =>
            TweakAppsVisible || TweakFeaturesVisible || TweakPrivacyVisible
            || TweakServicesVisible || TweakHistoryVisible || TweakLogsVisible;

        public bool NoTweakCategoriesVisible => !AnyTweakCategoryVisible;

        public string TweakSubtitle =>
            "Run your privacy tweaks, app removals and cleanup natively after the install. Every category is on by default — uncheck anything you want to keep.";

        /// <summary>
        /// The tweak groups the pipeline will run, in a sensible order. The
        /// six privacy / cleanup categories are gated by the Tweaks page's
        /// master switch; the dark mode &amp; transparency defaults (a
        /// Personalization group) are chosen on the Customize page and are
        /// independent of that switch.
        /// </summary>
        public IReadOnlyList<TweakGroup> SelectedTweakGroups
        {
            get
            {
                var groups = new List<TweakGroup>();
                if (ApplyTweaks)
                {
                    if (TweakApps) groups.Add(TweakGroup.Apps);
                    if (TweakFeatures)
                    {
                        groups.Add(TweakGroup.Features);
                        groups.Add(TweakGroup.Capabilities);
                    }
                    if (TweakPrivacy) groups.Add(TweakGroup.Privacy);
                    if (TweakServices)
                    {
                        groups.Add(TweakGroup.Services);
                        groups.Add(TweakGroup.Tasks);
                    }
                    if (TweakHistory) groups.Add(TweakGroup.History);
                    if (TweakLogs) groups.Add(TweakGroup.Logs);
                }
                if (TweakPersonalization) groups.Add(TweakGroup.Personalization);
                return groups;
            }
        }

        // ── Windows look (Customize page) ─────────────────────────────────
        // The Customize step's appearance choices for Windows itself. Both are
        // applied at the very end of the install — deliberately after the
        // tweaks/cleanup step, because the Windhawk deploy's Explorer restart
        // makes the whole look (dark mode included) take effect without a
        // manual reboot.

        /// <summary>
        /// Dark mode &amp; transparency effects for Windows. Runs through the
        /// same native tweak engine as the catalog (it is a
        /// TweakGroup.Personalization group — see TweaksService), but it is
        /// chosen on the Customize page, and is independent of the Tweaks
        /// page's master switch since it is a look choice, not a tweak.
        /// </summary>
        [ObservableProperty] private bool _tweakPersonalization = true;

        public string TweakPersonalizationLabel => "Dark mode & transparency effects";

        /// <summary>
        /// When on, the pipeline installs Windhawk and deploys the curated
        /// mod set from Assets/windhawk_mods.json (the dark translucent
        /// dock-style taskbar customization the main app also offers under
        /// Personalization).
        /// </summary>
        [ObservableProperty] private bool _installWindhawkCustomization = true;

        // ── Customization (Customize page) ────────────────────────────────

        /// <summary>The full tint palette (shared with the main app's catalog).</summary>
        public IReadOnlyList<TintPreset> Tints => TintPresets.All;

        /// <summary>
        /// The chosen tint card. Default (no tint) is pre-selected so the grid
        /// always shows a selection; a preset with a non-null Hex carries a real
        /// color that the post-install step writes into the app's backdrop config.
        /// </summary>
        [ObservableProperty] private TintPreset? _selectedTint;

        /// <summary>Custom picker color as "#RRGGBB", or null when a preset card is chosen.</summary>
        [ObservableProperty] private string? _customTintHex;

        /// <summary>True once the user touched the tint grid (even picking Default) —
        /// drives whether the post-install step writes the backdrop config at all.</summary>
        public bool TintTouched { get; private set; }

        /// <summary>True once the user touched the background image (even clearing it).</summary>
        public bool BackgroundTouched { get; private set; }

        /// <summary>Background image the user picked in the wizard.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasBackgroundImage))]
        [NotifyPropertyChangedFor(nameof(BackgroundImageFileName))]
        private string _backgroundImagePath = string.Empty;

        public bool HasBackgroundImage => !string.IsNullOrEmpty(BackgroundImagePath);

        public string BackgroundImageFileName =>
            string.IsNullOrEmpty(BackgroundImagePath) ? string.Empty : Path.GetFileName(BackgroundImagePath);

        /// <summary>The tint to apply to the installed app: preset hex, custom hex, or null.</summary>
        public string? EffectiveTintHex =>
            SelectedTint is { Hex: not null } ? SelectedTint.Hex
            : CustomTintHex;

        public InstallerViewModel(IServiceProvider services)
        {
            _services = services;
            _selectedTint = Tints.First(); // Default — no tint unless the user picks one.
        }

        partial void OnSelectedTintChanged(TintPreset? value)
        {
            if (value is null) return;
            CustomTintHex = null;
            TintTouched = true;
        }

        /// <summary>Applies a custom color from the picker (deselects the preset cards).</summary>
        public void ApplyCustomTint(Windows.UI.Color color)
        {
            SelectedTint = null;
            CustomTintHex = "#" + TintPresets.ToHex(color);
            TintTouched = true;
        }

        [RelayCommand]
        private async Task BrowseBackgroundImageAsync()
        {
            // The broker-based WinUI picker can hard-fail in elevated contexts
            // (this app runs requireAdministrator, and on machines logged in as
            // the built-in Administrator its activation can take the process
            // down with no managed exception at all). So: try the native picker,
            // and only fall back to a helper-process dialog when it THROWS — a
            // normal user cancel must not open a second dialog.
            string? picked;
            try
            {
                var (succeeded, path) = await PickImageViaWinUiAsync();
                if (succeeded)
                {
                    picked = path;
                }
                else
                {
                    picked = await PickImageViaHelperProcessAsync();
                }
            }
            catch
            {
                try { picked = await PickImageViaHelperProcessAsync(); }
                catch { return; }
            }

            if (string.IsNullOrEmpty(picked) || !File.Exists(picked)) return; // user cancelled
            BackgroundImagePath = picked;
            BackgroundTouched = true;
        }

        /// <summary>
        /// The normal WinUI file picker. Returns (true, path-or-null) when the
        /// picker itself worked (regardless of whether the user picked a file)
        /// and (false, null) when it threw — the caller falls back then.
        /// </summary>
        private async Task<(bool Succeeded, string? Path)> PickImageViaWinUiAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                    ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                };
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".gif");
                picker.FileTypeFilter.Add(".tiff");
                picker.FileTypeFilter.Add(".webp");

                // Unpackaged WinUI 3 interop: the picker needs the window handle.
                if (App.MainWindow is { } window)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }

                var file = await picker.PickSingleFileAsync();
                return (true, file?.Path);
            }
            catch
            {
                return (false, null);
            }
        }

        /// <summary>
        /// Fallback image picker: an OpenFileDialog owned by a helper
        /// powershell process. The dialog belongs to that process instead of
        /// this one, so elevation / package-identity / broker restrictions
        /// never apply. Prints the chosen path to stdout; empty = cancelled.
        /// </summary>
        private static async Task<string?> PickImageViaHelperProcessAsync()
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.OpenFileDialog; $d.Filter = 'Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff;*.webp'; $d.Title = 'Choose a background image'; if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($d.FileName) }\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            string output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            return output.Length > 0 ? output : null;
        }

        [RelayCommand]
        private void ClearBackgroundImage()
        {
            BackgroundImagePath = string.Empty;
            BackgroundTouched = true;
        }

        // ── KalOS release resolution ──────────────────────────────────────

        [ObservableProperty] private string _kalosReleaseInfo = "Resolving latest release…";
        public GitHubReleaseInfo? ResolvedRelease { get; private set; }

        // ── Progress (Progress page) ───────────────────────────────────────

        [ObservableProperty] private bool _isRunning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
        private double _overallProgress;

        [ObservableProperty] private string _currentStep = string.Empty;
        [ObservableProperty] private string _currentDetail = string.Empty;

        /// <summary>The overall bar as a readout label, e.g. "42%".</summary>
        public string ProgressPercentText => $"{(int)Math.Round(OverallProgress)}%";
        public List<InstallStepLog> StepLog { get; } = new();

        // ── Finish ─────────────────────────────────────────────────────────

        [ObservableProperty] private bool _installSucceeded;
        [ObservableProperty] private string _finishSummary = string.Empty;

        // ── Detection / version loading ────────────────────────────────────

        public async Task DetectGpusAsync()
        {
            IsDetectingGpus = true;
            GpuStatusText = "Detecting graphics adapters…";
            try
            {
                var detection = _services.GetRequiredService<GpuDetectionService>();
                var gpus = await detection.GetGpusAsync();
                _gpus = gpus.Count > 0 ? gpus : new() { GpuInfo.Unknown() };
                SelectedGpu = _gpus[0];
                GpuStatusText = _gpus.Count == 0
                    ? "No graphics adapter detected."
                    : $"Found {_gpus.Count} adapter(s). Select one to check for a driver update.";
                OnPropertyChanged(nameof(Gpus));
                await LoadDriverVersionsAsync();
            }
            catch (Exception ex)
            {
                GpuStatusText = $"GPU detection failed: {ex.Message}";
            }
            finally
            {
                IsDetectingGpus = false;
            }
        }

        public async Task LoadDriverVersionsAsync()
        {
            if (SelectedGpu is null)
            {
                _driverVersions.Clear();
                SelectedDriver = null;
                OnPropertyChanged(nameof(DriverVersions));
                return;
            }

            IsLoadingVersions = true;
            try
            {
                var driver = _services.GetRequiredService<DriverService>();

                if (SelectedGpu.IsNvidia)
                {
                    // NVIDIA: the user picks from the WHQL release list. The
                    // lookup API can lag a brand-new release or go stale, so the
                    // curated latest (the same fallback the in-app GPU Drivers
                    // page uses) is always offered at the top when it is newer
                    // than the newest listed version — the wizard must never
                    // default to an outdated driver, and never download one.
                    var versions = (await driver.GetVersionHistoryAsync(SelectedGpu)).ToList();
                    var curated = NvidiaDriverProvider.GetCuratedLatest();
                    if (curated.Version is not null
                        && (versions.Count == 0
                            || DriverVersionComparer.Compare("NVIDIA", versions[0].Version, curated.Version) < 0))
                    {
                        versions.Insert(0, curated);
                    }
                    _driverVersions = versions;
                }
                else if (SelectedGpu.IsAmd)
                {
                    // AMD has no pick-a-version flow: surface the latest Adrenalin
                    // as a single auto-selected entry so the wizard's silent
                    // slim-and-install pipeline has a real DownloadUrl to work with
                    // (otherwise the AMD step would be skipped as "no driver").
                    var latest = await ResolveLatestDriverAsync(SelectedGpu);
                    _driverVersions = latest is null
                        ? new List<DriverInfo>()
                        : new List<DriverInfo>
                        {
                            new()
                            {
                                Version = "Latest (auto)",
                                DownloadUrl = latest.DownloadUrl,
                                ReleaseDate = latest.ReleaseDate,
                                DisplayString = latest.DisplayString ?? $"Latest Adrenalin ({latest.Version})"
                            }
                        };
                }
                else
                {
                    // Intel / unknown: no silent install — open the vendor page.
                    _driverVersions.Clear();
                }

                SelectedDriver = _driverVersions.FirstOrDefault();
                OnPropertyChanged(nameof(DriverVersions));
            }
            catch (Exception ex)
            {
                _driverVersions.Clear();
                SelectedDriver = null;
                OnPropertyChanged(nameof(DriverVersions));
                GpuStatusText = $"Could not load driver versions: {ex.Message}";
            }
            finally
            {
                IsLoadingVersions = false;
            }
        }

        public void BuildSoftwarePicks()
        {
            BrowserPicks.Clear(); AppPicks.Clear(); RuntimePicks.Clear();
            BrowserPicks.AddRange(SoftwareCatalog.Browsers.Select(e => new SoftwarePick { Entry = e }));
            AppPicks.AddRange(SoftwareCatalog.Apps.Select(e => new SoftwarePick { Entry = e }));
            // Pre-select the common runtimes (latest VC++ x64, .NET 8) so the user
            // gets a working baseline without expanding every group.
            foreach (var pick in SoftwareCatalog.Runtimes.Select(e => new SoftwarePick { Entry = e }))
            {
                pick.IsSelected =
                    pick.Entry.Name == "Visual C++ 2015-2022 (x64)" ||
                    pick.Entry.Name == ".NET 8.0 Desktop Runtime";
                RuntimePicks.Add(pick);
            }

            // The lists are mutated in place, so notify listeners explicitly —
            // otherwise x:Bind OneWay lists on the Software page stay empty.
            OnPropertyChanged(nameof(BrowserPicks));
            OnPropertyChanged(nameof(AppPicks));
            OnPropertyChanged(nameof(RuntimePicks));
        }

        public async Task ResolveReleaseAsync(CancellationToken ct = default)
        {
            var client = _services.GetRequiredService<GitHubReleaseClient>();
            ResolvedRelease = await client.GetLatestReleaseAsync(ct);
            KalosReleaseInfo = ResolvedRelease is null
                ? "Could not reach GitHub. KalOS install will use the script fallback."
                : $"KalOS {ResolvedRelease.Version} ({ResolvedRelease.Tag})";
        }

        /// <summary>
        /// Resolves the newest driver the vendor knows about for a GPU
        /// (NVIDIA WHQL, AMD Adrenalin, or the Intel vendor page). Used for the
        /// extra adapters when <see cref="UpdateAllGpus"/> is on. Never throws.
        /// </summary>
        public async Task<DriverInfo?> ResolveLatestDriverAsync(GpuInfo gpu)
        {
            try
            {
                var driver = _services.GetRequiredService<DriverService>();
                var check = await driver.CheckForUpdateAsync(gpu);
                return check.LatestDriver;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The software items the user ticked, in install order.</summary>
        public IReadOnlyList<CatalogEntry> SelectedSoftware =>
            AllPicks.Where(p => p.IsSelected).Select(p => p.Entry).ToList().AsReadOnly();

        // ── Run the pipeline ──────────────────────────────────────────────

        public async Task RunAsync(Action? onFinished = null)
        {
            var pipeline = _services.GetRequiredService<InstallerPipeline>();
            IsRunning = true;
            StepLog.Clear();
            try
            {
                await pipeline.RunAsync(this, onFinished);
            }
            finally
            {
                IsRunning = false;
            }
        }

        public void LogStep(string name, bool success, string? detail, bool skipped = false)
        {
            StepLog.Add(new InstallStepLog(name, success, detail, skipped));
            // Notify so the Progress page's ListView refreshes.
            OnPropertyChanged(nameof(StepLog));
        }
    }
}
