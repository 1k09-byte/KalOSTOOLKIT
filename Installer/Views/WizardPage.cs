using Microsoft.UI.Xaml.Controls;

namespace KaliteKit.Setup.Views
{
    /// <summary>
    /// Base for every wizard page. A page overrides <see cref="CanProceed"/>
    /// (whether Next is enabled) and <see cref="OnAdvance"/> (run on Next; if
    /// it returns false the wizard stays put). The shell's footer nav bar
    /// reads these to drive the Back/Next buttons.
    /// </summary>
    public abstract class WizardPage : Page
    {
        /// <summary>Whether the wizard's Next button is enabled on this page.</summary>
        public virtual bool CanProceed => true;

        /// <summary>Whether Back is allowed from this page (false on Progress).</summary>
        public virtual bool AllowBack => true;

        /// <summary>
        /// Called when the user clicks Next. Return true to advance, false to
        /// stay (e.g. while an async precondition is still in flight).
        /// </summary>
        public virtual bool OnAdvance() => true;

        /// <summary>The owning window. Lets a page tell the shell to refresh the
        /// nav bar after a state change (e.g. when GPU detection lands).</summary>
        protected void RefreshNav()
        {
            App.MainWindow?.RefreshNav();
        }
    }
}
