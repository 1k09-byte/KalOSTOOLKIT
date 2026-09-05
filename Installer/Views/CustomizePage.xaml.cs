using KaliteKit.Setup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KaliteKit.Setup.Views
{
    /// <summary>
    /// Step 4 — customize what the install leaves behind: a background image
    /// (copied into the app's data folder post-install) and a window tint from
    /// the shared palette or a custom picker.
    /// </summary>
    public sealed partial class CustomizePage : WizardPage
    {
        private InstallerViewModel Wizard => App.Wizard;

        public CustomizePage()
        {
            InitializeComponent();
            DataContext = Wizard;
        }

        /// <summary>Opens a color picker for a fully custom tint; applied live.</summary>
        private void CustomTintButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new ColorPicker
            {
                Width = 300,
                IsAlphaEnabled = false,
                IsColorSliderVisible = true,
                IsColorSpectrumVisible = true,
                IsHexInputVisible = true,
                Color = Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x6F, 0xB8),
            };

            picker.ColorChanged += (_, args) => Wizard.ApplyCustomTint(args.NewColor);

            var flyout = new Flyout
            {
                Content = picker,
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            };
            flyout.ShowAt(CustomTintButton);
        }

        // Both choices are optional — Default/no image is always valid.
        public override bool CanProceed => true;

        public override bool OnAdvance() => true;
    }
}
