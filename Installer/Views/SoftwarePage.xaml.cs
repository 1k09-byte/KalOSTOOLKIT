using KalOS.Setup.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace KalOS.Setup.Views
{
    /// <summary>
    /// Step 3 — the software checklist. The catalog (Models/SoftwareCatalog)
    /// is the single source of truth; this page just renders checkbox lists
    /// bound to the wizard VM's SoftwarePick collections.
    /// </summary>
    public sealed partial class SoftwarePage : WizardPage
    {
        private InstallerViewModel Wizard => App.Wizard;

        public SoftwarePage()
        {
            InitializeComponent();
            DataContext = Wizard;
            Loaded += SoftwarePage_Loaded;
        }

        private void SoftwarePage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // Build the picks lazily so a user who navigates back and forth
            // doesn't lose their selections on a rebuild.
            if (Wizard.BrowserPicks.Count == 0)
                Wizard.BuildSoftwarePicks();

            // x:Bind OneWay evaluates when the page loads — before this handler
            // runs — and adding items doesn't re-notify. Re-evaluate explicitly
            // so the Browser/Apps/Runtimes lists actually populate.
            Bindings.Update();
            RefreshNav();
        }

        // Next is always enabled — installing nothing but KalOS is valid.
        public override bool CanProceed => true;

        public override bool OnAdvance()
        {
            // Nothing to validate; the pipeline reads SelectedSoftware directly.
            return true;
        }
    }
}
