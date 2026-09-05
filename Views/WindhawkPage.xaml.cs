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
        _ = ViewModel.LoadAsync();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else if (App.Current is App { MainWindow: MainWindow window }) window.NavigateToPage(typeof(PersonalizationPage));
        else Frame.Navigate(typeof(PersonalizationPage));
    }
}
