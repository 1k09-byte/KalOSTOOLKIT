using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KaliteKit.Services;

// ── Data model ───────────────────────────────────────────────────────────────

/// <summary>An open pull request on the project's GitHub repository.</summary>
/// <remarks>
/// A plain sealed class rather than a record: these instances are bound with
/// x:DataType in SettingsPage.xaml, and the WinUI XAML compiler cannot handle
/// record compiler-generated members.
/// </remarks>
public sealed class PrInfo
{
    public PrInfo(long number, string title, string author, string headRef, string headSha, string htmlUrl)
    {
        Number = number;
        Title = title;
        Author = author;
        HeadRef = headRef;
        HeadSha = headSha;
        HtmlUrl = htmlUrl;
    }

    public long Number { get; }
    public string Title { get; }
    public string Author { get; }
    public string HeadRef { get; }
    public string HeadSha { get; }
    public string HtmlUrl { get; }

    public string Label => $"#{Number} — {Title}";

    /// <summary>False when the PR has no known author (should never happen for real PRs; keeps the XAML binding simple).</summary>
    public bool HasAuthor => !string.IsNullOrEmpty(Author);
}

/// <summary>One file a pull request changes (from GET /pulls/{n}/files).</summary>
/// <remarks>Plain class for the same XAML-compiler reason as <see cref="PrInfo"/>.</remarks>
public sealed class PrChangedFile
{
    public PrChangedFile(string filename, string status, int additions, int deletions, string? previousFilename = null)
    {
        Filename = filename;
        Status = status;
        Additions = additions;
        Deletions = deletions;
        PreviousFilename = previousFilename;
    }

    public string Filename { get; }
    public string Status { get; }
    public int Additions { get; }
    public int Deletions { get; }

    /// <summary>Old path when Status is "renamed"; null otherwise.</summary>
    public string? PreviousFilename { get; }

    /// <summary>Compact "path (+a -d)" summary for the file list UI.</summary>
    public string Summary => $"{Filename} (+{Additions} -{Deletions})";
}

/// <summary>Result of merging (or undoing) a PR's changed files into the local checkout.</summary>
/// <param name="BackupDir">Backup folder for this merge; null when nothing was merged. Kept for manual cleanup / re-apply.</param>
public sealed record PrMergeResult(bool Success, IReadOnlyList<string> TouchedFiles, IReadOnlyList<string> Errors, string? BackupDir)
{
    public string Summary =>
        $"{TouchedFiles.Count} file(s)" + (Errors.Count > 0 ? $", {Errors.Count} error(s)" : string.Empty);
}

// ── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Detects contributor pull requests on GitHub and, as an explicit dev-tool
/// action, merges ONLY the files the PR changed into the local Git checkout —
/// with automatic per-merge backups and one-click undo. Nothing runs, builds,
/// or writes to the OS without the user clicking through a confirmation.
/// </summary>
public sealed class PrUpdateService
{
    public const string Owner = UpdateService.DefaultOwner;
    public const string Repo = UpdateService.DefaultRepo;

    private readonly LoggingService _log;
    private readonly HttpClient _http;

    /// <summary>
    /// Write options for the merge backup journal (indented, human-readable).
    /// </summary>
    private static readonly JsonSerializerOptions JournalWriteOptions = new() { WriteIndented = true };

    public PrUpdateService(LoggingService log)
    {
        _log = log;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KaliteKit-PrUpdater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    // ── Detection ────────────────────────────────────────────────────────

    /// <summary>Lists the repository's open pull requests (newest first). Empty on failure.</summary>
    public async Task<IReadOnlyList<PrInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string json = await _http.GetStringAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/pulls?state=open&per_page=50", cancellationToken);
            return ParsePullRequests(json);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<PrInfo>();
        }
        catch (Exception ex)
        {
            _log.Warn($"PR list fetch failed: {ex.Message}");
            return Array.Empty<PrInfo>();
        }
    }

    /// <summary>Fetches the files one pull request changes. Empty on failure.</summary>
    public async Task<IReadOnlyList<PrChangedFile>> GetChangedFilesAsync(long prNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            string json = await _http.GetStringAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/pulls/{prNumber}/files?per_page=100", cancellationToken);
            return ParseChangedFiles(json);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<PrChangedFile>();
        }
        catch (Exception ex)
        {
            _log.Warn($"PR file list fetch failed: {ex.Message}");
            return Array.Empty<PrChangedFile>();
        }
    }

    // ── Pure parsers (unit-tested) ───────────────────────────────────────

    /// <summary>Parses a GitHub pulls JSON array into PR records, newest (highest number) first.</summary>
    public static IReadOnlyList<PrInfo> ParsePullRequests(string json)
    {
        var list = new List<PrInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("number", out var numEl) || numEl.ValueKind != JsonValueKind.Number) continue;

                long number = numEl.GetInt64();
                string title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
                string author = item.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object &&
                                u.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString() ?? string.Empty
                    : string.Empty;
                string htmlUrl = item.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? string.Empty : string.Empty;
                string headRef = string.Empty, headSha = string.Empty;
                if (item.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object)
                {
                    if (head.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.String) headRef = r.GetString() ?? string.Empty;
                    if (head.TryGetProperty("sha", out var s) && s.ValueKind == JsonValueKind.String) headSha = s.GetString() ?? string.Empty;
                }

                if (number > 0 && !string.IsNullOrWhiteSpace(headSha))
                    list.Add(new PrInfo(number, title, author, headRef, headSha, htmlUrl));
            }
        }
        catch (JsonException)
        {
            // Invalid payload → empty list.
        }
        return list.OrderByDescending(p => p.Number).ToList();
    }

    /// <summary>Parses a GitHub pull-files JSON array. Unknown/odd entries are skipped.</summary>
    public static IReadOnlyList<PrChangedFile> ParseChangedFiles(string json)
    {
        var list = new List<PrChangedFile>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("filename", out var f) || f.GetString() is not { } filename || filename.Length == 0)
                    continue;

                string status = item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
                int additions = item.TryGetProperty("additions", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
                int deletions = item.TryGetProperty("deletions", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
                string? previous = item.TryGetProperty("previous_filename", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                list.Add(new PrChangedFile(filename, status, additions, deletions, previous));
            }
        }
        catch (JsonException)
        {
            // Invalid payload → empty list.
        }
        return list;
    }

    // ── Merge into local source (DEV TOOL ONLY) ─────────────────────────────

    /// <summary>
    /// Root of the local Git checkout this dev build is running from: walks up
    /// from the app binary until a .git folder is found. Null when the app was
    /// started from a plain install folder (no source tree) — the merge UI is
    /// hidden in consumer builds, so this is only a defensive fallback.
    /// </summary>
    public static string? RepoRoot
    {
        get
        {
            try
            {
                DirectoryInfo? dir = new(AppContext.BaseDirectory);
                while (dir is not null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// Root checkpoint folder for all PR merges. Lives beside the repo (see
    /// <see cref="RepoRoot"/>) so it never pollutes git status, and holds one
    /// timestamped backup per merge for one-click undo.
    /// </summary>
    public static string MergeBackupRoot => Path.Combine(RepoRoot ?? AppContext.BaseDirectory, ".kalitekit-pr-merge-backups");

    /// <summary>Path of a merge's backup manifest, or null when that merge never happened.</summary>
    public static string? MergeBackupPath(string headSha) =>
        Directory.Exists(MergeBackupRoot)
            ? Directory.GetDirectories(MergeBackupRoot, $"pr-{headSha}-*")
                       .OrderByDescending(d => d)
                       .Select(d => Path.Combine(d, "backup.json"))
                       .FirstOrDefault(File.Exists)
            : null;

    /// <summary>True when this PR head's files were already merged into the local source (undo available).</summary>
    public static bool IsMergedForSha(string headSha) => MergeBackupPath(headSha) is not null;

    /// <summary>
    /// Merges ONLY the PR's changed files into the local checkout: every file
    /// the PR touches is downloaded from the PR's head commit and written over
    /// the local copy (added files created, modified files overwritten,
    /// renames moved, deletions removed). Each merge is fully undoable —
    /// originals are backed up and the operation is journaled in backup.json.
    /// This is a DEV TOOL: consumer builds never see this code path, and
    /// nothing here runs without the user confirming in the UI.
    /// </summary>
    public async Task<PrMergeResult> MergePrIntoSourceAsync(
        PrInfo pr, IReadOnlyList<PrChangedFile>? knownFiles = null, CancellationToken cancellationToken = default)
    {
        var touched = new List<string>();
        try
        {
            var files = knownFiles is { Count: > 0 } ? knownFiles : await GetChangedFilesAsync(pr.Number, cancellationToken);
            if (files.Count == 0)
            {
                return new PrMergeResult(false, touched, new[] { $"Could not load the file list for PR #{pr.Number} (offline or rate-limited)." }, null);
            }

            // 1. Build the backup journal BEFORE touching anything.
            string? repoRoot = RepoRoot;
            if (repoRoot == null)
            {
                return new PrMergeResult(false, touched, new[] { "No source checkout found (the app is not running from a Git clone)." }, null);
            }
            string backupDir = Path.Combine(MergeBackupRoot, $"pr-{pr.HeadSha}-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(backupDir);
            var journal = new MergeJournal();

            foreach (var file in files)
            {
                // Guard: every target must resolve inside the repo.
                if (!TryResolveRepoPath(repoRoot, file.Filename, out var target))
                {
                    return new PrMergeResult(false, touched, new[] { $"PR file '{file.Filename}' resolves outside the repository — merge refused." }, backupDir);
                }

                touched.Add(file.Filename);

                string BackupAs(string relative, string subFolder)
                {
                    string backupFile = Path.Combine(backupDir, subFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    return backupFile;
                }

                async Task UpsertAsync()
                {
                    string? content = await DownloadFileAsync(pr.HeadSha, file.Filename, cancellationToken);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        throw new InvalidOperationException($"Could not download '{file.Filename}' from PR #{pr.Number}.");
                    }
                    if (File.Exists(target))
                    {
                        File.Copy(target, BackupAs(file.Filename, "overwritten"), overwrite: true);
                        journal.Overwritten.Add(file.Filename);
                    }
                    else
                    {
                        journal.Added.Add(file.Filename);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, content);
                }

                switch (file.Status)
                {
                    case "removed":
                        if (File.Exists(target))
                        {
                            File.Copy(target, BackupAs(file.Filename, "deleted"), overwrite: true);
                            journal.Deleted.Add(file.Filename);
                            File.Delete(target);
                        }
                        break;

                    case "renamed":
                        if (file.PreviousFilename is { } oldPath &&
                            TryResolveRepoPath(repoRoot, oldPath, out var oldTarget) &&
                            File.Exists(oldTarget))
                        {
                            File.Copy(oldTarget, BackupAs(oldPath, "renamed"), overwrite: true);
                            journal.RenamedFrom.Add(oldPath);
                            File.Delete(oldTarget);
                        }
                        await UpsertAsync(); // then create the file at its new path
                        break;

                    default: // added / changed / modified + any future status
                        await UpsertAsync();
                        break;
                }
            }

            File.WriteAllText(Path.Combine(backupDir, "backup.json"),
                JsonSerializer.Serialize(journal, JournalWriteOptions));
            _log.Success($"Merged {touched.Count} file(s) from PR #{pr.Number} into the local source (backup: {backupDir})");
            return new PrMergeResult(true, touched, Array.Empty<string>(), backupDir);
        }
        catch (Exception ex)
        {
            _log.Error($"PR merge failed: {ex.Message}");
            return new PrMergeResult(false, touched, new[] { ex.Message }, null);
        }
    }

    /// <summary>
    /// Undoes a merge: restores every overwritten/deleted file from the backup,
    /// removes files the merge added, and restores renames to their old paths.
    /// The backup folder is kept until deleted manually, so the merge can be
    /// re-applied by running it again.
    /// </summary>
    public PrMergeResult UndoMerge(string headSha)
    {
        var touched = new List<string>();
        try
        {
            string? backupPath = MergeBackupPath(headSha);
            if (backupPath == null)
            {
                return new PrMergeResult(false, touched, new[] { "No merge backup found for this pull request." }, null);
            }
            string backupDir = Path.GetDirectoryName(backupPath)!;
            var journal = JsonSerializer.Deserialize<MergeJournal>(File.ReadAllText(backupPath), JournalWriteOptions);
            if (journal == null)
            {
                return new PrMergeResult(false, touched, new[] { "The merge backup journal is unreadable." }, backupDir);
            }
            string? undoRoot = RepoRoot;
            if (undoRoot == null)
            {
                return new PrMergeResult(false, touched, new[] { "No source checkout found (the app is not running from a Git clone)." }, backupDir);
            }

            void Restore(string relative, string backupSubFolder)
            {
                if (!TryResolveRepoPath(undoRoot, relative, out var target)) return;
                string src = Path.Combine(backupDir, backupSubFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(src, target, overwrite: true);
                touched.Add(relative);
            }

            foreach (var added in journal.Added)
            {
                if (TryResolveRepoPath(undoRoot, added, out var p) && File.Exists(p))
                {
                    File.Delete(p);
                    touched.Add(added);
                }
            }
            foreach (var deleted in journal.Deleted) Restore(deleted, "deleted");
            foreach (var overwritten in journal.Overwritten) Restore(overwritten, "overwritten");
            foreach (var oldPath in journal.RenamedFrom) Restore(oldPath, "renamed");

            _log.Success($"Undid merge: restored {touched.Count} file(s) from {backupDir}");
            return new PrMergeResult(true, touched, Array.Empty<string>(), backupDir);
        }
        catch (Exception ex)
        {
            _log.Error($"PR merge undo failed: {ex.Message}");
            return new PrMergeResult(false, touched, new[] { ex.Message }, null);
        }
    }

    /// <summary>
    /// Resolves a repo-relative path against the actual checkout root and
    /// refuses anything that escapes it (absolute paths, ".." traversal, roots).
    /// </summary>
    internal static bool TryResolveRepoPath(string repoRoot, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (Path.IsPathRooted(relativePath)) return false;
        if (relativePath.Split('/').Any(seg => seg is "." or "..")) return false;
        try
        {
            string combined = Path.GetFullPath(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
            if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = combined;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal sealed class MergeJournal
    {
        public List<string> Overwritten { get; set; } = new();
        public List<string> Added { get; set; } = new();
        public List<string> Deleted { get; set; } = new();
        public List<string> RenamedFrom { get; set; } = new();
    }

    /// <summary>
    /// Writes the rebuild-and-reopen helper script and launches it. The hidden
    /// PowerShell waits for this app to exit, runs dotnet build on the repo,
    /// and — only when the build succeeds — starts the freshly built KaliteKit.exe.
    /// Returns the log path so the UI can point at it when the build fails.
    /// Only ever invoked from the explicit "Rebuild &amp; reopen" button.
    /// </summary>
    public static string LaunchRebuildAndRelaunch(int currentProcessId)
    {
        string logDir = UpdateService.UpdatesFolder;
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, "pr-rebuild.log");
        string scriptPath = Path.Combine(logDir, "rebuild-and-relaunch.ps1");

        static string Q(string? s) => "'" + (s ?? "").Replace("'", "''") + "'";
        File.WriteAllText(scriptPath, $@"
$ErrorActionPreference = 'Continue'
$log = {Q(logPath)}
try {{
  $old = Get-Process -Id {currentProcessId} -ErrorAction SilentlyContinue
  if ($old) {{ $old.WaitForExit() }}
  Set-Location -Path {Q(RepoRoot!)}
  ('Rebuild started ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) | Set-Content $log
  dotnet build KaliteKit.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 *>> $log
  if ($LASTEXITCODE -ne 0) {{
    ('BUILD FAILED (exit ' + $LASTEXITCODE + ')') | Add-Content $log
    exit 1
  }}
  'BUILD OK' | Add-Content $log
  $exe = Get-ChildItem -Path {Q(RepoRoot!)} -Filter KaliteKit.exe -Recurse -ErrorAction SilentlyContinue |
         Where-Object {{ $_.FullName -like '*\bin\x64\*' }} |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($exe) {{
    Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName
    ('Relaunched ' + $exe.FullName) | Add-Content $log
  }} else {{
    'KaliteKit.exe not found after build' | Add-Content $log
    exit 1
  }}
}} catch {{
  $_.Exception.Message | Add-Content $log
  exit 1
}}
");

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        });
        return logPath;
    }

    // ── Raw download ─────────────────────────────────────────────────────

    /// <summary>Downloads one file at the given commit SHA from raw.githubusercontent.com. Null on failure.</summary>
    private async Task<string?> DownloadFileAsync(string sha, string path, CancellationToken cancellationToken)
    {
        try
        {
            // Encode each path segment but keep the '/' separators intact.
            var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            string url = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{sha}/{encoded}";
            return await _http.GetStringAsync(url, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"Raw file download failed ({path}): {ex.Message}");
            return null;
        }
    }
}
