using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using KalOS.Services;
using KalOS.Setup.ViewModels;

namespace KalOS.Setup
{
    /// <summary>
    /// Composition root for the KalOS Setup wizard. The installer is an
    /// unpackaged, self-contained, <c>requireAdministrator</c> WinUI 3 app
    /// that source-shares the WinUI-free backend (driver stack + package
    /// managers + the new install services) and walks the user through:
    /// KalOS consumer deploy → GPU driver update → software → done.
    /// </summary>
    public partial class App : Application
    {
        private static MainWindow? _window;

        /// <summary>The shared service provider — pages resolve services from here.</summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>The wizard shell window — pages reach it through here for nav refreshes.</summary>
        public static MainWindow? MainWindow => _window;

        /// <summary>The single wizard state object every page binds to.</summary>
        public static InstallerViewModel Wizard { get; private set; } = null!;

        /// <summary>Just the assembly version, e.g. "1.0.0.0" — shown in the title bar.</summary>
        public static string AppVersion { get; } =
            typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

        public App()
        {
            InitializeComponent();

            // Never die silently: log UI-thread exceptions to
            // %LOCALAPPDATA%\KalOS\SetupCrash.log and keep the wizard alive so
            // the user can retry instead of the window closing by itself.
            UnhandledException += (_, e) =>
            {
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KalOS");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(
                        Path.Combine(dir, "SetupCrash.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
                }
                catch
                {
                }
                e.Handled = true;
            };

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            Wizard = new InstallerViewModel(Services);
            // Build the software catalog up front (pure in-memory data) so the
            // Software page's x:Bind lists are already populated the moment the
            // page binds — a lazy build in the page's Loaded handler is too late
            // and leaves the first visit empty.
            Wizard.BuildSoftwarePicks();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // ── Shared backend (source-shared from the main project) ───────
            services.AddSingleton<LogService>();
            services.AddSingleton<LoggingService>();
            services.AddSingleton<ProcessManager>();
            services.AddSingleton<ElevationService>();
            services.AddSingleton<PackageManagerService>();

            // GPU driver stack
            services.AddSingleton<GpuDetectionService>();
            services.AddSingleton<DriverDownloadService>();
            services.AddSingleton<DriverInstallService>();
            services.AddSingleton<DriverCleanupService>();
            services.AddSingleton<RadeonSlimmerService>();
            services.AddSingleton<RadeonPackageSlimmer>();
            services.AddSingleton<IDriverProvider, NvidiaDriverProvider>();
            services.AddSingleton<IDriverProvider, AmdDriverProvider>();
            services.AddSingleton<IDriverProvider, IntelDriverProvider>();
            services.AddSingleton<DriverService>();
            services.AddSingleton<CoreSpreadingService>();

            // New Phase 1 install services — the native KalOS deploy path.
            services.AddSingleton<GitHubReleaseClient>();
            services.AddSingleton<HttpFileDownloader>();

            // Windhawk install + curated mod deploy — powers the Customize
            // page's "Windows look" step (dark translucent dock), the same
            // service the main app's Personalization → Windhawk page uses.
            services.AddSingleton<WindhawkManagerService>();

            // Native tweaks engine (privacy/cleanup catalog generated from the
            // privacy.sexy scripts — no batch files at runtime).
            services.AddSingleton<TweaksService>();

            // The pipeline orchestrator that ties it all together.
            services.AddSingleton<InstallerPipeline>();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
