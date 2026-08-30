using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Models;

namespace KalOS.Services
{
    /// <summary>
    /// Dynamic driver lookup API for AMD Radeon Adrenalin WHQL packages.
    /// Queries public release indexes (TechPowerUp & AMD release metadata) to dynamically
    /// obtain the newest WHQL version, release date, and direct download links from AMD CDN.
    /// </summary>
    public class AmdDriverApiService
    {
        private static readonly HttpClient _httpClient = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Referer", "https://www.amd.com/");
            return client;
        }

        public async Task<DriverInfo?> GetLatestDriverAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var liveInfo = await QueryTechPowerUpFeedAsync(cancellationToken);
                if (liveInfo != null)
                {
                    return liveInfo;
                }
            }
            catch (Exception)
            {
                // Fallback to curated release below
            }

            return GetCuratedLatest();
        }

        private async Task<DriverInfo?> QueryTechPowerUpFeedAsync(CancellationToken cancellationToken)
        {
            const string url = "https://www.techpowerup.com/download/amd-radeon-graphics-drivers/";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html)) return null;

            // Pattern for Version title: <h3 class="title">\s*AMD Radeon Graphics Drivers ([0-9]+\.[0-9]+(?:\.[0-9]+)?)\s*(?:WHQL)?\s*</h3>
            var titleMatch = Regex.Match(html, @"<h3[^>]*class=""title""[^>]*>\s*AMD\s+Radeon\s+Graphics\s+Drivers\s+([0-9]+\.[0-9]+(?:\.[0-9]+)?)\s*(WHQL)?", RegexOptions.IgnoreCase);
            if (!titleMatch.Success) return null;

            string version = titleMatch.Groups[1].Value.Trim();

            // Pattern for Filename: <div class="filename"[^>]*>([^<]+)</div>
            var fileMatch = Regex.Match(html, @"<div[^>]*class=""filename""[^>]*>\s*(whql-amd-software-[^<]+\.exe|non-whql-amd-software-[^<]+\.exe|amd-software-[^<]+\.exe)\s*</div>", RegexOptions.IgnoreCase);
            string filename = fileMatch.Success
                ? fileMatch.Groups[1].Value.Trim()
                : $"whql-amd-software-adrenalin-edition-{version}-win10-win11.exe";

            // Pattern for Date: <span class="date">([^<]+)</span>
            DateTime? releaseDate = null;
            var dateMatch = Regex.Match(html, @"<span[^>]*class=""date""[^>]*>([^<]+)</span>", RegexOptions.IgnoreCase);
            if (dateMatch.Success)
            {
                string rawDate = dateMatch.Groups[1].Value.Trim();
                // Clean ordinal suffixes (st, nd, rd, th)
                rawDate = Regex.Replace(rawDate, @"\b(\d+)(st|nd|rd|th)\b", "$1", RegexOptions.IgnoreCase);
                if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    releaseDate = parsedDate;
                }
            }

            string downloadUrl = $"https://drivers.amd.com/drivers/{filename}";

            return new DriverInfo
            {
                Version = version,
                DownloadUrl = downloadUrl,
                SupportUrl = "https://www.amd.com/en/support/download/drivers.html",
                ReleaseDate = releaseDate,
                DisplayString = $"AMD Adrenalin {version} WHQL"
            };
        }

        public static DriverInfo GetCuratedLatest()
        {
            const string version = "25.10.1";
            return new DriverInfo
            {
                Version = version,
                DownloadUrl = "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-25.10.1-win10-win11-may-rdna.exe",
                SupportUrl = "https://www.amd.com/en/support/download/drivers.html",
                ReleaseDate = new DateTime(2026, 8, 5),
                DisplayString = $"AMD Adrenalin {version} WHQL"
            };
        }
    }
}