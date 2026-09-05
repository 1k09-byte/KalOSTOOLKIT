using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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

        /// <summary>Backs the Startup section of the Settings page.</summary>
        public StartupViewModel StartupViewModel { get; }

        /// <summary>Backs the Tint Color section (shared with the Personalization page).</summary>
        public PersonalizationViewModel TintViewModel { get; }

        /// <summary>Keeps the preview banner alive while it is showing.</summary>
        private StartupBannerWindow? _previewBanner;

        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsPage"/> class.
        /// </summary>
        public SettingsPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            StartupViewModel = App.Services.GetRequiredService<StartupViewModel>();
            TintViewModel = App.Services.GetRequiredService<PersonalizationViewModel>();
        }

        /// <summary>Opens a color picker for a fully custom tint; applied live.</summary>
        private void CustomTintButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new ColorPicker
            {
                Width = 300,
                IsAlphaEnabled = false,
                IsColorSliderVisible = true,
                IsColorSpectrumVisible = true,
                IsHexInputVisible = true,
                Color = Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x6F, 0xB8),
            };

            picker.ColorChanged += (_, args) => TintViewModel.ApplyCustomColor(args.NewColor);

            var flyout = new Flyout
            {
                Content = picker,
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            };
            flyout.ShowAt(SettingsCustomTintButton);
        }

        /// <summary>
        /// Shows the login banner as a non-invoking preview so the user can see
        /// what it looks like without actually running any startup work.
        /// </summary>
        private void PreviewStartup_Click(object sender, RoutedEventArgs e)
        {
            var startup = App.Services.GetRequiredService<StartupTasksService>();
            var update = App.Services.GetRequiredService<UpdateService>();
            var settings = startup.Load();

            _previewBanner = new StartupBannerWindow(startup, update, settings);
            _previewBanner.Closed += (_, _) => _previewBanner = null;
            // Tracked so a graceful app exit closes the preview banner before
            // the main window — its close must be the last one for the
            // DispatcherQueue event loop to exit cleanly.
            App.TrackWindow(_previewBanner);
            _previewBanner.Preview();
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

        /// <summary>
        /// Shows every published KalOS version with its date and release notes,
        /// the running build marked "current", and a link out to GitHub.
        /// </summary>
        private async void ShowReleaseHistory_Click(object sender, RoutedEventArgs e)
        {
            var status = new TextBlock
            {
                Text = "Loading release history…",
                FontSize = 12,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, 8, 0, 0)
            };
            var dialog = new ContentDialog
            {
                Title = "Release history",
                Content = status,
                PrimaryButtonText = "View on GitHub",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            // Fetch off the UI thread; the dialog is already visible with a
            // status line so a slow/rate-limited GitHub response isn't silent.
            IReadOnlyList<ReleaseHistoryEntry> releases;
            try
            {
                releases = await App.Services.GetRequiredService<UpdateService>().GetReleaseHistoryAsync();
            }
            catch
            {
                releases = Array.Empty<ReleaseHistoryEntry>();
            }

            if (releases.Count == 0)
            {
                status.Text = "Could not load the release history from GitHub. It may be rate-limited or offline — use \"View on GitHub\" instead.";
            }
            else
            {
                var rows = new StackPanel { Spacing = 2 };
                foreach (var release in releases)
                {
                    if (rows.Children.Count > 0)
                    {
                        rows.Children.Add(new Border
                        {
                            Height = 1,
                            Background = ThemeBrush("DividerStrokeColorDefaultBrush")
                        });
                    }
                    rows.Children.Add(BuildReleaseRow(release));
                }
                dialog.Content = new ScrollViewer
                {
                    Content = rows,
                    MaxHeight = 460,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(0, 0, 12, 0)
                };
            }

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                OpenGitHubReleases();
            }
        }

        /// <summary>Builds one release row: version + date (+ "current" badge) and the release notes.</summary>
        private static FrameworkElement BuildReleaseRow(ReleaseHistoryEntry entry)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = $"v{entry.Version}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13
            });
            if (entry.PublishedAt is { } when)
            {
                header.Children.Add(new TextBlock
                {
                    Text = when.ToLocalTime().ToString("MMM d, yyyy"),
                    FontSize = 12,
                    Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            if (entry.IsCurrent)
            {
                header.Children.Add(new Border
                {
                    Background = ThemeBrush("AccentFillColorDefaultBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "current",
                        FontSize = 11,
                        Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush")
                    }
                });
            }

            var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(header);
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(entry.Notes)
                    ? "No release notes for this version."
                    : entry.Notes,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = ThemeBrush(string.IsNullOrWhiteSpace(entry.Notes)
                    ? "TextFillColorTertiaryBrush"
                    : "TextFillColorSecondaryBrush")
            });
            return panel;
        }

        /// <summary>Opens the project's GitHub Releases page in the browser.</summary>
        private static void OpenGitHubReleases()
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    $"https://github.com/{UpdateService.DefaultOwner}/{UpdateService.DefaultRepo}/releases")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // Browser launch is best-effort.
            }
        }

    }
}
