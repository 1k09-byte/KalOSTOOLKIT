using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using KalOS.Services;
using KalOS.ViewModels;
using KalOS.Views;
using System.Linq;
using System.Collections.Generic;
using WinUIEx;

namespace KalOS
{
    /// <summary>
    /// The main application window containing the navigation shell.
    /// </summary>
    public sealed partial class MainWindow : WinUIEx.WindowEx
    {
        private static readonly Dictionary<string, Type> PageRegistry = new()
        {
            ["Home"] = typeof(HomePage),
            ["SystemOverview"] = typeof(SystemOverviewPage),
            ["Browsers"] = typeof(BrowserPage),
            ["GpuDrivers"] = typeof(GpuDriversPage),
            ["AffinityManager"] = typeof(AffinityManagerPage),
            ["Sdio"] = typeof(SdioPage),
            ["Bios"] = typeof(BiosPage),
            ["AdditionalTweaks"] = typeof(AdditionalTweaksPage),
            ["Personalization"] = typeof(PersonalizationPage),
            ["VisualEffects"] = typeof(VisualEffectsPage),
            ["Windhawk"] = typeof(WindhawkPage),
            ["Settings"] = typeof(SettingsPage),
        };

        private readonly ThemeService _themeService;
        private readonly BackdropService _backdropService;

        public void ShowModalOverlay(UIElement content)
        {
            AppModalContent.Content = content;
            AppModalOverlay.Visibility = Visibility.Visible;
        }

        public void HideModalOverlay()
        {
            AppModalOverlay.Visibility = Visibility.Collapsed;
            AppModalContent.Content = null;
        }
        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow"/> class.
        /// </summary>
        public MainWindow()
        {
            this.InitializeComponent();

            // Match the WinUI Gallery shell: the platform title bar owns the window chrome,
            // while NavigationView owns the app's information architecture.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            AppTitleBar.Title = "KalOS";
            Title = "KalOS";

            _themeService = App.Services.GetRequiredService<ThemeService>();
            _backdropService = App.Services.GetRequiredService<BackdropService>();

            // Add maximize on launch
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            App.MainWindowHandle = hwnd;
            
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            // Dismiss any open dialogs BEFORE teardown begins — closing a
            // window while a ContentDialog is open crashes native XAML
            // teardown (the 0xc0000005 "Exception Processing Message" box
            // seen on WER-disabled machines when the app is closed).
            try
            {
                appWindow.Closing += (_, _) => App.HideOpenDialogs();
            }
            catch { }

            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            // Set window icon
            try
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                    appWindow.SetIcon(iconPath);
            }
            catch { }

            // Apply default theme (Dark) to the root element
            RootGrid.RequestedTheme = _themeService.CurrentTheme;
            UpdateCaptionButtonColors(_themeService.CurrentTheme);

            // Initialize the backdrop service — sets the composition backdrop
            // controllers (Mica/Acrylic) so Personalization tints can apply.
            _backdropService.Initialize(this);
            _backdropService.UpdateSystemBackdropState(ResolveEffectiveTheme(_themeService.CurrentTheme));

            // Keep the backdrop's input state in sync with window focus.
            Activated += (_, args) => _backdropService.UpdateSystemBackdropState(
                ResolveEffectiveTheme(_themeService.CurrentTheme),
                args.WindowActivationState != WindowActivationState.Deactivated);

            _themeService.ThemeChanged += OnThemeChanged;
            // When in System (Default) mode, the OS theme can change underneath us.
            // ActualThemeChanged is the only signal that fires then — update
            // title-bar colors and backdrop (which are set manually) to stay in sync.
            RootGrid.ActualThemeChanged += (_, _) =>
            {
                if (_themeService.CurrentTheme == ElementTheme.Default)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var effective = ResolveEffectiveTheme(ElementTheme.Default);
                        UpdateCaptionButtonColors(effective);
                        _backdropService.UpdateSystemBackdropState(effective);
                    });
                }
            };
            _backdropService.BackdropChanging += OnBackdropChanging;
            _backdropService.BackdropChanged += OnBackdropChanged;

            // Apply background image from saved settings.
            ApplyBackgroundImage();

            // Drop the wallpaper decode when the window closes so an in-flight
            // BitmapImage completion can't touch the destroyed visual tree
            // during XAML teardown — a known source of the native 0xc0000005
            // close-crash dialog.
            Closed += (_, _) =>
            {
                try { BackgroundImage.Source = null; } catch { }
                _backgroundImageBitmap = null;
            };

            // Refresh the background image when settings change.
            var settingsVm = App.Services.GetRequiredService<ViewModels.SettingsViewModel>();
            settingsVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ViewModels.SettingsViewModel.BackgroundImagePath)
                    or nameof(ViewModels.SettingsViewModel.BackgroundImageFit)
                    or nameof(ViewModels.SettingsViewModel.BackgroundImageVerticalAlignment)
                    or nameof(ViewModels.SettingsViewModel.BackgroundImageHorizontalAlignment))
                {
                    DispatcherQueue.TryEnqueue(() => ApplyBackgroundImage());
                }
                else if (e.PropertyName is nameof(ViewModels.SettingsViewModel.BackgroundImageOpacity))
                {
                    // Opacity changes must NOT re-run ApplyBackgroundImage — re-decoding the
                    // bitmap blanks the image for a frame ("flicker to blank"). Just set the
                    // cached element's Opacity directly for a smooth live fade.
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (BackgroundImage.Visibility == Visibility.Visible)
                        {
                            BackgroundImage.Opacity = App.Services
                                .GetRequiredService<ViewModels.SettingsViewModel>()
                                .BackgroundImageOpacity;
                        }
                    });
                }
            };
            
            // Start background preloading of heavy ViewModels
            var services = App.Services;
            
            // Kick off Affinity Manager scan on UI thread (it will await Task.Run internally)
            var affinityVm = services.GetRequiredService<ViewModels.AffinityManagerViewModel>();
            _ = affinityVm.LoadDevicesAsync();

            // Pre-scan Browsers
            var browserVm = services.GetRequiredService<ViewModels.BrowserViewModel>();
            DispatcherQueue.TryEnqueue(() => _ = browserVm.ScanForInstalledBrowsersAsync());

            // Land on Home — the dashboard + restore points + status overview is the
            // natural opening screen for a freshly installed copy of the app.
            ContentFrame.Navigate(typeof(HomePage));
            ContentFrame.Navigated += ContentFrame_Navigated;

            // Sync the nav-pane highlight so the selected menu item matches the frame.
            // ItemInvoked is the only path that normally sets SelectedItem for us, so
            // we do it explicitly here on first launch — MenuItems[0] is the Home item.
            NavView.SelectedItem = NavView.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), "Home", StringComparison.Ordinal));
        }

        private void AppTitleBar_PaneToggleRequested(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
        {
            NavView.IsPaneOpen = !NavView.IsPaneOpen;
        }

        private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            // Ensure the pane highlight stays in sync when navigation was triggered
            // programmatically (e.g., Visual Effects → Back).
            var pageType = ContentFrame.CurrentSourcePageType;
            if (pageType != null)
            {
                NavigationViewItem? item = null;
                foreach (var menuItem in NavView.MenuItems.OfType<NavigationViewItem>())
                {
                    item ??= FindNavItemForPage(menuItem, pageType);
                }
                foreach (var footerItem in NavView.FooterMenuItems.OfType<NavigationViewItem>())
                {
                    item ??= FindNavItemForPage(footerItem, pageType);
                }
                // Only update when we found a matching nav item; sub-pages like
                // VisualEffects deliberately leave the parent (Personalization) highlighted
                // and rely on the in-page Back button + NavigationView back arrow.
                if (item != null) NavView.SelectedItem = item;
            }
        }

        private void ContentFrame_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Let scroll events propagate to the hosted page content.
        }

        private void OnThemeChanged(object? sender, ElementTheme theme)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    RootGrid.RequestedTheme = theme;
                }
                catch (Exception)
                {
                    RootGrid.RequestedTheme = theme;
                }
                UpdateCaptionButtonColors(theme);
                _backdropService.UpdateSystemBackdropState(ResolveEffectiveTheme(theme));
            });
        }

        /// <summary>Resolves Default to the effective rendered theme (used for the backdrop config).</summary>
        private ElementTheme ResolveEffectiveTheme(ElementTheme theme)
        {
            if (theme != ElementTheme.Default) return theme;
            var actual = RootGrid.ActualTheme;
            return actual == ElementTheme.Default ? ElementTheme.Dark : actual;
        }

        private void UpdateCaptionButtonColors(ElementTheme theme)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                var titleBar = appWindow.TitleBar;

                // Resolve Default to actual effective theme
                var effective = theme;
                if (effective == ElementTheme.Default)
                {
                    effective = RootGrid.ActualTheme;
                    if (effective == ElementTheme.Default)
                        effective = ElementTheme.Dark;
                }

                if (effective == ElementTheme.Light)
                {
                    // Light mode: white background needs black shapes
                    titleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                    titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                    titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
                    titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x66, 0x66, 0x66);
                    titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x15, 0x00, 0x00, 0x00);
                    titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x25, 0x00, 0x00, 0x00);
                }
                else
                {
                    // Dark mode: dark background needs white shapes
                    titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                    titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                    titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                    titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x99, 0x99, 0x99);
                    titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF);
                    titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF);
                }
                titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            }
            catch { }
        }

        private void OnBackdropChanging(object? sender, BackdropType backdrop)
        {
            // No-op: theme/backdrop changes are handled natively by the system
        }

        private void OnBackdropChanged(object? sender, BackdropType backdrop)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (backdrop == BackdropType.None)
                {
                    RootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
                }
                else
                {
                    RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
            });
        }

        // Cached decoded bitmap + the path it was decoded from. Re-applying fit/alignment
        // must not re-decode the file (that blanks the Image for a frame); only a path
        // change triggers a fresh decode.
        private string? _backgroundImageLoadedPath;
        private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _backgroundImageBitmap;

        /// <summary>
        /// Loads and applies the background image from saved settings.
        /// Called on startup and can be called again when settings change.
        /// </summary>
        private void ApplyBackgroundImage()
        {
            try
            {
                var settings = KalOS.Services.UpdateService.LoadSettings();
                if (string.IsNullOrEmpty(settings.BackgroundImagePath) || !System.IO.File.Exists(settings.BackgroundImagePath))
                {
                    BackgroundImage.Visibility = Visibility.Collapsed;
                    BackgroundOverlay.Visibility = Visibility.Collapsed;
                    BackgroundImage.Source = null;
                    _backgroundImageBitmap = null;
                    _backgroundImageLoadedPath = null;
                    return;
                }

                var uri = new Uri(settings.BackgroundImagePath, UriKind.Absolute);
                // Reuse the cached bitmap unless the path actually changed.
                if (_backgroundImageBitmap == null || _backgroundImageLoadedPath != settings.BackgroundImagePath)
                {
                    _backgroundImageBitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
                    _backgroundImageLoadedPath = settings.BackgroundImagePath;
                }
                BackgroundImage.Source = _backgroundImageBitmap;
                BackgroundImage.Opacity = settings.BackgroundImageOpacity;
                BackgroundImage.Stretch = settings.BackgroundImageFit switch
                {
                    "Uniform" => Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    "Fill" => Microsoft.UI.Xaml.Media.Stretch.Fill,
                    "None" => Microsoft.UI.Xaml.Media.Stretch.None,
                    _ => Microsoft.UI.Xaml.Media.Stretch.UniformToFill
                };
                BackgroundImage.HorizontalAlignment = settings.BackgroundImageHorizontalAlignment switch
                {
                    "Left" => HorizontalAlignment.Left,
                    "Right" => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Center
                };
                BackgroundImage.VerticalAlignment = settings.BackgroundImageVerticalAlignment switch
                {
                    "Top" => VerticalAlignment.Top,
                    "Bottom" => VerticalAlignment.Bottom,
                    _ => VerticalAlignment.Center
                };
                BackgroundImage.Visibility = Visibility.Visible;
                BackgroundOverlay.Visibility = Visibility.Visible;
            }
            catch
            {
                BackgroundImage.Visibility = Visibility.Collapsed;
                BackgroundOverlay.Visibility = Visibility.Collapsed;
                BackgroundImage.Source = null;
                _backgroundImageBitmap = null;
                _backgroundImageLoadedPath = null;
            }
        }
        
        public void NavigateToPage(Type pageType)
        {
            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType);
            }

            NavigationViewItem? item = null;
            foreach (var menuItem in NavView.MenuItems.OfType<NavigationViewItem>())
            {
                item ??= FindNavItemForPage(menuItem, pageType);
            }
            // FooterMenuItems is the documented location for secondary navigation such as Settings.
            foreach (var footerItem in NavView.FooterMenuItems.OfType<NavigationViewItem>())
            {
                item ??= FindNavItemForPage(footerItem, pageType);
            }
            NavView.SelectedItem = item;
        }

        private NavigationViewItem? FindNavItemForPage(NavigationViewItem parent, Type pageType)
        {
            if (PageRegistry.TryGetValue(parent.Tag?.ToString() ?? "", out var registeredType) && registeredType == pageType)
            {
                return parent;
            }

            foreach (var child in parent.MenuItems.OfType<NavigationViewItem>())
            {
                var found = FindNavItemForPage(child, pageType);
                if (found != null) return found;
            }

            return null;
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            // NavigationView can report the invoked item through either the container
            // or InvokedItem. Resolve both paths so every module remains clickable.
            if (args.IsSettingsInvoked)
            {
                NavigateToPage(typeof(SettingsPage));
                return;
            }

            var item = args.InvokedItemContainer as NavigationViewItem;
            var tag = item?.Tag?.ToString() ?? (args.InvokedItem as NavigationViewItem)?.Tag?.ToString();
            if (tag != null && PageRegistry.TryGetValue(tag, out var pageType))
            {
                NavigateToPage(pageType);
            }
        }
    }
}
