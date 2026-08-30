using System;
using System.Diagnostics;
using System.IO;
using KalOS.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using KalOS.Services;
using KalOS.ViewModels;

namespace KalOS.Views
{
    /// <summary>
    /// Settings page allowing the user to configure app theme, backdrop, and license info.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>
        /// Gets the ViewModel for Settings.
        /// </summary>
        public SettingsViewModel ViewModel { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsPage"/> class.
        /// </summary>
        public SettingsPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        }

        /// <summary>
        /// Shows the current session's log inside the app (live-updating) instead of
        /// shelling out to Explorer. Opening the folder stays available as an
        /// explicit, opt-in action from the dialog.
        /// </summary>
        private async void ViewLogs_Click(object sender, RoutedEventArgs e)
        {
            var logging = App.Services.GetRequiredService<LoggingService>();
            var list = new StackPanel { Spacing = 2 };
            var scroll = new ScrollViewer
            {
                Content = list,
                MaxHeight = 420,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 12, 0)
            };

            // Cap the in-dialog list so a very long session can't grow it unbounded.
            void Append(CleanupLog entry)
            {
                if (list.Children.Count >= 2000) list.Children.RemoveAt(0);
                list.Children.Add(BuildLogRow(entry));
            }

            foreach (var entry in logging.Logs) Append(entry);

            // Keep appending while the dialog is open so the user sees new activity.
            void OnLogAdded(CleanupLog entry) => DispatcherQueue.TryEnqueue(() => Append(entry));
            logging.LogAdded += OnLogAdded;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Application log",
                    Content = scroll,
                    PrimaryButtonText = "Open folder",
                    CloseButtonText = "Close",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.Content.XamlRoot
                };
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary) OpenLogsFolder();
            }
            finally
            {
                logging.LogAdded -= OnLogAdded;
            }
        }

        /// <summary>Builds one timestamped, color-coded log row.</summary>
        private static FrameworkElement BuildLogRow(CleanupLog entry)
        {
            var time = new TextBlock
            {
                Text = entry.Timestamp.ToString("HH:mm:ss"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = ThemeBrush("TextFillColorTertiaryBrush")
            };
            var level = new TextBlock
            {
                Text = entry.Level,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = LevelBrush(entry.Level)
            };
            var message = new TextBlock
            {
                Text = entry.Message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush")
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(time, 0);
            Grid.SetColumn(level, 1);
            Grid.SetColumn(message, 2);
            grid.Children.Add(time);
            grid.Children.Add(level);
            grid.Children.Add(message);
            return grid;
        }

        /// <summary>Color for a log level: errors stand out, successes read green.</summary>
        private static Brush LevelBrush(string level) => level switch
        {
            "Success" => ThemeBrush("SuccessBrush"),
            "Error" => ThemeBrush("ErrorBrush"),
            "Warn" => ThemeBrush("WarningBrush"),
            _ => ThemeBrush("TextFillColorSecondaryBrush")
        };

        /// <summary>Resolves a themed brush by key, with a safe fallback.</summary>
        private static Brush ThemeBrush(string key)
        {
            try
            {
                return (Brush)Application.Current.Resources[key];
            }
            catch
            {
                return new SolidColorBrush(Microsoft.UI.Colors.Gray);
            }
        }

        /// <summary>Opens the KalOS log folder in Explorer (explicit opt-in).</summary>
        private static void OpenLogsFolder()
        {
            try
            {
                string logDir = LogService.GetLogDirectory();
                Directory.CreateDirectory(logDir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logDir}\"") { UseShellExecute = true });
            }
            catch
            {
                // Best-effort — Explorer can fail on locked-down shells.
            }
        }

        /// <summary>Shows the third-party notices file in a scrollable dialog.</summary>
        private async void ShowNotices_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var noticesPath = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
                string text = File.Exists(noticesPath)
                    ? File.ReadAllText(noticesPath)
                    : "Third-party notices file not found.";

                var dialog = new ContentDialog
                {
                    Title = "Third-Party Notices",
                    Content = new ScrollViewer
                    {
                        MaxHeight = 480,
                        Content = new TextBlock
                        {
                            Text = text,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    },
                    CloseButtonText = "Close",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception)
            {
                // Never crash the Settings page over a missing notices file.
            }
        }
    }
}
