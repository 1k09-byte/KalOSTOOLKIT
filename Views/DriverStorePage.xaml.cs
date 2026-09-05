using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KaliteKit.Models;
using KaliteKit.Services;
using KaliteKit.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace KaliteKit.Views
{
    /// <summary>
    /// Driver Store Manager page. The VIEW owns every confirmation dialog —
    /// the ViewModel assumes consent. Safety flows implemented here per spec
    /// section 7: boot-critical typed confirmation (7.1), force-delete
    /// device disclosure (7.2), restore point before delete with fail-safe
    /// abort (7.3), inbox-driver friction (7.4), offline-path re-confirmation
    /// per session (7.5).
    /// </summary>
    public sealed partial class DriverStorePage : Page
    {
        public DriverStoreViewModel ViewModel { get; }

        public DriverStorePage()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<DriverStoreViewModel>();
            ViewModel.ErrorOccurred += async (_, message) => await ShowErrorAsync(message);
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (ViewModel.Packages.Count == 0 && !ViewModel.IsBusy)
                _ = ViewModel.RefreshAsync();
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.CancelSizeComputation();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void UseFallback_Click(object sender, RoutedEventArgs e) => await ViewModel.UsePnputilFallbackAsync();

        private async void CancelBatch_Click(object sender, RoutedEventArgs e) => ViewModel.CancelBatch();

        // ── Offline target ──────────────────────────────────────────────

        private async void BrowseOfflineRoot_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder is null) return;

            var path = folder.Path;
            if (!OfflineStoreValidator.IsValidOfflineRoot(path))
            {
                await ShowErrorAsync($"'{path}' does not contain a Windows\\System32\\DriverStore structure — it is not a valid offline image root.");
                return;
            }
            ViewModel.OfflineRoot = path;
        }

        private void ConfirmOfflineRoot_Click(object sender, RoutedEventArgs e)
        {
            if (!OfflineStoreValidator.IsValidOfflineRoot(ViewModel.OfflineRoot))
            {
                _ = ShowErrorAsync("Enter a valid offline image root first (a folder containing Windows\\System32\\DriverStore).");
                return;
            }
            ViewModel.OfflineRootConfirmed = true;
        }

        // ── Add driver ──────────────────────────────────────────────────

        private async void AddDriver_Click(object sender, RoutedEventArgs e)
        {
            var inf = await PickInfAsync();
            if (inf is null) return;

            // One dialog, three outcomes (spec 5.3): add+install / add only / cancel.
            var result = await ShowTripleChoiceDialog(
                title: "Add driver package",
                body: $"{inf.Name}\n\n" +
                      "• Add and install — stage the package AND install it onto any currently-connected matching device.\n" +
                      "• Add only (stage) — make it available in the store without touching any device.",
                primaryLabel: "Add and install",
                secondaryLabel: "Add only (stage)");

            if (result == ContentDialogResult.None) return; // cancelled
            bool install = result == ContentDialogResult.Primary;

            await ViewModel.AddDriverAsync(inf.Path, install);
        }

        // ── Export ──────────────────────────────────────────────────────

        private async void ExportSelected_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedRows.Count == 0)
            {
                await ShowErrorAsync("Select at least one package to export.");
                return;
            }
            var folder = await PickFolderAsync();
            if (folder is null) return;
            await ViewModel.ExportSelectedAsync(folder.Path);
        }

        private async void ExportOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: DriverPackageRow row }) return;
            var folder = await PickFolderAsync();
            if (folder is null) return;
            await ViewModel.ExportRowsAsync(new List<DriverPackageRow> { row }, folder.Path);
        }

        private async void ExportAll_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder is null) return;
            ViewModel.SelectAll();
            await ViewModel.ExportSelectedAsync(folder.Path);
            ViewModel.SelectNone();
        }

        // ── Delete (single / selected) with full safety gating ─────────

        private async void DeleteOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: DriverPackageRow row }) return;
            await DeleteWithSafetyChecksAsync(new List<DriverPackageRow> { row });
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var rows = ViewModel.SelectedRows;
            if (rows.Count == 0)
            {
                await ShowErrorAsync("Select at least one package to delete.");
                return;
            }
            await DeleteWithSafetyChecksAsync(rows);
        }

        private async Task DeleteWithSafetyChecksAsync(IReadOnlyList<DriverPackageRow> rows)
        {
            bool createRestorePoint = true;

            // 7.3: restore point on by default; if the user opted out (not yet
            // exposed in Settings — default ON), warn once. Today: always on.
            createRestorePoint = true;

            // Split the selection into normal / in-use / boot-critical / inbox
            // — each gets its own treatment (7.1, 7.2, 7.4).
            var bootCritical = rows.Where(r => r.BootCritical).ToList();
            var inbox = rows.Where(r => r.IsInbox && !r.BootCritical).ToList();
            var inUse = rows.Where(r => r.InUse && !r.BootCritical).ToList();
            var normal = rows.Except(bootCritical).Except(inbox).Except(inUse).ToList();

            // 7.1 — boot-critical: severe confirmation with typed phrase, per item.
            foreach (var row in bootCritical)
            {
                bool? ok = await ConfirmTypedAsync(
                    title: "DELETE BOOT-CRITICAL DRIVER",
                    body: $"{row.Provider} — {row.Record.InfName}\n\n" +
                          "This driver package is one Windows depends on to start. Removing it could prevent the system from starting correctly and may require recovery media or a full reinstall to fix.\n\n" +
                          $"Type the package name ({row.Record.InfName}) to confirm.",
                    phrase: row.Record.InfName);
                if (ok != true) return;
            }

            // 7.4 — inbox: at least the same friction as boot-critical.
            foreach (var row in inbox)
            {
                bool? ok = await ConfirmTypedAsync(
                    title: "DELETE WINDOWS (INBOX) DRIVER",
                    body: $"{row.Provider} — {row.Record.InfName}\n\n" +
                          "This is a Microsoft-shipped (inbox) driver. Removing it can affect core Windows functionality in ways a vendor's optional package will not.\n\n" +
                          $"Type the package name ({row.Record.InfName}) to confirm.",
                    phrase: row.Record.InfName);
                if (ok != true) return;
            }

            // 7.2 — in-use: force-delete is a distinct, per-item acknowledgment.
            foreach (var row in inUse)
            {
                var devices = row.Record.AssociatedDevices
                    .Select(d => string.IsNullOrEmpty(d.Description) ? d.InstanceId : d.Description)
                    .Distinct().Take(5);
                bool ok = await ConfirmDangerAsync(
                    title: "DRIVER IN USE — force delete?",
                    body: $"{row.Provider} — {row.Record.InfName} is currently used by:\n  • {string.Join("\n  • ", devices)}\n\n" +
                          "Force deletion can make this hardware STOP WORKING IMMEDIATELY. You may need to reinstall a driver for it to function again.\n\n" +
                          "Force-delete is applied to this item individually — it is never silently included in a routine batch.",
                    confirm: "Force delete");
                if (!ok) return;

                await ViewModel.ForceDeleteConfirmedAsync(row, createRestorePoint);
            }

            // Normal deletes (and normal-only remainder of the batch).
            if (normal.Count > 0)
            {
                string listing = string.Join("\n", normal.Select(r => $"  • {r.Provider} — {r.Record.InfName}").Take(12));
                if (normal.Count > 12) listing += $"\n  • … and {normal.Count - 12} more";
                string offlineNote = ViewModel.IsOfflineTarget
                    ? $"\n\nThis will modify: {ViewModel.OfflineRoot}"
                    : string.Empty;

                bool ok = await ConfirmDangerAsync(
                    title: "Delete driver packages?",
                    body: $"A System Restore point will be created first.{offlineNote}\n\n" +
                          $"You are deleting {normal.Count} package(s):\n{listing}\n\n" +
                          "Suggestion: export these packages first so you can reinstall them later.",
                    confirm: "Delete");
                if (!ok) return;

                await ViewModel.DeleteConfirmedAsync(normal, createRestorePoint);
            }
        }

        // ── Smart Cleanup ───────────────────────────────────────────────

        private void SmartCleanup_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ComputeCleanupCandidates();
            CleanupPanel.Visibility = ViewModel.CleanupCandidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CloseCleanup_Click(object sender, RoutedEventArgs e) =>
            CleanupPanel.Visibility = Visibility.Collapsed;

        private async void DeleteCleanup_Click(object sender, RoutedEventArgs e)
        {
            var rows = ViewModel.CleanupCandidates.Where(c => c.IsSelected).ToList();
            if (rows.Count == 0)
            {
                await ShowErrorAsync("No candidates are checked.");
                return;
            }

            string listing = string.Join("\n", rows.Select(r => $"  • {r.Name} ({r.InfName})").Take(12));
            if (rows.Count > 12) listing += $"\n  • … and {rows.Count - 12} more";
            bool ok = await ConfirmDangerAsync(
                title: "Delete Smart Cleanup candidates?",
                body: $"A System Restore point will be created first.\n\nDeleting {rows.Count} package(s):\n{listing}\n\n" +
                      "This is the final confirmation — the checked packages are exactly what will be removed.",
                confirm: "Delete");
            if (!ok) return;

            CleanupPanel.Visibility = Visibility.Collapsed;
            await ViewModel.DeleteCleanupConfirmedAsync(rows, createRestorePoint: true);
        }

        // ── Dialog helpers ──────────────────────────────────────────────

        private async Task ShowErrorAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Driver Store Manager",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private async Task<bool> ConfirmDangerAsync(string title, string body, string confirm)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = body,
                PrimaryButtonText = confirm,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        /// <summary>Three-outcome dialog: Primary / Secondary / None (cancel).</summary>
        private async Task<ContentDialogResult> ShowTripleChoiceDialog(string title, string body, string primaryLabel, string secondaryLabel)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = body,
                PrimaryButtonText = primaryLabel,
                SecondaryButtonText = secondaryLabel,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            return await dialog.ShowAsync();
        }

        /// <summary>Typed-phrase confirmation for the highest-friction deletes (7.1/7.4).</summary>
        private async Task<bool?> ConfirmTypedAsync(string title, string body, string phrase)
        {
            var input = new TextBox { PlaceholderText = $"Type {phrase}", HorizontalAlignment = HorizontalAlignment.Stretch };
            var error = new TextBlock
            {
                Text = "The text does not match the package name.",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
            };
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new StackPanel { Spacing = 8 },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            var panel = (StackPanel)dialog.Content;
            panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            panel.Children.Add(error);

            dialog.IsPrimaryButtonEnabled = false;
            input.TextChanged += (_, _) =>
            {
                bool match = string.Equals(input.Text.Trim(), phrase, System.StringComparison.Ordinal);
                dialog.IsPrimaryButtonEnabled = match;
                error.Visibility = match ? Visibility.Collapsed : Visibility.Visible;
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        // ── Pickers (hwnd-attached, matching app convention) ────────────

        private async Task<StorageFolder?> PickFolderAsync()
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            return await picker.PickSingleFolderAsync();
        }

        private async Task<StorageFile?> PickInfAsync()
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".inf");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            return await picker.PickSingleFileAsync();
        }
    }
}
