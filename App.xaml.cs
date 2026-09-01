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
        private StartupBannerWindow? _startupBanner;
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
            "Edit Toolkit";
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
            services.AddSingleton<StartupTasksService>();
            services.AddSingleton<DiskCleanupService>();

            // ── BIOS management ─────────────────────────────────────────────
            services.AddSingleton<KalOS.Services.Bios.IWmiClient, KalOS.Services.Bios.SystemManagementWmiClient>();
            services.AddSingleton<KalOS.Services.Bios.ScewinService>();
            services.AddSingleton<KalOS.Services.Bios.BiosProviderFactory>();
            services.AddSingleton<KalOS.Services.Bios.BiosUpdateService>();

            // ── GPU driver stack ────────────────────────────────────────
            services.AddSingleton<GpuDetectionService>();
            services.AddSingleton<DriverDownloadService>();
            services.AddSingleton<DriverInstallService>();
            services.AddSingleton<DriverCleanupService>();
            services.AddSingleton<RadeonSlimmerService>();
            services.AddSingleton<AmdAutoDetectService>();
            services.AddSingleton<RadeonPackageSlimmer>();
            services.AddSingleton<IDriverProvider, NvidiaDriverProvider>();
            services.AddSingleton<IDriverProvider, AmdDriverProvider>();
            services.AddSingleton<IDriverProvider, IntelDriverProvider>();
            services.AddSingleton<DriverService>();
            services.AddSingleton<CoreSpreadingService>();

            // ── Setup wizard (first-run install experience) ──────────────────
            // The wizard UI is compiled into this app (see KalOS.csproj's
            // Installer/** includes); these are the pieces its pipeline needs
            // that the consumer pages don't already register.
            services.AddSingleton<TweaksService>();
            services.AddSingleton<GitHubReleaseClient>();
            services.AddSingleton<HttpFileDownloader>();
            services.AddSingleton<KalOS.Setup.InstallerPipeline>();
            services.AddSingleton<KalOS.Setup.ViewModels.InstallerViewModel>();


            // ── ViewModels ─────────────────────────────────────────────
            services.AddTransient<MainViewModel>();
            services.AddTransient<HomeViewModel>();
            services.AddTransient<PersonalizationViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<StartupViewModel>();
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

            // Consumer build: startup at Windows login is mandatory — (re)write
            // the HKCU Run key on every launch so the banner — and any update
            // applied in between — always shows after the next boot, even if
            // the user deleted the registry entry manually. The dev/edit build
            // leaves the Run key to the Settings toggle instead.
#if CONSUMER_BUILD
            StartupTasksService.EnableAutostart();
#endif

            // Launched at Windows login (HKCU Run key writes "KalOS.exe --startup").
            // Skip the main window entirely: show only the drop-down banner that
            // runs the user's startup command list and checks for toolkit updates.
            var cmdArgs = Environment.GetCommandLineArgs();
            if (Array.IndexOf(cmdArgs, "--startup") >= 0 || Array.IndexOf(cmdArgs, "-startup") >= 0)
            {
                StartStartupBanner();
                return;
            }

#if CONSUMER_BUILD
            // One big app (consumer build only): a fresh install opens straight
            // into the embedded setup wizard (Install KalOS → drivers → software
            // → tweaks); the moment the wizard's pipeline completes, the marker
            // flips and the same process swaps into the full consumer app.
            // --setup forces the wizard again on an already-set-up machine.
            bool forceSetup = Array.IndexOf(cmdArgs, "--setup") >= 0 || Array.IndexOf(cmdArgs, "-setup") >= 0;
            if (!forceSetup && KalOS.Setup.SetupState.IsSetupComplete)
            {
                _window = new MainWindow();
                _window.Activate();

                StartUpdateCheck();
                ShowUpdateLogIfAny();
                ShowRollbackIfRequired();
            }
            else
            {
                LaunchSetupWizard();
            }
#else
            // Edit-toolkit (dev) build: the first-run setup wizard is a
            // consumer-only feature — always open the consumer shell directly.
            _window = new MainWindow();
            _window.Activate();

            StartUpdateCheck();
            ShowUpdateLogIfAny();
            ShowRollbackIfRequired();
#endif
        }

        /// <summary>
        /// First-run mode: shows the setup wizard instead of the consumer
        /// shell. When the wizard window closes and the install pipeline has
        /// completed (setup marker written), the consumer shell is opened
        /// first — so the app never drops to zero windows, which would exit
        /// the process — and the wizard window is then closed behind it.
        /// Closing the wizard WITHOUT a completed install exits the app
        /// normally; the wizard shows again on the next launch.
        /// </summary>
        private void LaunchSetupWizard()
        {
            KalOS.Setup.SetupState.Embedded = true;
            KalOS.Setup.App.InitializeWizard();

            var wizard = new KalOS.Setup.MainWindow();
            KalOS.Setup.App.MainWindow = wizard;
            _window = wizard;

            // Shared between both close paths so whichever fires first wins.
            bool swapped = false;

            void SwapToConsumer()
            {
                if (swapped || !KalOS.Setup.SetupState.IsSetupComplete) return;
                swapped = true;

                // Open the consumer shell FIRST so the app never drops to zero
                // windows — that would exit the process before the swap.
                _window = new MainWindow();
                _window.Activate();

                StartUpdateCheck();
                ShowUpdateLogIfAny();
                ShowRollbackIfRequired();

                wizard.Close(); // re-enters the Closing hook; 'swapped' lets it through
            }

            // Path 1 — the Finish page's Close/exit (Window.Close() bypasses
            // AppWindow.Closing, so the host must be handed it explicitly).
            KalOS.Setup.SetupState.EmbeddedCloseHandler = SwapToConsumer;

            // Path 2 — the title-bar ✕ / Alt+F4 after a completed setup.
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(wizard);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                appWindow.Closing += (s, e) =>
                {
                    if (swapped || !KalOS.Setup.SetupState.IsSetupComplete) return;
                    // Cancel this close, then run the swap on the dispatcher —
                    // calling wizard.Close() from inside a pending Closing event
                    // would re-enter the handler mid-close.
                    e.Cancel = true;
                    wizard.DispatcherQueue.TryEnqueue(SwapToConsumer);
                };
            }
            catch
            {
                // If the close interception is unavailable, closing the wizard
                // simply exits; the marker survives and the next launch is
                // the consumer app.
            }

            wizard.Activate();
        }

        /// <summary>
        /// Startup-banner mode: shows the top-right drop-down, runs the user's
        /// configured startup commands hidden, checks for updates, then exits
        /// (unless an update was found, in which case the banner stays up until
        /// the user clicks Open or closes it).
        /// </summary>
        private void StartStartupBanner()
        {
            try
            {
                var startup = Services.GetRequiredService<StartupTasksService>();
                var update = Services.GetRequiredService<UpdateService>();
                var settings = startup.Load();

                var banner = new StartupBannerWindow(startup, update, settings);
                // Keep the banner alive after OnLaunched returns: WinUI only
                // keeps a window alive while a reference is reachable, so cache it.
                _startupBanner = banner;
                banner.Closed += (_, _) =>
                {
                    // When the banner closes (auto-hide, close button, or update
                    // notice dismissed), exit the process so the login launch ends.
                    Environment.Exit(0);
                };
                banner.Run();
            }
            catch (Exception ex)
            {
                // Never leave the user with an invisible hung process at login.
                try
                {
                    Services.GetRequiredService<LogService>()
                        .WriteAsync("App", "StartupBanner", $"Failed: {ex}", isError: true);
                }
                catch { }
                Environment.Exit(0);
            }
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

            // If this build ships OS changes (os-changes.json), offer to apply
            // them right here. The manifest rides inside the update zip, so each
            // release can change the OS without recompiling the app.
            var osManifest = OsChangeService.LoadFromInstallDir();
            // Only offer "Apply changes" when this release actually ships changes.
            // A manifest with an empty "changes" array (no tweaks this release)
            // must not nag — the popup falls back to "View apply log" only.
            var needsApply = osManifest is { Changes.Count: > 0 } && !OsChangeService.IsApplied(osManifest);

            var dialog = new ContentDialog
            {
                Title = $"Update log — KalOS {rec.Version}",
                Content = body,
                PrimaryButtonText = needsApply ? "Apply changes" : "View apply log",
                SecondaryButtonText = needsApply ? "View apply log" : string.Empty,
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = content.XamlRoot,
            };

            if (needsApply && osManifest != null)
            {
                dialog.PrimaryButtonClick += (s, args) => ApplyOsChanges(dialog, osManifest, applyLogPath, args);
            }
            _ = ShowUpdateLogDialogAsync(dialog, applyLogPath);
        }

        /// <summary>
        /// Applies the update's os-changes.json manifest (the "Apply changes"
        /// button on the update log popup), then reports the result and logs it
        /// so "View apply log" shows what happened.
        /// </summary>
        private void ApplyOsChanges(ContentDialog dialog, OsChangeManifest manifest, string applyLogPath,
            ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            if (_window?.Content is not FrameworkElement content || content.XamlRoot == null) return;

            _ = Task.Run(() =>
            {
                // Run the registry/service writes off the UI thread; the app is
                // elevated (requireAdministrator), so no UAC prompt appears.
                var result = new OsChangeResult();
                var ok = new OsChangeService().TryApply(manifest, result);

                string logLine = ok
                    ? $"[OK] Applied {result.Applied.Count} OS change(s) at {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {string.Join("; ", result.Applied)}"
                    : $"[FAILED] {result.Summary}: {string.Join("; ", result.Errors)}";
                try { System.IO.File.AppendAllText(applyLogPath, logLine + Environment.NewLine); } catch { }

                var details = new System.Text.StringBuilder();
                details.AppendLine(result.Errors.Count > 0 ? "Applied:" : "All changes applied:");
                foreach (var a in result.Applied) details.AppendLine("  • " + a);
                if (result.Errors.Count > 0)
                {
                    details.AppendLine();
                    details.AppendLine("Errors:");
                    foreach (var e in result.Errors) details.AppendLine("  ✗ " + e);
                }

                // Show the result on the UI thread. ContentDialog buttons were
                // cancelled, so hide it and swap in the result dialog.
                dialog.DispatcherQueue.TryEnqueue(() =>
                {
                    dialog.Hide();
                    var resultDialog = new ContentDialog
                    {
                        Title = ok ? "OS changes applied" : "Some OS changes failed",
                        Content = new TextBlock
                        {
                            Text = details.ToString(),
                            TextWrapping = TextWrapping.Wrap,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        },
                        CloseButtonText = "OK",
                        XamlRoot = content.XamlRoot,
                    };
                    _ = resultDialog.ShowAsync();
                });
            });
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
                else
                {
                    // Startup-banner mode (no main window): the banner is broken,
                    // so exit instead of lingering as an invisible process that
                    // holds the single-instance mutex and blocks the next launch.
                    Environment.Exit(0);
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
