using System;
using System.Threading.Tasks;
using KalOS.Models;
using KalOS.Services;
using KalOS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KalOS.Views;

/// <summary>
/// GPU Drivers page. Pure view: binds to <see cref="GpuDriversViewModel"/>,
/// confirms destructive actions, and forwards clicks — every WMI, HTTP, and
/// vendor detail lives in the services below the ViewModel.
/// </summary>
public sealed partial class GpuDriversPage : Page
{
    public GpuDriversPage()
    {
        ViewModel = App.Services.GetRequiredService<GpuDriversViewModel>();
        InitializeComponent();
    }

    public GpuDriversViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Auto-check once per session; revisit only refreshes on demand.
        if (!ViewModel.HasBeenChecked && !ViewModel.IsWorking)
        {
            _ = ViewModel.CheckForUpdatesAsync();
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is GpuDriverItem item)
        {
            if (item.Latest is null) return;

            // ── NVIDIA: KalOS's own install dialog ───────────────────────
            // Mirrors NVCleanstall's "Select Driver Version To Install" screen
            // (best driver / driver files on disk) plus the component checklist,
            // all in-app — then runs the built-in silent pipeline.
            if (item.IsNvidia)
            {
                // Version history for the "Manually select a driver version"
                // list — fetched before the dialog opens so it's populated.
                var versions = await ViewModel.GetNvidiaVersionHistoryAsync(item);
                var nvDialog = new NvInstallDialog(item, versions)
                {
                    XamlRoot = RootPage.XamlRoot,
                };

                if (await nvDialog.ShowAsync() != ContentDialogResult.Primary) return;

                var validationError = nvDialog.Validate();
                if (validationError != null)
                {
                    ViewModel.HasError = true;
                    ViewModel.ErrorMessage = validationError;
                    return;
                }

                // ── Step 2: post-install tweaks (NovaOS-sourced) ────────────
                var tweaksDialog = new NvTweaksDialog
                {
                    XamlRoot = RootPage.XamlRoot,
                };

                if (await tweaksDialog.ShowAsync() != ContentDialogResult.Primary) return;

                await ViewModel.InstallAsync(item, nvDialog.Components, nvDialog.OnDiskDriverPath, nvDialog.SelectedDriver, tweaksDialog.Tweaks);
                return;
            }

        string vendorDetail = item.IsNvidia
            ? "The stock installer is never run: only the signed display driver is staged via pnputil, then older driver-store packages, container services, scheduled tasks, and leftover folders are removed — NVIDIA clean-install grade, minus the bloat."
            : "The Adrenalin package is extracted silently and stripped down to the display driver only — no Radeon Software, no RAS telemetry, no audio drivers. Only the signed display INF is installed via pnputil, then AMD bloat services, scheduled tasks, and leftover folders are removed.";

        var contentPanel = new StackPanel { Spacing = 12 };
        var dialog = new ContentDialog
        {
            Title = "Install driver update?",
            Content = contentPanel,
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootPage.XamlRoot,
        };

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            Text = $"{item.Name}\n\n{item.Latest.DisplayString ?? item.Latest.Version} will replace the currently installed display driver ({item.Gpu.DriverVersion}).\n\n{vendorDetail} A reboot is recommended afterwards.",
        };
        contentPanel.Children.Add(text);

        if (item.IsAmd)
        {
            await ViewModel.PrepareAndOpenAmdSlimmerAsync(item, RootPage.XamlRoot);
            return;
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.InstallAsync(item);
        }
    }    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is GpuDriverItem item)
        {
            if (item.Status == DriverStatus.UpToDate)
            {
                await ViewModel.RunAmdPostInstallDebloatCommand.ExecuteAsync(null);
            }
            else
            {
                Install_Click(sender, e);
            }
        }
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is GpuDriverItem item)
        {
            ViewModel.OpenDownloadPage(item);
        }
    }

    private async void RunAmdCleanup_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RunAmdCleanupCommand.ExecuteAsync(null);
    }

    private async void RunAmdAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.UpdateAmdOfficialCommand.ExecuteAsync(null);
    }

    private void ErrorInfoBar_CloseClick(Microsoft.UI.Xaml.Controls.InfoBar sender, object args)
    {
        ViewModel.ClearError();
    }

    private async void GpuAudio_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts)
        {
            if (ts.IsOn != ViewModel.IsGpuAudioEnabled)
            {
                await ViewModel.ToggleGpuAudioAsync();
            }
        }
    }
}


