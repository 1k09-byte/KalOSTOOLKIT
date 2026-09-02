using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services
{
    /// <summary>A resolved KalOS release: tag, parsed version, and the zip payload URL.</summary>
    public sealed record GitHubReleaseInfo(string Tag, string Version, string ZipUrl, string? Notes)
    {
        /// <summary>Human-facing release page, derived from the tag.</summary>
        public string PageUrl => $"https://github.com/{GitHubReleaseClient.Owner}/{GitHubReleaseClient.Repo}/releases/tag/{Tag}";
    }

    /// <summary>
    /// Resolves the newest KalOS consumer release on GitHub without hitting the
    /// REST API rate limits: it reads the <c>/releases/latest</c> redirect for
    /// the tag (the same technique <see cref="UpdateService"/> and
    /// <c>install-kalos.ps1</c> use) and then scrapes the release's
    /// <c>expanded_assets</c> fragment for the actual zip payload, so whichever
    /// zip name the release attaches works.
    ///
    /// Important: a release may carry BOTH the app zip and the new
    /// <c>KalOS-Setup-*.zip</c> wizard payload. <see cref="SelectZipAssetUrl"/>
    /// always prefers the app zip so the installer never downloads itself.
    /// </summary>
    public sealed class GitHubReleaseClient
    {
        public const string Owner = "1k09-byte";
        public const string Repo = "KalOSTOOLKIT";

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS-Setup/1.0 (release-resolver)");
            return client;
        }

        /// <summary>
        /// Resolves the latest published release. Returns null when GitHub is
        /// unreachable or the response shape changed — callers must treat that
        /// as "cannot install right now", never as "up to date".
        /// </summary>
        public async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
        {
            string? tag = await ResolveLatestTagAsync(cancellationToken).ConfigureAwait(false);
            if (tag is null) return null;

            string version = tag.TrimStart('v', 'V');
            string? zipUrl = await ResolveZipAssetUrlAsync(tag, cancellationToken).ConfigureAwait(false);

            // Last resort, mirrors UpdateService: construct the canonical asset
            // name. Releases ship KalOS-v{version}-win-x64.zip, not the legacy
            // KalOS.zip (which 404s). The download step will surface a 404 if it
            // truly is absent.
            zipUrl ??= $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/KalOS-v{version}-win-x64.zip";

            return new GitHubReleaseInfo(tag, version, zipUrl, Notes: null);
        }

        /// <summary>Tag of the newest release via the /releases/latest redirect (no API, no rate limit).</summary>
        private static async Task<string?> ResolveLatestTagAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://github.com/{Owner}/{Repo}/releases/latest?t={DateTime.UtcNow.Ticks}");
                using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                // No-redirect client: any 3xx carries the tag in Location.
                string? location = response.Headers.Location?.ToString();
                if (string.IsNullOrWhiteSpace(location)) return null;

                return ParseTagFromRedirect(location);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Scrapes the release's expanded-assets fragment for the app zip payload.</summary>
        private static async Task<string?> ResolveZipAssetUrlAsync(string tag, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await Client.GetAsync(
                    $"https://github.com/{Owner}/{Repo}/releases/expanded_assets/{tag}", cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return SelectZipAssetUrl(html, tag);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Extracts the tag from a <c>/releases/tag/…</c> redirect location. Extracted for tests.</summary>
        internal static string? ParseTagFromRedirect(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return null;
            var match = Regex.Match(location, @"/tag/v?([^/]+)/?$");
            return match.Success ? "v" + match.Groups[1].Value.Trim() : null;
        }

        /// <summary>
        /// Picks the KalOS <b>app</b> zip from an expanded-assets HTML fragment.
        /// Preference order: the versioned <c>KalOS-v{version}-win-x64.zip</c>,
        /// then a plain <c>KalOS.zip</c>, then any other zip that is not the
        /// Setup wizard payload (so a release carrying both never makes the
        /// installer download itself). Extracted for tests.
        /// </summary>
        internal static string? SelectZipAssetUrl(string assetsHtml, string tag)
        {
            if (string.IsNullOrWhiteSpace(assetsHtml)) return null;

            var matches = Regex.Matches(assetsHtml, "href=\"(?<u>/[^\"]+/releases/download/[^\"]+\\.zip)\"");
            var urls = matches.Select(m => m.Groups["u"].Value).Distinct().ToArray();
            if (urls.Length == 0) return null;

            string version = tag.TrimStart('v', 'V');
            string absolute(string u) => "https://github.com" + u;
            bool isSetup(string u) => u.Contains("setup", StringComparison.OrdinalIgnoreCase)
                                   || u.Contains("installer", StringComparison.OrdinalIgnoreCase);

            string? relative =
                urls.FirstOrDefault(u => u.Contains($"KalOS-v{version}-win-x64.zip", StringComparison.OrdinalIgnoreCase))
             ?? urls.FirstOrDefault(u => u.EndsWith("/KalOS.zip", StringComparison.OrdinalIgnoreCase))
             ?? urls.FirstOrDefault(u => !isSetup(u));

            return relative is { } u ? absolute(u) : null;
        }

        /// <summary>Fetches the release body (notes) for the Finish-page summary. Never throws.</summary>
        public async Task<string?> TryGetReleaseNotesAsync(string tag, CancellationToken cancellationToken = default)
        {
            try
            {
                using var api = new HttpClient();
                api.Timeout = TimeSpan.FromSeconds(15);
                api.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS-Setup/1.0");
                api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                string json = await api.GetStringAsync(
                    $"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{tag.TrimStart('v', 'V')}", cancellationToken)
                    .ConfigureAwait(false);

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("body", out var body) &&
                    body.ValueKind == System.Text.Json.JsonValueKind.String &&
                    body.GetString() is { } text &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Rate-limited / offline — notes are optional.
            }
            return null;
        }
    }
}
