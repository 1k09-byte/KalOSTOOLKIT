using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using KalOS.ViewModels;

namespace KalOS.Views
{
    public sealed partial class BrowserPage : Page
    {
        public BrowserViewModel ViewModel { get; }

        public BrowserPage()
        {
            ViewModel = App.Services.GetRequiredService<BrowserViewModel>();
            this.InitializeComponent();

            // SelectorBar needs an explicit SelectedItem for the pill to render;
            // Web Browsers is the default view.
            CategorySelector.SelectedItem = BrowsersSelectorItem;
        }

        private void CategorySelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            BrowsersSection.Visibility = sender.SelectedItem == BrowsersSelectorItem ? Visibility.Visible : Visibility.Collapsed;
            SoftwareSection.Visibility = sender.SelectedItem == SoftwareSelectorItem ? Visibility.Visible : Visibility.Collapsed;
            RuntimesSection.Visibility = sender.SelectedItem == RuntimesSelectorItem ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            if (!ViewModel.HasScanned && !ViewModel.IsScanning)
            {
                await ViewModel.ScanForInstalledBrowsersAsync();
            }

        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is InstallableItem item)
            {
                ViewModel.InstallItemCommand.Execute(item);
            }
        }

        private async void UninstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is InstallableItem item)
            {
                var dialog = new ContentDialog
                {
                    Title = "Uninstall confirmation",
                    PrimaryButtonText = "Uninstall",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                if (item is BrowserItem)
                {
                    dialog.Content = $"Uninstall {item.Name} and erase all of its data?";
                }
                else
                {
                    dialog.Content = $"Uninstall {item.Name}?";
                }

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    ViewModel.UninstallItemCommand.Execute(item);
                }
            }
        }

        private void DirectInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is InstallableItem item)
            {
                ViewModel.DirectInstallCommand.Execute(item);
            }
        }

        private void CancelInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is InstallableItem item)
            {
                ViewModel.CancelInstallCommand.Execute(item);
            }
        }
    }
}
