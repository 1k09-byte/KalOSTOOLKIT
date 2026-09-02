using System;
using KalOS.Helpers;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

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
    /// Service to manage runtime backdrop material selection, including the
    /// optional user-picked tint color (Personalization → Tint Color). Uses the
    /// composition-level <see cref="SystemBackdropController"/>s (not the XAML
    /// wrapper types) because those expose TintColor/TintOpacity for Mica and
    /// Acrylic alike.
    /// </summary>
    public sealed class BackdropService : IDisposable
    {
        private const string ConfigFile = "app-backdrop.json";
        private const float TintOpacity = 0.8f;
        private BackdropType _currentBackdrop = BackdropType.Acrylic;
        private Window? _window;
        private ISystemBackdropControllerWithTargets? _controller;
        private SystemBackdropConfiguration? _configuration;
        private ICompositionSupportsSystemBackdrop? _target;
        private bool _disposed;

        public BackdropService()
        {
            // Restore the persisted backdrop + tint before the first window is shown.
            var config = JsonConfigHelper.LoadSync<BackdropConfig>(ConfigFile);
            if (config is not null && Enum.TryParse<BackdropType>(config.Backdrop, out var saved))
            {
                _currentBackdrop = saved;
            }
            CurrentTint = string.IsNullOrWhiteSpace(config?.TintColor) ? null : config.TintColor;
        }

        /// <summary>
        /// Gets the current backdrop type.
        /// </summary>
        public BackdropType CurrentBackdrop => _currentBackdrop;

        /// <summary>Current tint color as RRGGBB hex, or null for the default (no tint).</summary>
        public string? CurrentTint { get; private set; }

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

            Persist();
        }

        /// <summary>
        /// Sets the window tint color (RRGGBB hex, or null/empty for the default
        /// no-tint look), re-applies the current backdrop, and persists the choice.
        /// </summary>
        public void SetTintColor(string? hex)
        {
            CurrentTint = string.IsNullOrWhiteSpace(hex) ? null : hex.Trim();
            ApplyBackdrop(_currentBackdrop);
            Persist();
        }

        /// <summary>
        /// Keeps the backdrop controller's theme/input state in sync with the
        /// window — call on theme changes and window activation changes.
        /// </summary>
        public void UpdateSystemBackdropState(ElementTheme effectiveTheme, bool isInputActive = true)
        {
            if (_disposed || _configuration is null) return;
            _configuration.IsInputActive = isInputActive;
            _configuration.Theme = effectiveTheme switch
            {
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default
            };
        }

        /// <summary>Persists the backdrop + tint so both survive restarts.</summary>
        private void Persist()
        {
            _ = JsonConfigHelper.SaveAsync(ConfigFile, new BackdropConfig
            {
                Backdrop = _currentBackdrop.ToString(),
                TintColor = CurrentTint ?? string.Empty,
            });
        }

        private sealed class BackdropConfig
        {
            public string Backdrop { get; set; } = string.Empty;
            public string TintColor { get; set; } = string.Empty;
        }

        private void ApplyBackdrop(BackdropType backdrop)
        {
            if (_disposed || _window == null) return;

            _controller?.Dispose();
            _controller = null;
            // Release any XAML-managed backdrop so the controllers own the material.
            _window.SystemBackdrop = null;

            if (backdrop == BackdropType.None) return;

            try
            {
                var tint = KalOS.Models.TintPresets.ParseHex(CurrentTint);

                ISystemBackdropControllerWithTargets controller = backdrop switch
                {
                    BackdropType.Mica => new MicaController { Kind = MicaKind.Base },
                    BackdropType.MicaAlt => new MicaController { Kind = MicaKind.BaseAlt },
                    _ => new DesktopAcrylicController()
                };

                if (tint is { } color)
                {
                    // TintColor/TintOpacity live on the concrete controller types.
                    if (controller is MicaController mica)
                    {
                        mica.TintColor = color;
                        mica.TintOpacity = TintOpacity;
                    }
                    else if (controller is DesktopAcrylicController acrylic)
                    {
                        acrylic.TintColor = color;
                        acrylic.TintOpacity = TintOpacity;
                    }
                }

                _target ??= _window.As<ICompositionSupportsSystemBackdrop>();
                _configuration ??= new SystemBackdropConfiguration { IsInputActive = true };

                controller.AddSystemBackdropTarget(_target);
                controller.SetSystemBackdropConfiguration(_configuration);

                _controller = controller;
            }
            catch (Exception)
            {
                // Fallback: no backdrop if the system does not support it
                _controller?.Dispose();
                _controller = null;
                _window.SystemBackdrop = null;
            }
        }

        public void Dispose()
        {
            _controller?.Dispose();
            _controller = null;
        }

        /// <summary>
        /// Detaches and disposes the composition backdrop BEFORE the window is
        /// destroyed. A SystemBackdropController left attached to a window while
        /// XAML tears it down corrupts the CoreMessaging heap and crashes the
        /// process at exit — the 0xC0000005 access violation in
        /// ucrtbase/CoreMessagingXP seen on every close. After this, all
        /// backdrop operations become no-ops.
        /// </summary>
        public void Teardown()
        {
            _disposed = true;
            _controller?.Dispose();
            _controller = null;
            _configuration = null;
            _target = null;
            _window = null;
        }
    }
}