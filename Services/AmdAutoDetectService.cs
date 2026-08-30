using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Helpers;

namespace KalOS.Services
{
    /// <summary>
    /// Downloads and executes the official AMD Auto-Detect and Install tool directly from AMD servers.
    /// Strictly verifies Authenticode digital signatures before execution and destroys untrusted files.
    /// </summary>
    public class AmdAutoDetectService
    {
        private static readonly HttpClient _httpClient = CreateClient();
        private readonly DriverDownloadService _downloadService;
        private readonly LoggingService _log;

        private const string CuratedAutoDetectUrl =
            "https://drivers.amd.com/drivers/installer/26.10/whql/amd-software-adrenalin-edition-26.8.1-minimalsetup-260818_web.exe";

        private static readonly string TargetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KalOS", "drivers");

        private static readonly string TargetExePath = Path.Combine(TargetDir, "AMD-AutoDetect-Installer.exe");

        public AmdAutoDetectService(DriverDownloadService downloadService, LoggingService log)
        {
            _downloadService = downloadService;
            _log = log;
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Referer", "https://www.amd.com/");
            return client;
        }

        /// <summary>
        /// Scrapes official AMD support download page to resolve the newest live Auto-Detect installer URL.
        /// </summary>
        public async Task<string> ResolveAutoDetectUrlAsync(CancellationToken cancellationToken = default)
        {
            const string supportPageUrl = "https://www.amd.com/en/support/download/drivers.html";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, supportPageUrl);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        var match = Regex.Match(html, @"href=""(https://drivers\.amd\.com/drivers/installer/[^""]*minimalsetup[^""]*\.exe)""", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            return match.Groups[1].Value;
                        }

                        var genericMatch = Regex.Match(html, @"href=""(https://drivers\.amd\.com/drivers/installer/[^""]*\.exe)""", RegexOptions.IgnoreCase);
                        if (genericMatch.Success)
                        {
                            return genericMatch.Groups[1].Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not query AMD support page for Auto-Detect link: {ex.Message}");
            }

            return CuratedAutoDetectUrl;
        }

        /// <summary>
        /// Downloads the official AMD Auto-Detect tool, verifies its Authenticode digital signature,
        /// and launches it as administrator. If signature verification fails, the file is destroyed immediately.
        /// </summary>
        public async Task<(bool Success, string Message)> DownloadAndLaunchAutoDetectAsync(
            IProgress<double>? downloadProgress = null,
            IProgress<string>? statusProgress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                statusProgress?.Report("Resolving official AMD Auto-Detect installer…");
                _log.Info("Resolving official AMD Auto-Detect installer URL...");
                string installerUrl = await ResolveAutoDetectUrlAsync(cancellationToken).ConfigureAwait(false);

                Directory.CreateDirectory(TargetDir);
                if (File.Exists(TargetExePath))
                {
                    try { File.Delete(TargetExePath); } catch { }
                }

                statusProgress?.Report("Downloading official AMD Auto-Detect installer…");
                _log.Info($"Downloading official AMD Auto-Detect tool from: {installerUrl}");

                await _downloadService.DownloadAsync(installerUrl, TargetExePath, downloadProgress, cancellationToken)
                    .ConfigureAwait(false);

                if (!File.Exists(TargetExePath))
                {
                    return (false, "Downloaded file not found on disk.");
                }

                statusProgress?.Report("Verifying AMD Authenticode digital signature…");
                _log.Info("Verifying Authenticode digital signature for downloaded installer...");

                bool isSignatureValid = AuthenticodeHelper.VerifyAmdSignature(TargetExePath, out string signer, out string? error);

                if (!isSignatureValid)
                {
                    _log.Error($"Authenticode signature verification FAILED for AMD installer: {error}. Destroying file.");
                    try
                    {
                        File.Delete(TargetExePath);
                    }
                    catch (Exception delEx)
                    {
                        _log.Warn($"Could not immediately delete untrusted file: {delEx.Message}");
                    }

                    return (false, $"Security verification failed: {error}. The installer was blocked and removed.");
                }

                _log.Success($"AMD Authenticode signature verified successfully. Signer: {signer}");
                statusProgress?.Report("Signature verified. Launching AMD Auto-Detect…");

                var psi = new ProcessStartInfo(TargetExePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = TargetDir
                };

                Process.Start(psi);
                _log.Success("Official AMD Auto-Detect tool launched successfully.");
                return (true, "Official AMD Auto-Detect tool launched.");
            }
            catch (OperationCanceledException)
            {
                _log.Info("AMD Auto-Detect download cancelled.");
                if (File.Exists(TargetExePath))
                {
                    try { File.Delete(TargetExePath); } catch { }
                }
                return (false, "Download cancelled.");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to download or run AMD Auto-Detect: {ex.Message}");
                if (File.Exists(TargetExePath))
                {
                    try { File.Delete(TargetExePath); } catch { }
                }
                return (false, $"Error: {ex.Message}");
            }
        }
    }
}