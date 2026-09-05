using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using KaliteKit.Services;
using KaliteKit.Setup.ViewModels;

namespace KaliteKit.Setup
{
    /// <summary>
    /// Composition root for the KaliteKit Setup wizard. The installer is an
    /// unpackaged, self-contained, <c>requireAdministrator</c> WinUI 3 app
    /// that source-shares the WinUI-free backend (driver stack + package
    /// managers + the new install services) and walks the user through:
    /// KaliteKit consumer deploy → GPU driver update → software → done.
    ///
    /// The standalone release embeds the KaliteKit consumer payload (see
    /// <see cref="BundledPayload"/>), so the single exe installs KaliteKit
    /// fully offline; the GPU-driver and software steps stay optional and
    /// only run for what the user explicitly selects.
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
            // %LOCALAPPDATA%\KaliteKit\SetupCrash.log and keep the wizard alive so
            // the user can retry instead of the window closing by itself.
            UnhandledException += (_, e) =>
            {
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaliteKit");
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
            // CoreSpreadingService (NIC interrupt pinning / RSS tuning — a
            // network-behavior modifier) is deliberately NOT registered: the
            // installer never tunes the network stack.

            // New Phase 1 install services — the native KaliteKit deploy path.
            services.AddSingleton<GitHubReleaseClient>();
            services.AddSingleton<HttpFileDownloader>();

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
