using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
            var versions = await GetDriverVersionsAsync(gpu, cancellationToken).ConfigureAwait(false);
            return versions.Count > 0 ? versions[0] : GetCuratedLatest();
        }

        /// <summary>
        /// Version history for the GPU's series — the newest WHQL DCH Game Ready
        /// releases, newest first. Powers the "Manually select a driver version"
        /// option in the NVIDIA install dialog (NVCleanstall-style version list).
        /// </summary>
        public async Task<IReadOnlyList<DriverInfo>> GetDriverVersionsAsync(
            GpuInfo gpu, CancellationToken cancellationToken, int maxResults = 30)
        {
            // Laptop detection: the model name's vendor marker OR the machine's
            // chassis type (GpuInfo.IsLaptop, stamped by the detection service).
            // NVIDIA notebook GPUs need the notebook psid/pfid series for the
            // DriverManualLookup API even though the package is the same DCH build.
            var isNotebook = gpu.IsMobileGpu;

            var queries = isNotebook ? NotebookSeriesQueries : DesktopSeriesQueries;

            foreach (var (psid, pfid, label) in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var url = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                              $"?func=DriverManualLookup&psid={psid}&pfid={pfid}" +
                              $"&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults={maxResults}";

                    using var resp = await DriverHttp.Client.GetAsync(url, cancellationToken)
                        .ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode) continue;

                    var json = await DriverHttp.TryReadStringAsync(resp).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    var versions = ParseLookupVersions(json);
                    if (versions.Count > 0) return versions;
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

            return Array.Empty<DriverInfo>();
        }

        /// <summary>
        /// Parses the NVIDIA <c>DriverManualLookup</c> JSON response into the
        /// newest <see cref="DriverInfo"/>. Extracted for unit testing.
        /// </summary>
        internal static DriverInfo? ParseLookupResponse(string json) =>
            ParseLookupVersions(json).FirstOrDefault();

        /// <summary>
        /// Parses every <c>IDS</c> entry of a <c>DriverManualLookup</c> response
        /// into a newest-first version list. Tolerant of the API's real output
        /// shape — the service emits spaced JSON (<c>"Version" : "616.56"</c>),
        /// so the old <c>"Version":"</c> marker match never hit and the lookup
        /// silently always fell back to the curated entry.
        /// </summary>
        internal static List<DriverInfo> ParseLookupVersions(string json)
        {
            var results = new List<DriverInfo>();
            if (string.IsNullOrWhiteSpace(json)) return results;

            var versionMatches = VersionRegex.Matches(json);
            for (int i = 0; i < versionMatches.Count; i++)
            {
                string version = versionMatches[i].Groups["v"].Value;
                if (string.IsNullOrWhiteSpace(version)) continue;

                // Segment holding this entry's remaining fields: from the end of
                // this Version match to the start of the next one (or the end).
                int segStart = versionMatches[i].Index + versionMatches[i].Length;
                int segEnd = i + 1 < versionMatches.Count ? versionMatches[i + 1].Index : json.Length;
                string segment = json.Substring(segStart, segEnd - segStart);

                var urlMatch = UrlRegex.Match(segment);
                if (!urlMatch.Success) continue;
                string url = urlMatch.Groups["u"].Value;
                if (string.IsNullOrWhiteSpace(url)) continue;

                DateTime? releaseDate = null;
                var dateMatch = DateRegex.Match(segment);
                if (dateMatch.Success && DateTime.TryParse(dateMatch.Groups["d"].Value, out var parsed))
                    releaseDate = parsed;

                results.Add(new DriverInfo
                {
                    Version = version,
                    DownloadUrl = url,
                    ReleaseDate = releaseDate,
                    DisplayString = $"NVIDIA Game Ready {version}"
                });
            }

            // The API's sort isn't guaranteed — order newest first by release date.
            return results
                .OrderByDescending(d => d.ReleaseDate ?? DateTime.MinValue)
                .ToList();
        }

        // Spacing-tolerant: the API emits "Version" : "616.56". "DisplayVersion"
        // / "GFE_DisplayVersion" can't match because the key needs a leading quote.
        private static readonly Regex VersionRegex =
            new("\"Version\"\\s*:\\s*\"(?<v>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex UrlRegex =
            new("\"DownloadURL\"\\s*:\\s*\"(?<u>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DateRegex =
            new("\"ReleaseDateTime\"\\s*:\\s*\"(?<d>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Offline/unreachable fallback — a curated recent Game Ready version with
        /// a direct download URL. Keep this in sync with stable releases so the
        /// version comparison works and the silent pipeline can download.
        /// </summary>
        public static DriverInfo GetCuratedLatest() => GetCuratedLatest(isNotebook: false);

        /// <summary>
        /// Notebook-aware variant: laptops need the notebook (mobile) package —
        /// NVIDIA's desktop installer refuses to run on notebook hardware
        /// ("This graphics driver could not find compatible graphics hardware").
        /// </summary>
        public static DriverInfo GetCuratedLatest(bool isNotebook)
        {
            const string version = "616.56";
            string package = isNotebook ? "notebook" : "desktop";
            return new DriverInfo
            {
                Version = version,
                DownloadUrl = $"https://us.download.nvidia.com/Windows/{version}/{version}-{package}-win10-win11-64bit-international-dch-whql.exe",
                ReleaseDate = new DateTime(2026, 8, 26),
                DisplayString = $"NVIDIA Game Ready {version} ({(isNotebook ? "Notebook" : "Desktop")})"
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
    /// Serves AMD/Adrenalin drivers via dynamic lookup API.
    /// Obtains the newest WHQL release with direct AMD CDN download links.
    /// </summary>
    public sealed class AmdDriverProvider : IDriverProvider
    {
        private readonly AmdDriverApiService _apiService = new();

        public string Vendor => "AMD";

        public bool CanHandle(GpuInfo gpu) => gpu.IsAmd;

        public async Task<DriverInfo?> GetLatestDriverAsync(GpuInfo gpu, CancellationToken cancellationToken)
        {
            return await GetLatestDriverAsync(gpu, AmdDriverApiService.AmdPackageVariant.Desktop, cancellationToken);
        }

        /// <summary>Variant-aware lookup: Notebook resolves the desktop+notebook "combined" INF package.</summary>
        public async Task<DriverInfo?> GetLatestDriverAsync(
            GpuInfo gpu,
            AmdDriverApiService.AmdPackageVariant variant,
            CancellationToken cancellationToken)
        {
            return await _apiService.GetLatestDriverAsync(variant, cancellationToken);
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