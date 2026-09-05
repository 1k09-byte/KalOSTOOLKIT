using System;
using KaliteKit.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KaliteKit.Views;

public sealed partial class BiosPage : Page
{
    public BiosPage()
    {
        ViewModel = App.Services.GetRequiredService<BiosViewModel>();
        InitializeComponent();
    }

    public BiosViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (ViewModel.IsConfigured && ViewModel.Settings.Count == 0 && !ViewModel.IsBusy)
        {
            await ViewModel.RefreshAsync();
        }
        else if (ViewModel.BiosVersion == "Unknown")
        {
            await ViewModel.InitializeSystemInfoAsync();
        }
    }

    private async void ApplyConfirmation_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PendingChanges.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = "Confirm BIOS changes",
            Content = $"You are about to write {ViewModel.PendingChanges.Count} setting(s) to NVRAM.\n\nA backup of the current state will be taken automatically.\n\nIncorrect values can prevent your system from booting. Continue?",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.ApplyAsync();
        }
    }
}