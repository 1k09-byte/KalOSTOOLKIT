using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Models;

namespace KalOS.Services
{
    /// <summary>
    /// The single driver façade the UI talks to. Routes a GPU to its vendor
    /// <see cref="IDriverProvider"/>, decides whether an update exists, and —
    /// when the user approves — downloads and installs it. All vendor, WMI,
    /// HTTP, and process knowledge lives below this line.
    /// </summary>
    public class DriverService
    {
        private readonly IEnumerable<IDriverProvider> _providers;
        private readonly DriverDownloadService _downloads;
        private readonly DriverInstallService _installs;
        private readonly LoggingService _log;

        private static readonly string DefaultWorkDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KalOS", "drivers");

        private readonly string _workDir;

        public DriverService(
            IEnumerable<IDriverProvider> providers,
            DriverDownloadService downloads,
            DriverInstallService installs,
            LoggingService log)
            : this(providers, downloads, installs, log, DefaultWorkDir)
        {
        }

        /// <summary>Test seam: lets unit tests point the work directory at a temp folder.</summary>
        internal DriverService(
            IEnumerable<IDriverProvider> providers,
            DriverDownloadService downloads,
            DriverInstallService installs,
            LoggingService log,
            string workDir)
        {
            _providers = providers;
            _downloads = downloads;
            _installs = installs;
            _log = log;
            _workDir = workDir;
        }

        /// <summary>The provider that owns this GPU, or null for unsupported adapters.</summary>
        public IDriverProvider? FindProvider(GpuInfo gpu) =>
            _providers.FirstOrDefault(p => p.CanHandle(gpu));

        /// <summary>
        /// Detect → query the vendor source → compare versions. Never throws
        /// except for cancellation; every other failure becomes an Error result.
        /// </summary>
        public async Task<DriverCheckResult> CheckForUpdateAsync(
            GpuInfo gpu,
            CancellationToken cancellationToken = default)
        {
            var provider = FindProvider(gpu);
            if (provider == null)
            {
                return new DriverCheckResult { Status = DriverStatus.Unsupported };
            }

            try
            {
                var latest = await provider.GetLatestDriverAsync(gpu, cancellationToken);
                if (latest == null)
                {
                    return new DriverCheckResult
                    {
                        Status = DriverStatus.Error,
                        Error = "Unable to find a driver for this GPU."
                    };
                }

                int? comparison = DriverVersionComparer.Compare(gpu.Vendor, gpu.DriverVersion, latest.Version);
                if (comparison == null)
                {
                    // Honest "can't tell" — still surface the latest known so the
                    // user can open the vendor page and compare manually.
                    return new DriverCheckResult
                    {
                        Status = DriverStatus.Unknown,
                        LatestDriver = latest
                    };
                }

                return new DriverCheckResult
                {
                    Status = comparison < 0 ? DriverStatus.UpdateAvailable : DriverStatus.UpToDate,
                    LatestDriver = latest
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"Driver check failed for {gpu.Name}: {ex.Message}");
                return new DriverCheckResult
                {
                    Status = DriverStatus.Error,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Runs an approved update end to end. NVIDIA and AMD get the full silent
        /// pipeline (download → 7z extract → strip → pnputil display-only install →
        /// debloat). Intel and unknown vendors have no silent path, so the vendor
        /// download page is opened instead. Progress flows through <paramref name="progress"/>.
        /// </summary>
        public async Task<bool> UpdateAsync(
            GpuInfo gpu,
            DriverInfo driver,
            IProgress<DriverUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            NvidiaInstallComponents? nvidiaComponents = null,
            string? sourceExePath = null)
        {
            var display = driver.DisplayString ?? $"driver {driver.Version}";
            bool hasSilentPath = (gpu.IsNvidia || gpu.IsAmd)
                && Uri.IsWellFormedUriString(driver.DownloadUrl, UriKind.Absolute);

            if (!hasSilentPath)
            {
                progress?.Report(new DriverUpdateProgress
                {
                    Phase = DriverUpdatePhase.Done,
                    Percent = 100,
                    Message = "Opening the vendor download page…"
                });
                OpenInBrowser(driver.DownloadUrl);
                return true;
            }

            Directory.CreateDirectory(_workDir);
            string vendorTag = gpu.IsNvidia ? "nvidia" : "amd";
            string exePath = Path.Combine(_workDir, $"{vendorTag}-driver-{SanitizeVersion(driver.Version)}.exe");
            string extractDir = Path.Combine(_workDir, "extracted");

            try
            {
                progress?.Report(new DriverUpdateProgress
                {
                    Phase = DriverUpdatePhase.Downloading,
                    Percent = 0,
                    Message = $"Installing update (Downloading {display}…)"
                });

                var downloadProgress = new Progress<double>(pct => progress?.Report(new DriverUpdateProgress
                {
                    Phase = DriverUpdatePhase.Downloading,
                    Percent = pct,
                    Message = $"Installing update (Downloading {display}… {pct:F0}%)"
                }));

                if (sourceExePath != null)
                {
                    // User-supplied package ("Use driver files on disk"): validate
                    // it, then copy it to the path the extraction step expects —
                    // no network download in this flow.
                    ValidateLocalPackage(sourceExePath);
                    progress?.Report(new DriverUpdateProgress
                    {
                        Phase = DriverUpdatePhase.Downloading,
                        Percent = 100,
                        Message = $"Using driver package: {Path.GetFileName(sourceExePath)}"
                    });
                    _log.Info($"Using on-disk driver package: {sourceExePath}");
                    File.Copy(sourceExePath, exePath, overwrite: true);
                }
                else
                {
                    await _downloads.DownloadAsync(driver.DownloadUrl, exePath, downloadProgress, cancellationToken);
                }

                bool installed;
                if (gpu.IsNvidia)
                {
                    installed = await _installs.InstallNvidiaViaExtractionAsync(
                        exePath, extractDir,
                        new Progress<string>(message => progress?.Report(new DriverUpdateProgress
                        {
                            Phase = message.StartsWith("Installing", StringComparison.Ordinal)
                                ? DriverUpdatePhase.Installing
                                : DriverUpdatePhase.Extracting,
                            Percent = 100,
                            Message = message
                        })),
                        cancellationToken,
                        nvidiaComponents);
                }
                else
                {
                    installed = await _installs.InstallAmdViaExtractionAsync(
                        exePath, extractDir,
                        new Progress<string>(message => progress?.Report(new DriverUpdateProgress
                        {
                            Phase = message.StartsWith("Installing", StringComparison.Ordinal)
                                ? DriverUpdatePhase.Installing
                                : DriverUpdatePhase.Extracting,
                            Percent = 100,
                            Message = message
                        })),
                        cancellationToken);
                }

                progress?.Report(new DriverUpdateProgress
                {
                    Phase = DriverUpdatePhase.Done,
                    Percent = 100,
                    Message = installed ? "Driver updated." : "Update did not complete."
                });

                return installed;
            }
            finally
            {
                progress?.Report(new DriverUpdateProgress
                {
                    Phase = DriverUpdatePhase.CleaningUp,
                    Percent = 100,
                    Message = "Cleaning up downloaded files…"
                });

                // pnputil or real-time scanning can briefly lock the installer;
                // retry a few times before giving up so the download never lingers.
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        if (File.Exists(exePath)) File.Delete(exePath);
                        break;
                    }
                    catch
                    {
                        if (attempt == 3)
                        {
                            _log.Warn($"Could not delete driver download '{Path.GetFileName(exePath)}'");
                        }
                        else
                        {
                            await Task.Delay(1000);
                        }
                    }
                }

                _installs.CleanupExtracted(extractDir);
            }
        }

        /// <summary>
        /// Sanity-checks a user-supplied driver package before it replaces a
        /// download: it must exist, be a plausible size, and start with the PE
        /// "MZ" header — the same bar the download pipeline enforces.
        /// </summary>
        private static void ValidateLocalPackage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("The selected driver file does not exist.");
            if (new FileInfo(path).Length < 10_000_000)
                throw new InvalidOperationException("The selected file is too small to be a driver package.");

            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[2];
            if (fs.Read(header) < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidOperationException("The selected file is not a valid Windows executable.");
        }

        /// <summary>
        /// Reclaims space from interrupted installs: deletes leftover driver
        /// downloads, partial downloads, and extraction folders in the work
        /// directory that have not been modified recently. Safe to call whenever
        /// no install is running — anything younger than <paramref name="minAge"/>
        /// is left alone so an in-flight operation is never touched.
        /// </summary>
        public void CleanStaleDownloads(TimeSpan? minAge = null)
        {
            try
            {
                if (!Directory.Exists(_workDir)) return;

                var cutoff = DateTime.UtcNow - (minAge ?? TimeSpan.FromMinutes(30));
                foreach (var entry in new DirectoryInfo(_workDir).EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (entry.LastWriteTimeUtc > cutoff) continue;

                        if (entry is DirectoryInfo dir)
                        {
                            Directory.Delete(dir.FullName, recursive: true);
                        }
                        else
                        {
                            File.Delete(entry.FullName);
                        }
                        _log.Info($"Removed leftover driver file: {entry.Name}");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Could not remove leftover driver file '{entry.Name}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Driver leftover cleanup failed: {ex.Message}");
            }
        }

        /// <summary>Opens the vendor page in the default browser; false when there is nothing usable to open.</summary>
        public bool OpenInBrowser(string? url)
        {
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not open '{url}': {ex.Message}");
                return false;
            }
        }

        private static string SanitizeVersion(string version)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                version = version.Replace(c, '_');
            }
            return version;
        }
    }
}
