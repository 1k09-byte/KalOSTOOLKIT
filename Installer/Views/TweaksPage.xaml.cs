using KalOS.Setup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KalOS.Setup.Views
{
    /// <summary>
    /// Lets the user pick which tweak categories to run after the install
    /// (apps removal, OneDrive, Edge, features, privacy, services, history,
    /// logs). Everything defaults to on — mirroring the privacy.sexy scripts
    /// the catalog was generated from.
    /// </summary>
    public sealed partial class TweaksPage : WizardPage
    {
        private InstallerViewModel Wizard => App.Wizard;

        public TweaksPage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Reflects the master switch onto the page: unchecked collapses the
        /// category list behind a note, checked brings it back. Reads the
        /// checkbox's own state (the TwoWay binding writes to the VM only after
        /// the event fires) and pushes it into the VM directly, same as the
        /// GPU page's skip toggle.
        /// </summary>
        private void ApplyTweaksCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = ApplyTweaksCheck.IsChecked == true;
            App.Wizard.ApplyTweaks = enabled;
            CategoryList.IsHitTestVisible = enabled;
            CategoryList.Opacity = enabled ? 1.0 : 0.45;
            SkipNote.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            WarnBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
