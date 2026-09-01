using System.Linq;
using KalOS.Setup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KalOS.Setup.Views
{
    /// <summary>
    /// Step 6 — the final page. Shows the overall result and a per-step list,
    /// plus a Close button (the shell footer has no Next/Cancel anymore, so
    /// this page owns exiting the wizard).
    /// </summary>
    public sealed partial class FinishPage : WizardPage
    {
        private InstallerViewModel Wizard => App.Wizard;

        public FinishPage()
        {
            InitializeComponent();
            Loaded += FinishPage_Loaded;
        }

        private void FinishPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (Wizard.InstallSucceeded)
            {
                StatusIcon.Glyph = "\uE73E"; // CheckMark
                StatusIcon.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
                FinishTitle.Text = "Installation complete";
            }
            else
            {
                StatusIcon.Glyph = "\uE7BA"; // Warning
                StatusIcon.Foreground = (Brush)Application.Current.Resources["WarningBrush"];
                FinishTitle.Text = "Installation finished with errors";
            }

            FinishText.Text = Wizard.FinishSummary;

            // Skipped steps (e.g. "GPU driver" when the user opted out) were
            // never installed — keep them off the "What was installed" list.
            ResultsList.ItemsSource = Wizard.StepLog
                .Where(s => !s.Skipped)
                .Select(s => $"{(s.Success ? "✓" : "✗")}  {s.Name}")
                .ToList();
            RefreshNav();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is { } window) window.RequestClose();
        }

        public override bool CanProceed => true;
        public override bool OnAdvance()
        {
            if (App.MainWindow is { } window) window.RequestClose();
            return false;
        }
    }
}
