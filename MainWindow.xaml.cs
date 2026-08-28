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

            // Show the app version in the title bar and window title (e.g. "KalOS 1.1.4.0" or "KalOS Edit App").
            AppTitleBar.Title = $"KalOS {App.AppVersion}";
            Title = $"KalOS {App.AppVersion}";

            _themeService = App.Services.GetRequiredService<ThemeService>();
            _backdropService = App.Services.GetRequiredService<BackdropService>();

            // Add maximize on launch
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            App.MainWindowHandle = hwnd;
            
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            
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

            // Initialize the backdrop service — sets Window.SystemBackdrop
            _backdropService.Initialize(this);

            _themeService.ThemeChanged += OnThemeChanged;
            _backdropService.BackdropChanging += OnBackdropChanging;
            _backdropService.BackdropChanged += OnBackdropChanged;
            
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
            NavView.IsBackEnabled = ContentFrame.CanGoBack;

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

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();

                // Sync the nav highlight with the page we landed on
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
                    NavView.SelectedItem = item;
                }
            }
        }

        private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
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

        private void ToolSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            string query = sender.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            Type? pageType = query switch
            {
                var value when value.Contains("system") || value.Contains("hardware") || value.Contains("overview") => typeof(SystemOverviewPage),
                var value when value.Contains("home") => typeof(HomePage),
                var value when value.Contains("browser") || value.Contains("software") => typeof(BrowserPage),
                var value when value.Contains("gpu") || value.Contains("driver") => typeof(GpuDriversPage),
                var value when value.Contains("sdio") || value.Contains("other driver") => typeof(SdioPage),
                var value when value.Contains("bios") || value.Contains("uefi") || value.Contains("firmware") => typeof(BiosPage),
                var value when value.Contains("affinity") || value.Contains("cpu") => typeof(AffinityManagerPage),
                var value when value.Contains("personal") => typeof(PersonalizationPage),
                var value when value.Contains("tweak") => typeof(AdditionalTweaksPage),
                var value when value.Contains("visual") || value.Contains("effect") => typeof(VisualEffectsPage),
                var value when value.Contains("windhawk") || value.Contains("mod") => typeof(WindhawkPage),
                var value when value.Contains("setting") => typeof(SettingsPage),
                _ => null
            };

            if (pageType != null)
            {
                NavigateToPage(pageType);
                sender.Text = string.Empty;
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
            });
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
