using KaliteKit.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KaliteKit.Views;

public sealed partial class SystemRestoreDialog : ContentDialog
{
    public HomeViewModel ViewModel { get; }
    public RestorePointItem? SelectedItem { get; private set; }

    public SystemRestoreDialog(HomeViewModel viewModel)
    {
        this.InitializeComponent();
        ViewModel = viewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.RestorePoints.CollectionChanged += (_, __) => UpdateEmptyPlaceholder();
        this.Loaded += (_, __) => UpdateEmptyPlaceholder();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.RestorePointCount))
            UpdateEmptyPlaceholder();
    }

    private void UpdateEmptyPlaceholder()
    {
        EmptyPlaceholder.Visibility = ViewModel.RestorePointCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreList.Visibility = ViewModel.RestorePointCount == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var desc = NewDescriptionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(desc)) desc = "KaliteKit App Restore Point";
        await ViewModel.CreateRestorePointWithDescriptionAsync(desc);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.LoadRestorePointsAsync();
    }

    private void RestoreList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedItem = RestoreList.SelectedItem as RestorePointItem;
        if (SelectedItem != null)
        {
            SelectedPanel.Visibility = Visibility.Visible;
            SelectedDescText.Text = SelectedItem.Description;
            SelectedTimeText.Text = $"{SelectedItem.CreationTime}  •  #{SelectedItem.SequenceNumber}";
            EditDescriptionBox.Text = SelectedItem.Description;
        }
        else
        {
            SelectedPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem == null) return;
        var confirm = new ContentDialog
        {
            Title = "Restore System",
            Content = $"Are you sure you want to restore your system to '{SelectedItem.Description}' ({SelectedItem.CreationTime})?\n\nYour computer will restart automatically during the process.",
            PrimaryButtonText = "Restore Now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        var result = await confirm.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.RestoreSystem(SelectedItem.SequenceNumber);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem == null) return;
        var confirm = new ContentDialog
        {
            Title = "Delete Restore Point",
            Content = $"Delete restore point '{SelectedItem.Description}' ({SelectedItem.CreationTime})?\n\nThis cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        var result = await confirm.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteRestorePointAsync(SelectedItem.SequenceNumber);
        }
    }

    private async void EditSave_Click(object sender, RoutedEventArgs e)
    {
        var newDesc = EditDescriptionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newDesc))
        {
            ViewModel.RestorePointStatus = "Please enter a description for the new point.";
            return;
        }
        await ViewModel.CreateRestorePointWithDescriptionAsync(newDesc);
    }
}
