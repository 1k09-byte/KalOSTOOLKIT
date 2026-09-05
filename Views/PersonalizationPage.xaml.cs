using System;
using System.Collections.Generic;
using System.ComponentModel;
using KaliteKit.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace KaliteKit.Views;

public sealed partial class PersonalizationPage : Page
{
    public PersonalizationViewModel ViewModel { get; }

    public PersonalizationPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<PersonalizationViewModel>();
    }

    private void VisualEffectsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(typeof(VisualEffectsPage));
    }

    private void WindhawkButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(typeof(WindhawkPage));
    }

    private void NavigateTo(Type pageType)
    {
        if (App.Current is App { MainWindow: MainWindow window })
        {
            window.NavigateToPage(pageType);
        }
        else
        {
            Frame.Navigate(pageType);
        }
    }

    private async void UniGetUiButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "UniGetUI",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        // Content host shared by every "tab" (cards / apps / uni getui).
        var host = new StackPanel { Spacing = 0 };
        var scroller = new ScrollViewer
        {
            MaxHeight = 560,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = host,
        };
        dialog.Content = scroller;

        // Track PropertyChanged subscriptions so they are released when the
        // dialog closes (the view models are singletons - leaking leaks cards).
        var subscriptions = new List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)>();

        void SwitchTo(Panel panel)
        {
            host.Children.Clear();
            host.Children.Add(panel);
            scroller.ChangeView(0, 0, null);
        }

        dialog.Closed += (_, _) =>
        {
            foreach (var (source, handler) in subscriptions)
            {
                source.PropertyChanged -= handler;
            }
            subscriptions.Clear();
        };

        // UniGetUI is the only remaining external tool — open its panel directly.
        SwitchTo(BuildUniGetUiPanel(dialog, subscriptions, null));
        await dialog.ShowAsync();
    }

    // ── UniGetUI panel ─────────────────────────────────────────────────

    private Panel BuildUniGetUiPanel(
        ContentDialog dialog,
        List<(INotifyPropertyChanged, PropertyChangedEventHandler)> subscriptions,
        Action? goBack)
    {
        var uni = App.Services.GetRequiredService<WingetUiViewModel>();
        uni.RefreshState();

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(BuildSectionHeader(goBack, "UniGetUI", "Graphical interface for winget, chocolatey, scoop and more."));

        var status = new TextBlock
        {
            Text = uni.StatusText,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
        };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };

        var installBtn = new Button
        {
            Content = "Install",
            CornerRadius = new CornerRadius(6),
            Style = TryGetStyle("AccentButtonStyle"),
        };
        var pinBtn = new Button { Content = "Pin to taskbar", CornerRadius = new CornerRadius(6) };
        installBtn.Command = uni.InstallCommand;
        pinBtn.Command = uni.PinToTaskbarCommand;

        void Refresh()
        {
            status.Text = uni.StatusText;
            installBtn.Visibility = uni.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
            pinBtn.Visibility = uni.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
            progress.Visibility = uni.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = uni.IsInstalling;
        }
        Refresh();
        Subscribe(subscriptions, uni, Refresh);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        buttons.Children.Add(installBtn);
        buttons.Children.Add(pinBtn);

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new FontIcon
        {
            Glyph = "\uE896",
            FontSize = 36,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"],
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        content.Children.Add(new TextBlock
        {
            Text = "UniGetUI",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 18,
        });
        content.Children.Add(status);
        content.Children.Add(buttons);
        content.Children.Add(progress);

        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20),
            Child = content,
        });

        return root;
    }

    // ── Shared helpers ─────────────────────────────────────────────────

    private static Panel BuildSectionHeader(Action? goBack, string title, string subtitle)
    {
        var header = new StackPanel { Spacing = 2 };

        if (goBack != null)
        {
            var back = new Button
            {
                Content = "Back",
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            back.Click += (_, _) => goBack();
            header.Children.Add(back);
        }

        header.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 20,
            Margin = new Thickness(0, 4, 0, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 2, 0, 0),
        });
        return header;
    }

    private static void Subscribe(
        List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> subscriptions,
        INotifyPropertyChanged source,
        Action refresh)
    {
        PropertyChangedEventHandler handler = (_, _) =>
        {
            if (App.Current is App { MainWindow: MainWindow window }
                && window.DispatcherQueue is { } queue
                && !queue.HasThreadAccess)
            {
                queue.TryEnqueue(() => refresh());
            }
            else
            {
                refresh();
            }
        };
        source.PropertyChanged += handler;
        subscriptions.Add((source, handler));
    }

    private static Style? TryGetStyle(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;
    }
}