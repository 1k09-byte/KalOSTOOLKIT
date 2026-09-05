using KaliteKit.Services;
using Microsoft.UI.Xaml.Controls;

namespace KaliteKit.Views
{
    /// <summary>
    /// Second step of the NVIDIA install flow: optional post-install tweaks,
    /// ported from NovaOS's "Disable Nvidia Telemetry" script (credited on the
    /// dialog). Steps that could break components the user kept on the previous
    /// screen are guarded at apply time by the install service.
    /// </summary>
    public sealed partial class NvTweaksDialog : ContentDialog
    {
        /// <summary>Tweak choices read when the dialog closes.</summary>
        public NvInstallTweaks Tweaks => new()
        {
            DisableDriverTelemetry = CbDisableTelemetry.IsChecked == true,
            UninstallVisionAndAnsel = CbUninstallVisionAnsel.IsChecked == true,
            DisableNvidiaTasks = CbDisableTasks.IsChecked == true,
            RemoveNvBackendStartup = CbNvBackendStartup.IsChecked == true,
            DeleteTelemetryFiles = CbDeleteFiles.IsChecked == true,
        };

        public NvTweaksDialog()
        {
            InitializeComponent();
        }
    }
}
