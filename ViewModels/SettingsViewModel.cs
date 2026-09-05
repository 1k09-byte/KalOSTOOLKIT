using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaliteKit.Services;
using Microsoft.UI.Xaml;

namespace KaliteKit.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ThemeService _themeService;
        private readonly BackdropService _backdropService;
        private readonly UpdateService _updateService;
        private readonly UpdateSettings _updateSettings;
        private UpdateInfo? _pendingUpdate;

        // Debounces settings-disk writes while the opacity slider is being dragged:
        // the live preview applies instantly (MainWindow listens to the property),
        // but the file is written at most once every 250 ms and once more after the
        // last change so the final value is never lost.
        public bool PendingUpdateIsRollback => _pendingUpdate?.IsRollback == true;

        [ObservableProperty]
        private string _selectedTheme = "Dark";

        [ObservableProperty]
        private string _selectedBackdrop = "Mica";

        [ObservableProperty]
        private bool _autoCheckForUpdates = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCheckingVisible))]
        private bool _isCheckingForUpdates;

        [ObservableProperty]
        private bool _hasUpdate;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLastChecked))]
        private string _lastCheckedText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCheckingVisible))]
        private bool _isDownloading;

        [ObservableProperty]
        private double _downloadProgress;

        [ObservableProperty]
        private string _updateStatusText = string.Empty;

        /// <summary>Raised when an automatic (startup) check finds a newer version, so the app can show a dialog.</summary>
        public event Action<Version>? UpdateAvailable;

        public string CurrentVersionText => $"You're on KaliteKit {UpdateService.CurrentVersion}";

        /// <summary>True while a check runs but nothing is downloading (drives the progress spinner).</summary>
        public bool IsCheckingVisible => IsCheckingForUpdates && !IsDownloading;

        /// <summary>True once any check has completed, so the last-checked line can appear.</summary>
        public bool HasLastChecked => !string.IsNullOrEmpty(LastCheckedText);

        /// <summary>
        /// Update features are consumer-build only (the distributed package built by
        /// publish-consumer.ps1 with CONSUMER_BUILD defined). The dev toolkit never
        /// shows the Updates section or nags about new versions.
        /// </summary>
#if CONSUMER_BUILD
        public bool IsUpdateFeatureVisible => true;
#else
        public bool IsUpdateFeatureVisible => false;
#endif

        public List<string> Themes { get; } = new() { "Light", "Dark", "System" };

        public List<string> Backdrops { get; } = new() { "Mica", "Mica Alt", "Acrylic" };

        [ObservableProperty]
        private string _backgroundImagePath = string.Empty;

        [ObservableProperty]
        private double _backgroundImageOpacity = 0.5;

        [ObservableProperty]
        private string _backgroundImageFit = "UniformToFill";

        [ObservableProperty]
        private string _backgroundImageVerticalAlignment = "Center";

        [ObservableProperty]
        private string _backgroundImageHorizontalAlignment = "Center";

        public List<string> ImageFits { get; } = new() { "UniformToFill", "Uniform", "Fill", "None" };

        public List<string> VerticalAlignments { get; } = new() { "Center", "Top", "Bottom" };

        public List<string> HorizontalAlignments { get; } = new() { "Center", "Left", "Right" };

        public bool HasBackgroundImage => !string.IsNullOrEmpty(BackgroundImagePath);

        public SettingsViewModel(ThemeService themeService, BackdropService backdropService, UpdateService updateService)
        {
            _themeService = themeService;
            _backdropService = backdropService;
            _updateService = updateService;

            // Map persisted theme to dropdown (System == Default).
            _selectedTheme = _themeService.CurrentTheme switch
            {
                ElementTheme.Light => "Light",
                ElementTheme.Default => "System",
                _ => "Dark"
            };
            _selectedBackdrop = _backdropService.CurrentBackdrop switch
            {
                BackdropType.Mica => "Mica",
                BackdropType.MicaAlt => "Mica Alt",
                BackdropType.Acrylic => "Acrylic",
                _ => "Mica"
            };

            _updateSettings = UpdateService.LoadSettings();
            _autoCheckForUpdates = _updateSettings.AutoCheckForUpdates;
            _updateStatusText = CurrentVersionText;

            // Load background image settings.
            _backgroundImagePath = _updateSettings.BackgroundImagePath;
            _backgroundImageOpacity = _updateSettings.BackgroundImageOpacity;
            _backgroundImageFit = _updateSettings.BackgroundImageFit;
            _backgroundImageVerticalAlignment = _updateSettings.BackgroundImageVerticalAlignment;
            _backgroundImageHorizontalAlignment = _updateSettings.BackgroundImageHorizontalAlignment;
        }

        partial void OnAutoCheckForUpdatesChanged(bool value)
        {
            _updateSettings.AutoCheckForUpdates = value;
            UpdateService.SaveSettings(_updateSettings);
        }

        partial void OnSelectedThemeChanged(string value)
        {
            var theme = value switch
            {
                "Light" => ElementTheme.Light,
                "System" => ElementTheme.Default,
                _ => ElementTheme.Dark
            };
            _themeService.SetTheme(theme);
        }

        partial void OnSelectedBackdropChanged(string value)
        {
            var backdrop = value switch
            {
                "Mica" => BackdropType.Mica,
                "Mica Alt" => BackdropType.MicaAlt,
                "Acrylic" => BackdropType.Acrylic,
                _ => BackdropType.None
            };
            _backdropService.SetBackdrop(backdrop);
        }

        partial void OnBackgroundImagePathChanged(string value)
        {
            _updateSettings.BackgroundImagePath = value;
            UpdateService.SaveSettings(_updateSettings);
            OnPropertyChanged(nameof(HasBackgroundImage));
        }

        partial void OnBackgroundImageOpacityChanged(double value)
        {
            _updateSettings.BackgroundImageOpacity = value;
            DebouncedSaveBackgroundSettings();
        }

        /// <summary>
        /// Saves background settings at most every 250 ms while a slider is dragged,
        /// plus one trailing save after the final change so nothing is lost.
        /// </summary>
        private System.Timers.Timer? _backgroundSaveDebounce;

        private void DebouncedSaveBackgroundSettings()
        {
            _backgroundSaveDebounce ??= new System.Timers.Timer(250) { AutoReset = false };
            _backgroundSaveDebounce.Elapsed += (_, _) =>
            {
                // Serialize the already-mutated settings instance (holds the latest
                // opacity). The timer is stopped before each restart, so this fires at
                // most ~250ms after the last slider change.
                UpdateService.SaveSettings(_updateSettings);
            };
            _backgroundSaveDebounce.Stop();
            _backgroundSaveDebounce.Start();
        }


        partial void OnBackgroundImageFitChanged(string value)
        {
            _updateSettings.BackgroundImageFit = value;
            UpdateService.SaveSettings(_updateSettings);
        }

        partial void OnBackgroundImageVerticalAlignmentChanged(string value)
        {
            _updateSettings.BackgroundImageVerticalAlignment = value;
            UpdateService.SaveSettings(_updateSettings);
        }

        partial void OnBackgroundImageHorizontalAlignmentChanged(string value)
        {
            _updateSettings.BackgroundImageHorizontalAlignment = value;
            UpdateService.SaveSettings(_updateSettings);
        }

        [RelayCommand]
        private async Task BrowseImageAsync()
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

            // WinUI 3 interop: must initialize with the window handle.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return; // user cancelled

            BackgroundImagePath = file.Path;
        }

        [RelayCommand]
        private void ClearImage()
        {
            BackgroundImagePath = string.Empty;
        }

        /// <summary>Kicks off the startup auto-check in the background (used by App.OnLaunched).</summary>
        public void RunStartupCheck()
        {
            if (!AutoCheckForUpdates || IsCheckingForUpdates) return;
            _ = CheckForUpdatesInternalAsync(raiseEvent: true);
        }

        [RelayCommand]
        public async Task CheckForUpdatesAsync()
        {
            await CheckForUpdatesInternalAsync(raiseEvent: false);
        }

        private async Task CheckForUpdatesInternalAsync(bool raiseEvent)
        {
            if (IsCheckingForUpdates) return;
            IsCheckingForUpdates = true;
            // Hide the Download &amp; install button while a fresh check runs.
            HasUpdate = false;
            UpdateStatusText = "Checking for updates…";
            try
            {
                UpdateInfo? info = await _updateService.CheckForUpdatesAsync();
                if (info == null)
                {
                    UpdateStatusText = "You're up to date.";
                }
                else
                {
                    _pendingUpdate = info;
                    OnPropertyChanged(nameof(PendingUpdateIsRollback));
                    HasUpdate = true;
                    UpdateStatusText = $"KaliteKit {info.Version} is available.";
                    if (raiseEvent && !info.IsRollback) UpdateAvailable?.Invoke(info.Version);
                }
                LastCheckedText = $"Last checked {DateTime.Now:HH:mm}";
            }
            catch (Exception)
            {
                // A failed check must never crash the app (it used to surface as
                // an unobserved task exception with no popup). Show a neutral
                // status instead so the user knows the check ran.
                UpdateStatusText = "Update check failed.";
                LastCheckedText = $"Last checked {DateTime.Now:HH:mm}";
            }
            finally
            {
                IsCheckingForUpdates = false;
            }
        }

        [RelayCommand]
        public async Task DownloadAndInstallAsync()
        {
            if (_pendingUpdate == null || IsCheckingForUpdates) return;
            IsCheckingForUpdates = true;
            IsDownloading = true;
            DownloadProgress = 0;
            UpdateStatusText = $"Downloading KaliteKit {_pendingUpdate.Version}…";
            try
            {
                // Progress<T> marshals callbacks back to the UI thread, which
                // x:Bind requires for the progress bar and status text.
                var progress = new Progress<double>(p =>
                {
                    DownloadProgress = p;
                    UpdateStatusText = $"{_pendingUpdate!.Version}… {p * 100:0}% downloaded";
                });
                bool ok = await _updateService.DownloadAndApplyAsync(_pendingUpdate, progress);
                if (ok)
                {
                    // Remember what was applied so the restarted app can show
                    // the update log (release notes + apply result).
                    UpdateService.SaveLastUpdateRecord(_pendingUpdate);
                    UpdateStatusText = "Installing — the app will restart automatically.";
                    // The apply helper is already running: exit so it can swap
                    // the files and relaunch the new build. Deferred so the UI
                    // thread unwinds first (avoids the native hard-error box).
                    App.ExitSoon();
                }
                else
                {
                    IsDownloading = false;
                    UpdateStatusText = "The update could not be installed. Check the update log and try again.";
                }
            }
            finally
            {
                IsCheckingForUpdates = false;
            }
        }
    }
}
