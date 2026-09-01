using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KalOS.Models;
using KalOS.Services;
using KalOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace KalOS.Views
{
    /// <summary>
    /// KalOS's own NVIDIA install dialog — the counterpart of the AMD Slimmer
    /// flow, but fully in-app: it mirrors NVCleanstall's "Select Driver Version
    /// To Install" screen (best driver / driver files on disk) and adds the
    /// component checklist in the same dialog. On Next, the page opens the
    /// post-install tweaks dialog (NovaOS-sourced), then runs the built-in
    /// silent pipeline (download or use the picked file → extract → strip →
    /// pnputil → debloat → tweaks) with progress on the card and status bar.
    /// </summary>
    public sealed partial class NvInstallDialog : ContentDialog
    {
        private readonly GpuDriverItem _item;

        /// <summary>Absolute path to a driver .exe on disk, or null to download.</summary>
        public string? OnDiskDriverPath { get; private set; }

        /// <summary>Explicitly chosen version from the manual list, or null.</summary>
        public DriverInfo? SelectedDriver { get; private set; }

        /// <summary>Component keep-choices read when the dialog closes.</summary>
        public NvidiaInstallComponents Components => new()
        {
            KeepHDAudio = CbHdAudio.IsChecked == true,
            KeepPhysX = CbPhysX.IsChecked == true,
            KeepNvidiaApp = CbNvApp.IsChecked == true,
            KeepUSBC = CbUSBC.IsChecked == true,
            KeepTelemetry = CbTelemetry.IsChecked == true,
            KeepMsvcRuntimes = CbMsvc.IsChecked == true,
            KeepFrameViewSdk = CbFrameView.IsChecked == true,
            KeepVirtualAudio = CbVirtualAudio.IsChecked == true,
            KeepNvPlatformControllers = CbNvPlatformControllers.IsChecked == true,
            KeepDlsr = CbDlsr.IsChecked == true,
            KeepNvContainer = CbNvContainer.IsChecked == true,
            KeepShadowPlay = CbShadowPlay.IsChecked == true,
            KeepNvBackend = CbNvBackend.IsChecked == true,
            KeepNvidiaAppMessageBus = CbMessageBus.IsChecked == true,
        };

        private const int ShortVersionListCount = 10;
        private readonly IReadOnlyList<DriverInfo> _versions;

        public NvInstallDialog(GpuDriverItem item, IReadOnlyList<DriverInfo> versionHistory)
        {
            _item = item;
            _versions = versionHistory ?? Array.Empty<DriverInfo>();
            InitializeComponent();

            HardwareText.Text = item.Name;
            CurrentDriverText.Text = string.IsNullOrWhiteSpace(item.Gpu.DriverVersion)
                ? "N/A"
                : item.Gpu.DriverVersion;

            DchText.Text = "DCH: Yes";
            MobileText.Text = IsMobileGpu(item.Name) ? "Mobile: Yes" : "Mobile: No";

            var latest = item.Latest;
            BestDriverLabel.Text = latest?.DisplayString ?? $"Game Ready {latest?.Version}";

            if (_versions.Count > 0)
            {
                CbShowAllVersions.IsEnabled = true;
                RbManualVersion.IsEnabled = true;
                VersionCombo.IsEnabled = false; // enabled by the radio selection
                RebuildVersionList();
            }
            else
            {
                RbManualVersion.IsEnabled = false;
                VersionListHint.Text = "Version list unavailable (NVIDIA API unreachable) — use " +
                                       "\"Install best driver\" or a package on disk.";
                VersionListHint.Visibility = Visibility.Visible;
            }
        }

        private static bool IsMobileGpu(string name) =>
            name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Notebook", StringComparison.OrdinalIgnoreCase);

        private async void Browse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads,
                    ViewMode = PickerViewMode.List,
                };
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    DiskPathBox.Text = file.Path;
                }
            }
            catch (Exception)
            {
                // Picker unavailable (e.g. no window handle yet) — leave the box
                // editable so the user can paste a path instead.
                DiskPathBox.IsEnabled = true;
            }
        }

        private void RebuildVersionList()
        {
            bool showAll = CbShowAllVersions.IsChecked == true;
            var slice = showAll ? _versions : _versions.Take(ShortVersionListCount).ToList();

            VersionCombo.ItemsSource = slice.Select(d => new ComboBoxItem
            {
                Content = d.ReleaseDate is { } date
                    ? $"Game Ready {d.Version} — {date:MMM d, yyyy}"
                    : $"Game Ready {d.Version}",
                Tag = d,
            }).ToList();

            VersionCombo.IsEnabled = RbManualVersion.IsChecked == true && slice.Count > 0;
        }

        private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedDriver = (VersionCombo.SelectedItem as ComboBoxItem)?.Tag as DriverInfo;
        }

        private void SourceRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (VersionCombo == null) return; // XAML init order
            VersionCombo.IsEnabled = RbManualVersion.IsChecked == true && _versions.Count > 0;
            DiskPathBox.IsEnabled = RbDriverOnDisk.IsChecked == true;
            BrowseBtn.IsEnabled = RbDriverOnDisk.IsChecked == true;
        }

        private void ShowAllVersions_Changed(object sender, RoutedEventArgs e) => RebuildVersionList();

        /// <summary>Validates the selection; returns an error message or null.</summary>
        public string? Validate()
        {
            SelectedDriver = null;
            OnDiskDriverPath = null;

            if (RbManualVersion.IsChecked == true)
            {
                // Read the picked entry straight from the combo — the radio was
                // switched on after the dialog closed, so don't rely on stale state.
                SelectedDriver = (VersionCombo.SelectedItem as ComboBoxItem)?.Tag as DriverInfo;
                if (SelectedDriver == null)
                    return "Pick a driver version from the list, or switch back to \"Install best driver for my hardware\".";
            }

            if (RbDriverOnDisk.IsChecked == true)
            {
                string path = DiskPathBox.Text.Trim('"', ' ');
                if (string.IsNullOrWhiteSpace(path)) return "Pick a driver .exe file or switch back to \"Install best driver for my hardware\".";
                if (!File.Exists(path)) return "The selected driver file does not exist.";
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "The driver file must be a .exe package.";
                if (new FileInfo(path).Length < 10_000_000) return "That file is too small to be a GeForce driver package.";
                OnDiskDriverPath = path;
            }

            return null;
        }

        /// <summary>Called by the page while the install runs in the background.</summary>
        public void ReportProgress(double pct, string message)
        {
            Progress.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Visible;
            if (pct >= 0 && pct <= 100)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = pct;
            }
            else
            {
                Progress.IsIndeterminate = true;
            }
            StatusText.Text = message;
        }

        public void BeginWorking()
        {
            IsPrimaryButtonEnabled = false;
            IsSecondaryButtonEnabled = false;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "Preparing…";
        }

        public void EndWorking()
        {
            IsPrimaryButtonEnabled = true;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
        }
    }
}
