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
    /// Optional NVIDIA components to KEEP from a driver package during install,
    /// mirroring NVCleanstall's "Select Components To Install" list. When null,
    /// the package is stripped to the display driver only. The display driver
    /// itself is always installed regardless of these flags.
    /// </summary>
    public sealed record NvidiaInstallComponents
    {
        public bool KeepHDAudio { get; init; }
        public bool KeepPhysX { get; init; }
        public bool KeepNvidiaApp { get; init; }
        public bool KeepUSBC { get; init; }
        public bool KeepTelemetry { get; init; }

        public bool KeepMsvcRuntimes { get; init; }
        public bool KeepFrameViewSdk { get; init; }
        public bool KeepVirtualAudio { get; init; }
        public bool KeepNvPlatformControllers { get; init; }
        public bool KeepDlsr { get; init; }

        // NV App Components group (sub-stripped inside the kept app folder).
        public bool KeepNvContainer { get; init; }
        public bool KeepShadowPlay { get; init; }
        public bool KeepNvBackend { get; init; }
        public bool KeepNvidiaAppMessageBus { get; init; }

        /// <summary>Any kept component that depends on the NV Container services.</summary>
        public bool KeepsAnyContainerUser =>
            KeepNvidiaApp || KeepNvContainer || KeepShadowPlay ||
            KeepNvBackend || KeepNvidiaAppMessageBus;

        public static NvidiaInstallComponents DisplayOnly => new();
    }

    /// <summary>
    /// Optional AMD components to KEEP from an Adrenalin package during install,
    /// mirroring the edit tool's Radeon Software Slimmer options. When null, the
    /// package is stripped to the display driver only. The display driver itself
    /// is always installed regardless of these flags.
    /// </summary>
    public sealed record AmdInstallComponents
    {
        /// <summary>Keep the AMD Software: Adrenalin Edition UI (CNext) package.</summary>
        public bool KeepRadeonSoftware { get; init; }

        /// <summary>Keep the HDMI/DisplayPort audio driver package.</summary>
        public bool KeepAudio { get; init; }

        /// <summary>Keep the AMD User Experience Program / crash-reporting packages.</summary>
        public bool KeepTelemetry { get; init; }

        /// <summary>Keep AMD scheduled tasks instead of removing them during debloat.</summary>
        public bool KeepScheduledTasks { get; init; }

        public static AmdInstallComponents DisplayOnly => new();
    }

    /// <summary>
    /// Optional post-install NVIDIA tweaks, ported from NovaOS's
    /// "Disable Nvidia Telemetry" script (registry telemetry opt-outs,
    /// NvCamera removal, task disabling, NvBackend startup removal, and
    /// telemetry/camera file sweeps). Applied after the driver install when
    /// the user opts in on the tweaks dialog.
    /// </summary>
    public sealed record NvInstallTweaks
    {
        /// <summary>Registry telemetry opt-outs: SendTelemetryData=0, FTS RID flags=0, Control Panel opt-out, NvCamera key deleted.</summary>
        public bool DisableDriverTelemetry { get; init; }

        /// <summary>Uninstall Display.3DVision / Display.Audio / Ansel leftovers via Installer2 (best-effort; fresh installs already strip them).</summary>
        public bool UninstallVisionAndAnsel { get; init; }

        /// <summary>Disable NVIDIA telemetry/update scheduled tasks (NvTm*, NvProfile, NvNodeLauncher, driver-update checks…).</summary>
        public bool DisableNvidiaTasks { get; init; }

        /// <summary>Delete the NvBackend Run-key autostart entry.</summary>
        public bool RemoveNvBackendStartup { get; init; }

        /// <summary>Delete telemetry/camera files: NvTelemetry64.dll, NvCamera folders, DisplayDriverRAS plugin, System32\drivers\NVIDIA Corporation.</summary>
        public bool DeleteTelemetryFiles { get; init; }

        public bool IsDefault =>
            !DisableDriverTelemetry && !UninstallVisionAndAnsel && !DisableNvidiaTasks &&
            !RemoveNvBackendStartup && !DeleteTelemetryFiles;

        public static NvInstallTweaks None => new();
    }

    /// <summary>
    /// Silent driver installation backend. NVIDIA packages are extracted with a
    /// standalone 7-Zip runner (never the interactive setup.exe wizard), stripped
    /// of every optional sub-package the user did not ask to keep, and only the
    /// display INF is installed via pnputil. Optional components (GeForce
    /// Experience, NVIDIA App, HD Audio bundle, PhysX) are kept only when the user
    /// selects them via <see cref="NvidiaInstallComponents"/>. After the install,
    /// NVIDIA-clean-install-grade cleanup runs: container/tracker services are
    /// disabled, scheduled tasks removed, leftover folders purged, and all older
    /// NVIDIA display driver-store packages deleted (only the freshly installed
    /// one is kept).
    /// </summary>
    public class DriverInstallService
    {
        private readonly LoggingService _log;
        private readonly ProcessManager _processManager;
        private readonly DriverDownloadService _downloadService;
        private readonly RadeonSlimmerService _slimmer;

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

        public DriverInstallService(LoggingService log, ProcessManager processManager, DriverDownloadService downloadService, RadeonSlimmerService? slimmer = null)
        {
            _log = log;
            _processManager = processManager;
            _downloadService = downloadService;
            _slimmer = slimmer ?? new RadeonSlimmerService();
            Directory.CreateDirectory(ToolsDir);
        }

        /// <summary>
        /// Locates the display INF inside an extracted package. Modern NVIDIA
        /// packages ship <c>nv_disp.inf</c>; some mobile/OEM/hybrid variants use
        /// <c>nv_dispi.inf</c>. Kept for compatibility — prefer
        /// <see cref="FindNvidiaDisplayInfs"/> when several candidates are useful.
        /// </summary>
        internal static string? FindDisplayInf(string extractedDir) =>
            FindNvidiaDisplayInfs(extractedDir).FirstOrDefault();

        /// <summary>
        /// All display INFs inside an extracted NVIDIA package, preferred first:
        /// <c>Display.Driver\nv_disp.inf</c>, then <c>nv_dispi.inf</c>, then a
        /// recursive search for either name. Trying each in turn lets pnputil pick
        /// the one that actually matches the device (e.g. hybrid systems where the
        /// Intel-flavoured nv_dispi.inf does not apply to the discrete GPU).
        /// </summary>
        internal static IEnumerable<string> FindNvidiaDisplayInfs(string extractedDir)
        {
            if (!Directory.Exists(extractedDir)) return Array.Empty<string>();

            var found = new List<string>();
            var displayDir = Path.Combine(extractedDir, "Display.Driver");
            foreach (var name in new[] { "nv_disp.inf", "nv_dispi.inf" })
            {
                var candidate = Path.Combine(displayDir, name);
                if (File.Exists(candidate)) found.Add(candidate);
            }
            foreach (var name in new[] { "nv_disp.inf", "nv_dispi.inf" })
            {
                found.AddRange(Directory.GetFiles(extractedDir, name, SearchOption.AllDirectories));
            }

            // The recursive pass re-finds the Display.Driver copies — dedupe so
            // each INF is tried exactly once, in preference order.
            return found.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Outcome of a single pnputil install attempt.</summary>
        public enum PnpUtilInstallOutcome
        {
            /// <summary>The driver was installed (or is already staged and in use).</summary>
            Installed,

            /// <summary>pnputil exit 259 (ERROR_NO_MORE_ITEMS): no device matched this INF,
            /// or the device is already using a newer/better driver. Nothing to do.</summary>
            NothingToInstall,
        }

        /// <summary>
        /// Installs the display-only driver from an already-extracted package via
        /// pnputil. Returns <see cref="PnpUtilInstallOutcome.Installed"/> on success
        /// (exit 0 or 3010 — reboot required is still a success), and
        /// <see cref="PnpUtilInstallOutcome.NothingToInstall"/> for exit 259, which
        /// Microsoft documents as "no devices match the supplied driver or the target
        /// device is already using a better or newer driver". Any other exit code
        /// throws <see cref="InvalidOperationException"/> with the actual pnputil
        /// output (stdout + stderr) so callers surface actionable errors.
        /// The app manifest requires administrator, so no extra elevation is needed.
        /// </summary>
        public async Task<PnpUtilInstallOutcome> InstallViaPnpUtilAsync(string infPath)
        {
            _log.Info($"Installing display driver: {infPath}");

            var (output, error, exitCode) = await _processManager.RunWithOutputAndErrorAsync(
                "pnputil", $"/add-driver \"{infPath}\" /install", TimeSpan.FromMinutes(5));

            if (exitCode == 0 || exitCode == 3010) // 3010 = installed, reboot required
            {
                _log.Success("Display driver installed successfully via pnputil");
                return PnpUtilInstallOutcome.Installed;
            }

            // pnputil writes its status text to stdout, so surface that first;
            // stderr is a fallback when stdout is empty.
            string detail = string.IsNullOrWhiteSpace(output)
                ? error.Trim()
                : output.Trim();

            if (exitCode == 259)
            {
                _log.Info($"pnputil: nothing to install for {Path.GetFileName(infPath)} (exit 259): {detail}");
                return PnpUtilInstallOutcome.NothingToInstall;
            }

            string failureDetail = string.IsNullOrWhiteSpace(detail)
                ? $"pnputil exit code {exitCode}"
                : detail;
            _log.Error($"pnputil failed: {failureDetail}");
            throw new InvalidOperationException(
                $"pnputil failed to install the display driver (exit code {exitCode}).\n\n{failureDetail}");
        }

        /// <summary>
        /// Tries each candidate display INF (in order) until one installs. When a
        /// candidate reports "nothing to install" (exit 259 — no matching device
        /// or an already-newer driver) the next candidate is tried, so a package
        /// that ships several display INFs (e.g. NVIDIA nv_disp vs nv_dispi, or
        /// AMD Display vs Display2 for iGPU/dGPU) still lands on the right one.
        /// If every candidate reports 259 the machine's driver is already current
        /// and the step counts as done — not a failure. Hard failures are retried
        /// once after a short delay (driver-store races during re-enumeration),
        /// then thrown with the accumulated pnputil output.
        /// </summary>
        private async Task InstallViaPnpUtilCandidatesAsync(
            IEnumerable<string> infCandidates,
            string vendor)
        {
            var candidates = infCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No display INF found in extracted {vendor} package. " +
                    "The extraction may have produced an unexpected folder layout.");
            }

            _log.Info($"Locating {vendor} display driver INF ({candidates.Count} candidate(s))...");

            for (int pass = 0; pass < 2; pass++)
            {
                var failures = new List<string>();

                foreach (var inf in candidates)
                {
                    try
                    {
                        var outcome = await InstallViaPnpUtilAsync(inf);
                        if (outcome == PnpUtilInstallOutcome.Installed) return;
                    }
                    catch (InvalidOperationException ex)
                    {
                        failures.Add(ex.Message);
                        _log.Warn($"{vendor}: INF '{Path.GetFileName(inf)}' failed — {ex.Message}");
                    }
                }

                // All candidates said "nothing to install" — deterministic, so no
                // retry pass needed. The machine's display keeps working and the
                // driver is already current (or the INF simply doesn't apply).
                if (failures.Count == 0)
                {
                    _log.Info($"{vendor}: nothing to install — pnputil reported no matching device or an already-newer driver for every candidate.");
                    return;
                }

                if (pass == 0)
                {
                    _log.Warn($"{vendor}: install failed on first pass — retrying in 3 seconds...");
                    await Task.Delay(3000);
                    continue;
                }

                throw new InvalidOperationException(string.Join(" | ", failures));
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
            CancellationToken cancellationToken = default,
            NvidiaInstallComponents? components = null,
            NvInstallTweaks? tweaks = null)
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

            bool extracted = await TryExtractSelectiveAsync(extractor, driverExePath, extractDir, components);
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

            status?.Report("Stripping unselected components from the package…");
            StripPackageContents(extractDir, components);

            status?.Report("Installing display driver (display-only)...");
            await InstallViaPnpUtilCandidatesAsync(FindNvidiaDisplayInfs(extractDir), "NVIDIA");

            status?.Report("Removing NVIDIA telemetry and update tasks...");
            await DebloatNvidiaAsync(components);

            if (tweaks is { IsDefault: false })
            {
                status?.Report("Applying post-install tweaks...");
                await ApplyNvTweaksAsync(tweaks, components);
            }

            status?.Report("Removing older NVIDIA driver packages (clean install)…");
            await RemovePreviousNvidiaPackagesAsync();

            status?.Report("Driver installed — stripped and debloated.");
            return true;
        }

        // ── AMD silent pipeline ──────────────────────────────────────────

        /// <summary>
        /// Locates the display INF inside an extracted AMD Adrenalin package.
        /// AMD uses <c>Packages\Drivers\Display\**\*.inf</c> in recent releases.
        internal static readonly string[] SelectiveExtractIncludes =
        {
            "Display.Driver\\*",
            "NVI2\\*",
            "Packages\\Drivers\\Display\\*",
            "Packages\\Drivers\\Display2\\*",
            "Drivers\\Display\\*",
            "Drivers\\Display2\\*",
            "setup.cfg",
            "setup.exe",
            "ListDevices.txt",
        };

        /// <summary>
        /// Locates all display INFs inside extracted AMD packages (covering RDNA 3 in Display and RDNA 1/2 in Display2).
        /// </summary>
        internal static List<string> FindAllAmdDisplayInfs(string extractedDir)
        {
            var results = new List<string>();
            if (!Directory.Exists(extractedDir)) return results;

            // Search for primary display driver INFs (u0*.inf) in Display and Display2
            foreach (var sub in new[] { "Display2", "Display" })
            {
                var dir = Path.Combine(extractedDir, "Packages", "Drivers", sub);
                if (Directory.Exists(dir))
                {
                    var infs = Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories)
                        .Where(f => Path.GetFileName(f).StartsWith("u0", StringComparison.OrdinalIgnoreCase) ||
                                    f.Contains("WT6A_INF", StringComparison.OrdinalIgnoreCase));
                    results.AddRange(infs);
                }
            }

            // Also check alternate root Drivers/Display
            var altDir = Path.Combine(extractedDir, "Drivers");
            if (Directory.Exists(altDir))
            {
                var infs = Directory.GetFiles(altDir, "*.inf", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f).StartsWith("u0", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("WT6A_INF", StringComparison.OrdinalIgnoreCase));
                results.AddRange(infs);
            }

            if (results.Count == 0)
            {
                var allInfs = Directory.GetFiles(extractedDir, "*.inf", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f).StartsWith("u0", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("Display", StringComparison.OrdinalIgnoreCase));
                results.AddRange(allInfs);
            }

            // Prioritize primary u0*.inf drivers first so pnputil binds the GPU
            return results.Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderByDescending(f => Path.GetFileName(f).StartsWith("u0", StringComparison.OrdinalIgnoreCase))
                          .ToList();
        }

        internal static string? FindAmdDisplayInf(string extractedDir)
        {
            return FindAllAmdDisplayInfs(extractedDir).FirstOrDefault();
        }

        /// <summary>
        /// Strips the extracted AMD package down to display driver essentials.
        /// AMD Adrenalin packages contain CNext (Radeon Software UI), Branding,
        /// HALS, audio drivers, and more — only the display driver content is kept.
        /// </summary>
        internal void StripAmdPackageContents(string extractDir, AmdInstallComponents? components = null)
        {
            if (!Directory.Exists(extractDir)) return;

            var chosen = components ?? AmdInstallComponents.DisplayOnly;

            var allowedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Packages", "Drivers", "Config",
            };

            var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Setup.exe", "InstallManagerApp.exe", "AMDCleanupUtility.exe", "Bin64",
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

            // Inside Packages, keep Drivers/Display and Drivers/Display2 (plus
            // Drivers/Audio when the audio driver was kept); everything else is
            // deleted unless the user explicitly asked to keep it (Adrenalin UI,
            // telemetry, audio) — the same categories the edit tool's Radeon
            // Software Slimmer flow exposes.
            var packagesDir = Path.Combine(extractDir, "Packages");
            if (Directory.Exists(packagesDir))
            {
                foreach (var entry in new DirectoryInfo(packagesDir).EnumerateFileSystemInfos().ToArray())
                {
                    if (string.Equals(entry.Name, "Drivers", StringComparison.OrdinalIgnoreCase))
                    {
                        var driversDir = Path.Combine(packagesDir, "Drivers");
                        if (Directory.Exists(driversDir))
                        {
                            foreach (var driverEntry in new DirectoryInfo(driversDir).EnumerateFileSystemInfos().ToArray())
                            {
                                bool keep = string.Equals(driverEntry.Name, "Display", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(driverEntry.Name, "Display2", StringComparison.OrdinalIgnoreCase)
                                    || (chosen.KeepAudio && IsAmdAudioFolder(driverEntry.Name));
                                if (keep) continue;
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

                    bool keepOptional = (chosen.KeepRadeonSoftware && IsAmdUiFolder(entry.Name))
                        || (chosen.KeepTelemetry && IsAmdTelemetryFolder(entry.Name))
                        || (chosen.KeepAudio && IsAmdAudioFolder(entry.Name));
                    if (keepOptional) continue;

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

        // Folder-name heuristics match the edit tool's own detection (CNext /
        // CCC2 / Settings / Radeon Software for the UI; UEP / Experience / Crash
        // / Report for telemetry; Audio / HDMI for the audio driver).
        private static bool IsAmdUiFolder(string name) =>
            name.Contains("CNext", StringComparison.OrdinalIgnoreCase)
            || name.Contains("CCC2", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Settings", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RadeonSoftware", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Adrenalin", StringComparison.OrdinalIgnoreCase);

        private static bool IsAmdTelemetryFolder(string name) =>
            name.Contains("UEP", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Experience", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Crash", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Report", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Telemetry", StringComparison.OrdinalIgnoreCase);

        private static bool IsAmdAudioFolder(string name) =>
            name.Contains("Audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HDMI", StringComparison.OrdinalIgnoreCase);


        /// <summary>
        /// Extracts an AMD Adrenalin package silently, strips to display-only,
        /// installs via pnputil, then runs AMD-specific debloat.
        /// </summary>
        /// <summary>
        /// Robustly extracts an AMD Adrenalin installer package using 7zr (selective or full)
        /// or AMD's native self-extractor flag (-extract).
        /// </summary>
        public async Task<bool> ExtractAmdInstallerAsync(
            string driverExePath,
            string extractDir,
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            _log.Info("Extracting AMD driver package silently...");
            status?.Report("Extracting AMD driver package silently...");

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(extractDir);

            string? extractor = await GetSilentExtractorAsync(cancellationToken);
            if (extractor is null)
            {
                _log.Warn("No standalone 7zr extractor available; trying AMD native self-extractor...");
                return await TryAmdSelfExtractAsync(driverExePath, extractDir);
            }

            bool extracted = await TryExtractAsync(extractor, driverExePath, extractDir);
            if (!extracted)
            {
                _log.Info("Full 7z extraction returned non-zero; trying selective extraction...");
                extracted = await TryExtractSelectiveAsync(extractor, driverExePath, extractDir);
            }
            if (!extracted)
            {
                _log.Info("7z extraction failed; trying AMD native self-extractor...");
                extracted = await TryAmdSelfExtractAsync(driverExePath, extractDir);
            }

            if (!extracted)
            {
                _log.Error("Failed to extract AMD driver package.");
                status?.Report("Extraction failed — package could not be unpacked.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Extracts an AMD Adrenalin package silently, strips to display-only,
        /// installs via pnputil, then runs AMD-specific debloat.
        /// </summary>
        public async Task<bool> InstallAmdViaExtractionAsync(
            string driverExePath,
            string extractDir,
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default,
            AmdInstallComponents? components = null)
        {
            bool extracted = await ExtractAmdInstallerAsync(driverExePath, extractDir, status, cancellationToken);
            if (!extracted) return false;

            var chosen = components ?? AmdInstallComponents.DisplayOnly;
            status?.Report(chosen == AmdInstallComponents.DisplayOnly
                ? "Stripping AMD package to display driver only…"
                : "Stripping AMD package to the selected components…");
            StripAmdPackageContents(extractDir, chosen);

            status?.Report("Installing AMD display driver (display-only)...");
            await InstallViaPnpUtilCandidatesAsync(FindAllAmdDisplayInfs(extractDir), "AMD");

            status?.Report("Removing AMD telemetry and Radeon Software services...");
            await DebloatAmdAsync(chosen);

            await MaybeLaunchRadeonSlimmerAsync(status, cancellationToken);

            status?.Report("Driver installed — stripped and debloated.");
            return true;
        }

        /// <summary>
        /// Offers Post-Install slimming of the installed AMD components: makes
        /// sure GSDragoon's Radeon Software Slimmer is present and launches it so
        /// the user can trim the installed Radeon/AMD components interactively.
        /// Non-fatal — a download failure never fails the driver install.
        /// </summary>
        private async Task MaybeLaunchRadeonSlimmerAsync(IProgress<string>? status, CancellationToken cancellationToken)
        {
            try
            {
                string? exe = await _slimmer.EnsureAsync(_log, status, cancellationToken);
                if (exe != null)
                {
                    status?.Report("Opening Radeon Software Slimmer for optional Post-Install slimming…");
                    _slimmer.Launch(exe, _log);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not launch Radeon Software Slimmer: {ex.Message}");
            }
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
        public async Task<string?> GetSilentExtractorAsync(CancellationToken cancellationToken = default)
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
        /// Extracts only the display-driver payload (plus installer metadata)
        /// instead of unpacking the entire multi-gigabyte package, so peak
        /// install storage drops from ~3&nbsp;GB to roughly download + display
        /// driver (~1.5&nbsp;GB). Returns true only when a display INF actually
        /// landed, so a package with an unexpected layout falls back to a full
        /// extraction (7-Zip exits 0 even when no mask matches — the INF check
        /// is the real success signal).
        /// </summary>
        private async Task<bool> TryExtractSelectiveAsync(
            string extractor,
            string driverExePath,
            string extractDir,
            NvidiaInstallComponents? components = null)
        {
            try
            {
                Directory.CreateDirectory(extractDir);
                var includes = SelectiveExtractIncludes.ToList();
                foreach (var folder in OptionalComponentFolders(components ?? NvidiaInstallComponents.DisplayOnly))
                    includes.Add(folder + "\\*");
                string includeArgs = string.Join(" ", includes.Select(p => $"\"{p}\""));
                int exit = await _processManager.RunAsync(extractor,
                    $"x \"{driverExePath}\" -o\"{extractDir}\" -y {includeArgs}", TimeSpan.FromMinutes(10));
                if (exit != 0 && exit != 1)
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
                if (exit != 0 && exit != 1)
                {
                    _log.Warn($"Extractor '{extractor}' returned exit code {exit}.");
                    return false;
                }

                return FindDisplayInf(extractDir) != null || FindAmdDisplayInf(extractDir) != null;
            }
            catch (Exception ex)
            {
                _log.Warn($"Extraction with {extractor} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Strips the extracted NVIDIA package to the selected components using an
        /// allowlist. The display driver (<c>Display.Driver</c>) and catalog
        /// metadata (<c>NVI2</c>) are always kept. The caller chooses which extra
        /// components to keep; everything unselected is deleted so pnputil can
        /// never pull in anything beyond what was asked for. Passing null keeps
        /// the display driver only. The NV App Components group is additionally
        /// sub-stripped inside the kept app folder.
        /// </summary>
        internal void StripPackageContents(string extractDir, NvidiaInstallComponents? components = null)
        {
            if (!Directory.Exists(extractDir)) return;

            var chosen = components ?? NvidiaInstallComponents.DisplayOnly;

            // Directories to keep — everything else is a separate optional component
            var allowedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Display.Driver",  // signed display driver (always)
                "NVI2",            // installer metadata / catalog references (always)
            };
            foreach (var folder in OptionalComponentFolders(chosen))
                allowedDirs.Add(folder);

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

            if (chosen.KeepNvidiaApp)
                StripAppSubcomponents(extractDir, chosen);
        }

        /// <summary>
        /// The NV App Components group (NV Container, ShadowPlay, NV Backend,
        /// MessageBus) lives as subfolders inside the kept app folder rather than
        /// at the package root. Delete the unselected ones; unknown subfolders are
        /// left alone (runtimes, display helpers, localizations …).
        /// </summary>
        private static void StripAppSubcomponents(string extractDir, NvidiaInstallComponents c)
        {
            string[] appFolders = { "NVIDIA App", "NVIDIAapp", "NVApp", "GFExperience" };

            foreach (var folder in appFolders)
            {
                string appDir = Path.Combine(extractDir, folder);
                if (!Directory.Exists(appDir)) continue;

                foreach (var sub in new DirectoryInfo(appDir).EnumerateDirectories().ToArray())
                {
                    if (ShouldKeepAppSubcomponent(sub.Name, c)) continue;

                    try
                    {
                        DeleteRecursive(sub.FullName);
                    }
                    catch
                    {
                        // Best effort — an unremovable subfolder doesn't fail the install.
                    }
                }
            }
        }

        private static bool ShouldKeepAppSubcomponent(string name, NvidiaInstallComponents c)
        {
            if (name.Equals("NV Container", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NVContainer", StringComparison.OrdinalIgnoreCase)) return c.KeepNvContainer;
            if (name.Equals("ShadowPlay", StringComparison.OrdinalIgnoreCase)) return c.KeepShadowPlay;
            if (name.Equals("NV Backend", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NvBackend", StringComparison.OrdinalIgnoreCase)) return c.KeepNvBackend;
            if (name.Equals("NVIDIA App MessageBus", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("MessageBus", StringComparison.OrdinalIgnoreCase)) return c.KeepNvidiaAppMessageBus;

            return true; // unknown subfolders are not a listed component — keep
        }

        /// <summary>
        /// Top-level folder name(s) for each NVIDIA component the user chose to
        /// keep. Multiple aliases are listed because folder casing/spacing varies
        /// between driver versions (e.g. <c>HDAudio</c> vs <c>HD Audio</c>).
        /// Components with no matching folder in the current package are simply
        /// absent — the aliases are harmless in both directions.
        /// </summary>
        private static IEnumerable<string> OptionalComponentFolders(NvidiaInstallComponents c)
        {
            if (c.KeepHDAudio)
            {
                yield return "HDAudio";
                yield return "HD Audio";
            }
            if (c.KeepPhysX) yield return "PhysX";
            if (c.KeepNvidiaApp)
            {
                yield return "NVIDIA App";
                yield return "NVApp";
                yield return "NVIDIAapp";
                // Legacy packages ship GeForce Experience instead of the App.
                yield return "GFExperience";
            }
            if (c.KeepUSBC)
            {
                yield return "USB-C";
                yield return "USBC";
                yield return "USB-C Driver";
            }
            if (c.KeepTelemetry)
            {
                yield return "NvTelemetry";
                yield return "Telemetry";
            }
            if (c.KeepMsvcRuntimes)
            {
                yield return "MSVCRT";
                yield return "MSVC";
            }
            if (c.KeepFrameViewSdk)
            {
                yield return "FrameViewSDK";
                yield return "FrameView SDK";
            }
            if (c.KeepVirtualAudio)
            {
                yield return "VirtualAudio";
                yield return "NVIDIA Virtual Audio";
                yield return "NVVAD";
            }
            if (c.KeepNvPlatformControllers)
            {
                yield return "NVPlatformControllers";
                yield return "NV Platform Controllers";
            }
            if (c.KeepDlsr)
            {
                yield return "NVIDIA DLSR";
                yield return "DLSR";
            }
            // NV Container / ShadowPlay / NV Backend / MessageBus are sub-stripped
            // inside the kept app folder — see StripAppSubcomponents.
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
        public async Task DebloatNvidiaAsync(NvidiaInstallComponents? components = null)
        {
            var chosen = components ?? NvidiaInstallComponents.DisplayOnly;
            _log.Info("Debloating NVIDIA: disabling telemetry/container/tracker services, tasks, and stale folders...");

            // Services the user explicitly kept (NV App group) must not be disabled.
            var servicesToDisable = new List<string>
            {
                "NvModuleTracker"                // NVIDIA Module Tracker (driver usage tracking)
            };
            if (!chosen.KeepTelemetry)
                servicesToDisable.Add("NvTelemetryContainer");  // NVIDIA Telemetry Container
            if (!chosen.KeepsAnyContainerUser)
            {
                servicesToDisable.Add("NvContainerLocalSystem");        // NVIDIA LocalSystem Container
                servicesToDisable.Add("NvContainerNetworkService");     // NVIDIA NetworkService Container
                servicesToDisable.Add("NVDisplay.ContainerLocalSystem");// NVIDIA Display Container
            }

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
                // Telemetry tasks always go unless the user kept telemetry; other
                // NVIDIA tasks are only removed when nothing from the NV App group
                // was kept (their update/helper tasks must survive for those).
                string taskFilter = chosen.KeepsAnyContainerUser
                    ? "($_.TaskName -like 'NvTelemetry*' -or $_.TaskName -like '*Telemetry*')"
                    : "($_.TaskName -like 'Nv*' -or $_.TaskName -like 'NVIDIA*')";
                string command =
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"Get-ScheduledTask | " +
                    $"Where-Object {{ {taskFilter} }} | " +
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
            // only telemetry/backends/installer caches go. NvBackend is skipped
            // when the user explicitly kept it.
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var staleFolders = new List<string>
            {
                Path.Combine(programFiles, "NVIDIA Corporation", "DisplayDriverRAS"),
                Path.Combine(programFiles, "NVIDIA Corporation", "Installer2"),
                Path.Combine(programFiles, "NVIDIA Corporation", "Updater"),
                Path.Combine(programData, "NVIDIA Corporation", "NVSMI"),
            };
            if (!chosen.KeepTelemetry)
                staleFolders.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NvTelemetry"));
            if (!chosen.KeepNvBackend)
            {
                staleFolders.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NvBackend"));
                staleFolders.Add(Path.Combine(programData, "NVIDIA Corporation", "NvBackend"));
                staleFolders.Add(Path.Combine(programData, "NVIDIA", "NvBackend"));
            }

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

        // ── NVIDIA post-install tweaks (ported from NovaOS's "Disable Nvidia Telemetry" script) ──

        /// <summary>
        /// Applies the user-selected NovaOS-sourced tweaks after the driver
        /// install. Every step is best-effort: a missing key/task/file is
        /// logged and skipped, a real failure warns but never aborts the
        /// install (the driver itself is already in place). Steps that could
        /// break kept NV App components are guarded by the component choices.
        /// </summary>
        public async Task ApplyNvTweaksAsync(NvInstallTweaks tweaks, NvidiaInstallComponents? components = null)
        {
            var chosen = components ?? NvidiaInstallComponents.DisplayOnly;
            _log.Info("Applying NVIDIA post-install tweaks (NovaOS debloat)...");

            if (tweaks.DisableDriverTelemetry)
            {
                await SetRegDwordAsync(@"SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\Startup", "SendTelemetryData", 0);
                await SetRegDwordAsync(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID44231", 0);
                await SetRegDwordAsync(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID64640", 0);
                await SetRegDwordAsync(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID66610", 0);
                await SetRegDwordAsync(@"SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", "OptInOrOutPreference", 0);

                try
                {
                    const string cameraPath = @"SYSTEM\CurrentControlSet\Services\nvlddmkm\NvCamera";
                    using var probe = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(cameraPath);
                    if (probe != null)
                    {
                        probe.Close();
                        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(cameraPath, throwOnMissingSubKey: false);
                        _log.Success("Removed nvlddmkm\\NvCamera registry key");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not remove nvlddmkm\\NvCamera key: {ex.Message}");
                }

                // NvTelemetryContainer disable/stop is already part of the debloat
                // unless the user kept telemetry — the registry writes above are the
                // driver-side opt-out that debloat alone cannot cover.
            }

            if (tweaks.UninstallVisionAndAnsel)
            {
                // Fresh installs already strip 3D Vision / Ansel from the package,
                // so this only matters for leftovers from a previous full install.
                // Installer2 may have been purged by the debloat — run only when present.
                string nvi2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation", "Installer2", "InstallerCore", "NVI2.dll");
                if (File.Exists(nvi2))
                {
                    foreach (var package in new[] { "Display.3DVision", "Display.Audio", "Ansel" })
                    {
                        try
                        {
                            int code = await _processManager.RunAsync("rundll32.exe",
                                $"\"{nvi2}\",UninstallPackage {package}", TimeSpan.FromMinutes(2));
                            if (code == 0)
                                _log.Success($"Uninstalled NVIDIA package: {package}");
                            else
                                _log.Info($"NVIDIA package {package} not installed (exit {code}) — skipped");
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"Could not uninstall NVIDIA package {package}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    _log.Info("Installer2 not present — 3D Vision/Ansel already stripped on this install");
                }
            }

            if (tweaks.DisableNvidiaTasks)
            {
                // The debloat unregisters Nv*/NVIDIA* tasks, but only when nothing
                // from the NV App group was kept. These named disables cover the
                // update/telemetry tasks that must survive that filter, using the
                // same names as the NovaOS script (GUID-suffixed crash-report tasks too).
                var taskNames = new List<string>
                {
                    "NvTmMon", "NvTmRep", "NvProfile", "NvNodeLauncher",
                    "NvDriverUpdateCheckDaily", "NvBatteryBoostCheckOnLogon",
                    "NVIDIA GeForce Experience SelfUpdate",
                };
                for (int i = 1; i <= 4; i++)
                    taskNames.Add($"NvTmRep_CrashReport{i}_{{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}}");

                foreach (var name in taskNames)
                {
                    try
                    {
                        int code = await _processManager.RunAsync("schtasks",
                            $"/Change /TN \"{name}\" /Disable", TimeSpan.FromSeconds(30));
                        if (code == 0)
                            _log.Success($"Disabled scheduled task: {name}");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Could not disable task {name}: {ex.Message}");
                    }
                }
            }

            if (tweaks.RemoveNvBackendStartup && !chosen.KeepNvBackend)
            {
                try
                {
                    using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    if (runKey?.GetValue("NvBackend") != null)
                    {
                        runKey.DeleteValue("NvBackend", throwOnMissingValue: false);
                        _log.Success("Removed NvBackend autostart entry");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not remove NvBackend autostart: {ex.Message}");
                }
            }

            if (tweaks.DeleteTelemetryFiles)
            {
                string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string driverStore = Path.Combine(systemRoot, "DriverStore", "FileRepository");

                if (!chosen.KeepTelemetry)
                {
                    // NvTelemetry64.dll lives in every nv* driver-store package folder.
                    try
                    {
                        if (Directory.Exists(driverStore))
                        {
                            int removed = 0;
                            foreach (var dll in Directory.EnumerateFiles(driverStore, "NvTelemetry64.dll", SearchOption.AllDirectories))
                            {
                                try { File.Delete(dll); removed++; }
                                catch (Exception ex) { _log.Warn($"Could not delete {dll}: {ex.Message}"); }
                            }
                            _log.Success($"Deleted {removed} NvTelemetry64.dll file(s) from the driver store");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"NvTelemetry64.dll sweep failed: {ex.Message}");
                    }
                }

                // NvCamera (ShadowPlay capture) folders in driver-store packages.
                try
                {
                    if (Directory.Exists(driverStore))
                    {
                        int removed = 0;
                        foreach (var dir in Directory.EnumerateDirectories(driverStore, "NvCamera", SearchOption.AllDirectories))
                        {
                            try { Directory.Delete(dir, recursive: true); removed++; }
                            catch (Exception ex) { _log.Warn($"Could not delete {dir}: {ex.Message}"); }
                        }
                        if (removed > 0) _log.Success($"Deleted {removed} NvCamera folder(s) from the driver store");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"NvCamera sweep failed: {ex.Message}");
                }

                // DisplayDriverRAS telemetry plugin — only when no NV Container user was kept.
                if (!chosen.KeepsAnyContainerUser)
                {
                    string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                    TryDeleteFile(Path.Combine(programFiles, "NVIDIA Corporation", "Display.NvContainer",
                        "plugins", "LocalSystem", "_DisplayDriverRAS.dll"));
                    TryDeleteFolder(Path.Combine(programFiles, "NVIDIA Corporation",
                        "Display.NvContainer", "plugins", "LocalSystem", "DisplayDriverRAS"));
                    TryDeleteFolder(Path.Combine(programData, "NVIDIA Corporation", "DisplayDriverRAS"));

                    // The drivers\NVIDIA Corporation folder NovaOS removes wholesale.
                    TryDeleteFolder(Path.Combine(systemRoot, "drivers", "NVIDIA Corporation"));
                }
            }

            _log.Success("NVIDIA post-install tweaks complete");
        }

        private async Task SetRegDwordAsync(string keyPath, string name, int value)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
                key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
                _log.Success($"[reg-dword] HKLM\\{keyPath} \\ {name} = {value}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[reg-dword] Could not set HKLM\\{keyPath} \\ {name}: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _log.Success($"Deleted file: {path}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not delete {path}: {ex.Message}");
            }
        }

        private void TryDeleteFolder(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    _log.Success($"Deleted folder: {path}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not delete {path}: {ex.Message}");
            }
        }

        // ── AMD debloat ───────────────────────────────────────────────

        /// <summary>
        /// Removes AMD bloat: Radeon Software services (CNext/CN), RAS telemetry,
        /// scheduled tasks with AMD prefixes, and leftover component folders.
        /// </summary>
        public async Task DebloatAmdAsync(AmdInstallComponents? components = null)
        {
            var chosen = components ?? AmdInstallComponents.DisplayOnly;
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

            if (!chosen.KeepScheduledTasks)
            {
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
            }
            else
            {
                _log.Info("AMD scheduled tasks kept — user asked to preserve them.");
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
