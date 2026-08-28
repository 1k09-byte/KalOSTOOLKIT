using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Models;

namespace KalOS.Services
{
    /// <summary>
    /// Shared <see cref="HttpClient"/> and small response helpers for the
    /// vendor providers. One client avoids socket exhaustion and honors the
    /// handler lifetime rule.
    /// </summary>
    internal static class DriverHttp
    {
        internal static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = System.Net.DecompressionMethods.All });
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS/1.1 (gpu-driver-check)");
            return client;
        }

        /// <summary>A safe short read of a response body; null on any failure.</summary>
        internal static async Task<string?> TryReadStringAsync(HttpResponseMessage response)
        {
            try
            {
                using var content = response.Content;
                return await content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Serves NVIDIA GeForce drivers from NVIDIA's lookup API. Tries multiple
    /// product-series queries (RTX 50/40/30/20, GTX 16/10) since the API requires
    /// a series ID and the latest Game Ready driver is the same across all of them.
    /// When the live lookup is unreachable, falls back to a curated latest with a
    /// direct download URL — never returns an empty version, because that blocks
    /// version comparison and disables the auto-install button.
    /// </summary>
    public sealed class NvidiaDriverProvider : IDriverProvider
    {
        public string Vendor => "NVIDIA";

        public bool CanHandle(GpuInfo gpu) => gpu.IsNvidia;

        /// <summary>
        /// NVIDIA product-series IDs for the DriverManualLookup API.
        /// The latest Game Ready driver is the same version across all desktop
        /// GeForce series — we just need one query to succeed.
        /// Ordered newest-first so the most-likely match hits first.
        /// </summary>
        private static readonly (int psid, int pfid, string label)[] DesktopSeriesQueries =
        {
            (131, 1066, "RTX 50 Desktop"),
            (127, 1039, "RTX 40 Desktop"),
            (120,  929, "RTX 30 Desktop"),
            (107,  879, "RTX 20 Desktop"),
            (112,  895, "GTX 16 Desktop"),
            (101,  815, "GTX 10 Desktop"),
        };

        private static readonly (int psid, int pfid, string label)[] NotebookSeriesQueries =
        {
            (133, 1070, "RTX 50 Notebook"),
            (129, 1028, "RTX 40 Notebook"),
            (123,  933, "RTX 30 Notebook"),
            (111,  880, "RTX 20 Notebook"),
            (115,  892, "GTX 16 Notebook"),
            (102,  816, "GTX 10 Notebook"),
        };

        public async Task<DriverInfo?> GetLatestDriverAsync(GpuInfo gpu, CancellationToken cancellationToken)
        {
            var isNotebook = gpu.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
                || gpu.Name.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
                || gpu.Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase);

            var queries = isNotebook ? NotebookSeriesQueries : DesktopSeriesQueries;

            foreach (var (psid, pfid, label) in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var url = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                              $"?func=DriverManualLookup&psid={psid}&pfid={pfid}" +
                              $"&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";

                    using var resp = await DriverHttp.Client.GetAsync(url, cancellationToken)
                        .ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode) continue;

                    var json = await DriverHttp.TryReadStringAsync(resp).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    var latest = ParseLookupResponse(json);
                    if (latest != null) return latest;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // HttpClient timeout (20s) — NVIDIA unreachable, try next series.
                    break; // All series hit the same server; if one times out, they all will.
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Offline / blocked / API shape changed — try next series.
                }
            }

            // Every API query failed — return curated latest so version comparison
            // still works and the auto-install button is available.
            return GetCuratedLatest();
        }

        /// <summary>
        /// Parses the NVIDIA <c>DriverManualLookup</c> JSON response into the
        /// newest <see cref="DriverInfo"/>. Extracted for unit testing.
        /// </summary>
        internal static DriverInfo? ParseLookupResponse(string json)
        {
            // Structure: { "IDS": [ { "downloadInfo": { "Version": "...", 
            // "DownloadURL": "...", "ReleaseDateTime": "..." } } ], ... }
            const string versionMarker = "\"Version\":\"";
            const string urlMarker = "\"DownloadURL\":\"";
            const string dateMarker = "\"ReleaseDateTime\":\"";

            int vIdx = json.IndexOf(versionMarker, StringComparison.OrdinalIgnoreCase);
            int uIdx = json.IndexOf(urlMarker, StringComparison.OrdinalIgnoreCase);
            if (vIdx < 0 || uIdx < 0) return null;

            string? version = Capture(json, vIdx + versionMarker.Length);
            string? url = Capture(json, uIdx + urlMarker.Length);
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) return null;

            DateTime? releaseDate = null;
            int dIdx = json.IndexOf(dateMarker, StringComparison.OrdinalIgnoreCase);
            if (dIdx >= 0)
            {
                string? dateStr = Capture(json, dIdx + dateMarker.Length);
                if (DateTime.TryParse(dateStr, out var parsed)) releaseDate = parsed;
            }

            return new DriverInfo
            {
                Version = version,
                DownloadUrl = url,
                ReleaseDate = releaseDate,
                DisplayString = $"NVIDIA Game Ready {version}"
            };
        }

        /// <summary>
        /// Offline/unreachable fallback — a curated recent Game Ready version with
        /// a direct download URL. Keep this in sync with stable releases so the
        /// version comparison works and the silent pipeline can download.
        /// </summary>
        internal static DriverInfo GetCuratedLatest()
        {
            const string version = "616.56";
            return new DriverInfo
            {
                Version = version,
                DownloadUrl = $"https://us.download.nvidia.com/Windows/{version}/{version}-desktop-win10-win11-64bit-international-dch-whql.exe",
                ReleaseDate = new DateTime(2026, 8, 26),
                DisplayString = $"NVIDIA Game Ready {version}"
            };
        }

        private static string? Capture(string json, int start)
        {
            int end = json.IndexOf('"', start);
            if (end < 0 || end - start > 200) return null;
            return json.Substring(start, end - start);
        }
    }

    /// <summary>
    /// Serves AMD/Adrenalin drivers. AMD's live host is gated; the curated
    /// latest includes a direct download URL so the silent pipeline can run.
    /// </summary>
    public sealed class AmdDriverProvider : IDriverProvider
    {
        public string Vendor => "AMD";

        public bool CanHandle(GpuInfo gpu) => gpu.IsAmd;

        public async Task<DriverInfo?> GetLatestDriverAsync(GpuInfo gpu, CancellationToken cancellationToken)
        {
            // AMD's download host requires a referer and driver-match round-trips;
            // a fair live scrape is unreliable across regions. Keep a curated
            // latest with the direct download URL for silent extraction.
            await Task.CompletedTask;
            return new DriverInfo
            {
                Version = "25.10.1",
                DownloadUrl = "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-25.10.1-win10-win11-may-rdna.exe",
                ReleaseDate = new DateTime(2026, 8, 5),
                DisplayString = "AMD Adrenalin 25.10.1"
            };
        }
    }

    /// <summary>Serves Intel Graphics drivers. Intel's download center blocks automation; open it in the browser.</summary>
    public sealed class IntelDriverProvider : IDriverProvider
    {
        public string Vendor => "Intel";

        public bool CanHandle(GpuInfo gpu) => gpu.IsIntel;

        public async Task<DriverInfo?> GetLatestDriverAsync(GpuInfo gpu, CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            return new DriverInfo
            {
                Version = "31.0.101.5768",
                DownloadUrl = "https://www.intel.com/content/www/us/en/download-center/home.html",
                ReleaseDate = null,
                DisplayString = "Intel Graphics 31.0.101.5768"
            };
        }
    }
}