using System;
using KalOS.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;

namespace KalOS.Services
{
    /// <summary>
    /// Supported backdrop types for the application window.
    /// </summary>
    public enum BackdropType
    {
        /// <summary>Standard Mica backdrop.</summary>
        Mica,

        /// <summary>Mica Alt backdrop.</summary>
        MicaAlt,

        /// <summary>Desktop Acrylic backdrop.</summary>
        Acrylic,

        /// <summary>No system backdrop (solid background).</summary>
        None
    }

    /// <summary>
    /// Service to manage runtime backdrop material selection via the Window.SystemBackdrop API.
    /// </summary>
    public class BackdropService
    {
        private const string ConfigFile = "app-backdrop.json";
        private BackdropType _currentBackdrop = BackdropType.Acrylic;
        private Window? _window;

        public BackdropService()
        {
            // Restore the persisted backdrop before the first window is shown.
            var config = JsonConfigHelper.LoadSync<BackdropConfig>(ConfigFile);
            if (config is not null && Enum.TryParse<BackdropType>(config.Backdrop, out var saved))
            {
                _currentBackdrop = saved;
            }
        }

        /// <summary>
        /// Gets the current backdrop type.
        /// </summary>
        public BackdropType CurrentBackdrop => _currentBackdrop;

        /// <summary>
        /// Occurs when the backdrop type is about to change.
        /// </summary>
        public event EventHandler<BackdropType>? BackdropChanging;

        /// <summary>
        /// Occurs when the backdrop type changes.
        /// </summary>
        public event EventHandler<BackdropType>? BackdropChanged;

        /// <summary>
        /// Initializes the service with the target window and applies the default backdrop.
        /// </summary>
        /// <param name="window">The main application window.</param>
        public void Initialize(Window window)
        {
            _window = window;
            ApplyBackdrop(_currentBackdrop);
        }

        /// <summary>
        /// Sets the system backdrop type, applies it, and notifies subscribers.
        /// </summary>
        /// <param name="backdrop">The new backdrop type.</param>
        public async void SetBackdrop(BackdropType backdrop)
        {
            if (_currentBackdrop == backdrop) return;

            BackdropChanging?.Invoke(this, backdrop);

            // Wait 160ms for the 150ms FadeInStoryboard to complete (with a tiny buffer)
            await System.Threading.Tasks.Task.Delay(160);

            _currentBackdrop = backdrop;
            ApplyBackdrop(backdrop);

            // Wait 50ms for the new backdrop to flush its white flash
            await System.Threading.Tasks.Task.Delay(50);

            BackdropChanged?.Invoke(this, backdrop);

            // Persist the choice so it survives restarts.
            _ = JsonConfigHelper.SaveAsync(ConfigFile, new BackdropConfig { Backdrop = backdrop.ToString() });
        }

        private sealed class BackdropConfig
        {
            public string Backdrop { get; set; } = string.Empty;
        }

        private void ApplyBackdrop(BackdropType backdrop)
        {
            if (_window == null) return;

            try
            {
                _window.SystemBackdrop = backdrop switch
                {
                    BackdropType.Mica => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                    BackdropType.MicaAlt => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                    BackdropType.Acrylic => new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
                    _ => null
                };
            }
            catch (Exception)
            {
                // Fallback: no backdrop if the system does not support it
                _window.SystemBackdrop = null;
            }
        }
    }
}
