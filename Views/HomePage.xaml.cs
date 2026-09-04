using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KalOS.Services;
using KalOS.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KalOS.Views
{
    public sealed partial class HomePage : Page
    {
        public HomeViewModel ViewModel { get; }

        public HomePage()
        {
            this.InitializeComponent();
            ViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<HomeViewModel>(App.Services);

            Loaded += (_, _) =>
            {
                ViewModel.StartMetricsLoop();
                _themeService = App.Services.GetRequiredService<ThemeService>();
                _themeService.ThemeChanged += OnThemeChanged;
            };
            Unloaded += (_, _) =>
            {
                ViewModel.StopMetricsLoop();
                if (_themeService != null) _themeService.ThemeChanged -= OnThemeChanged;
            };
        }

        private ThemeService? _themeService;

        private void OnThemeChanged(object? sender, ElementTheme e) => ViewModel.OnThemeChanged();

        /// <summary>Navigates to the page tagged on a module tile (Tag holds the page type key).</summary>
        private void NavigateCard_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            Type? page = tag switch
            {
                "Browsers" => typeof(BrowserPage),
                "GpuDrivers" => typeof(GpuDriversPage),
                "AffinityManager" => typeof(AffinityManagerPage),
                "Personalization" => typeof(PersonalizationPage),
                "ProcessControl" => typeof(ProcessControlPage),
                "SystemOverview" => typeof(SystemOverviewPage),
                "AdditionalTweaks" => typeof(AdditionalTweaksPage),
                "Sdio" => typeof(SdioPage),
                "Bios" => typeof(BiosPage),
                _ => null
            };

            if (page != null && App.Current is App { MainWindow: MainWindow window })
            {
                window.NavigateToPage(page);
            }
        }

        private void JoinDiscord_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://discord.gg/faQgV6yayY") { UseShellExecute = true });
            }
            catch (Exception)
            {
                // Opening a browser must never crash the Home page.
            }
        }

        private async void OpenRestoreManager_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // Ensure list is fresh
            await ViewModel.LoadRestorePointsAsync();

            if (App.Current is App { MainWindow: MainWindow window })
            {
                var modal = new SystemRestoreModal(ViewModel);
                window.ShowModalOverlay(modal);
            }
            else
            {
                var xamlRoot = (App.Current as App)?.MainWindow?.Content?.XamlRoot
                            ?? this.XamlRoot
                            ?? this.Content?.XamlRoot;

                var dialog = new SystemRestoreDialog(ViewModel)
                {
                    XamlRoot = xamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private async void RestoreButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is RestorePointItem item)
            {
                var dialog = new ContentDialog
                {
                    Title = "Restore System",
                    Content = $"Are you sure you want to restore your system to '{item.Description}' ({item.CreationTime})?\n\nYour computer will restart automatically during the process.",
                    PrimaryButtonText = "Restore Now",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    ViewModel.RestoreSystem(item.SequenceNumber);
                }
            }
        }
    }
}
