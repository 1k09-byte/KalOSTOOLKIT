using KaliteKit.Models;
using KaliteKit.Services;
using KaliteKit.Setup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KaliteKit.Setup.Views
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
            UpdateForSkipState();
            RefreshNav();
        }

        private void SkipGpuCheck_Changed(object sender, RoutedEventArgs e)
        {
            // The TwoWay binding may push the new value into the VM only after
            // this event fires, so read the checkbox's own state and push it
            // into the VM up front — otherwise the note and the Next button
            // would reflect the previous toggle (stale data).
            bool skip = SkipGpuCheck.IsChecked == true;
            Wizard.SkipGpuDrivers = skip;
            UpdateForSkipState(skip);
            RefreshNav();
        }

        /// <summary>Applies the current skip state to the page chrome.</summary>
        private void UpdateForSkipState() => UpdateForSkipState(Wizard.SkipGpuDrivers);

        /// <summary>
        /// Shows or hides the whole GPU section based on the skip toggle.
        /// When the user opted out of GPU drivers nothing is detected/installed
        /// and the page can always proceed.
        /// </summary>
        private void UpdateForSkipState(bool skip)
        {
            bool install = !skip;
            DetectBar.Visibility = install ? Visibility.Visible : Visibility.Collapsed;
            GpuCard.Visibility = install ? Visibility.Visible : Visibility.Collapsed;
            SkipNote.Visibility = install ? Visibility.Collapsed : Visibility.Visible;
            UpdateAllCheck.Visibility = install && Wizard.Gpus.Count > 1
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateForSelectedGpu();
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
            bool install = !Wizard.SkipGpuDrivers;
            var gpu = Wizard.SelectedGpu;
            bool nvidia = gpu?.IsNvidia == true;
            bool amd = gpu?.IsAmd == true;
            bool intel = gpu?.IsIntel == true;
            // Laptop detection (chassis type / battery + model name marker).
            bool laptop = gpu?.IsMobileGpu == true;

            DriverCard.Visibility = install && (nvidia || amd) ? Visibility.Visible : Visibility.Collapsed;
            NvidiaOptionsCard.Visibility = install && nvidia ? Visibility.Visible : Visibility.Collapsed;
            AmdOptionsCard.Visibility = install && amd ? Visibility.Visible : Visibility.Collapsed;
            IntelBar.IsOpen = install && intel;
            VersionsRing.IsActive = install && Wizard.IsLoadingVersions;

            if (nvidia)
            {
                VendorNote.Text = laptop
                    ? "Laptop detected — the NVIDIA notebook (mobile) driver series is queried; the desktop installer would reject this hardware."
                    : "Desktop detected — the NVIDIA Game Ready desktop package is queried.";
                VendorNote.Visibility = Visibility.Visible;
            }
            else if (amd)
            {
                DriverHint.Text = "AMD — the latest driver will be downloaded, slimmed, and installed silently.";
                VendorNote.Text = laptop
                    ? "Laptop detected — the combined desktop+notebook INF package is required for laptop iGPUs/dGPUs and is resolved automatically."
                    : null;
                VendorNote.Visibility = laptop ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                VendorNote.Visibility = Visibility.Collapsed;
            }
        }

        public override bool CanProceed =>
            Wizard.SkipGpuDrivers ||
            (Wizard.SelectedGpu is not null &&
             (!Wizard.SelectedGpu.IsNvidia || Wizard.SelectedDriver is not null));
    }
}
