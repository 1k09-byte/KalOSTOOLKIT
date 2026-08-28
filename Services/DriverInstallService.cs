using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services
{
    /// <summary>One driver package entry from <c>pnputil /enum-drivers</c>.</summary>
    internal sealed record ParsedDriverPackage(
        string PublishedName,
        string OriginalName,
        string ClassGuid,
        int[] Version,
        bool IsNvidia);

    /// <summary>
    /// Silent driver installation backend. NVIDIA packages are extracted with a
    /// standalone 7-Zip runner (never the interactive setup.exe wizard), stripped
    /// of every optional sub-package, and only the display INF is installed via
    /// pnputil — no GeForce Experience, NVIDIA App, HD Audio bundle, PhysX, or
    /// telemetry. After the install, NVIDIA-clean-install-grade cleanup runs:
    /// container/tracker services are disabled, scheduled tasks removed, leftover
    /// folders purged, and all older NVIDIA display driver-store packages deleted
    /// (only the freshly installed one is kept).
    /// </summary>
    public class DriverInstallService
    {
        private readonly LoggingService _log;
        private readonly ProcessManager _processManager;
        private readonly DriverDownloadService _downloadService;

        /// <summary>
        /// Standalone 7-Zip runner (single exe, handles NVIDIA's self-extracting
        /// driver packages). Downloaded once into the app's tools folder;
        /// extraction with 7zr is fully silent — no installer UI ever appears.
        /// </summary>
        private const string SevenZipRunnerUrl = "https://www.7-zip.org/a/7zr.exe";

        private static readonly string ToolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KalOS", "tools");

        private static readonly string SevenZipRunnerPath = Path.Combine(ToolsDir, "7zr.exe");

        /// <summary>7zr.exe is roughly 600 KB; anything much smaller means a failed download.</summary>
        private const long SevenZipRunnerMinBytes = 100_000;

        public DriverInstallService(LoggingService log, ProcessManager processManager, DriverDownloadService downloadService)
        {
            _log = log;
            _processManager = processManager;
            _downloadService = downloadService;
            Directory.CreateDirectory(ToolsDir);
        }

        /// <summary>
        /// Locates the display INF inside an extracted package. Modern NVIDIA
        /// packages ship <c>nv_disp.inf</c>; some mobile/OEM variants use
        /// <c>nv_dispi.inf</c>.
        /// </summary>
        internal static string? FindDisplayInf(string extractedDir)
        {
            if (!Directory.Exists(extractedDir)) return null;

            var displayDir = Path.Combine(extractedDir, "Display.Driver");
            foreach (var name in new[] { "nv_disp.inf", "nv_dispi.inf" })
            {
                var candidate = Path.Combine(displayDir, name);
                if (File.Exists(candidate)) return candidate;
            }

            return new[] { "nv_disp.inf", "nv_dispi.inf" }
                .Select(name => Directory.GetFiles(extractedDir, name, SearchOption.AllDirectories).FirstOrDefault())
                .FirstOrDefault(path => path != null);
        }

        /// <summary>
        /// Installs the display-only driver from an already-extracted package via
        /// pnputil. Throws <see cref="InvalidOperationException"/> with the actual
        /// pnputil output on failure so callers surface actionable errors.
        /// The app manifest requires administrator, so no extra elevation is needed.
        /// </summary>
        public async Task InstallViaPnpUtilAsync(string infPath)
        {
            _log.Info($"Installing display driver: {infPath}");

            var (_, error, exitCode) = await _processManager.RunWithOutputAndErrorAsync(
                "pnputil", $"/add-driver \"{infPath}\" /install", TimeSpan.FromMinutes(5));

            if (exitCode == 0)
            {
                _log.Success("Display driver installed successfully via pnputil");
                return;
            }

            string detail = string.IsNullOrWhiteSpace(error)
                ? $"pnputil exit code {exitCode}"
                : error.Trim();
            _log.Error($"pnputil failed: {detail}");
            throw new InvalidOperationException(
                $"pnputil failed to install the display driver (exit code {exitCode}).\n\n{detail}");
        }

        /// <summary>
        /// Locates the display INF via the supplied finder function, then installs
        /// it with pnputil. Retries once after a short delay on transient failures
        /// (driver-store races during device re-enumeration).
        /// </summary>
        private async Task InstallViaPnpUtilWithRetryAsync(
            string extractedDir,
            Func<string, string?> infFinder,
            string vendor)
        {
            _log.Info($"Locating {vendor} display driver INF...");

            var infPath = infFinder(extractedDir);
            if (infPath is null)
            {
                throw new InvalidOperationException(
                    $"No display INF found in extracted {vendor} package. " +
                    "The extraction may have produced an unexpected folder layout.");
            }

            try
            {
                await InstallViaPnpUtilAsync(infPath);
            }
            catch (InvalidOperationException ex) when (!ex.Message.Contains("No display INF"))
            {
                _log.Warn($"First pnputil attempt failed — retrying in 3 seconds: {ex.Message}");
                await Task.Delay(3000);
                await InstallViaPnpUtilAsync(infPath);
            }
        }

        /// <summary>
        /// Extracts the NVIDIA driver package silently, strips it down to the
        /// display driver, installs via pnputil (display-only), then runs
        /// clean-install-grade cleanup. Cancellation is honored up to the point
        /// extraction begins; after that the run runs to completion to avoid
        /// leaving the driver store half-installed.
        /// </summary>
        public async Task<bool> InstallNvidiaViaExtractionAsync(
            string driverExePath,
            string extractDir,
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            _log.Info("Extracting NVIDIA driver package silently...");
            status?.Report("Extracting driver package silently...");

            cancellationToken.ThrowIfCancellationRequested();
            string? extractor = await GetSilentExtractorAsync(cancellationToken);
            if (extractor is null)
            {
                _log.Error("No silent archive extractor available (7zr download failed and 7-Zip is not installed). " +
                           "Refusing to launch the interactive installer.");
                status?.Report("Installation failed — no silent extractor available.");
                return false;
            }

            bool extracted = await TryExtractSelectiveAsync(extractor, driverExePath, extractDir);
            if (!extracted)
            {
                _log.Info("Selective extraction produced no display INF — falling back to full extraction.");
                extracted = await TryExtractAsync(extractor, driverExePath, extractDir);
            }
            if (!extracted)
            {
                _log.Error("Failed to extract driver package");
                status?.Report("Installation failed — package could not be extracted.");
                return false;
            }

            status?.Report("Stripping optional components from the package…");
            StripPackageContents(extractDir);

            status?.Report("Installing display driver (display-only)...");
            await InstallViaPnpUtilWithRetryAsync(extractDir, FindDisplayInf, "NVIDIA");

            status?.Report("Removing NVIDIA telemetry and update tasks...");
            await DebloatNvidiaAsync();

            status?.Report("Removing older NVIDIA driver packages (clean install)…");
            await RemovePreviousNvidiaPackagesAsync();

            status?.Report("Driver installed — stripped and debloated.");
            return true;
        }

        // ── AMD silent pipeline ──────────────────────────────────────────

        /// <summary>
        /// Locates the display INF inside an extracted AMD Adrenalin package.
        /// AMD uses <c>Packages\Drivers\Display\**\*.inf</c> in recent releases.
        /// </summary>
        internal static string? FindAmdDisplayInf(string extractedDir)
        {
            if (!Directory.Exists(extractedDir)) return null;

            // Modern Adrenalin layout: Packages/Drivers/Display/<arch>/<infname>.inf
            var displayDir = Path.Combine(extractedDir, "Packages", "Drivers", "Display");
            if (Directory.Exists(displayDir))
            {
                var inf = Directory.GetFiles(displayDir, "*.inf", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (inf != null) return inf;
            }

            // Some older packages put the INF at Drivers/Display directly
            var altDir = Path.Combine(extractedDir, "Drivers", "Display");
            if (Directory.Exists(altDir))
            {
                var inf = Directory.GetFiles(altDir, "*.inf", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (inf != null) return inf;
            }

            // Last resort: any .inf whose name contains "display" or starts with "u0"
            // (AMD uses u0XXXXXX.inf naming for their display drivers)
            return Directory.GetFiles(extractedDir, "*.inf", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                {
                    var name = Path.GetFileName(f);
                    return name.StartsWith("u0", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("display", StringComparison.OrdinalIgnoreCase);
                });
        }

        /// <summary>
        /// Strips the extracted AMD package down to display driver essentials.
        /// AMD Adrenalin packages contain CNext (Radeon Software UI), Branding,
        /// HALS, audio drivers, and more — only the display driver content is kept.
        /// </summary>
        internal void StripAmdPackageContents(string extractDir)
        {
            if (!Directory.Exists(extractDir)) return;

            // AMD allowlist: directories and root files required for display-only install
            var allowedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Packages",   // contains Drivers/Display; we prune inside below
                "Drivers",    // alternate layout
                "Config",     // installer config metadata
            };

            var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Setup.exe", "InstallManagerApp.exe", "AMDCleanupUtility.exe",
                "Bin64",  // kept for the top-level reference
            };

            foreach (var entry in new DirectoryInfo(extractDir).EnumerateFileSystemInfos().ToArray())
            {
                if (allowedDirs.Contains(entry.Name) || allowedFiles.Contains(entry.Name))
                    continue;

                try
                {
                    DeleteRecursive(entry.FullName);
                    _log.Info($"Stripped '{entry.Name}' from AMD package");
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not strip '{entry.Name}': {ex.Message}");
                }
            }

            // Inside Packages, keep only Drivers/Display; strip everything else
            var packagesDir = Path.Combine(extractDir, "Packages");
            if (Directory.Exists(packagesDir))
            {
                foreach (var entry in new DirectoryInfo(packagesDir).EnumerateFileSystemInfos().ToArray())
                {
                    if (string.Equals(entry.Name, "Drivers", StringComparison.OrdinalIgnoreCase))
                    {
                        // Inside Drivers, keep only Display
                        var driversDir = Path.Combine(packagesDir, "Drivers");
                        if (Directory.Exists(driversDir))
                        {
                            foreach (var driverEntry in new DirectoryInfo(driversDir).EnumerateFileSystemInfos().ToArray())
                            {
                                if (string.Equals(driverEntry.Name, "Display", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                try
                                {
                                    DeleteRecursive(driverEntry.FullName);
                                    _log.Info($"Stripped AMD Drivers/{driverEntry.Name}");
                                }
                                catch (Exception ex)
                                {
                                    _log.Warn($"Could not strip AMD Drivers/{driverEntry.Name}: {ex.Message}");
                                }
                            }
                        }
                        continue;
                    }

                    try
                    {
                        DeleteRecursive(entry.FullName);
                        _log.Info($"Stripped AMD Packages/{entry.Name}");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Could not strip AMD Packages/{entry.Name}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Extracts an AMD Adrenalin package silently, strips to display-only,
        /// installs via pnputil, then runs AMD-specific debloat.
        /// </summary>
        public async Task<bool> InstallAmdViaExtractionAsync(
            string driverExePath,
            string extractDir,
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            _log.Info("Extracting AMD driver package silently...");
            status?.Report("Extracting AMD driver package silently...");

            cancellationToken.ThrowIfCancellationRequested();
            string? extractor = await GetSilentExtractorAsync(cancellationToken);
            if (extractor is null)
            {
                _log.Error("No silent archive extractor available for AMD package.");
                status?.Report("Installation failed — no silent extractor available.");
                return false;
            }

            bool extracted = await TryExtractSelectiveAsync(extractor, driverExePath, extractDir);
            if (!extracted)
            {
                _log.Info("Selective extraction produced no display INF — trying full extraction...");
                extracted = await TryExtractAsync(extractor, driverExePath, extractDir);
            }
            if (!extracted)
            {
                // AMD packages are EXE self-extractors; try extracting with the
                // package itself using its silent extraction flag.
                _log.Info("7z extraction failed; trying AMD self-extractor...");
                extracted = await TryAmdSelfExtractAsync(driverExePath, extractDir);
            }

            if (!extracted)
            {
                _log.Error("Failed to extract AMD driver package");
                status?.Report("Installation failed — package could not be extracted.");
                return false;
            }

            status?.Report("Stripping AMD package to display driver only…");
            StripAmdPackageContents(extractDir);

            status?.Report("Installing AMD display driver (display-only)...");
            await InstallViaPnpUtilWithRetryAsync(extractDir, FindAmdDisplayInf, "AMD");

            status?.Report("Removing AMD telemetry and Radeon Software services...");
            await DebloatAmdAsync();

            status?.Report("Driver installed — stripped and debloated.");
            return true;
        }

        /// <summary>
        /// AMD packages are self-extracting EXEs that support <c>-install</c> but
        /// also support <c>-extract &lt;path&gt;</c> for silent extraction.
        /// </summary>
        private async Task<bool> TryAmdSelfExtractAsync(string exePath, string extractDir)
        {
            try
            {
                Directory.CreateDirectory(extractDir);
                int exit = await _processManager.RunAsync(exePath,
                    $"-extract \"{extractDir}\"", TimeSpan.FromMinutes(15));
                if (exit != 0)
                {
                    _log.Warn($"AMD self-extractor returned exit code {exit}.");
                    return false;
                }
                return FindAmdDisplayInf(extractDir) != null;
            }
            catch (Exception ex)
            {
                _log.Warn($"AMD self-extraction failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns a silent 7-Zip extractor, or null when none is available. The
        /// bundled 7zr.exe is preferred; the system-installed 7-Zip is fallback.
        /// setup.exe is deliberately never used for extraction: on modern NVIDIA
        /// packages its SFX shell ignores -x and pops the interactive
        /// "Specify the folder where the driver files are to be saved" wizard.
        /// </summary>
        private async Task<string?> GetSilentExtractorAsync(CancellationToken cancellationToken)
        {
            if (IsValidSevenZipRunner(SevenZipRunnerPath))
                return SevenZipRunnerPath;

            try
            {
                _log.Info("Downloading 7-Zip standalone runner (7zr.exe)...");
                string tempPath = SevenZipRunnerPath + ".tmp";
                await _downloadService.DownloadAsync(SevenZipRunnerUrl, tempPath, cancellationToken: cancellationToken);
                if (IsValidSevenZipRunner(tempPath))
                {
                    File.Move(tempPath, SevenZipRunnerPath, overwrite: true);
                    _log.Success($"Bundled 7zr.exe ready at {SevenZipRunnerPath}");
                    return SevenZipRunnerPath;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not download 7zr.exe: {ex.Message}");
            }

            // Fall back only when a real system-installed 7-Zip CLI is present.
            // Returning a bare command name here made a missing executable look
            // like a corrupt NVIDIA package in the UI.
            foreach (var candidate in new[] { "7z.exe", "7zz.exe" })
            {
                if (IsCommandAvailable(candidate))
                    return candidate;
            }

            return null;
        }

        private static bool IsValidSevenZipRunner(string path)
            => File.Exists(path) && new FileInfo(path).Length > SevenZipRunnerMinBytes;

        private static bool IsCommandAvailable(string command)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = "-h",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(startInfo);
                if (process is null) return false;
                process.WaitForExit(5000);
                return process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Patterns extracted from a driver package before any stripping. Modern
        /// NVIDIA packages ship the display driver in a root <c>Display.Driver</c>
        /// folder (some also nest a <c>Display.Driver\Display.Driver</c>
        /// subfolder, covered by the recursive <c>\*</c> mask); AMD uses
        /// <c>Packages\Drivers\Display</c>, with older layouts at
        /// <c>Drivers\Display</c>. <c>NVI2</c> and the root config files are kept
        /// because pnputil may reference them for catalog validation.
        /// </summary>
        internal static readonly string[] SelectiveExtractIncludes =
        {
            "Display.Driver\\*",
            "NVI2\\*",
            "Packages\\Drivers\\Display\\*",
            "Drivers\\Display\\*",
            "setup.cfg",
            "setup.exe",
            "ListDevices.txt",
        };

        /// <summary>
        /// Extracts only the display-driver payload (plus installer metadata)
        /// instead of unpacking the entire multi-gigabyte package, so peak
        /// install storage drops from ~3&nbsp;GB to roughly download + display
        /// driver (~1.5&nbsp;GB). Returns true only when a display INF actually
        /// landed, so a package with an unexpected layout falls back to a full
        /// extraction (7-Zip exits 0 even when no mask matches — the INF check
        /// is the real success signal).
        /// </summary>
        private async Task<bool> TryExtractSelectiveAsync(string extractor, string driverExePath, string extractDir)
        {
            try
            {
                Directory.CreateDirectory(extractDir);
                string includeArgs = string.Join(" ", SelectiveExtractIncludes.Select(p => $"\"{p}\""));
                int exit = await _processManager.RunAsync(extractor,
                    $"x \"{driverExePath}\" -o\"{extractDir}\" -y {includeArgs}", TimeSpan.FromMinutes(10));
                if (exit != 0)
                {
                    _log.Warn($"Selective extractor '{extractor}' returned exit code {exit}.");
                    return false;
                }

                return FindDisplayInf(extractDir) != null || FindAmdDisplayInf(extractDir) != null;
            }
            catch (Exception ex)
            {
                _log.Warn($"Selective extraction with {extractor} failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TryExtractAsync(string extractor, string driverExePath, string extractDir)
        {
            try
            {
                Directory.CreateDirectory(extractDir);
                int exit = await _processManager.RunAsync(extractor,
                    $"x \"{driverExePath}\" -o\"{extractDir}\" -y", TimeSpan.FromMinutes(10));
                if (exit != 0)
                {
                    _log.Warn($"Extractor '{extractor}' returned exit code {exit}.");
                    return false;
                }

                return FindDisplayInf(extractDir) != null;
            }
            catch (Exception ex)
            {
                _log.Warn($"Extraction with {extractor} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Strips the extracted NVIDIA package to display-driver essentials using
        /// an allowlist. Kept entries:
        /// <list type="bullet">
        ///   <item><c>Display.Driver</c> — the signed display driver</item>
        ///   <item><c>NVI2</c> — installer metadata some INFs reference for catalog validation</item>
        ///   <item>Root files: <c>setup.cfg</c>, <c>setup.exe</c>, <c>ListDevices.txt</c> — pnputil may reference these</item>
        /// </list>
        /// Everything else (HD Audio, PhysX, GFE, NVIDIA App, telemetry, EULAs) is
        /// deleted so pnputil can never pull in anything beyond the display driver.
        /// </summary>
        internal void StripPackageContents(string extractDir)
        {
            if (!Directory.Exists(extractDir)) return;

            // Directories to keep — everything else is a separate optional component
            var allowedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Display.Driver",  // signed display driver
                "NVI2",            // installer metadata / catalog references
            };

            // Root files to keep — pnputil may need these for INF resolution
            var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "setup.cfg",
                "setup.exe",
                "ListDevices.txt",
            };

            foreach (var entry in new DirectoryInfo(extractDir).EnumerateFileSystemInfos().ToArray())
            {
                bool keep = entry is DirectoryInfo
                    ? allowedDirs.Contains(entry.Name)
                    : allowedFiles.Contains(entry.Name);

                if (keep) continue;

                try
                {
                    DeleteRecursive(entry.FullName);
                    _log.Info($"Stripped '{entry.Name}' from driver package");
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not strip '{entry.Name}': {ex.Message}");
                }
            }
        }

        private static void DeleteRecursive(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// Clean-install step: deletes every staged NVIDIA *display* package from
        /// the driver store except the newest one (the version just installed).
        /// Matching is locale-proof — packages are identified by the display class
        /// GUID rather than localized labels. Packages that are still in use fail
        /// to delete harmlessly and are logged.
        /// </summary>
        public async Task RemovePreviousNvidiaPackagesAsync()
        {
            const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

            _log.Info("Removing older NVIDIA display packages from the driver store...");

            var (_, output, _) = await _processManager.RunWithOutputAndErrorAsync(
                "pnputil", "/enum-drivers", TimeSpan.FromMinutes(2));
            if (string.IsNullOrWhiteSpace(output))
            {
                _log.Warn("pnputil /enum-drivers produced no output; skipping old-package cleanup");
                return;
            }

            var nvidiaDisplayPackages = ParseDriverPackages(output)
                .Where(p => p.IsNvidia && string.Equals(p.ClassGuid, DisplayClassGuid, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var keep = PickNewest(nvidiaDisplayPackages);
            if (keep == null)
            {
                _log.Info("No NVIDIA display packages found in the driver store");
                return;
            }

            _log.Info($"Keeping newest NVIDIA display package: {keep.PublishedName} ({keep.OriginalName}, {string.Join(".", keep.Version)})");

            foreach (var package in nvidiaDisplayPackages.Where(p => !p.PublishedName.Equals(keep.PublishedName, StringComparison.OrdinalIgnoreCase)))
            {
                // No /force: a package still bound to an active device (i.e. the
                // one currently driving the GPU) refuses to leave, which is fine.
                int exitCode = await _processManager.RunAsync("pnputil",
                    $"/delete-driver {package.PublishedName}", TimeSpan.FromSeconds(60));

                if (exitCode == 0)
                {
                    _log.Success($"Removed old driver package {package.PublishedName} ({package.OriginalName}, {string.Join(".", package.Version)})");
                }
                else
                {
                    _log.Warn($"Kept {package.PublishedName} — in use or undeletable (code {exitCode})");
                }
            }
        }

        /// <summary>The highest-versioned package; ties broken arbitrarily but deterministically.</summary>
        internal static ParsedDriverPackage? PickNewest(IReadOnlyList<ParsedDriverPackage> packages)
        {
            ParsedDriverPackage? best = null;
            foreach (var package in packages)
            {
                if (best == null || CompareVersions(package.Version, best.Version) > 0)
                {
                    best = package;
                }
            }
            return best;
        }

        private static int CompareVersions(int[] left, int[] right)
        {
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                int l = i < left.Length ? left[i] : 0;
                int r = i < right.Length ? right[i] : 0;
                if (l != r) return l.CompareTo(r);
            }
            return 0;
        }

        /// <summary>
        /// Parses <c>pnputil /enum-drivers</c> output into structured blocks.
        /// Deliberately label-agnostic so non-English Windows parses identically:
        /// blocks are split on blank lines and fields are recognized by shape —
        /// published names match <c>oem&amp;&lt;n&gt;.inf</c>, the original name is the
        /// first non-published .inf token, the class GUID is the braced GUID, and
        /// the driver version is the last dotted four-group number in the block
        /// (skipping dot-formatted dates like German locales print). The NVIDIA
        /// flag is a simple text hit on the provider value.
        /// </summary>
        internal static List<ParsedDriverPackage> ParseDriverPackages(string pnputilOutput)
        {
            var packages = new List<ParsedDriverPackage>();
            var infToken = new Regex(@"\b[A-Za-z][A-Za-z0-9_\-]*\.inf\b", RegexOptions.Compiled);
            var publishedToken = new Regex(@"\boem\d+\.inf\b", RegexOptions.Compiled);
            var guidToken = new Regex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.Compiled);
            var versionToken = new Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,6}\.\d{1,6}\b", RegexOptions.Compiled);

            foreach (var block in Regex.Split(pnputilOutput.Replace("\r\n", "\n"), @"\n\s*\n"))
            {
                if (string.IsNullOrWhiteSpace(block)) continue;

                string? published = publishedToken.Match(block) switch
                {
                    Match m when m.Success => m.Value,
                    _ => null,
                };
                if (published == null) continue;

                string original = infToken.Matches(block)
                    .Select(m => m.Value)
                    .FirstOrDefault(name => !publishedToken.IsMatch(name)) ?? "";

                string guid = guidToken.Match(block) switch
                {
                    Match m when m.Success => m.Value.ToLowerInvariant(),
                    _ => "",
                };

                int[] version = versionToken.Matches(block) is { Count: > 0 } matches
                    ? matches.Cast<Match>().Last().Value.Split('.').Select(int.Parse).ToArray()
                    : Array.Empty<int>();

                bool isNvidia = block.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

                packages.Add(new ParsedDriverPackage(published, original, guid, version, isNvidia));
            }

            return packages;
        }

        /// <summary>
        /// Removes NVIDIA bloat that a prior full driver install may have left:
        /// telemetry/container/tracker services are disabled, NVIDIA scheduled
        /// tasks are deleted, and leftover component folders (telemetry hosts,
        /// DisplayDriverRAS, GeForce Experience backends, installer caches) are
        /// purged — the same sweep NVIDIA's "clean installation" performs. A
        /// display-only pnputil install never creates these itself.
        /// </summary>
        public async Task DebloatNvidiaAsync()
        {
            _log.Info("Debloating NVIDIA: disabling telemetry/container/tracker services, tasks, and stale folders...");

            string[] servicesToDisable =
            {
                "NvTelemetryContainer",          // NVIDIA Telemetry Container
                "NvContainerLocalSystem",        // NVIDIA LocalSystem Container
                "NvContainerNetworkService",     // NVIDIA NetworkService Container
                "NVDisplay.ContainerLocalSystem",// NVIDIA Display Container (telemetry / update checks)
                "NvModuleTracker"                // NVIDIA Module Tracker (driver usage tracking)
            };

            foreach (var service in servicesToDisable)
            {
                try
                {
                    // `sc config` fails with 1060 when the service does not exist.
                    int code = await _processManager.RunAsync("sc", $"config \"{service}\" start= disabled",
                        TimeSpan.FromSeconds(30));
                    if (code == 0)
                    {
                        await _processManager.RunAsync("sc", $"stop \"{service}\"", TimeSpan.FromSeconds(30));
                        _log.Success($"Disabled service: {service}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not disable service {service}: {ex.Message}");
                }
            }

            try
            {
                const string command =
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ScheduledTask | " +
                    "Where-Object { $_.TaskName -like 'Nv*' -or $_.TaskName -like 'NVIDIA*' } | " +
                    "Unregister-ScheduledTask -Confirm:$false\"";
                int exit = await _processManager.RunAsync("powershell", command, TimeSpan.FromMinutes(2));
                if (exit == 0)
                    _log.Success("Removed NVIDIA scheduled tasks");
                else
                    _log.Warn($"NVIDIA scheduled-task removal returned code {exit}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not remove NVIDIA scheduled tasks: {ex.Message}");
            }

            // Purge component folders a previous full install may have dropped.
            // The NVIDIA Control Panel + driver service folders are left alone —
            // only telemetry/backends/installer caches go.
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string[] staleFolders =
            {
                Path.Combine(programFiles, "NVIDIA Corporation", "NvTelemetry"),
                Path.Combine(programFiles, "NVIDIA Corporation", "DisplayDriverRAS"),
                Path.Combine(programFiles, "NVIDIA Corporation", "NvBackend"),
                Path.Combine(programFiles, "NVIDIA Corporation", "Installer2"),
                Path.Combine(programFiles, "NVIDIA Corporation", "Updater"),
                Path.Combine(programData, "NVIDIA Corporation", "NvBackend"),
                Path.Combine(programData, "NVIDIA Corporation", "NVSMI"),
                Path.Combine(programData, "NVIDIA", "NvBackend")
            };

            foreach (var folder in staleFolders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    Directory.Delete(folder, recursive: true);
                    _log.Success($"Removed leftover folder: {folder}");
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not remove {folder}: {ex.Message}");
                }
            }

            _log.Success("NVIDIA debloat complete");
        }

        // ── AMD debloat ───────────────────────────────────────────────

        /// <summary>
        /// Removes AMD bloat: Radeon Software services (CNext/CN), RAS telemetry,
        /// scheduled tasks with AMD prefixes, and leftover component folders.
        /// </summary>
        public async Task DebloatAmdAsync()
        {
            _log.Info("Debloating AMD: disabling Radeon Software/RAS services and tasks...");

            string[] servicesToDisable =
            {
                "AMD Crash Defender Service",
                "AMD External Events Utility",
                "AMDRyzenMasterDriverV22",
            };

            foreach (var service in servicesToDisable)
            {
                try
                {
                    int code = await _processManager.RunAsync("sc", $"config \"{service}\" start= disabled",
                        TimeSpan.FromSeconds(30));
                    if (code == 0)
                    {
                        await _processManager.RunAsync("sc", $"stop \"{service}\"", TimeSpan.FromSeconds(30));
                        _log.Success($"Disabled service: {service}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not disable service {service}: {ex.Message}");
                }
            }

            try
            {
                const string command =
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ScheduledTask | " +
                    "Where-Object { $_.TaskName -like 'AMD*' -or $_.TaskName -like 'Radeon*' } | " +
                    "Unregister-ScheduledTask -Confirm:$false\"";
                await _processManager.RunAsync("powershell", command, TimeSpan.FromMinutes(2));
                _log.Success("Removed AMD scheduled tasks");
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not remove AMD scheduled tasks: {ex.Message}");
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string[] staleFolders =
            {
                Path.Combine(programFiles, "AMD", "CNext"),
                Path.Combine(programFiles, "AMD", "CIM"),
                Path.Combine(programData, "AMD", "PPC"),
                Path.Combine(programData, "AMD", "Installer"),
            };

            foreach (var folder in staleFolders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    Directory.Delete(folder, recursive: true);
                    _log.Success($"Removed leftover folder: {folder}");
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not remove {folder}: {ex.Message}");
                }
            }

            _log.Success("AMD debloat complete");
        }

        /// <summary>
        /// Deletes the extracted driver folder, retrying a few times because
        /// pnputil or real-time scanning can briefly lock the freshly installed
        /// INF/driver files. Failure is logged (not swallowed) so leftover
        /// multi-gigabyte folders are never silently left behind.
        /// </summary>
        public void CleanupExtracted(string extractDir)
        {
            if (!Directory.Exists(extractDir)) return;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        catch { }
                    }
                    foreach (var dir in Directory.GetDirectories(extractDir, "*", SearchOption.AllDirectories))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }

                    try { Directory.Delete(extractDir, true); } catch { }

                    if (!Directory.Exists(extractDir))
                    {
                        _log.Info("Cleaned up extracted driver files");
                        return;
                    }
                }
                catch { }

                if (attempt < 3) Thread.Sleep(1000);
            }

            _log.Warn($"Could not fully clean extracted driver folder: {extractDir} — it will be removed on the next app start.");
        }
    }
}
