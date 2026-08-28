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
            this.InitializeComponent();
        }

        public bool IsSaved { get; private set; } = false;

        private void SaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            IsSaved = true;
            // Native ContentDialogs automatically hide after a button click
        }
    }
}
