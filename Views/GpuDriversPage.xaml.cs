using System;
using System.Threading.Tasks;
using KalOS.Models;
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
            ? "The stock installer is never run: the package is stripped of every optional component (GeForce Experience/NVIDIA App, HD Audio, PhysX, telemetry), only the signed display driver is staged via pnputil, then older driver-store packages, container services, scheduled tasks, and leftover folders are removed — NVIDIA clean-install grade, minus the bloat."
            : "The Adrenalin package is extracted silently and stripped down to the display driver only — no Radeon Software, no RAS telemetry, no audio drivers. Only the signed display INF is installed via pnputil, then AMD bloat services, scheduled tasks, and leftover folders are removed.";

        var dialog = new ContentDialog
        {
            Title = "Install driver update?",
            Content = new StackPanel { Spacing = 12 },
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
        ((StackPanel)dialog.Content).Children.Add(text);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.InstallAsync(item);
        }
        }
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is GpuDriverItem item)
        {
            ViewModel.OpenDownloadPage(item);
        }
    }

    private void ErrorInfoBar_CloseClick(Microsoft.UI.Xaml.Controls.InfoBar sender, object args)
    {
        ViewModel.ClearError();
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is GpuDriverItem item)
        {
            var dialog = new ContentDialog
            {
                Title = "Driver Cleanup (DDU-Style)",
                PrimaryButtonText = "Clean",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var scrollViewer = new ScrollViewer { MaxHeight = 450 };
            var rootPanel = new StackPanel { Spacing = 16, Margin = new Thickness(0, 0, 16, 0) };

            var warnText = new TextBlock 
            { 
                Text = "WARNING: Cleaning removes driver folders and components. This cannot be easily undone.", 
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            };
            rootPanel.Children.Add(warnText);

            var genHeader = new TextBlock { Text = "General Options", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
            rootPanel.Children.Add(genHeader);

            var cbMonitors = new CheckBox { Content = "Remove present and non-present monitors (Recommended)", IsChecked = ViewModel.CleanupRemoveMonitors };
            var cbRestore = new CheckBox { Content = "Create a system restore point", IsChecked = ViewModel.CleanupCreateRestorePoint };
            var cbVulkan = new CheckBox { Content = "Remove Vulkan Runtime", IsChecked = ViewModel.CleanupRemoveVulkanRuntime };
            rootPanel.Children.Add(cbMonitors);
            rootPanel.Children.Add(cbRestore);
            rootPanel.Children.Add(cbVulkan);

            CheckBox? cbNvFolders = null, cbPhysX = null, cb3DTV = null, cbGfe = null, cbBroadcast = null, cbNvDch = null, cbNvCache = null, cbKeepSettings = null;
            if (item.Gpu.IsNvidia)
            {
                var nvBorder = new Border { BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 12, 0, 4) };
                var nvHeader = new TextBlock { Text = "NVIDIA Specific Options", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                rootPanel.Children.Add(nvBorder);
                rootPanel.Children.Add(nvHeader);

                cbNvFolders = new CheckBox { Content = "Remove 'C:\\NVIDIA' driver folders", IsChecked = ViewModel.CleanupRemoveNvidiaFolders };
                cbPhysX = new CheckBox { Content = "Remove PhysX", IsChecked = ViewModel.CleanupRemovePhysX };
                cb3DTV = new CheckBox { Content = "Remove 3DTV Play", IsChecked = ViewModel.CleanupRemove3DTVPlay };
                cbGfe = new CheckBox { Content = "Remove GeForce Experience / NVIDIA App", IsChecked = ViewModel.CleanupRemoveGeForceExperience };
                cbBroadcast = new CheckBox { Content = "Remove NVIDIA Broadcast", IsChecked = ViewModel.CleanupRemoveNvidiaBroadcast };
                cbNvDch = new CheckBox { Content = "Remove NVIDIA Control Panel (MS Store)", IsChecked = ViewModel.CleanupRemoveNvidiaControlPanelDCH };
                cbNvCache = new CheckBox { Content = "Remove NVIDIA Shader Cache", IsChecked = ViewModel.CleanupRemoveNvidiaShaderCache };
                cbKeepSettings = new CheckBox { Content = "Keep NVIDIA Control Panel Settings", IsChecked = ViewModel.CleanupKeepNvidiaControlPanelSettings };

                rootPanel.Children.Add(cbNvFolders);
                rootPanel.Children.Add(cbPhysX);
                rootPanel.Children.Add(cb3DTV);
                rootPanel.Children.Add(cbGfe);
                rootPanel.Children.Add(cbBroadcast);
                rootPanel.Children.Add(cbNvDch);
                rootPanel.Children.Add(cbNvCache);
                rootPanel.Children.Add(cbKeepSettings);
            }

            CheckBox? cbAmdFolders = null, cbAmdKmpfd = null, cbAmdAudio = null, cbAmdCache = null, cbAmdDch = null;
            if (item.Gpu.IsAmd)
            {
                var amdBorder = new Border { BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 12, 0, 4) };
                var amdHeader = new TextBlock { Text = "AMD Specific Options", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                rootPanel.Children.Add(amdBorder);
                rootPanel.Children.Add(amdHeader);

                cbAmdFolders = new CheckBox { Content = "Remove 'C:\\AMD' driver folders", IsChecked = ViewModel.CleanupRemoveAmdFolders };
                cbAmdKmpfd = new CheckBox { Content = "Remove the AMDKMPFD filter", IsChecked = ViewModel.CleanupRemoveAmdKmpfd };
                cbAmdAudio = new CheckBox { Content = "Remove AMD Audio Bus", IsChecked = ViewModel.CleanupRemoveAmdAudioBus };
                cbAmdCache = new CheckBox { Content = "Remove AMD Crimson Shader Cache", IsChecked = ViewModel.CleanupRemoveAmdCrimsonShaderCache };
                cbAmdDch = new CheckBox { Content = "Remove AMD Control Panel (MS Store)", IsChecked = ViewModel.CleanupRemoveAmdControlPanelDCH };

                rootPanel.Children.Add(cbAmdFolders);
                rootPanel.Children.Add(cbAmdKmpfd);
                rootPanel.Children.Add(cbAmdAudio);
                rootPanel.Children.Add(cbAmdCache);
                rootPanel.Children.Add(cbAmdDch);
            }

            scrollViewer.Content = rootPanel;
            dialog.Content = scrollViewer;

            var result = await dialog.ShowAsync();
            
            if (result == ContentDialogResult.Primary)
            {
                ViewModel.CleanupRemoveMonitors = cbMonitors.IsChecked;
                ViewModel.CleanupCreateRestorePoint = cbRestore.IsChecked;
                ViewModel.CleanupRemoveVulkanRuntime = cbVulkan.IsChecked;

                if (item.Gpu.IsNvidia)
                {
                    ViewModel.CleanupRemoveNvidiaFolders = cbNvFolders!.IsChecked;
                    ViewModel.CleanupRemovePhysX = cbPhysX!.IsChecked;
                    ViewModel.CleanupRemove3DTVPlay = cb3DTV!.IsChecked;
                    ViewModel.CleanupRemoveGeForceExperience = cbGfe!.IsChecked;
                    ViewModel.CleanupRemoveNvidiaBroadcast = cbBroadcast!.IsChecked;
                    ViewModel.CleanupRemoveNvidiaControlPanelDCH = cbNvDch!.IsChecked;
                    ViewModel.CleanupRemoveNvidiaShaderCache = cbNvCache!.IsChecked;
                    ViewModel.CleanupKeepNvidiaControlPanelSettings = cbKeepSettings!.IsChecked;
                }

                if (item.Gpu.IsAmd)
                {
                    ViewModel.CleanupRemoveAmdFolders = cbAmdFolders!.IsChecked;
                    ViewModel.CleanupRemoveAmdKmpfd = cbAmdKmpfd!.IsChecked;
                    ViewModel.CleanupRemoveAmdAudioBus = cbAmdAudio!.IsChecked;
                    ViewModel.CleanupRemoveAmdCrimsonShaderCache = cbAmdCache!.IsChecked;
                    ViewModel.CleanupRemoveAmdControlPanelDCH = cbAmdDch!.IsChecked;
                }

                await ViewModel.RunCleanupAsync(item);
            }
        }
    }
}
