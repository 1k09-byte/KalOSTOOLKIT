using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KalOS.Services;
using KalOS.ViewModels;

namespace KalOS
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\KalOS.SingleInstance";

        private Window? _window;
        private Mutex? _instanceMutex;
        public Window? MainWindow => _window;

        /// <summary>
        /// Gets the service provider containing the application services.
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>HWND of the single main window, cached so view-layer helpers (file pickers, dialogs) can attach without an instance reference.</summary>
        public static IntPtr MainWindowHandle;

        /// <summary>Just the assembly version, e.g. "1.1.4.0" — shown in the window title bar.</summary>
        public static string AppVersion { get; } =
#if CONSUMER_BUILD
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
#else
            "Edit App";
#endif

        /// <summary>Display name, version, OS, and architecture of this build — used by startup/crash diagnostics.</summary>
        public static string BuildInfo =>
            $"KalOS {AppVersion} | {RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture} | user {Environment.UserName}";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        // Kernel32.dll error-mode constants used by SetErrorMode.
        private const uint SEM_FAILCRITICALERRORS = 0x0001;
        private const uint SEM_NOGPFAULTERRORBOX = 0x0002;   // suppresses the "has stopped working" popup
        private const uint SEM_NOALIGNMENTFAULTEXCEPT = 0x0004;
        private const uint SEM_NOOPENFILEERRORBOX = 0x8000;  // suppresses "Unknown Hard Error" dialogs

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint uMode);

        /// <summary>
        /// Initializes the singleton application object.
        /// </summary>
        public App()
        {
            // Suppress the OS-level "Unknown Hard Error" / "has stopped working"
            // message boxes. Those dialogs are raised natively (NtRaiseHardError / WER)
            // and can never be caught by .NET exception handlers, so they would
            // otherwise block the app as a modal instead of being logged and routed
            // through the managed crash handlers below.
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOALIGNMENTFAULTEXCEPT | SEM_NOOPENFILEERRORBOX);

            // Enable Per-Monitor DPI Awareness V2 to fix blurry popups/dropdowns
            SetProcessDpiAwarenessContext(new IntPtr(-4));

            this.InitializeComponent();

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            Services = serviceCollection.BuildServiceProvider();

            // NOTE: no crash logging is wired in the constructor. Handlers that can
            // throw (e.g. File.WriteAllText to a hardcoded path) must never be
            // attached here: an exception escaping an UnhandledException handler
            // bypasses all managed recovery and surfaces as the native
            // "Unknown Hard Error" system dialog. The only handlers are attached
            // in OnLaunched and route through the fully no-throw LogCrash().
        }

        private void ConfigureServices(ServiceCollection services)
        {
            // ── Core Services ─────────────────────────────────────────
            services.AddSingleton<ThemeService>();
            services.AddSingleton<BackdropService>();
            services.AddSingleton<LogService>();
            services.AddSingleton<LoggingService>();
            services.AddSingleton<ProcessManager>();
            services.AddSingleton<RadioStackService>();
            services.AddSingleton<PackageManagerService>();
            services.AddSingleton<WindowsServiceManager>();
            services.AddSingleton<ElevationService>();
            services.AddSingleton<WindhawkManagerService>();
            services.AddSingleton<HardwareMonitorService>();
            services.AddSingleton<SystemRefreshService>();
            services.AddSingleton<UpdateService>();

            // ── BIOS management ─────────────────────────────────────────────
            services.AddSingleton<KalOS.Services.Bios.IWmiClient, KalOS.Services.Bios.SystemManagementWmiClient>();
            services.AddSingleton<KalOS.Services.Bios.ScewinService>();
            services.AddSingleton<KalOS.Services.Bios.BiosProviderFactory>();

            // ── GPU driver stack ────────────────────────────────────────
            services.AddSingleton<GpuDetectionService>();
            services.AddSingleton<DriverDownloadService>();
            services.AddSingleton<DriverInstallService>();
            services.AddSingleton<DriverCleanupService>();
            services.AddSingleton<IDriverProvider, NvidiaDriverProvider>();
            services.AddSingleton<IDriverProvider, AmdDriverProvider>();
            services.AddSingleton<IDriverProvider, IntelDriverProvider>();
            services.AddSingleton<DriverService>();

            // ── ViewModels ─────────────────────────────────────────────
            services.AddTransient<MainViewModel>();
            services.AddTransient<HomeViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<BrowserViewModel>();
            services.AddSingleton<AffinityManagerViewModel>();
            services.AddSingleton<WingetUiViewModel>();
            services.AddSingleton<WindhawkViewModel>();
            services.AddSingleton<GpuDriversViewModel>();
            services.AddSingleton<SdioManagerService>();
            services.AddSingleton<SdioViewModel>();
            services.AddSingleton<AdditionalTweaksViewModel>();
            services.AddSingleton<SystemOverviewViewModel>();
            services.AddTransient<BiosViewModel>();

        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            this.UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // Single instance: a second launch exits immediately instead of
            // running a second copy that could race on installers/registry and
            // confuse which build is actually on screen.
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                this.Exit();
                return;
            }

            // Startup diagnostics: the very first line of every log file identifies
            // the exact build, OS, and architecture — "which exe is running" is
            // never ambiguous again.
            Services.GetRequiredService<LogService>()
                .WriteAsync("App", "Startup", BuildInfo + " | path " + AppContext.BaseDirectory);

            _window = new MainWindow();
            _window.Activate();

            StartUpdateCheck();
            ShowUpdateLogIfAny();
            ShowRollbackIfRequired();
        }

        private void ShowRollbackIfRequired(UpdateInfo? update = null)
        {
#if CONSUMER_BUILD
            if (update == null && !File.Exists(UpdateService.RollbackStatePath)) return;
            var queue = _window?.DispatcherQueue;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                queue?.TryEnqueue(() => ShowMandatoryRollbackDialog(update));
            });
#endif
        }

        private void ShowMandatoryRollbackDialog(UpdateInfo? preFetchedUpdate)
        {
            if (_window?.Content is not FrameworkElement root || root.XamlRoot == null) return;
            var settingsVm = Services.GetRequiredService<SettingsViewModel>();
            
            var progressBar = new ProgressBar
            {
                IsIndeterminate = false, Maximum = 100, Value = 0,
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 0)
            };
            var progressText = new TextBlock
            {
                Text = "Starting download...", Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0), FontSize = 12, Opacity = 0.7
            };
            var panel = new StackPanel
            {
                Children = 
                {
                    new TextBlock
                    {
                        Text = "This version of KalOS has been removed from GitHub because it is unstable. A rollback to the previous stable version is required. The app will close and install it now.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 440
                    },
                    progressBar, progressText
                }
            };

            var dialog = new ContentDialog
            {
                Title = "KalOS version removed",
                Content = panel,
                PrimaryButtonText = "Roll back now",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root.XamlRoot
            };

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                var deferral = args.GetDeferral();
                args.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false;
                progressBar.Visibility = Visibility.Visible;
                progressText.Visibility = Visibility.Visible;

                try
                {
                    var update = preFetchedUpdate ?? await Services.GetRequiredService<UpdateService>().CheckForUpdatesAsync();
                    if (File.Exists(UpdateService.RollbackStatePath)) File.Delete(UpdateService.RollbackStatePath);
                    if (update == null) { RestartApp(); return; }

                    var progress = new Progress<double>(p => 
                    {
                        progressBar.Value = p * 100;
                        progressText.Text = $"Downloading update: {progressBar.Value:0}%";
                    });

                    await Services.GetRequiredService<UpdateService>().DownloadAndApplyAsync(update, progress);
                    Environment.Exit(0);
                }
                catch { }
                finally { deferral.Complete(); }
            };

            _ = dialog.ShowAsync();
        }

        private void StartUpdateCheck()
        {
#if CONSUMER_BUILD
            var settingsVm = Services.GetRequiredService<SettingsViewModel>();
            var updateService = Services.GetRequiredService<UpdateService>();
            
            settingsVm.UpdateAvailable += OnUpdateAvailable;
            updateService.RollbackRequired += ShowRollbackIfRequired;
            
            // Delay briefly so the window is visible before the check runs, then
            // run the check ON THE UI THREAD. The view model raises
            // PropertyChanged (x:Bind requires the UI thread) and the update
            // dialog touches the XamlRoot — doing this on a background thread
            // used to throw and silently kill the check.
            var queue = _window?.DispatcherQueue;
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                if (queue is null) return;
                queue.TryEnqueue(() =>
                {
                    try { settingsVm.RunStartupCheck(); } catch { /* never crash on update plumbing */ }
                });
            });
#endif
        }

        /// <summary>
        /// After an update restarts the app, show the update log (release notes
        /// and the apply result) once.
        /// </summary>
        private void ShowUpdateLogIfAny()
        {
#if CONSUMER_BUILD
            var rec = UpdateService.LoadLastUpdateRecord();
            if (rec is null) return;
            // Only relevant if the running build actually matches the record.
            if (!Version.TryParse(rec.Version, out var v) || v != UpdateService.CurrentVersion) return;
            // Show once: clear now so a further restart doesn't repeat it.
            UpdateService.ClearLastUpdateRecord();

            var queue = _window?.DispatcherQueue;
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                if (queue is null) return;
                queue.TryEnqueue(() => ShowUpdateLogDialog(rec));
            });
#endif
        }

        private void ShowUpdateLogDialog(UpdateRecord rec)
        {
            if (_window?.Content is not FrameworkElement content || content.XamlRoot == null) return;
            var applyLogPath = System.IO.Path.Combine(UpdateService.AppDataFolder, "updates", "update.log");
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock
            {
                Text = $"KalOS was updated to {rec.Version} on {rec.AppliedAt:g}.",
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(rec.Notes) ? "No release notes were provided for this version." : "What's new:",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (!string.IsNullOrWhiteSpace(rec.Notes))
            {
                body.Children.Add(new TextBlock { Text = rec.Notes, TextWrapping = TextWrapping.Wrap });
            }
            body.Children.Add(new TextBlock
            {
                Text = $"Apply log: {applyLogPath}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            });

            var dialog = new ContentDialog
            {
                Title = $"Update log — KalOS {rec.Version}",
                Content = body,
                PrimaryButtonText = "View apply log",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = content.XamlRoot,
            };
            _ = ShowUpdateLogDialogAsync(dialog, applyLogPath);
        }

        /// <summary>Reads the apply-log file so it can be shown inside the app.</summary>
        private static string ReadApplyLog(string path)
        {
            try
            {
                return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : $"Apply log not found: {path}";
            }
            catch (Exception ex)
            {
                return $"Could not read apply log: {ex.Message}";
            }
        }

        private async Task ShowUpdateLogDialogAsync(ContentDialog dialog, string applyLogPath)
        {
            try
            {
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                // Show the log content in a dialog instead of shell-opening the
                // .log file: .log files often have no default handler, so the
                // old Process.Start silently did nothing.
                if (_window?.Content is not FrameworkElement content || content.XamlRoot == null) return;
                var view = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = ReadApplyLog(applyLogPath),
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    MaxHeight = 300,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                var logDialog = new ContentDialog
                {
                    Title = "Apply log",
                    Content = view,
                    CloseButtonText = "OK",
                    XamlRoot = content.XamlRoot,
                };
                await logDialog.ShowAsync();
            }
            catch
            {
                // Never let the update-log plumbing crash the app.
            }
        }

        private void OnUpdateAvailable(Version version)
        {
            if (_window?.Content is not FrameworkElement content || content.XamlRoot == null) return;
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                var settingsVm = Services.GetRequiredService<SettingsViewModel>();
                var rollback = settingsVm.PendingUpdateIsRollback;
                var dialog = new ContentDialog
                {
                    Title = rollback ? "Rollback required" : "Update available",
                    Content = new TextBlock
                    {
                        Text = rollback
                            ? $"KalOS {AppVersion} is newer than the published version {version}. This build is unstable and must be rolled back. The app will restart automatically after the rollback."
                            : $"KalOS {version} is ready to download and install.\n\nYour app will restart automatically after the update finishes.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420
                    },
                    PrimaryButtonText = rollback ? "Roll back now" : "Download & install",
                    CloseButtonText = rollback ? string.Empty : "Later",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = content.XamlRoot
                };
                _ = ShowUpdateDialogAsync(dialog, settingsVm);
            });
        }

        private async Task ShowUpdateDialogAsync(ContentDialog dialog, SettingsViewModel settingsVm)
        {
            try
            {
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                // The old dialog is gone the moment a button is pressed, and the
                // download runs invisibly unless the user happens to be on the
                // Settings page (the only place with a progress bar). Morph a
                // fresh dialog into a live progress view so the download is
                // visible no matter which page is open.
                var status = new TextBlock
                {
                    Text = "Preparing download…",
                    TextWrapping = TextWrapping.Wrap,
                };
                var bar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = 0,
                    Width = 320,
                };
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(status);
                panel.Children.Add(bar);

                dialog.Title = "Downloading update";
                dialog.Content = panel;
                dialog.PrimaryButtonText = string.Empty;
                dialog.CloseButtonText = "Cancel";
                dialog.DefaultButton = ContentDialogButton.None;

                void OnVmChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(SettingsViewModel.DownloadProgress))
                    {
                        bar.Value = settingsVm.DownloadProgress;
                    }
                    else if (e.PropertyName == nameof(SettingsViewModel.UpdateStatusText))
                    {
                        status.Text = settingsVm.UpdateStatusText;
                    }
                }

                settingsVm.PropertyChanged += OnVmChanged;
                try
                {
                    // Fire-and-forget the dialog so it stays up while the
                    // download runs; the VM's progress callbacks update it.
                    _ = dialog.ShowAsync();
                    await settingsVm.DownloadAndInstallAsync();
                }
                finally
                {
                    settingsVm.PropertyChanged -= OnVmChanged;
                    // On success the app has already exited (Environment.Exit).
                    // On failure, close the progress dialog so the Settings
                    // page status (with the real error) is visible again.
                    dialog.Hide();
                }
            }
            catch
            {
                // The dialog failing to show must never crash the app.
            }
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // Contain FIRST — before any work that could itself throw — so the
            // app's own recovery path always survives a failure in logging.
            e.Handled = true;

            LogCrash("WinUI UnhandledException", e.Exception);

            try
            {
                if (_window?.DispatcherQueue is { } queue)
                {
                    queue.TryEnqueue(() => ShowCrashRecoveryDialog(e.Exception));
                }
            }
            catch
            {
                // Never let handler bookkeeping become a second crash.
            }
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogCrash("AppDomain UnhandledException", ex);
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            LogCrash("TaskScheduler UnobservedTaskException", e.Exception);
        }

        /// <summary>Offers to restart the app after a handled WinUI exception.</summary>
        private async void ShowCrashRecoveryDialog(Exception exception)
        {
            try
            {
                if (_window?.Content is not FrameworkElement root) return;

                var dialog = new ContentDialog
                {
                    Title = "KalOS ran into a problem",
                    Content = new TextBlock
                    {
                        Text = $"Something unexpected happened and the app may be unstable. Restarting fresh is recommended.\n\nDetails: {exception.Message}",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420,
                    },
                    PrimaryButtonText = "Restart",
                    CloseButtonText = "Continue anyway",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = root.XamlRoot,
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    RestartApp();
                }
            }
            catch
            {
                // The window may be too broken to show a dialog; crash log already written.
            }
        }

        /// <summary>Restarts the app from its own exe, then exits the current instance.</summary>
        private static void RestartApp()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.ProcessPath))
                {
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true });
                }
            }
            catch { }
            Environment.Exit(0);
        }

        private void LogCrash(string source, Exception ex)
        {
            try
            {
                var crashDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KalOS", "CrashLogs");
                Directory.CreateDirectory(crashDir);
                var logPath = System.IO.Path.Combine(crashDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                // ToString() includes inner exceptions, which matters for
                // AggregateExceptions from unobserved tasks.
                System.IO.File.WriteAllText(logPath, $"[{DateTime.Now}] {BuildInfo}\n[{DateTime.Now}] [{source}] {ex}\n\n");

                // Also mirror to the main log so session logs and crash logs agree.
                Services.GetRequiredService<LogService>()
                    .WriteAsync("Crash", source, $"{ex.GetType().Name}: {ex.Message}", isError: true);

                // Keep only the last 5 crash logs
                var oldLogs = Directory.GetFiles(crashDir, "crash_*.txt")
                    .OrderByDescending(f => f)
                    .Skip(5);
                foreach (var old in oldLogs)
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }
        }
    }
}
