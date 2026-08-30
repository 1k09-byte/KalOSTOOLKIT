using Microsoft.UI.Xaml.Controls;
using KalOS.ViewModels;
using System.Collections.Generic;

namespace KalOS.Views
{
    public sealed partial class DeviceAffinityDialog : ContentDialog
    {
        public DeviceAffinityViewModel ViewModel { get; }

        public DeviceAffinityDialog(PciDeviceItem device, IEnumerable<CpuCoreInfo> cores)
        {
            ViewModel = new DeviceAffinityViewModel(device, cores);

            // WinUI caps ContentDialog width at the ContentDialogMaxWidth theme
            // resource (548) regardless of the inner layout. Override it on this
            // instance so the dialog opens wider. Corner radius is set in XAML
            // via the OverlayCornerRadius resource.
            this.Resources["ContentDialogMaxWidth"] = 920d;

            this.InitializeComponent();
        }

        public bool IsSaved { get; private set; } = false;

        private void SaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            IsSaved = true;
            // Native ContentDialogs automatically hide after a button click
        }

        private void SelectAllThreads_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            => ViewModel.SetAllThreads(true);

        private void ClearThreads_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            => ViewModel.SetAllThreads(false);
    }
}
