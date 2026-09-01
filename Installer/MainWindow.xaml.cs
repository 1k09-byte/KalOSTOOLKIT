using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using KalOS.Setup.Views;

namespace KalOS.Setup
{
    /// <summary>
    /// The installer shell window. Standard WinUI 3 layout: an extended
    /// title bar over a Mica backdrop, a <see cref="NavigationView"/> pane
    /// listing the wizard steps (plus a pinned Install entry), and the
    /// wizard frame + Back/Next/Cancel footer as the pane content.
    /// The pages bind to <see cref="App.Wizard"/> for state and call
    /// <see cref="GoNext"/> / <see cref="GoBack"/> / <see cref="GoTo"/>
    /// to move between steps.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly Type[] _pages =
        {
            typeof(WelcomePage),
            typeof(DriversPage),
            typeof(SoftwarePage),
            typeof(CustomizePage),
            typeof(ProgressPage),
            typeof(FinishPage),
        };

        // Nav item tags map 1:1 to indexes into _pages. Progress runs the
        // install and Finish is the results view (both stay reachable only
        // via the flow, so they have no pane item).
        private int _index;
        private bool _suppressSelection;

        public MainWindow()
        {
            InitializeComponent();
            Title = $"KalOS Installer v{App.AppVersion}";
            TitleBarText.Text = $"KalOS Installer v{App.AppVersion}";

            // Mica over the whole window (falls back to a solid color on
            // systems that don't support it).
            try { SystemBackdrop = new MicaBackdrop(); } catch { }

            // WinUI Gallery-style extended title bar: the app paints the
            // caption area and only the AppTitleBar grid is draggable.
            try { ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar); } catch { }

            CenterOnScreen();
            WizardFrame.Navigate(_pages[0]);
            UpdateChrome();
        }

        private void CenterOnScreen()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);

                appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1080, Height = 700 });
                var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.None);
                if (area is not null)
                {
                    var work = area.WorkArea;
                    appWindow.Move(new Windows.Graphics.PointInt32
                    {
                        X = Math.Max(0, work.X + (work.Width - 1080) / 2),
                        Y = Math.Max(0, work.Y + (work.Height - 700) / 2),
                    });
                }
            }
            catch { }
        }

        // ── NavigationView ───────────────────────────────────────────────

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            // Ignore programmatic selection sync (see UpdateNavSelection).
            if (_suppressSelection) return;
            if (args.SelectedItemContainer?.Tag is string tag &&
                int.TryParse(tag, out int index))
            {
                GoTo(index);
            }
        }

        private void InstallPaneButton_Click(object sender, RoutedEventArgs e)
        {
            // Kick off the install directly — the sidebar entry is the "do it" button.
            if (App.Wizard.IsRunning) return;
            if (_index == _pages.Length - 2) return; // already installing
            _index = _pages.Length - 2;
            WizardFrame.Navigate(_pages[_index]);
            UpdateChrome();
        }

        private void UpdateNavSelection()
        {
            // The sidebar only hosts the config steps; while the install
            // (or the results page) is up, the last entry stays selected.
            if (NavView.MenuItems.Count > 0 &&
                NavView.MenuItems[Math.Min(_index, NavView.MenuItems.Count - 1)] is NavigationViewItem item &&
                !ReferenceEquals(NavView.SelectedItem, item))
            {
                _suppressSelection = true;
                try { NavView.SelectedItem = item; }
                finally { _suppressSelection = false; }
            }

            // The pane freezes while the pipeline runs.
            bool running = App.Wizard.IsRunning;
            NavView.IsEnabled = !running;
            InstallPaneButton.IsEnabled = !running;
        }

        // ── Chrome refresh ───────────────────────────────────────────────

        private void UpdateChrome()
        {
            UpdateNavSelection();

            BackButton.Visibility = _index > 0 && _index < _pages.Length - 1
                ? Visibility.Visible : Visibility.Collapsed;

            bool last = _index == _pages.Length - 1;
            NextButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;

            NextButton.Content = _index switch
            {
                3 => "Install",       // Customize page → run the pipeline
                _ => "Next",
            };

            NextButton.IsEnabled = WizardFrame.Content is WizardPage page && page.CanProceed;
        }

        // ── Footer button handlers ───────────────────────────────────────

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (WizardFrame.Content is WizardPage page && !page.AllowBack) return;
            GoBack();
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (WizardFrame.Content is not WizardPage page) return;
            if (!page.CanProceed) return;
            if (!page.OnAdvance()) return;
            GoNext();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_index == _pages.Length - 1) { Close(); return; }
            if (App.Wizard.IsRunning) return;
            Close();
        }

        // ── Page transitions (always fresh Navigate — no frame stack) ───

        public void GoNext()
        {
            if (_index >= _pages.Length - 1) return;
            NavigateTo(_index + 1);
        }

        public void GoBack()
        {
            if (_index <= 0) return;
            NavigateTo(_index - 1);
        }

        /// <summary>
        /// Navigates to a step from the nav pane. The Progress view can't be
        /// jumped to manually (it re-runs the pipeline); all navigation is
        /// blocked mid-run.
        /// </summary>
        public void GoTo(int index)
        {
            if (index < 0 || index >= _pages.Length) return;
            if (index == _pages.Length - 2) return; // ProgressPage — install runs via Next/Install
            NavigateTo(index);
        }

        /// <summary>The one navigation funnel; guards the running state.</summary>
        private void NavigateTo(int index)
        {
            if (App.Wizard.IsRunning) return;
            if (index == _index) { UpdateChrome(); return; }
            _index = index;
            WizardFrame.Navigate(_pages[index]);
            UpdateChrome();
        }

        /// <summary>Called by pages when their validity changes so the Next button refreshes.</summary>
        public void RefreshNav() => UpdateChrome();
    }
}