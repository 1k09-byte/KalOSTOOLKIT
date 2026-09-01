using KalOS.Models;
using KalOS.Services;
using KalOS.Setup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KalOS.Setup.Views
{
    /// <summary>
    /// Step 2 — GPU selection + driver version selection. Mirrors the in-app
    /// GPU Drivers page but trimmed to the wizard's "pick one adapter + one
    /// version" flow. NVIDIA gets a silent install; AMD gets the slim-and-
    /// install path; Intel opens the vendor page (no silent install).
    /// </summary>
    public sealed partial class DriversPage : WizardPage
    {
        private InstallerViewModel Wizard => App.Wizard;

        public DriversPage()
        {
            InitializeComponent();
            Loaded += DriversPage_Loaded;
        }

        private async void DriversPage_Loaded(object sender, RoutedEventArgs e)
        {
            await Wizard.DetectGpusAsync();
            DetectRing.IsActive = false;
            DetectBar.IsOpen = true;
            DetectBar.Title = Wizard.Gpus.Count > 0 ? "Detection complete" : "No adapter found";
            DetectBar.Message = Wizard.GpuStatusText;
            DetectBar.Severity = Wizard.Gpus.Count > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            DetectBar.Content = null;

            GpuCombo.ItemsSource = Wizard.Gpus;
            if (Wizard.SelectedGpu is not null) GpuCombo.SelectedItem = Wizard.SelectedGpu;

            // "Update both GPUs" only makes sense with more than one adapter.
            UpdateAllCheck.Visibility = Wizard.Gpus.Count > 1
                ? Visibility.Visible : Visibility.Collapsed;

            UpdateForSelectedGpu();
            RefreshNav();
        }

        private async void GpuCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GpuCombo.SelectedItem is GpuInfo gpu)
            {
                Wizard.SelectedGpu = gpu;
                await Wizard.LoadDriverVersionsAsync();
                DriverCombo.ItemsSource = Wizard.DriverVersions;
                if (Wizard.SelectedDriver is not null)
                    DriverCombo.SelectedItem = Wizard.SelectedDriver;
            }
            UpdateForSelectedGpu();
            RefreshNav();
        }

        private void DriverCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriverCombo.SelectedItem is DriverInfo info)
            {
                Wizard.SelectedDriver = info;
                DriverHint.Text = info.ReleaseDate is { } d
                    ? $"Released {d:yyyy-MM-dd}"
                    : info.DisplayString ?? string.Empty;
            }
            RefreshNav();
        }

        private void UpdateForSelectedGpu()
        {
            var gpu = Wizard.SelectedGpu;
            bool nvidia = gpu?.IsNvidia == true;
            bool amd = gpu?.IsAmd == true;
            bool intel = gpu?.IsIntel == true;

            DriverCard.Visibility = nvidia ? Visibility.Visible : Visibility.Collapsed;
            IntelBar.IsOpen = intel;
            VersionsRing.IsActive = Wizard.IsLoadingVersions;

            if (amd)
            {
                DriverHint.Text = "AMD — the latest driver will be downloaded, slimmed, and installed silently.";
                DriverCard.Visibility = Visibility.Visible;
            }
        }

        public override bool CanProceed =>
            Wizard.SelectedGpu is not null &&
            (!Wizard.SelectedGpu.IsNvidia || Wizard.SelectedDriver is not null);
    }
}
