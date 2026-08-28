using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using KalOS.ViewModels;
using Microsoft.UI.Xaml;

namespace KalOS.Views
{
    public sealed partial class AffinityManagerPage : Page
    {
        public AffinityManagerViewModel ViewModel { get; }

        public AffinityManagerPage()
        {
            ViewModel = App.Services.GetRequiredService<AffinityManagerViewModel>();
            this.Resources["CategoryToIconConverter"] = new CategoryToIconConverter();
            this.Resources["MsiColorConverter"] = new MsiColorConverter();
            this.InitializeComponent();

            // Source must be set in code because {x:Bind} is not allowed inside <Page.Resources>.
            DeviceGroups.Source = ViewModel.GroupedDevices;
        }

        private async void CardButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Button btn && btn.DataContext is PciDeviceItem item)
            {
                var dialog = new DeviceAffinityDialog(item, ViewModel.SystemCores)
                {
                    XamlRoot = this.Content.XamlRoot
                };

                await dialog.ShowAsync();
                if (dialog.IsSaved)
                {
                    ulong mask = dialog.ViewModel.GetCalculatedMask();
                    int policy = dialog.ViewModel.Policies.IndexOf(dialog.ViewModel.DevicePolicy);
                    int priority = dialog.ViewModel.Priorities.IndexOf(dialog.ViewModel.DevicePriority);
                    if (policy < 0) policy = 4; // Default to Specified Proc
                    if (priority < 0) priority = 2; // Default to Normal (NOT High \u2014 High has been observed to cause DPC preemption 0x7E BSODs on some hardware)

                    // When the user clears every thread checkbox, mask = 0. Writing
                    // AssignmentSetOverride=0 with IrqPolicySpecifiedProcessors would tell the driver
                    // "no processors available", which can prevent interrupt routing entirely.
                    // Auto-fall back to IrqPolicyMachineDefault so the OS picks the affinity itself.
                    // This makes "uncheck the last thread" actually do something the user expects:
                    // it releases their manual override and lets the OS decide. Priority is also
                    // reset to Undefined since the OS will pick whatever it wants anyway.
                    if (mask == 0 && policy == 4)
                    {
                        policy = 0;   // IrqPolicyMachineDefault
                        priority = 0; // Undefined
                    }

                    // Registry write may be blocked (not elevated, ACL denies). Caller now returns
                    // a bool so we can show a real error instead of silently proceeding to a restart
                    // prompt that will then re-read the OLD (unchanged) registry value.
                    bool writeOk = ViewModel.SetDeviceAffinityManually(
                        item, mask, policy, priority,
                        dialog.ViewModel.MsiEnabled,
                        dialog.ViewModel.MsiLimit,
                        out string? writeError);

                    if (!writeOk)
                    {
                        ViewModel.StatusText = writeError == null
                            ? "Failed to write affinity. Run as Administrator."
                            : $"Failed to write affinity: {writeError}";
                        await ShowErrorDialog("Could Not Apply Changes",
                            writeError ?? "The registry write was blocked. Try running the app as Administrator.");
                        return;
                    }

                    // Ask before restarting: the device may be in active use, so let
                    // the user defer the restart to the next reboot if they prefer.
                    bool restartNow = await ConfirmRestartDialog(item);
                    if (restartNow)
                    {
                        // Restart in the background. Failed restarts surface in the status bar
                        // (changes still take effect on the next cold boot if pnputil rejected
                        // the hot-restart).
                        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            bool restartOk = ViewModel.RestartDevice(item.DeviceId, out string? restartError);
                            dispatcher?.TryEnqueue(() =>
                            {
                                ViewModel.StatusText = restartOk
                                    ? $"Affinity applied — {item.Name} restarted in the background."
                                    : $"Affinity written but device restart deferred: {restartError ?? "pnputil rejected"}. Takes effect on next reboot.";
                            });
                        });
                        ViewModel.StatusText = $"Affinity written for {item.Name}; restarting device in the background…";
                    }
                    else
                    {
                        ViewModel.StatusText = $"Affinity written for {item.Name} — takes effect after the device is restarted.";
                    }

                    ViewModel.ReadMsiRegistry(item); // Update UI instantly without full rescan
                }
            }
        }

        /// <summary>Asks whether to restart the device now or defer to the next reboot.</summary>
        private async System.Threading.Tasks.Task<bool> ConfirmRestartDialog(PciDeviceItem item)
        {
            var xamlRoot = this.Content?.XamlRoot;
            if (xamlRoot == null) return false;

            var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "Restart device?",
                Content = $"Changes to {item.Name} take effect after the device restarts.\n\nRestart it now?",
                PrimaryButtonText = "Restart now",
                CloseButtonText = "Later",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };
            var result = await dlg.ShowAsync();
            return result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
        }

        private async System.Threading.Tasks.Task ShowErrorDialog(string title, string body)
        {
            // XamlRoot may be null during teardown or pre-load. Caller already awaits Surrounding code,
            // but protect ShowAsync() from NRE with a null-conditional accessor.
            var xamlRoot = this.Content?.XamlRoot;
            if (xamlRoot == null)
            {
                System.Diagnostics.Debug.WriteLine($"AffinityManagerPage: cannot show error dialog (XamlRoot null). {title}: {body}");
                return;
            }

            var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = title,
                Content = body,
                CloseButtonText = "OK",
                XamlRoot = xamlRoot
            };
            await dlg.ShowAsync();
        }
    }

    /// <summary>
    /// Maps a PCI device category to a Segoe Fluent Icon glyph. Per the app's
    /// iconography rule, generic device categories use font glyphs rather than
    /// raster PNG stand-ins; only brand marks stay as bitmaps.
    /// </summary>
    public class CategoryToIconConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        private static readonly Dictionary<string, string> _iconMap = new()
        {
            ["Graphics Cards"] = "\uE7F4",  // GPU
            ["Audio Controllers"] = "\uE767", // Volume
            ["Network Interface Controllers"] = "\uE968", // Network
            ["XHCI Controllers"] = "\uE88F", // USB
            ["Storage Controllers"] = "\uE74E", // Hard drive
        };

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var cat = value as string ?? "";
            return _iconMap.TryGetValue(cat, out var glyph) ? glyph : "\uE7F4";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    public class MsiColorConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isEnabled)
            {
                // Return dark green if true, dark red if false
                return isEnabled 
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 30, 100, 30))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 100, 30, 30));
            }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}
