using KalOS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KalOS.Views
{
    /// <summary>
    /// Additional Tweaks — experimental Wi-Fi/Bluetooth radio toggles under
    /// Windows Settings.
    /// </summary>
    public sealed partial class AdditionalTweaksPage : Page
    {
        public AdditionalTweaksViewModel ViewModel { get; }

        public AdditionalTweaksPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<AdditionalTweaksViewModel>();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.LoadPrioritySeparation();
            ViewModel.LoadSvcHostSplit();
            ViewModel.LoadFullscreenMode();
            ViewModel.LoadUac();
            ViewModel.LoadVbs();
            ViewModel.LoadHvci();
            _ = ViewModel.DetectAsync();
        }
    }
}
