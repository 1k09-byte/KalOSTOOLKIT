using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KaliteKit.Services
{
    /// <summary>
    /// Downloads and launches GSDragoon's Radeon Software Slimmer (GPL-3.0,
    /// third-party) at runtime — the binary is never bundled into KaliteKit.
    /// Slimming is driven in the tool's own GUI: "Pre Install" to slim the
    /// Adrenalin installer before installing, "Post Install" to trim the
    /// installed Radeon Software. Exposure is interactive by design.
    /// </summary>
    public class RadeonSlimmerService
    {
        private static readonly HttpClient _http = CreateClient();

        static RadeonSlimmerService() { }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KaliteKit/1.1 (radeon-slimmer)");
            return client;
        }

        // .NET Framework 4.8 build: present on Windows 10 1903+ / 11 with no
        // extra runtime, unlike the net8/net9 builds which need a Desktop runtime.
        private const string SlimmerReleaseUrl =
            "https://github.com/GSDragoon/RadeonSoftwareSlimmer/releases/download/1.12.0/" +
            "RadeonSoftwareSlimmer_1.12.0_net48.zip";

        private static readonly string SlimmerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KaliteKit", "tools", "radeon-slimmer");

        private const string SlimmerExeName = "RadeonSoftwareSlimmer.exe";

        /// <summary>True when a previously-downloaded copy is already extracted and runnable.</summary>
        public bool IsAvailable() => LocateExe() != null;

        private static string? LocateExe()
            => Directory.Exists(SlimmerDir)
                ? Directory.GetFiles(SlimmerDir, SlimmerExeName, SearchOption.AllDirectories)
                    .OrderBy(p => p.Count(c => c == System.IO.Path.DirectorySeparatorChar))
                    .FirstOrDefault()
                : null;

        /// <summary>
        /// Ensures Radeon Software Slimmer is downloaded and extracted, returning
        /// the path to its main exe (or null if it could not be obtained).
        /// </summary>
        public async Task<string?> EnsureAsync(
            LoggingService log,
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            var existing = LocateExe();
            if (existing != null) return existing;

            status?.Report("Downloading Radeon Software Slimmer…");
            const string zipName = "RadeonSoftwareSlimmer.zip";
            string zipPath = Path.Combine(SlimmerDir, zipName);
            Directory.CreateDirectory(SlimmerDir);

            try
            {
                using var response = await _http.GetAsync(SlimmerReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);

                ZipFile.ExtractToDirectory(zipPath, SlimmerDir, overwriteFiles: true);
                log.Success($"Radeon Software Slimmer ready at {SlimmerDir}");
                return LocateExe();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Warn($"Could not obtain Radeon Software Slimmer: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Launches the extracted Slimmer so the user can slim interactively.
        /// KaliteKit runs elevated, so the child inherits the admin token.
        /// </summary>
        public bool Launch(string? exePath, LoggingService log, string? installerPath = null)
        {
            if (exePath == null || !File.Exists(exePath)) return false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exePath)!,
                };
                if (!string.IsNullOrEmpty(installerPath) && File.Exists(installerPath))
                {
                    psi.Arguments = $"\"{installerPath}\"";
                }
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"Could not launch Radeon Software Slimmer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Ensures Radeon Software Slimmer is downloaded/extracted and launches it.
        /// </summary>
        public async Task<bool> LaunchOrDownloadAsync(LoggingService log, IProgress<string>? status = null, string? installerPath = null, CancellationToken cancellationToken = default)
        {
            var exe = await EnsureAsync(log, status, cancellationToken).ConfigureAwait(false);
            if (exe != null)
            {
                return Launch(exe, log, installerPath);
            }
            return false;
        }
    }
}

