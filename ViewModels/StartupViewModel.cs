using CommunityToolkit.Mvvm.ComponentModel;
using KaliteKit.Services;

namespace KaliteKit.ViewModels
{
    /// <summary>
    /// Backs the "Startup" section of the Settings page. In the consumer build
    /// startup is mandatory (the Run key is rewritten on every launch in App),
    /// so only the update-check toggle is shown there. The dev/edit build gets
    /// a real Run-at-startup toggle so it can be tested on and off.
    /// </summary>
    public sealed partial class StartupViewModel : ObservableObject
    {
        private readonly StartupTasksService _service;
        private readonly StartupSettings _settings;

        [ObservableProperty]
        private bool _runAtStartup;

        [ObservableProperty]
        private bool _checkUpdatesAtStartup = true;

#if CONSUMER_BUILD
        /// <summary>The on/off toggle only exists in the dev/edit build.</summary>
        public bool IsStartupToggleVisible => false;
#else
        public bool IsStartupToggleVisible => true;
#endif

        /// <summary>Inverse of the toggle visibility, for the always-on text.</summary>
        public bool IsStartupAlwaysOnVisible => !IsStartupToggleVisible;

        public StartupViewModel(StartupTasksService service)
        {
            _service = service;
            _settings = service.Load();

            _runAtStartup = StartupTasksService.IsRegisteredInRunKey();
            _checkUpdatesAtStartup = _settings.CheckUpdatesAtStartup;
        }

        partial void OnRunAtStartupChanged(bool value)
        {
            if (value) StartupTasksService.EnableAutostart();
            else StartupTasksService.DisableAutostart();
        }

        partial void OnCheckUpdatesAtStartupChanged(bool value)
            => Persist();

        /// <summary>Writes the current startup-banner preferences back to startup.json.</summary>
        private void Persist()
        {
            _settings.CheckUpdatesAtStartup = CheckUpdatesAtStartup;
            _service.Save(_settings);
        }
    }
}