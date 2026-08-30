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

        string vendorDetail = item.IsNvidia
            ? "The stock installer is never run: only the signed display driver is staged via pnputil, then older driver-store packages, container services, scheduled tasks, and leftover folders are removed — NVIDIA clean-install grade, minus the bloat. Pick below which optional components you want to keep."
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

        // NVIDIA lets the user choose which optional components to keep. The
        // display driver is always installed; every unchecked component is stripped.
        NvidiaInstallComponents components = NvidiaInstallComponents.DisplayOnly;
        if (item.IsNvidia)
        {
            var separator = new Border
            {
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 8, 0, 0),
            };
            var header = new TextBlock
            {
                Text = "Components to keep (display driver is always installed)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
            };
            contentPanel.Children.Add(separator);
            contentPanel.Children.Add(header);

            var cbPhysX = new CheckBox { Content = "PhysX System Software" };
            var cbHdAudio = new CheckBox { Content = "HD Audio driver" };
            var cbGfe = new CheckBox { Content = "GeForce Experience" };
            var cbNvApp = new CheckBox { Content = "NVIDIA App" };
            contentPanel.Children.Add(cbPhysX);
            contentPanel.Children.Add(cbHdAudio);
            contentPanel.Children.Add(cbGfe);
            contentPanel.Children.Add(cbNvApp);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                components = new NvidiaInstallComponents
                {
                    KeepPhysX = cbPhysX.IsChecked == true,
                    KeepHDAudio = cbHdAudio.IsChecked == true,
                    KeepGeForceExperience = cbGfe.IsChecked == true,
                    KeepNvidiaApp = cbNvApp.IsChecked == true,
                };
            }
            else
            {
                return;
            }

            await ViewModel.InstallAsync(item, components);
            return;
        }

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
    }





    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
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


