using System;
using System.IO;
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
    /// component checklist in the same dialog. On Install, the ViewModel runs
    /// the built-in silent pipeline (download or use the picked file → extract
    /// → strip → pnputil → debloat) with progress reported back here.
    /// </summary>
    public sealed partial class NvInstallDialog : ContentDialog
    {
        private readonly GpuDriverItem _item;

        /// <summary>Absolute path to a driver .exe on disk, or null to download.</summary>
        public string? OnDiskDriverPath { get; private set; }

        /// <summary>Component keep-choices read when the dialog closes.</summary>
        public NvidiaInstallComponents Components => new()
        {
            KeepPhysX = CbPhysX.IsChecked == true,
            KeepHDAudio = CbHdAudio.IsChecked == true,
            KeepGeForceExperience = CbGfe.IsChecked == true,
            KeepNvidiaApp = CbNvApp.IsChecked == true,
        };

        public NvInstallDialog(GpuDriverItem item)
        {
            _item = item;
            InitializeComponent();

            HardwareText.Text = item.Name;
            CurrentDriverText.Text = string.IsNullOrWhiteSpace(item.Gpu.DriverVersion)
                ? "N/A"
                : item.Gpu.DriverVersion;

            DchText.Text = "DCH: Yes";
            MobileText.Text = IsMobileGpu(item.Name) ? "Mobile: Yes" : "Mobile: No";

            var latest = item.Latest;
            BestDriverLabel.Text = latest?.DisplayString ?? $"Game Ready {latest?.Version}";

            // Keep the dialog's primary click routed through result handling
            // (the page awaits ShowAsync and reads OnDiskDriverPath + Components).
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

        /// <summary>Validates the selection; returns an error message or null.</summary>
        public string? Validate()
        {
            if (RbDriverOnDisk.IsChecked == true)
            {
                string path = DiskPathBox.Text.Trim('"', ' ');
                if (string.IsNullOrWhiteSpace(path)) return "Pick a driver .exe file or switch back to \"Install best driver for my hardware\".";
                if (!File.Exists(path)) return "The selected driver file does not exist.";
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "The driver file must be a .exe package.";
                if (new FileInfo(path).Length < 10_000_000) return "That file is too small to be a GeForce driver package.";
                OnDiskDriverPath = path;
            }
            else
            {
                OnDiskDriverPath = null;
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
