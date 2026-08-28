using System;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services
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
        /// </summary>
        public async Task<PackageResult> UninstallAsync(
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
    }
}