using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Settings;
using KaliteKit.Services;
using KaliteKit.ViewModels;

namespace KaliteKit
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\KaliteKit.SingleInstance";

        private const int SW_RESTORE = 9;

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private Window? _window;
        private StartupBannerWindow? _startupBanner;
        private Window? _rulesWindow;
        private Mutex? _instanceMutex;
        public Window? MainWindow => _window;

        /// <summary>
        /// Gets the service provider containing the application services.
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>HWND of the single main window, cached so view-layer helpers (file pickers, dialogs) can attach without an instance reference.</summary>
        public static IntPtr MainWindowHandle;

        /// <summary>The tray-icon / run-in-background service (null until the main window is created).</summary>
        public static TrayIconService? TrayService;

        /// <summary>Just the assembly version, e.g. "1.1.4.0" — shown in the window title bar.</summary>
        public static string AppVersion { get; } =
#if CONSUMER_BUILD
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
#else
            "Edit Toolkit";
#endif

        /// <summary>Display name, version, OS, and architecture of this build — used by startup/crash diagnostics.</summary>
        public static string BuildInfo =>
            $"KaliteKit {AppVersion} | {RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture} | user {Environment.UserName}";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        // Kernel32.dll error-mode constants used by SetErrorMode.
        private const uint SEM_FAILCRITICALERRORS = 0x0001;
        private const uint SEM_NOGPFAULTERRORBOX = 0x0002;   // suppresses the "has stopped working" popup
        private const uint SEM_NOALIGNMENTFAULTEXCEPT = 0x0004;
        private const uint SEM_NOOPENFILEERRORBOX = 0x8000;  // suppresses "Unknown Hard Error" dialogs

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint uMode);

        // ── Native crash dumps ──────────────────────────────────────────────
        // The 0xc0000005 "Exception Processing Message" dialog is a native
        // access violation inside XAML/CoreMessaging teardown that no managed
        // handler can catch. Two dump mechanisms are enabled so a recurring
        // crash always leaves an exact stack: WER LocalDumps (written by
        // Windows itself — zero in-process risk) and a vectored exception
        // handler that writes a minidump on any access violation.

        [DllImport("kernel32.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string fileName);

        // Loaded-module snapshot (name + base + size), captured at startup so
        // the crash handler can map the faulting address to a module with a
        // couple of pointer walks — no API calls on the faulting thread.
        private static (ulong Base, uint Size, string Name)[] _loadedModules = Array.Empty<(ulong, uint, string)>();

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file,
            uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int VectoredExceptionHandler(IntPtr exceptionPointers);

        private static VectoredExceptionHandler? _vectoredHandler;

        // Pre-opened dump file: the handler must make only native calls (the
        // faulting thread may be mid-teardown, so no allocation or CLR work).
        private static System.IO.FileStream? _nativeDumpFile;
        private static string? _nativeDumpPath;

        private static int NativeExceptionDumpHandler(IntPtr exceptionPointers)
        {
            try
            {
                // EXCEPTION_POINTERS → ExceptionRecord (first pointer).
                IntPtr record = Marshal.ReadIntPtr(exceptionPointers);
                if (record == IntPtr.Zero || Marshal.ReadInt32(record) != unchecked((int)0xC0000005))
                {
                    return 0; // EXCEPTION_CONTINUE_SEARCH
                }

                // EXCEPTION_RECORD (x64): Code@0, Flags@4, Record@8, Address@0x10.
                ulong faultAddress = (ulong)Marshal.ReadInt64(record, 0x10);

                // Map the faulting address to a loaded module (snapshot taken
                // at startup) so the culprit is known even if the dump fails.
                string module = "unknown";
                ulong moduleOffset = faultAddress;
                foreach (var m in _loadedModules)
                {
                    if (faultAddress >= m.Base && faultAddress < (ulong)(m.Base + m.Size))
                    {
                        module = m.Name;
                        moduleOffset = faultAddress - m.Base;
                        break;
                    }
                }

                // Walk the stack (heuristic): read the CONTEXT's RSP/RIP from
                // EXCEPTION_POINTERS and scan the stack for return addresses
                // that land inside a loaded module. Crude, but it reveals the
                // call chain (e.g. XAML teardown ← KaliteKit code) without a debugger.
                // AMD64 CONTEXT: ContextRecord@8, Rip@0xF8, Rsp@0x98.
                var chain = new System.Text.StringBuilder();
                try
                {
                    IntPtr context = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);
                    if (context != IntPtr.Zero)
                    {
                        ulong rip = (ulong)Marshal.ReadInt64(context, 0xF8);
                        ulong rsp = (ulong)Marshal.ReadInt64(context, 0x98);
                        chain.Append($"\nrip: 0x{rip:X}\nrsp: 0x{rsp:X}\n");
                        ulong seen = 0;
                        for (ulong addr = rsp; addr < rsp + 0x8000 && seen < 24; addr += 8)
                        {
                            ulong value;
                            try { value = (ulong)Marshal.ReadInt64((IntPtr)addr); }
                            catch { break; }
                            if (value == 0) continue;
                            foreach (var m in _loadedModules)
                            {
                                if (value >= m.Base && value < (ulong)(m.Base + m.Size))
                                {
                                    chain.Append($"  {m.Name} + 0x{value - m.Base:X}\n");
                                    seen++;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                bool dumpOk = false;
                int dumpError = 0;
                if (_nativeDumpFile?.SafeFileHandle is { IsInvalid: false } handle)
                {
                    dumpOk = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(),
                        handle.DangerousGetHandle(),
                        0x2 /* MiniDumpWithDataSegs */, exceptionPointers, IntPtr.Zero, IntPtr.Zero);
                    dumpError = Marshal.GetLastWin32Error();
                }

                // Always leave a text record with the faulting module — a
                // minidump alone is useless without a debugger installed.
                try
                {
                    string txt = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(_nativeDumpPath) ?? string.Empty,
                        System.IO.Path.GetFileNameWithoutExtension(_nativeDumpPath) + ".txt");
                    System.IO.File.WriteAllText(txt,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Access violation 0xC0000005\n" +
                        $"fault address: 0x{faultAddress:X}\n" +
                        $"module: {module} + 0x{moduleOffset:X}\n" +
                        $"minidump: {(dumpOk ? "ok" : $"failed (error {dumpError})")}\n" +
                        chain.ToString());
                }
                catch { }
            }
            catch { }
            return 0; // never swallow the exception — WER / the OS still sees it
        }

        /// <summary>Enables both dump mechanisms. Safe to call once at startup; never throws.</summary>
        private static void EnableNativeCrashDumps()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KaliteKit", "CrashLogs");
                Directory.CreateDirectory(dir);

                // WER LocalDumps — Windows writes a dump on any crash of this exe.
                try
                {
                    using var baseKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\KaliteKit.exe");
                    if (baseKey is not null)
                    {
                        baseKey.SetValue("DumpFolder", dir);
                        baseKey.SetValue("DumpType", 2, Microsoft.Win32.RegistryValueKind.DWord); // full dump
                    }
                }
                catch { }

                // In-process backup for machines where WER is disabled entirely
                // (exactly the machines that show the raw System Error box).
                // Clean stale 0-byte files first (a previous run was hard-killed).
                try
                {
                    foreach (string stale in Directory.GetFiles(dir, "native_*.dmp"))
                    {
                        if (new System.IO.FileInfo(stale).Length == 0) System.IO.File.Delete(stale);
                    }
                }
                catch { }

                _nativeDumpPath = System.IO.Path.Combine(dir, $"native_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");
                _nativeDumpFile = System.IO.File.Create(_nativeDumpPath);

                // Pre-load dbghelp so the handler never triggers a first-use
                // loader lock, and snapshot the module list for address mapping.
                LoadLibraryW("dbghelp.dll");
                try
                {
                    _loadedModules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                        .Select(m => ((ulong)m.BaseAddress.ToInt64(), (uint)m.ModuleMemorySize, m.ModuleName ?? string.Empty))
                        .ToArray();
                }
                catch { }

                _vectoredHandler = NativeExceptionDumpHandler;
                AddVectoredExceptionHandler(1 /* first: observe, never swallow */,
                    Marshal.GetFunctionPointerForDelegate(_vectoredHandler));
            }
            catch { /* dump plumbing must never break startup */ }
        }

        /// <summary>
        /// Other top-level windows the app can create beyond the main window
        /// (e.g. the Settings page's startup-banner preview). Tracked so
        /// shutdown can close them first — the main window's close must be the
        /// last one for the DispatcherQueue event loop to exit on its own.
        /// </summary>
        private static readonly List<Window> AuxiliaryWindows = new();

        /// <summary>
        /// ContentDialogs currently on screen. Tracked so shutdown can dismiss
        /// them BEFORE any window closes: closing a window while a dialog is
        /// open crashes native XAML teardown (the 0xc0000005 "Exception
        /// Processing Message" System Error box seen on machines with WER
        /// disabled). Every ShowAsync registers its dialog here.
        /// </summary>
        private static readonly List<ContentDialog> OpenDialogs = new();

        /// <summary>Registers a dialog so it is dismissed before window teardown.</summary>
        internal static void TrackDialog(ContentDialog dialog)
        {
            lock (OpenDialogs)
            {
                if (OpenDialogs.Contains(dialog)) return;
                OpenDialogs.Add(dialog);
                dialog.Closed += (_, _) =>
                {
                    lock (OpenDialogs) OpenDialogs.Remove(dialog);
                };
            }
        }

        /// <summary>
        /// Dismisses every open dialog (tracked dialogs plus any open popups
        /// still attached to the main window's XamlRoot). Called from window
        /// Closing handlers and from <see cref="ShutdownProcess"/> so native
        /// teardown never races a live popup.
        /// </summary>
        internal static void HideOpenDialogs()
        {
            List<ContentDialog> tracked;
            lock (OpenDialogs) tracked = OpenDialogs.ToList();
            foreach (var dialog in tracked)
            {
                try { dialog.Hide(); } catch { }
            }

            try
            {
                if ((Current as App)?._window?.Content is FrameworkElement root && root.XamlRoot is { } xamlRoot)
                {
                    foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                    {
                        try { popup.IsOpen = false; } catch { }
                    }
                }
            }
            catch { }
        }

        internal static void TrackWindow(Window window)
        {
            lock (AuxiliaryWindows)
            {
                AuxiliaryWindows.Add(window);
                window.Closed += (_, _) =>
                {
                    lock (AuxiliaryWindows) AuxiliaryWindows.Remove(window);
                };
            }
        }

        /// <summary>
        /// Ends the process through the XAML runtime instead of Environment.Exit.
        ///
        /// WinUI Desktop apps start with DispatcherShutdownMode.OnLastWindowClose:
        /// closing the last window already makes the DispatcherQueue event loop
        /// exit and Main return, and the XAML runtime finishes all window/COM
        /// teardown before the process ends. Calling Environment.Exit on top of
        /// that kills threads while teardown is still unwinding — the resulting
        /// native access violation surfaces as the "Exception Processing Message
        /// 0xc0000005 - Unexpected parameters" System Error dialog on machines
        /// with Windows Error Reporting disabled (privacy-tweaked installs).
        /// So the process is never terminated directly while XAML is alive:
        /// windows are closed first, then the event loop is asked to exit via
        /// Application.Exit (PostQuitMessage) — the same clean path the runtime
        /// uses for OnLastWindowClose. Work is deferred to the dispatcher so
        /// the caller's frame unwinds first.
        /// </summary>
        /// <summary>Removes the empty pre-opened dump file after a clean exit.</summary>
        internal static void CleanupEmptyNativeDump()
        {
            try
            {
                _nativeDumpFile?.Dispose();
                _nativeDumpFile = null;
                if (_nativeDumpPath is { } path && System.IO.File.Exists(path)
                    && new System.IO.FileInfo(path).Length == 0)
                {
                    System.IO.File.Delete(path);
                }
            }
            catch { }
        }

        internal static void ExitSoon()
        {
            try
            {
                var app = Current as App;
                var queue = app?._window?.DispatcherQueue ?? app?._startupBanner?.DispatcherQueue;
                if (app is not null && queue is not null && queue.TryEnqueue(app.ShutdownProcess))
                    return;
            }
            catch { /* no dispatcher on this thread — exit directly below */ }
            ExitWithoutUi();
        }

        /// <summary>Runs on the UI thread: closes every app window, then ends the event loop.</summary>
        private void ShutdownProcess()
        {
            // Dismiss dialogs first — closing a window with a ContentDialog up
            // crashes native XAML teardown (0xc0000005 on close).
            HideOpenDialogs();

            // Normal exit: remove the (still empty) pre-opened dump file.
            CleanupEmptyNativeDump();

            lock (AuxiliaryWindows)
            {
                foreach (var window in AuxiliaryWindows.ToArray())
                {
                    try { window.Close(); } catch { }
                }
            }
            try { _startupBanner?.Close(); } catch { }
            try { _window?.Close(); } catch { }

            // PostQuitMessage-based exit: the loop drains the current callback
            // and remaining teardown first, then Application.Start returns and
            // the process ends normally.
            try { Exit(); } catch { }
        }

        /// <summary>
        /// Exit path for when no window's dispatcher is reachable (startup
        /// edge before any window exists, or shutdown already underway). With
        /// no live window there is no XAML teardown to race, so ending the
        /// process directly is safe.
        /// </summary>
        private static void ExitWithoutUi()
        {
            try
            {
                if (Current is { } app) app.Exit();
                else Environment.Exit(0);
            }
            catch
            {
                Environment.Exit(0);
            }
        }

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

            // Don't install the low-level vectored handler when a debugger is
            // attached: the handler walks the raw stack with Marshal.ReadInt64
            // and writes a minidump. Under the VS debugger that races the
            // debugger's own vectored handlers and corrupts the CLR, surfacing
            // as System.ExecutionEngineException 0x80131506 only while
            // debugging (Ctrl+F5 works fine). The WER LocalDumps registry
            // + managed LogCrash() still cover non-debugger crashes.
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                EnableNativeCrashDumps();
            }

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
            services.AddSingleton<RestorePointService>();
            services.AddSingleton<WindowsServiceManager>();
            services.AddSingleton<ElevationService>();
            services.AddSingleton<WindhawkManagerService>();
            services.AddSingleton<HardwareMonitorService>();
            services.AddSingleton<SystemRefreshService>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<StartupTasksService>();
            services.AddSingleton<DiskCleanupService>();
            services.AddSingleton<ProcessControlService>();

            // ── BIOS management ─────────────────────────────────────────────
            services.AddSingleton<KaliteKit.Services.Bios.IWmiClient, KaliteKit.Services.Bios.SystemManagementWmiClient>();
            services.AddSingleton<KaliteKit.Services.Bios.ScewinService>();
            services.AddSingleton<KaliteKit.Services.Bios.BiosProviderFactory>();
            services.AddSingleton<KaliteKit.Services.Bios.BiosUpdateService>();

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
            // The wizard UI is compiled into this app (see KaliteKit.csproj's
            // Installer/** includes); these are the pieces its pipeline needs
            // that the consumer pages don't already register.
            services.AddSingleton<GitHubReleaseClient>();
            services.AddSingleton<HttpFileDownloader>();
            services.AddSingleton<KaliteKit.Setup.InstallerPipeline>();
            services.AddSingleton<KaliteKit.Setup.ViewModels.InstallerViewModel>();


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
            services.AddSingleton<AdditionalTweaksViewModel>();
            services.AddSingleton<SystemOverviewViewModel>();
            services.AddTransient<BiosViewModel>();
            services.AddSingleton<ProcessControlViewModel>();
            services.AddSingleton<DriverStoreViewModel>();

        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // WASDK 2.3.1+ opt-in XAML performance work is now applied in
            // Program.Main — BEFORE Application.Start — which is the only
            // point where XamlOptionalChanges may be modified. Doing it here
            // in OnLaunched is too late (XAML is already initialized) and
            // throws InvalidOperationException 0x8000000D:
            // "XamlOptionalChanges cannot be modified after XAML has been initialized."

            this.UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // Single instance: a second UI launch exits immediately instead of
            // running a second copy that could race on installers/registry and
            // confuse which build is actually on screen.
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);

            var cmdArgs = Environment.GetCommandLineArgs();
            bool isRulesSession = Array.IndexOf(cmdArgs, "--rules") >= 0 || Array.IndexOf(cmdArgs, "-rules") >= 0;

            // A closing UI spawns its --rules replacement BEFORE it exits, so
            // the single-instance mutex is still held for a moment. A rules
            // session therefore waits (bounded) for the UI to release it —
            // surfaced as AbandonedMutexException — instead of silently dying
            // and leaving the engine with no owner.
            bool haveInstance = createdNew;
            if (!haveInstance && isRulesSession)
            {
                var takeoverDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (!haveInstance && DateTime.UtcNow < takeoverDeadline)
                {
                    try
                    {
                        haveInstance = _instanceMutex!.WaitOne(TimeSpan.Zero);
                    }
                    catch (System.Threading.AbandonedMutexException)
                    {
                        haveInstance = true; // previous owner died — the OS granted ownership
                    }
                    catch { break; }
                    if (!haveInstance) System.Threading.Thread.Sleep(250);
                }
            }

            if (!haveInstance)
            {
                // If the running copy is hidden in the tray, wake it instead of
                // exiting silently — the user launched KaliteKit expecting a window.
                try
                {
                    var existing = System.Diagnostics.Process.GetProcessesByName("KaliteKit");
                    foreach (var p in existing)
                    {
                        if (p.Id == Environment.ProcessId) continue;
                        if (p.MainWindowHandle == IntPtr.Zero) continue;

                        // The window exists but may be hidden (tray) — restore it.
                        ShowWindow(p.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(p.MainWindowHandle);
                    }
                }
                catch { }

                this.Exit();
                return;
            }

            // Hidden background mode: enforces sticky process-control rules
            // from login with no UI. Uses a 1×1 off-screen tool window to
            // keep the dispatcher loop alive while the engine runs.
            if (isRulesSession)
            {
                // Rules sessions are background services, not "the app": drop
                // the single-instance claim so a real UI launch always works.
                // Engine ownership is guarded separately by the engine mutex.
                try { _instanceMutex?.ReleaseMutex(); } catch { }
                try { _instanceMutex?.Dispose(); } catch { }
                _instanceMutex = null;

                StartRulesBackgroundMode();
                return;
            }

            // Process Control engine: applies rules to running + newly
            // launched processes while the app is open (any UI mode). Waits
            // briefly for the background session to hand over the engine.
            try { Services.GetRequiredService<ProcessControlService>().Start(backgroundSession: false); } catch { }

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
            // Hidden login session for always-on sticky-rule enforcement.
            ProcessControlService.EnableRulesAutostart();
#endif

            // Launched at Windows login (HKCU Run key writes "KaliteKit.exe --startup").
            // Skip the main window entirely: show only the drop-down banner that
            // runs the user's startup command list and checks for toolkit updates.
            if (Array.IndexOf(cmdArgs, "--startup") >= 0 || Array.IndexOf(cmdArgs, "-startup") >= 0)
            {
                StartStartupBanner();
                return;
            }

#if CONSUMER_BUILD
            // One big app (consumer build only): a fresh install opens straight
            // into the embedded setup wizard (Install KaliteKit → drivers → software
            // → tweaks); the moment the wizard's pipeline completes, the marker
            // flips and the same process swaps into the full consumer app.
            // --setup forces the wizard again on an already-set-up machine.
            bool forceSetup = Array.IndexOf(cmdArgs, "--setup") >= 0 || Array.IndexOf(cmdArgs, "-setup") >= 0;
            if (!forceSetup && KaliteKit.Setup.SetupState.IsSetupComplete)
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
            KaliteKit.Setup.SetupState.Embedded = true;
            KaliteKit.Setup.App.InitializeWizard();

            var wizard = new KaliteKit.Setup.MainWindow();
            KaliteKit.Setup.App.MainWindow = wizard;
            _window = wizard;

            // Shared between both close paths so whichever fires first wins.
            bool swapped = false;

            void SwapToConsumer()
            {
                if (swapped || !KaliteKit.Setup.SetupState.IsSetupComplete) return;
                swapped = true;

                // Open the consumer shell FIRST so the app never drops to zero
                // windows — that would exit the process before the swap.
                _window = new MainWindow();
                _window.Activate();

                StartUpdateCheck();
                ShowUpdateLogIfAny();
                ShowRollbackIfRequired();

                // wizard.Close() bypasses AppWindow.Closing, so dismiss dialogs
                // explicitly before tearing the wizard window down.
                HideOpenDialogs();
                wizard.Close(); // re-enters the Closing hook; 'swapped' lets it through
            }

            // Path 1 — the Finish page's Close/exit (Window.Close() bypasses
            // AppWindow.Closing, so the host must be handed it explicitly).
            KaliteKit.Setup.SetupState.EmbeddedCloseHandler = SwapToConsumer;

            // Path 2 — the title-bar ✕ / Alt+F4 after a completed setup.
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(wizard);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                appWindow.Closing += (s, e) =>
                {
                    // Dismiss dialogs before teardown — a ContentDialog open at
                    // window close crashes native XAML teardown (0xc0000005).
                    App.HideOpenDialogs();
                    if (swapped || !KaliteKit.Setup.SetupState.IsSetupComplete) return;
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
                    // The banner is the last window here, so the runtime would end
                    // the app on its own (DispatcherShutdownMode.OnLastWindowClose);
                    // ExitSoon only makes sure of it — without the Environment.Exit
                    // that used to kill threads mid-teardown and raise the native
                    // 0xc0000005 hard-error dialog on machines with WER disabled.
                    ExitSoon();
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
                ExitSoon();
            }
        }

        /// <summary>
        /// Hidden background mode (--rules): keeps a 1×1 off-screen window
        /// alive so the dispatcher loop never exits while the Process Control
        /// engine enforces sticky rules at login. No UI, no tray icon — the
        /// only trace is the process in Task Manager.
        /// </summary>
        private void StartRulesBackgroundMode()
        {
            try
            {
                var window = new Window { Content = new Grid() };
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

                    // Tool window BEFORE first show: no taskbar button, no
                    // Alt-Tab entry. Without this the hidden engine window
                    // showed as a ghost "WinUI Desktop" taskbar entry after
                    // the user closed the main window.
                    long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW));
                    appWindow.Title = "KaliteKit Rules Engine";

                    appWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 1, 1));
                }
                catch { }
                window.Activate();
                _rulesWindow = window;
            }
            catch
            {
                // Without a window the process exits; rules simply won't run at login.
            }

            try
            {
                Services.GetRequiredService<ProcessControlService>().Start(backgroundSession: true);
                Services.GetRequiredService<LogService>()
                    .WriteAsync("App", "RulesMode", "Background rule enforcement started (" + BuildInfo + ")");
            }
            catch { }

            // When the UI app launches it takes over the engine; this session
            // then exits quietly. When the UI closes, it spawns a replacement
            // --rules process — so enforcement never has a gap.
            ProcessControlService.WaitForEngineStopRequest(() =>
            {
                try
                {
                    Services.GetRequiredService<LogService>()
                        .WriteAsync("App", "RulesMode", "Handing engine over to the UI session.");
                }
                catch { }
                try { Environment.Exit(0); } catch { }
            });
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
                        Text = "This version of KaliteKit has been removed from GitHub because it is unstable. A rollback to the previous stable version is required. The app will close and install it now.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 440
                    },
                    progressBar, progressText
                }
            };

            var dialog = new ContentDialog
            {
                Title = "KaliteKit version removed",
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
                    ExitSoon(); // deferred so the dialog deferral's finally block still runs
                }
                catch { }
                finally { deferral.Complete(); }
            };

            TrackDialog(dialog);
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
                Text = $"KaliteKit was updated to {rec.Version} on {rec.AppliedAt:g}.",
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
                Title = $"Update log — KaliteKit {rec.Version}",
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
                    TrackDialog(resultDialog);
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
                TrackDialog(dialog);
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
                TrackDialog(logDialog);
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
                            ? $"KaliteKit {AppVersion} is newer than the published version {version}. This build is unstable and must be rolled back. The app will restart automatically after the rollback."
                            : $"KaliteKit {version} is ready to download and install.\n\nYour app will restart automatically after the update finishes.",
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
                TrackDialog(dialog);
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
                    TrackDialog(dialog);
                    _ = dialog.ShowAsync();
                    await settingsVm.DownloadAndInstallAsync();
                }
                finally
                {
                    settingsVm.PropertyChanged -= OnVmChanged;
                    // On success the app has already requested a graceful exit
                    // (see App.ExitSoon).
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
                    ExitSoon();
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
                    Title = "KaliteKit ran into a problem",
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

                TrackDialog(dialog);
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
            ExitSoon();
        }

        private void LogCrash(string source, Exception ex)
        {
            try
            {
                var crashDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaliteKit", "CrashLogs");
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
