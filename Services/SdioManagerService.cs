using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services
{
    public class SdioManagerService
    {
        private readonly LoggingService _log;
        private readonly ProcessManager _processManager;
        private readonly DriverDownloadService _downloadService;

        private static readonly string ToolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KalOS", "tools", "SDIO");

        private string? _sdioExePath;

        public SdioManagerService(LoggingService log, ProcessManager processManager, DriverDownloadService downloadService)
        {
            _log = log;
            _processManager = processManager;
            _downloadService = downloadService;
            Directory.CreateDirectory(ToolsDir);
        }

        public bool IsSdioInstalled => LocateSdioExe(out _);

        private bool LocateSdioExe(out string path)
        {
            if (_sdioExePath != null && File.Exists(_sdioExePath))
            {
                path = _sdioExePath;
                return true;
            }
            
            // 1. Search Winget Portable Packages for the SDIO_x64 execution footprint
            string wingetPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPackages))
            {
                var candidates = Directory.GetFiles(wingetPackages, "SDIO_x64*.exe", SearchOption.AllDirectories);
                if (candidates.Length > 0)
                {
                    _sdioExePath = candidates[0];
                    path = _sdioExePath;
                    return true;
                }
            }

            // 2. Check traditional Machine-Wide Installation Vectors
            string[] commonPaths = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Snappy Driver Installer Origin", "SDIO_x64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Snappy Driver Installer Origin", "SDIO_x64.exe"),
                Path.Combine(ToolsDir, "SDIO_x64.exe") // Legacy KalOS extraction fallback
            };

            foreach (var cp in commonPaths)
            {
                if (File.Exists(cp))
                {
                    _sdioExePath = cp;
                    path = _sdioExePath;
                    return true;
                }
            }

            path = string.Empty;
            return false;
        }

        /// <summary>
        /// Installs Snappy Driver Installer Origin directly from the Microsoft WinGet manifest repositories.
        /// </summary>
        public async Task DownloadSdioAsync(CancellationToken cancellationToken)
        {
            _log.Info("Downloading SDIO via WinGet...");
            // Run winget from the system root so its console/COM subsystems initialize
            // in a stable, always-existing directory (avoids native hard errors when the
            // default CWD or the SDIO tools folder is not yet present).
            string workingDir = Path.GetPathRoot(Environment.SystemDirectory) ?? AppContext.BaseDirectory;
            // Constrain to the winget source. Searching the msstore source is what trips
            // a certificate error (0x8a15005e) on many machines and that path can surface
            // a native "Unknown Hard Error"; forcing --source winget avoids it entirely.
            var (output, error, exit) = await _processManager.RunWithOutputAndErrorAsync(
                "winget",
                "install --id GlennDelahoy.SnappyDriverInstallerOrigin -e --source winget --accept-package-agreements --accept-source-agreements --silent",
                TimeSpan.FromMinutes(15),
                cancellationToken,
                workingDir);
            
            if (exit != 0)
            {
                _log.Error($"WinGet failed to install SDIO. Error block: {error}");
                throw new Exception("WinGet auto-download failed.");
            }
            
            if (!LocateSdioExe(out _))
            {
                throw new Exception("SDIO successfully installed via WinGet but the executable was not found locally.");
            }
            _log.Success("Successfully provisioned SDIO backend.");
        }

        /// <summary>
        /// Generates sdio.cfg from user preferences and silently executes SDIO to auto-install missing drivers.
        /// </summary>
        public async Task<bool> RunSdioAutoInstallAsync(
            bool showNotInstalled,
            bool showNewer,
            bool showCurrent,
            bool showOlder,
            bool showBetterMatch,
            bool showWorseMatch,
            bool createRestorePoint,
            bool autoReboot,
            IProgress<string> outputProgress,
            CancellationToken ct)
        {
            if (!LocateSdioExe(out string exePath))
            {
                _log.Warn("SDIO binary is missing!");
                return false;
            }

            // We generate the sdio.cfg inline in the execution folder
            var cfgPath = Path.Combine(ToolsDir, "sdio.cfg");
            var cfgContent = $@"
-license
{(createRestorePoint ? "-restorepnt" : "-norestorepnt")}
-autoclose
-nogui
-ShowNotInstalled: {(showNotInstalled ? "1" : "0")}
-ShowNewer: {(showNewer ? "1" : "0")}
-ShowCurrent: {(showCurrent ? "1" : "0")}
-ShowOlder: {(showOlder ? "1" : "0")}
-ShowBetterMatch: {(showBetterMatch ? "1" : "0")}
-ShowWorseMatch: {(showWorseMatch ? "1" : "0")}
";
            
            // Note: In real SDIO, UI elements are configured with 'Show*' flags. 
            // It natively respects '-autoinstall' passed on the CLI.
            
            try
            {
                File.WriteAllText(cfgPath, cfgContent.Trim());
                _log.Info($"Generated sdio.cfg at {cfgPath}");

                outputProgress.Report("Launching Snappy Driver Installer Origin...");
                
                string args = $"-autoinstall -autoclose -license -nogui {(autoReboot ? "-reboot" : "")}";
                
                outputProgress.Report($"Executing: SDIO_x64.exe {args}");

                var (output, error, exitCode) = await _processManager.RunWithOutputAndErrorAsync(
                    exePath, args, TimeSpan.FromMinutes(60), ct);

                outputProgress.Report($"SDIO Execution Finished (Exit Code: {exitCode})");

                if (!string.IsNullOrWhiteSpace(output))
                {
                    _log.Info("SDIO Output: " + output);
                }

                return exitCode == 0;
            }
            catch (Exception ex)
            {
                _log.Error($"SDIO execution failed: {ex.Message}");
                outputProgress.Report($"ERROR: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Launches the SDIO application normally with its full legacy GUI for manual interaction.
        /// </summary>
        public async Task OpenSdioGuiAsync(CancellationToken ct = default)
        {
            if (!LocateSdioExe(out string exePath))
            {
                _log.Warn("SDIO binary is missing!");
                return;
            }

            try
            {                    await Task.Run(() =>
                    {
                        // SDIO_x64.exe is a console-subsystem executable; with UseShellExecute
                        // Windows allocates a visible terminal window next to its GUI. Launching
                        // with CreateNoWindow gives it a hidden console while its own GUI window
                        // still shows normally.
                        var psi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(exePath)
                        };
                        using var process = Process.Start(psi);
                    process?.WaitForExit();
                }, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to open SDIO GUI: {ex.Message}");
            }
        }
    }
}
