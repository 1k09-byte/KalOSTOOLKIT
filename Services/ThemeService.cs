using System;
using KalOS.Helpers;
using Microsoft.UI.Xaml;

namespace KalOS.Services
{
    /// <summary>
    /// Service to manage application theme switching.
    /// </summary>
    public class ThemeService
    {
        private const string ConfigFile = "app-theme.json";

        // KalOS is a dark-first utility: with no saved preference the app is
        // explicitly Dark (rather than following the system), so the Settings
        // dropdown can never disagree with what's on screen.
        private ElementTheme _currentTheme = ElementTheme.Dark;

        /// <summary>
        /// Gets the current theme.
        /// </summary>
        public ElementTheme CurrentTheme => _currentTheme;

        /// <summary>
        /// Occurs when the theme changes.
        /// </summary>
        public event EventHandler<ElementTheme>? ThemeChanged;

        public ThemeService()
        {
            // Restore the persisted theme before the first window is shown.
            var config = JsonConfigHelper.LoadSync<ThemeConfig>(ConfigFile);
            if (config is not null && Enum.TryParse<ElementTheme>(config.Theme, out var saved))
            {
                _currentTheme = saved;
            }
        }

        /// <summary>
        /// Sets the application theme, notifies subscribers, and persists the choice.
        /// </summary>
        /// <param name="theme">The new element theme to apply.</param>
        public void SetTheme(ElementTheme theme)
        {
            if (_currentTheme == theme) return;
            _currentTheme = theme;
            ThemeChanged?.Invoke(this, theme);

            _ = JsonConfigHelper.SaveAsync(ConfigFile, new ThemeConfig { Theme = theme.ToString() });
        }

        private sealed class ThemeConfig
        {
            public string Theme { get; set; } = string.Empty;
        }
    }
}
