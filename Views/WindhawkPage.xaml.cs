using System;
using KalOS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KalOS.Views;

public sealed partial class WindhawkPage : Page
{
    public WindhawkPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<WindhawkViewModel>();
    }

    public WindhawkViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Refresh the manifest + installed state every time the page is shown,
        // so a deploy done earlier (or a manual Windhawk change) is reflected.
        _ = ViewModel.LoadAsync();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else if (App.Current is App { MainWindow: MainWindow window })
        {
            window.NavigateToPage(typeof(PersonalizationPage));
        }
        else
        {
            Frame.Navigate(typeof(PersonalizationPage));
        }
    }




    private void UpdateMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WindhawkModItem item)
        {
            _ = ViewModel.UpdateModAsync(item);
        }
    }

    private void UninstallMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WindhawkModItem item)
        {
            _ = ViewModel.UninstallModAsync(item);
        }
    }
}
