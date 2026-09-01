using System;
using System.Diagnostics;
using KalOS.Services;
using KalOS.Setup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KalOS.Setup.Views
{
    /// <summary>
    /// Home — the dashboard. Resolves the latest KalOS release so the user
    /// sees what they are about to install, reports any existing KalOS
    /// install so an upgrade is obvious, and links out to the project's
    /// guide / GitHub / Discord / release notes.
    /// </summary>
    public sealed partial class WelcomePage : WizardPage
    {
        private InstallerViewModel Wizard => App.Wizard;

        public WelcomePage()
        {
            InitializeComponent();
            Loaded += WelcomePage_Loaded;
        }

        private async void WelcomePage_Loaded(object sender, RoutedEventArgs e)
        {
            ReleaseBar.Message = "Resolving latest release…";

            string? existing = null;
            try { existing = ZipPackageInstaller.GetInstalledVersion(ZipPackageInstaller.DefaultInstallDir); }
            catch { }

            if (existing is not null)
            {
                InstalledBar.IsOpen = true;
                InstalledBar.Title = "Existing install detected";
                InstalledBar.Message = $"KalOS {existing} is installed — this wizard will upgrade it.";
            }

            try
            {
                await Wizard.ResolveReleaseAsync();
                ReleaseBar.Title = "Latest release ready";
                ReleaseBar.Message = Wizard.KalosReleaseInfo;
                ReleaseBar.Severity = InfoBarSeverity.Success;
            }
            catch (Exception ex)
            {
                ReleaseBar.Title = "Could not resolve the latest release";
                ReleaseBar.Message = ex.Message;
                ReleaseBar.Severity = InfoBarSeverity.Warning;
            }
            RefreshNav();
        }

        /// <summary>Opens the card's linked page in the default browser.</summary>
        private void Card_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string url) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        public override bool CanProceed => true;
    }
}