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
        /// The adapters the driver step will update, in order. When
        /// <see cref="UpdateAllGpus"/> is off this is just the selected one.
        /// </summary>
        public IReadOnlyList<GpuInfo> GpusToUpdate
        {
            get
            {
                if (SelectedGpu is null) return Array.Empty<GpuInfo>();
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

        // ── Software selection (Software page) ────────────────────────────

        /// <summary>Force-install the privacy extensions on every selected browser.</summary>
        [ObservableProperty] private bool _installExtensions = true;

        public List<SoftwarePick> BrowserPicks { get; } = new();
        public List<SoftwarePick> AppPicks { get; } = new();
        public List<SoftwarePick> RuntimePicks { get; } = new();
        public IEnumerable<SoftwarePick> AllPicks =>
            BrowserPicks.Concat(AppPicks).Concat(RuntimePicks);

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
            if (file is null) return; // user cancelled
            BackgroundImagePath = file.Path;
            BackgroundTouched = true;
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
                    // NVIDIA: the user picks from the WHQL release list.
                    var versions = await driver.GetVersionHistoryAsync(SelectedGpu);
                    _driverVersions = versions.ToList();
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

        public void LogStep(string name, bool success, string? detail)
        {
            StepLog.Add(new InstallStepLog(name, success, detail));
            // Notify so the Progress page's ListView refreshes.
            OnPropertyChanged(nameof(StepLog));
        }
    }
}
