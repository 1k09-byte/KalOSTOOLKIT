using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KaliteKit.Services
{
    /// <summary>
    /// Multi-package-manager install/uninstall service.
    ///
    /// Priority chain for every install: winget → Chocolatey → Scoop → direct download.
    /// Each manager is only tried when (a) it is detected on the machine and
    /// (b) the item declares an ID for it. Failures on one leg never abort the
    /// chain — they are recorded in the result detail so the caller can fall
    /// through to the next manager. Detection is cached for 60 s; after a
    /// winget repair the cache can be invalidated with <see cref="InvalidateCache"/>.
    /// </summary>
    public sealed class PackageManagerService
    {
        private readonly ProcessManager _process;
        private readonly ElevationService _elevation;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(60);

        private Availability? _cached;
        private DateTime _cachedAt = DateTime.MinValue;

        public PackageManagerService(ProcessManager process, ElevationService elevation)
        {
            _process = process;
            _elevation = elevation;
        }

        /// <summary>Which package managers are currently detected on this machine.</summary>
        public record Availability(bool Winget, bool Chocolatey, bool Scoop)
        {
            public bool Any => Winget || Chocolatey || Scoop;
            public static Availability None => new(false, false, false);
        }

        /// <summary>Outcome of a package manager operation.</summary>
        /// <param name="Manager">Name of the manager that succeeded, or empty when none did.</param>
        public record PackageResult(string Manager, bool Success, string Detail);

        /// <summary>Detects available package managers, caching the result for 60 s.</summary>
        public async Task<Availability> DetectAsync(CancellationToken cancellationToken = default)
        {
            if (_cached != null && DateTime.UtcNow - _cachedAt < _cacheLifetime)
            {
                return _cached;
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                // Double-checked so concurrent callers don't re-probe after the first completes.
                if (_cached != null && DateTime.UtcNow - _cachedAt < _cacheLifetime)
                {
                    return _cached;
                }

                // winget is probed through WingetHelper so its App Execution Alias handling applies.
                bool winget = await WingetHelper.IsAvailableAsync(cancellationToken);
                bool chocolatey = await IsCliAvailableAsync("choco", "--version", cancellationToken);
                bool scoop = await IsCliAvailableAsync("scoop", "--version", cancellationToken);

                _cached = new Availability(winget, chocolatey, scoop);
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Forces the next <see cref="DetectAsync"/> call to re-probe the machine.</summary>
        public void InvalidateCache()
        {
            _cached = null;
        }

        /// <summary>
        /// Installs the item through the first available manager that declares an
        /// ID for it. Returns the winning manager or a failure containing every
        /// leg's outcome. Never throws for manager failures.
        /// </summary>
        public async Task<PackageResult> InstallAsync(
            string? wingetId,
            string? chocolateyId,
            string? scoopName,
            Action<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            var availability = await DetectAsync(cancellationToken);
            var failures = new System.Text.StringBuilder();

            if (availability.Winget && !string.IsNullOrEmpty(wingetId))
            {
                status?.Invoke("Installing via winget...");
                var result = await WingetHelper.RunAsync(
                    $"install --id {wingetId} --source winget -e --accept-package-agreements --accept-source-agreements --silent --force",
                    ensureSource: true,
                    cancellationToken);
                if (result.Success)
                {
                    return new PackageResult("winget", true, "Installed via winget.");
                }
                failures.Append("winget: ").Append(TrimDetail(result.StandardError, result.StandardOutput)).Append("; ");
            }

            if (availability.Chocolatey && !string.IsNullOrEmpty(chocolateyId))
            {
                if (!_elevation.IsElevated())
                {
                    failures.Append("Chocolatey skipped (needs administrator rights). ");
                }
                else
                {
                    status?.Invoke("winget failed — trying Chocolatey…");
                    var (output, error, exitCode) = await _process.RunWithOutputAndErrorAsync(
                        "choco",
                        $"install {chocolateyId} -y --no-progress --limit-output",
                        TimeSpan.FromMinutes(15),
                        cancellationToken);
                    if (exitCode == 0)
                    {
                        return new PackageResult("Chocolatey", true, "Installed via Chocolatey.");
                    }
                    failures.Append("choco: ").Append(TrimDetail(error, output)).Append("; ");
                }
            }

            if (availability.Scoop && !string.IsNullOrEmpty(scoopName))
            {
                status?.Invoke("Trying Scoop…");
                var (output, error, exitCode) = await _process.RunWithOutputAndErrorAsync(
                    "scoop",
                    $"install {scoopName}",
                    TimeSpan.FromMinutes(15),
                    cancellationToken);
                if (exitCode == 0)
                {
                    return new PackageResult("Scoop", true, "Installed via Scoop.");
                }
                failures.Append("scoop: ").Append(TrimDetail(error, output)).Append("; ");
            }

            string detail = failures.Length == 0
                ? "No package manager was available for this item."
                : failures.ToString().TrimEnd(' ', ';');
            return new PackageResult(string.Empty, false, detail);
        }

        /// <summary>
        /// Uninstalls through the first manager that declares an ID and succeeds.
        /// Best-effort: a missing package is not an error the caller needs to see.
        /// When <paramref name="displayName"/> is given, a last-resort fallback
        /// runs the app's own uninstaller from the Add/Remove Programs registry
        /// — apps installed via the direct-download fallback are invisible to
        /// winget, so without this they could never be uninstalled from here.
        /// </summary>
        public async Task<PackageResult> UninstallAsync(
            string? wingetId,
            string? chocolateyId,
            string? scoopName,
            Action<string>? status = null,
            CancellationToken cancellationToken = default,
            string? displayName = null)
        {
            var availability = await DetectAsync(cancellationToken);
            var failures = new System.Text.StringBuilder();

            if (availability.Winget && !string.IsNullOrEmpty(wingetId))
            {
                status?.Invoke("Uninstalling via winget…");
                var result = await WingetHelper.RunAsync(
                    $"uninstall --id {wingetId} -e --silent --force",
                    ensureSource: false,
                    cancellationToken);
                if (result.Success)
                {
                    return new PackageResult("winget", true, "Uninstalled via winget.");
                }
                failures.Append("winget: ").Append(TrimDetail(result.StandardError, result.StandardOutput)).Append("; ");
            }

            if (availability.Chocolatey && !string.IsNullOrEmpty(chocolateyId))
            {
                if (!_elevation.IsElevated())
                {
                    failures.Append("Chocolatey: skipped (needs administrator rights). ");
                }
                else
                {
                    status?.Invoke("winget unavailable… trying Chocolatey…");
                    var (output, error, exitCode) = await _process.RunWithOutputAndErrorAsync(
                        "choco",
                        $"uninstall {chocolateyId} -y --no-progress --limit-output",
                        TimeSpan.FromMinutes(10),
                        cancellationToken);
                    if (exitCode == 0)
                    {
                        return new PackageResult("Chocolatey", true, "Uninstalled via Chocolatey.");
                    }
                    failures.Append("choco: ").Append(TrimDetail(error, output)).Append("; ");
                }
            }

            if (availability.Scoop && !string.IsNullOrEmpty(scoopName))
            {
                status?.Invoke("Trying Scoop…");
                var (output, error, exitCode) = await _process.RunWithOutputAndErrorAsync(
                    "scoop",
                    $"uninstall {scoopName}",
                    TimeSpan.FromMinutes(10),
                    cancellationToken);
                if (exitCode == 0)
                {
                    return new PackageResult("Scoop", true, "Uninstalled via Scoop.");
                }
                failures.Append("scoop: ").Append(TrimDetail(error, output)).Append("; ");
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                status?.Invoke($"Trying {displayName}'s own uninstaller…");
                var (ok, arpDetail) = await TryUninstallFromRegistryAsync(displayName, cancellationToken);
                if (ok)
                {
                    return new PackageResult("UninstallString", true, "Uninstalled via the app's own uninstaller.");
                }
                if (arpDetail.Length > 0)
                {
                    failures.Append("uninstaller: ").Append(arpDetail).Append("; ");
                }
            }

            string detail = failures.Length == 0
                ? "No package manager was available for this item."
                : failures.ToString().TrimEnd(' ', ';');
            return new PackageResult(string.Empty, false, detail);
        }

        private async Task<bool> IsCliAvailableAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            try
            {
                var (output, _, exitCode) = await _process.RunWithOutputAndErrorAsync(
                    fileName, arguments, TimeSpan.FromSeconds(10));
                return exitCode == 0 && !string.IsNullOrWhiteSpace(output);
            }
            catch
            {
                return false;
            }
        }

        private static string TrimDetail(string error, string output)
        {
            string detail = !string.IsNullOrWhiteSpace(error) ? error : output;
            detail = detail.Replace("\r", " ").Replace("\n", " ").Trim();
            return detail.Length > 220 ? detail[..220] + "…" : detail;
        }

        // ── Add/Remove Programs fallback ──────────────────────────────────

        /// <summary>
        /// Finds the app in the Windows "Uninstall" registry keys (the
        /// Add/Remove Programs list) and runs its own uninstaller silently.
        /// Returns (true, "") on success, (false, detail) on a real failure,
        /// and (false, "") when no matching entry was found (nothing ran).
        /// </summary>
        private async Task<(bool Ok, string Detail)> TryUninstallFromRegistryAsync(
            string displayName, CancellationToken cancellationToken)
        {
            string[] roots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var root in roots)
                {
                    using var parent = hive.OpenSubKey(root);
                    if (parent is null) continue;

                    foreach (var sub in parent.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string? command, entryPath;
                        using (var entry = parent.OpenSubKey(sub))
                        {
                            if (entry is null) continue;
                            var name = entry.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(name) ||
                                !name.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // QuietUninstallString is the vendor's own silent
                            // command; fall back to UninstallString + flags.
                            command = entry.GetValue("QuietUninstallString") as string
                                      ?? entry.GetValue("UninstallString") as string;
                            if (string.IsNullOrWhiteSpace(command)) continue;
                            entryPath = $@"{root}\{sub}";
                        }

                        command = Silence(Environment.ExpandEnvironmentVariables(command.Trim()));
                        var (exe, args) = SplitCommandLine(command);
                        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                        {
                            return (false, $"uninstaller not found: {command}");
                        }

                        var (output, error, exitCode) = await _process.RunWithOutputAndErrorAsync(
                            exe, args, TimeSpan.FromMinutes(10), cancellationToken);

                        // The uninstaller may spawn a detached child; treat the
                        // entry disappearing as the ground truth, with exit 0
                        // as the fallback signal.
                        bool gone = true;
                        for (int i = 0; i < 30 && gone; i++)
                        {
                            using var check = hive.OpenSubKey(entryPath);
                            gone = check is not null;
                            if (gone) await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        }
                        if (!gone || exitCode == 0)
                        {
                            return (true, string.Empty);
                        }
                        return (false, TrimDetail(error, output));
                    }
                }
            }
            return (false, string.Empty);
        }

        /// <summary>
        /// Adds silent-mode flags for the common installer families so an
        /// uninstall never pops UI. Commands that already carry their own
        /// uninstall flags (Chromium setup.exe, QuietUninstallString) run as-is.
        /// </summary>
        private static string Silence(string command)
        {
            string lower = command.ToLowerInvariant();
            if (lower.Contains("msiexec"))
            {
                return lower.Contains("/qn") ? command : command + " /qn /norestart";
            }
            if (lower.Contains("unins000.exe")) // Inno Setup
            {
                return command + " /VERYSILENT /NORESTART /SUPPRESSMSGBOXES";
            }
            if (lower.Contains("update.exe")) // Squirrel (Discord, Slack, …)
            {
                return lower.Contains(" -s") ? command : command + " -s";
            }
            if (lower.Contains("helper.exe")) // Mozilla (Firefox family)
            {
                return command + " /s";
            }
            if (lower.Contains("uninstall.exe") || lower.Contains("uninst.exe")) // NSIS
            {
                return lower.Contains("/s") ? command : command + " /S";
            }
            return command;
        }

        /// <summary>Splits "C:\path\to\exe" args into (exe, args), quoted paths included.</summary>
        private static (string Exe, string Args) SplitCommandLine(string command)
        {
            command = command.Trim();
            if (command.StartsWith('"'))
            {
                int end = command.IndexOf('"', 1);
                if (end > 0)
                {
                    return (command[1..end], command[(end + 1)..].TrimStart());
                }
            }
            int space = command.IndexOf(' ');
            return space < 0 ? (command, string.Empty) : (command[..space], command[space..].TrimStart());
        }
    }
}