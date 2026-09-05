using System;
using System.Diagnostics;
using KaliteKit.Services;
using KaliteKit.Setup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KaliteKit.Setup.Views
{
    /// <summary>
    /// Home — the dashboard. In the standalone offline installer it reports
    /// that KaliteKit is bundled in the exe (no network); in the consumer app's
    /// embedded wizard it resolves the latest GitHub release so the user sees
    /// what they are about to deploy. Both report any existing KaliteKit install
    /// so an upgrade is obvious.
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
            string? existing = null;
            try { existing = ZipPackageInstaller.GetInstalledVersion(ZipPackageInstaller.DefaultInstallDir); }
            catch { }

            if (existing is not null)
            {
                InstalledBar.IsOpen = true;
                InstalledBar.Title = "Existing install detected";
                InstalledBar.Message = $"KaliteKit {existing} is installed — this wizard will upgrade it.";
            }

            // Standalone offline installer: KaliteKit rides inside this exe, so the
            // banner states the local package instead of phoning GitHub. No
            // network call happens on this page at all.
            if (!SetupState.Embedded)
            {
                if (BundledPayload.HasPayload)
                {
                    ReleaseBar.Title = "Offline package ready";
                    ReleaseBar.Message =
                        "KaliteKit is bundled inside this installer — it installs without an internet connection.";
                    ReleaseBar.Severity = InfoBarSeverity.Success;
                }
                else
                {
                    ReleaseBar.Title = "No bundled KaliteKit payload";
                    ReleaseBar.Message =
                        "This installer build carries no KaliteKit payload — publish it with "
                        + "publish-standalone.ps1 to produce the full offline installer.";
                    ReleaseBar.Severity = InfoBarSeverity.Warning;
                }
                RefreshNav();
                return;
            }

            // Embedded wizard (first run of the consumer app): resolve the
            // latest GitHub release so the user sees what will be deployed.
            ReleaseBar.Message = "Resolving latest release…";
            try
            {
                await Wizard.ResolveReleaseAsync();
                ReleaseBar.Title = "Latest release ready";
                ReleaseBar.Message = Wizard.KaliteKitReleaseInfo;
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