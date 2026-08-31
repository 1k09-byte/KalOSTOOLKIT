using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services;

/// <summary>Information about a release that should replace the running build.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string ZipAssetUrl, string PageUrl, string ReleaseNotes, bool IsRollback = false);

/// <summary>What the last update applied, persisted so the app can show an update log after restart.</summary>
public sealed record UpdateRecord(string Version, DateTime AppliedAt, string Notes);

/// <summary>One entry in a parsed release history (used to show an update log / changelog).</summary>
public sealed record ReleaseHistoryEntry(Version Version, string Tag, DateTime? PublishedAt, string Notes, bool IsCurrent);

/// <summary>A downloadable asset attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string BrowserDownloadUrl);

/// <summary>Persisted update preferences (stored next to the app's logs).</summary>
public sealed class UpdateSettings
{
    public bool AutoCheckForUpdates { get; set; } = true;

    // Startup banner wallpaper — read by StartupBannerWindow. String values
    // match the switch cases there; defaults mirror the reader's fallback.
    public string BackgroundImagePath { get; set; } = string.Empty;
    public double BackgroundImageOpacity { get; set; } = 0.35;
    public string BackgroundImageFit { get; set; } = "UniformToFill";
    public string BackgroundImageHorizontalAlignment { get; set; } = "Center";
    public string BackgroundImageVerticalAlignment { get; set; } = "Center";
}

/// <summary>
/// Self-updater for the distributed (consumer) build of KalOS. Checks the
/// project's GitHub Releases for a newer version, downloads the packaged zip
/// asset, and applies it in place: the app spawns a hidden PowerShell helper
/// that waits for it to exit, swaps the files, and relaunches the new build.
/// </summary>
public sealed class UpdateService
{
    public const string DefaultOwner = "1k09-byte";
    public const string DefaultRepo = "KalOSTOOLKIT";

    /// <summary>
    /// Fires when the app must forcefully roll back (e.g. current tag was removed).
    /// </summary>
    public event Action<UpdateInfo>? RollbackRequired;

    private readonly LoggingService _log;
    private readonly HttpClient _http;

    public UpdateService(LoggingService log, string? owner = null, string? repo = null)
    {
        _log = log;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS-Updater/1.0");
        _http.Timeout = TimeSpan.FromSeconds(45);
    }

    /// <summary>The version of the running build (from the KalOS assembly).</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KalOS");

    public static string UpdatesFolder => Path.Combine(AppDataFolder, "updates");

    public static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    public static string LastUpdateRecordPath => Path.Combine(AppDataFolder, "last-update.json");

    public static string RollbackStatePath => Path.Combine(AppDataFolder, "rollback-state.json");

    /// <summary>Persist what was applied so the app can show an update log after restart.</summary>
    public static void SaveLastUpdateRecord(UpdateInfo info)
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(LastUpdateRecordPath, JsonSerializer.Serialize(new UpdateRecord(info.Version.ToString(), DateTime.Now, info.ReleaseNotes ?? string.Empty)));
        }
        catch { }
    }

    public static UpdateRecord? LoadLastUpdateRecord()
    {
        try
        {
            if (File.Exists(LastUpdateRecordPath))
                return JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(LastUpdateRecordPath));
        }
        catch { }
        return null;
    }

    public static void ClearLastUpdateRecord()
    {
        try { if (File.Exists(LastUpdateRecordPath)) File.Delete(LastUpdateRecordPath); } catch { }
    }

    private sealed record RollbackState(string Version, string Reason, DateTime DetectedAt);

    private static void SaveRollbackState(Version version, string reason)
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(RollbackStatePath, JsonSerializer.Serialize(new RollbackState(version.ToString(), reason, DateTime.Now)));
        }
        catch { }
    }

    // ── Pure helpers (unit-tested) ────────────────────────────────────────

    /// <summary>Parses a release tag like "v1.0.0.4" (or "1.0.0") into a Version.</summary>
    public static bool TryParseReleaseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string cleaned = tag.Trim().TrimStart('v', 'V');
        if (Version.TryParse(cleaned, out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }
        return false;
    }

    /// <summary>True when the release version differs from the running build
    /// (upgrade or downgrade — the app always converges on the latest release).</summary>
    public static bool IsDifferent(Version latest, Version current) => latest != current;

    // Kept for API compatibility: a release is "actionable" whenever it differs.
    public static bool IsNewer(Version latest, Version current) => IsDifferent(latest, current);

    /// <summary>Picks the packaged zip asset for a release, preferring the exact naming convention.</summary>
    public static string? SelectZipAsset(IEnumerable<ReleaseAsset> assets, Version version)
    {
        var list = assets.ToList();
        var preferred = list.FirstOrDefault(a =>
            a.Name.Equals($"KalOS-v{version}-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (preferred != null) return preferred.BrowserDownloadUrl;
        var any = list.FirstOrDefault(a =>
            a.Name.StartsWith("KalOS", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return any?.BrowserDownloadUrl;
    }

    /// <summary>Parses a GitHub "latest release" JSON payload. Returns null when there is no newer version or no zip asset.</summary>
    public static UpdateInfo? ParseRelease(string json, Version currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) return null;
        // Offer the latest release whenever it DIFFERS from the running build —
        // including when it is lower (e.g. after the version line was reset) —
        // so the app always converges on whatever is published as latest.
        if (!TryParseReleaseVersion(tag, out var latest) || latest == currentVersion) return null;

        string page = root.TryGetProperty("html_url", out var h) && h.GetString() is { } hp ? hp : string.Empty;
        string notes = root.TryGetProperty("body", out var b) && b.GetString() is { } bn ? bn : string.Empty;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var n) && n.GetString() is { } name &&
                    a.TryGetProperty("browser_download_url", out var u) && u.GetString() is { } url)
                {
                    assets.Add(new ReleaseAsset(name, url));
                }
            }
        }

        string? zip = SelectZipAsset(assets, latest);
        if (zip == null) return null;
        return new UpdateInfo(latest, tag, zip, page, notes, latest < currentVersion);
    }

    /// <summary>
    /// Parses a GitHub "releases" JSON array into a history list, newest first,
    /// marking the entry that matches the running build. Invalid tags and
    /// non-array payloads are skipped/ignored. Released notes have the internal
    /// "<!-- discord-msg:… -->" marker stripped.
    /// </summary>
    public static List<ReleaseHistoryEntry> ParseReleaseHistory(string json, Version currentVersion)
    {
        var history = new List<ReleaseHistoryEntry>();
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return history;

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) continue;
                if (!TryParseReleaseVersion(tag, out var version)) continue;

                string notes = item.TryGetProperty("body", out var body) && body.GetString() is { } bn
                    ? StripDiscordMarker(bn)
                    : string.Empty;
                DateTime? published = item.TryGetProperty("published_at", out var p) &&
                                      p.TryGetDateTime(out var dt)
                    ? dt
                    : (DateTime?)null;

                history.Add(new ReleaseHistoryEntry(version, tag, published, notes, version == currentVersion));
            }
        }

        // Newest first.
        history.Sort((a, b) => b.Version.CompareTo(a.Version));
        return history;
    }

    /// <summary>Removes a leading "&lt;!-- discord-msg:… --&gt;" marker (and surrounding whitespace) from release notes.</summary>
    private static string StripDiscordMarker(string notes)
    {
        int start = notes.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0) return notes.Trim();
        int end = notes.IndexOf("-->", start, StringComparison.Ordinal);
        if (end < 0) return notes.Trim();
        return (notes.Substring(0, start) + notes.Substring(end + 3)).Trim();
    }

    // ── Network / apply ──────────────────────────────────────────────────

    /// <summary>Fetches all published releases from GitHub for the release-history dialog.</summary>
    public async Task<IReadOnlyList<ReleaseHistoryEntry>> GetReleaseHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS-Updater/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(20);
            string json = await client.GetStringAsync(
                $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases?per_page=100", cancellationToken);
            return ParseReleaseHistory(json, CurrentVersion);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<ReleaseHistoryEntry>();
        }
        catch (Exception ex)
        {
            _log.Warn($"Release history fetch failed: {ex.Message}");
            return Array.Empty<ReleaseHistoryEntry>();
        }
    }

    /// <summary>Checks GitHub for a newer release. Returns null when up to date, no releases exist, or the check failed.</summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Use a custom handler to disable redirect. This bypasses the GitHub REST API rate limits
            // completely by polling the standard public releases/latest page.
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);

            // Fetch the /latest redirect URL. Add a cache-busting t parameter.
            using var resp = await client.GetAsync(
                $"https://github.com/{DefaultOwner}/{DefaultRepo}/releases/latest?t={DateTime.UtcNow.Ticks}", cancellationToken);

            if (resp.StatusCode == System.Net.HttpStatusCode.Redirect ||
                resp.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                resp.StatusCode == System.Net.HttpStatusCode.Found ||
                resp.StatusCode == System.Net.HttpStatusCode.SeeOther)
            {
                var location = resp.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location))
                {
                    // location will be like: https://github.com/1k09-byte/KalOSTOOLKIT/releases/tag/v1.0.0.1
                    // or relative: /1k09-byte/KalOSTOOLKIT/releases/tag/v1.0.0.1
                    var match = System.Text.RegularExpressions.Regex.Match(location, @"/tag/v?([^/]+)/?$");
                    if (match.Success)
                    {
                        var tag = "v" + match.Groups[1].Value.Trim();
                        if (TryParseReleaseVersion(tag, out var latest) && latest != CurrentVersion)
                        {
                            var zipUrl = $"https://github.com/{DefaultOwner}/{DefaultRepo}/releases/download/{tag}/KalOS.zip";
                            var pageUrl = $"https://github.com/{DefaultOwner}/{DefaultRepo}/releases/tag/{tag}";
                            var notes = $"New version {tag} is available.\n\n{pageUrl}";
                            // Try to fetch the real release body so the update log shows actual notes, not just a link
                            try
                            {
                                using var apiClient = new HttpClient();
                                apiClient.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS-Updater/1.0");
                                apiClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                                string? bodyStr = null;
                                foreach (var tryTag in new[] { tag, tag.TrimStart('v', 'V') })
                                {
                                    try
                                    {
                                        var apiUrl = $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases/tags/{tryTag}";
                                        var apiResp = await apiClient.GetStringAsync(apiUrl, cancellationToken);
                                        using var apiDoc = JsonDocument.Parse(apiResp);
                                        if (apiDoc.RootElement.TryGetProperty("body", out var b) && b.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                                        {
                                            bodyStr = s;
                                            break;
                                        }
                                    }
                                    catch { /* try next */ }
                                }
                                if (!string.IsNullOrWhiteSpace(bodyStr))
                                {
                                    // Strip hidden discord marker if present
                                    notes = System.Text.RegularExpressions.Regex.Replace(bodyStr.Trim(), @"<!-- discord-msg:\d+ -->\s*", "").Trim();
                                    if (string.IsNullOrWhiteSpace(notes)) notes = $"New version {tag} is available.\n\n{pageUrl}";
                                }
                            }
                            catch { /* keep generic notes with link on failure (rate limit/offline) */ }
                            var update = new UpdateInfo(latest, tag, zipUrl, pageUrl, notes, latest < CurrentVersion);

                            if (update.IsRollback)
                            {
                                SaveRollbackState(CurrentVersion, $"Version {CurrentVersion} was superseded by an older stable build.");
                                RollbackRequired?.Invoke(update);
                            }
                            return update;
                        }
                    }
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads and extracts the update package, then spawns the hidden apply
    /// helper and returns. The caller must exit the app immediately so the
    /// helper can replace the files and relaunch the new build.
    /// </summary>
    private static string? rootTagFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<bool> DownloadAndApplyAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(UpdatesFolder);
            string zipPath = Path.Combine(UpdatesFolder, $"KalOS-{info.Version}.zip");

            // Stream the download to disk and report progress (0..1) so the UI
            // can show a live progress bar. The caller marshals the callback to
            // the UI thread.
            using (var resp = await _http.GetAsync(info.ZipAssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                using var src = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var fs = File.Create(zipPath);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                    read += n;
                    if (total > 0 && progress != null) progress.Report((double)read / total);
                }
            }

            string extractDir = Path.Combine(UpdatesFolder, $"v{info.Version}");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // Sanity checks: the package must contain KalOS at its version.
            // In a framework-dependent build KalOS.exe is a native apphost
            // launcher without CLR metadata, so read the version from the
            // managed KalOS.dll (fall back to the exe for single-file builds).
            string newExe = Path.Combine(extractDir, "KalOS.exe");
            if (!File.Exists(newExe))
            {
                _log.Error($"Update package is missing KalOS.exe: {zipPath}");
                return false;
            }
            string assemblyPath = Path.Combine(extractDir, "KalOS.dll");
            if (!File.Exists(assemblyPath)) assemblyPath = newExe;
            Version newVersion;
            try
            {
                newVersion = AssemblyName.GetAssemblyName(assemblyPath).Version ?? new Version(0, 0, 0);
            }
            catch (BadImageFormatException)
            {
                _log.Error($"Update package assembly is not a valid .NET image: {assemblyPath}");
                return false;
            }
            
            // Bypass strict strict version matching between GitHub Tag and internal Assembly metadata
            // as this is prone to developer workflow deployment mismatches.

            string scriptPath = Path.Combine(UpdatesFolder, "apply-update.ps1");
            string logPath = Path.Combine(UpdatesFolder, "update.log");
            File.WriteAllText(scriptPath, BuildApplyScript(Environment.ProcessId, extractDir, AppContext.BaseDirectory, zipPath, logPath));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Update apply failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The hidden helper script: waits for the app to exit, copies the new
    /// build over the old one, relaunches KalOS, and cleans up the temp files.
    /// </summary>
    internal static string BuildApplyScript(int oldProcessId, string srcDir, string dstDir, string zipPath, string logPath)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return $@"
$ErrorActionPreference = 'Stop'
try {{
    $old = Get-Process -Id {oldProcessId} -ErrorAction SilentlyContinue
    if ($old) {{ $old.WaitForExit() }}
    Copy-Item -Path (Join-Path {Q(srcDir)} '*') -Destination {Q(dstDir)} -Recurse -Force
    Start-Process -FilePath (Join-Path {Q(dstDir)} 'KalOS.exe') -WorkingDirectory {Q(dstDir)}
    Remove-Item -Path {Q(srcDir)} -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path {Q(zipPath)} -Force -ErrorAction SilentlyContinue
    Set-Content -Path {Q(logPath)} -Value ('[OK] Update applied at ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
}} catch {{
    Set-Content -Path {Q(logPath)} -Value ('[FAILED] ' + $_.Exception.Message)
}}
";
    }

    // ── Preferences ──────────────────────────────────────────────────────

    public static UpdateSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(SettingsPath)) ?? new UpdateSettings();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load update settings: {ex.Message}");
        }
        return new UpdateSettings();
    }

    public static void SaveSettings(UpdateSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save update settings: {ex.Message}");
        }
    }
}

