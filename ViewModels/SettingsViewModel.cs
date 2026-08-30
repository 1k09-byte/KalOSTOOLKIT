using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Services;
using Microsoft.UI.Xaml;

namespace KalOS.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ThemeService _themeService;
        private readonly BackdropService _backdropService;
        private readonly UpdateService _updateService;
        private readonly UpdateSettings _updateSettings;
        private UpdateInfo? _pendingUpdate;

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

        public string CurrentVersionText => $"You're on KalOS {UpdateService.CurrentVersion}";

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

        public List<string> Themes { get; } = new() { "Light", "Dark" };

        public List<string> Backdrops { get; } = new() { "Mica", "Mica Alt", "Acrylic" };

        public SettingsViewModel(ThemeService themeService, BackdropService backdropService, UpdateService updateService)
        {
            _themeService = themeService;
            _backdropService = backdropService;
            _updateService = updateService;

            // The app is dark-first: only an explicit Light preference (or a
            // persisted one) selects Light — everything else reports Dark so the
            // dropdown always matches what is actually rendered.
            _selectedTheme = _themeService.CurrentTheme == ElementTheme.Light ? "Light" : "Dark";
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
        }

        partial void OnAutoCheckForUpdatesChanged(bool value)
        {
            _updateSettings.AutoCheckForUpdates = value;
            UpdateService.SaveSettings(_updateSettings);
        }

        partial void OnSelectedThemeChanged(string value)
        {
            var theme = value == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
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
                    UpdateStatusText = $"KalOS {info.Version} is available.";
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
            UpdateStatusText = $"Downloading KalOS {_pendingUpdate.Version}…";
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
                    // the files and relaunch the new build.
                    Environment.Exit(0);
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
