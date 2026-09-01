using KalOS.Setup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KalOS.Setup.Views
{
    /// <summary>
    /// Step 5 — the live progress view. Kicks off the pipeline on load and
    /// renders the overall bar + a running step log. When the pipeline
    /// finishes it auto-advances to the Finish page.
    /// </summary>
    public sealed partial class ProgressPage : WizardPage
    {
        private InstallerViewModel Wizard => App.Wizard;

        public ProgressPage()
        {
            InitializeComponent();
            DataContext = Wizard;
            Loaded += ProgressPage_Loaded;
        }

        private async void ProgressPage_Loaded(object sender, RoutedEventArgs e)
        {
            await Wizard.RunAsync(onFinished: () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    StatusRing.IsActive = false;
                    if (App.MainWindow is { } window) window.GoNext();
                });
            });
        }

        public override bool AllowBack => false;
        public override bool CanProceed => false;

        /// <summary>
        /// Sets the ✓/✗ glyph + color per row via FontIcon. Avoids x:Bind
        /// function binds (the net472 XAML compiler mis-resolves them on
        /// nested record types and masks the real error).
        /// </summary>
        private void StepLogList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not InstallStepLog step) return;
            if (args.ItemContainer.ContentTemplateRoot is not Border card) return;
            if (card.Child is not Grid grid) return;
            if (grid.Children[0] is not FontIcon glyph) return;

            glyph.Glyph = step.Success ? "\uE73E" : "\uE711"; // CheckMark / Cancel
            glyph.Foreground = step.Success
                ? (Brush)Application.Current.Resources["SuccessBrush"]
                : (Brush)Application.Current.Resources["ErrorBrush"];
        }
    }
}
