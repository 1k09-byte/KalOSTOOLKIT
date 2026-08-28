using System;
using System.Threading.Tasks;
using KalOS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace KalOS.Views;

public sealed partial class SystemOverviewPage : Page
{
    public SystemOverviewViewModel ViewModel { get; }
    private Storyboard? _shimmerStoryboard;

    public SystemOverviewPage()
    {
        ViewModel = App.Services.GetRequiredService<SystemOverviewViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        StartShimmer();
        await ViewModel.LoadAsync();
        if (!ViewModel.IsInstalled)
        {
            await ShowInstallDialogAsync();
        }

        if (ViewModel.IsInstalled)
        {
            ViewModel.StartLiveUpdates();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.StopLiveUpdates();
        StopShimmer();
        base.OnNavigatedFrom(e);
    }

    private void StartShimmer()
    {
        var transforms = new[] { ShimmerTransformRings, ShimmerTransformCard1, ShimmerTransformCard2, ShimmerTransformCard3, ShimmerTransformCard4, ShimmerTransformCard5, ShimmerTransformCard6 };
        if (transforms[0] == null) return;
        StopShimmer();
        _shimmerStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var t in transforms)
        {
            if (t == null) continue;
            var anim = new DoubleAnimation
            {
                From = -180,
                To = 180,
                Duration = TimeSpan.FromMilliseconds(1100),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, t);
            Storyboard.SetTargetProperty(anim, "X");
            _shimmerStoryboard.Children.Add(anim);
        }
        _shimmerStoryboard.Begin();
    }

    private void StopShimmer()
    {
        _shimmerStoryboard?.Stop();
        _shimmerStoryboard = null;
    }

    private async void InstallMonitor_Click(object sender, RoutedEventArgs e)
    {
        await ShowInstallDialogAsync();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ScanAsync();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (RootPage.XamlRoot == null) return;

        var confirm = new ContentDialog
        {
            Title = "Uninstall LibreHardwareMonitor?",
            Content = new TextBlock
            {
                Text = "This will uninstall LibreHardwareMonitor via winget and stop live hardware monitoring. System Overview will show limited data until you reinstall it. Continue?",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460
            },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootPage.XamlRoot
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.UninstallAsync();
        }
    }

    private async Task ShowInstallDialogAsync()
    {
        if (ViewModel.IsInstalled || RootPage.XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            Title = "Install LibreHardwareMonitor?",
            Content = new TextBlock
            {
                Text = "System Overview needs LibreHardwareMonitor to read CPU, GPU, memory, storage, temperature, fan, load, and power details. It will be installed from the official winget package source.\n\nWould you like to install it now?",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootPage.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.InstallAndScanAsync();
        }
    }
}
