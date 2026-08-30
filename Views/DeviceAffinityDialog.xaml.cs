using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KalOS.ViewModels;

namespace KalOS.Views
{
    public sealed partial class DeviceAffinityDialog : ContentDialog
    {
        public DeviceAffinityViewModel ViewModel { get; }

        public DeviceAffinityDialog(PciDeviceItem device, IEnumerable<CpuCoreInfo> cores)
        {
            ViewModel = new DeviceAffinityViewModel(device, cores);
            this.InitializeComponent();
        }

        public bool IsSaved { get; private set; } = false;

        private void SaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            IsSaved = true;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectAll();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearAll();
        }

        private void SelectPCores_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectPCores();
        }

        private void SelectECores_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectECores();
        }
    }
}

