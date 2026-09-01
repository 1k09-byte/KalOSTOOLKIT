using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace KalOS.Services
{
    /// <summary>Outcome of a zip-package install. Errors mean the install did not happen.</summary>
    public sealed record ZipInstallResult(
        bool Success,
        string InstallDir,
        string? InstalledVersion,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Errors);

    /// <summary>
    /// Installs a zip payload into a target directory the way
    /// <c>install-kalos.ps1</c> does, but natively and with hard guards:
    ///
    /// 1. Extract to a staging folder next to the target (zip-slip guarded).
    /// 2. Validate the required-file checklist (KalOS.exe, hostfxr, hostpolicy,
    ///    coreclr, HardwareMonitorWorker) — an incomplete package never lands.
    /// 3. Stop any running KalOS.exe so the wipe-and-copy cannot race a live app.
    /// 4. Wipe the target, copy the staged tree in, remove staging.
    ///
    /// The app zip carries <c>HardwareMonitorWorker.exe</c> under
    /// <c>Tools\</c> in some builds, so the checklist matches by file NAME
    /// anywhere in the staged tree rather than by fixed path.
    /// </summary>
    public static class ZipPackageInstaller
    {
        /// <summary>Files that must exist inside the package for it to be a KalOS consumer payload.</summary>
        public static readonly string[] RequiredFiles =
        {
            "KalOS.exe",
            "hostfxr.dll",
            "hostpolicy.dll",
            "coreclr.dll",
            "HardwareMonitorWorker.exe",
        };

        /// <summary>Default install location, identical to install-kalos.ps1.</summary>
        public static string DefaultInstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "KalOS");

        /// <summary>
        /// Full install pipeline. Throws only on programming errors; every
        /// operational failure (locked files, bad zip, missing files) is
        /// reported through <see cref="ZipInstallResult.Errors"/> so the wizard
        /// can fall back to the script path instead of crashing.
        /// </summary>
        public static ZipInstallResult Install(
            string zipPath,
            string installDir,
            Action<string>? status = null)
        {
            var warnings = new List<string>();
            var errors = new List<string>();

            try
            {
                if (!File.Exists(zipPath))
                {
                    errors.Add($"Package not found: {zipPath}");
                    return new ZipInstallResult(false, installDir, null, warnings, errors);
                }

                string staging = installDir + ".staging-" + Guid.NewGuid().ToString("N");
                try
                {
                    status?.Invoke("Extracting package…");
                    ExtractToStaging(zipPath, staging);
                }
                catch (Exception ex)
                {
                    errors.Add($"Extraction failed: {ex.Message}");
                    TryDeleteTree(staging);
                    return new ZipInstallResult(false, installDir, null, warnings, errors);
                }

                try
                {
                    status?.Invoke("Validating package contents…");
                    ValidateRequiredFiles(staging);
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(ex.Message);
                    TryDeleteTree(staging);
                    return new ZipInstallResult(false, installDir, null, warnings, errors);
                }

                string? version = TryGetStagedVersion(staging);
                StopRunningApp(installDir, warnings, status);

                status?.Invoke("Replacing existing installation…");
                if (!WipeAndCopy(staging, installDir, errors, warnings))
                {
                    TryDeleteTree(staging);
                    return new ZipInstallResult(false, installDir, version, warnings, errors);
                }

                TryDeleteTree(staging);
                status?.Invoke("Package installed.");
                return new ZipInstallResult(true, installDir, version, warnings, errors);
            }
            catch (Exception ex)
            {
                errors.Add($"Unexpected install failure: {ex.Message}");
                return new ZipInstallResult(false, installDir, null, warnings, errors);
            }
        }

        /// <summary>Extracts the zip into <paramref name="stagingDir"/> with a zip-slip guard.</summary>
        public static void ExtractToStaging(string zipPath, string stagingDir)
        {
            Directory.CreateDirectory(stagingDir);
            string stagingRoot = Path.GetFullPath(stagingDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // macOS junk that expands to a misleading tree.
                if (entry.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith(".DS_Store", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destPath = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName));

                // Zip-slip guard: a crafted entry must never escape staging.
                if (!destPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unsafe zip entry refused: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }

        /// <summary>Throws a single, readable message when required files are missing. Extracted for tests.</summary>
        internal static void ValidateRequiredFiles(string stagedDir)
        {
            if (!Directory.Exists(stagedDir))
            {
                throw new InvalidOperationException($"Staged directory does not exist: {stagedDir}");
            }

            var missing = new List<string>();
            foreach (string required in RequiredFiles)
            {
                bool found = Directory
                    .EnumerateFiles(stagedDir, required, SearchOption.AllDirectories)
                    .Any();
                if (!found) missing.Add(required);
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "The package is missing required files: " + string.Join(", ", missing));
            }
        }

        /// <summary>File version of KalOS.exe in a staged/installed tree; null when unreadable.</summary>
        public static string? TryGetStagedVersion(string rootDir)
        {
            try
            {
                string? exe = Directory
                    .EnumerateFiles(rootDir, "KalOS.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (exe is null) return null;

                var info = FileVersionInfo.GetVersionInfo(exe);
                return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Version of an already-installed KalOS (for the Welcome page); null when absent.</summary>
        public static string? GetInstalledVersion(string installDir)
        {
            try
            {
                string exe = Path.Combine(installDir, "KalOS.exe");
                if (!File.Exists(exe)) return null;
                var info = FileVersionInfo.GetVersionInfo(exe);
                return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Stops running KalOS instances so the wipe-and-copy cannot race a live
        /// app (install-kalos.ps1 simply fails in that case; the wizard prefers
        /// to resolve it). Best-effort, reports what it did.
        /// </summary>
        private static void StopRunningApp(string installDir, List<string> warnings, Action<string>? status)
        {
            try
            {
                var running = Process.GetProcessesByName("KalOS")
                    .Where(p => !string.Equals(Environment.ProcessPath, p.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (running.Length == 0) return;

                status?.Invoke("Stopping the running KalOS app…");
                foreach (Process process in running)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                        warnings.Add("A running KalOS instance was stopped to allow the update.");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not stop a running KalOS instance: {ex.Message}");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                // Give file handles a moment to be released.
                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                warnings.Add($"Running-app check failed: {ex.Message}");
            }
        }

        /// <summary>Wipes the target and copies the staged tree in. Returns false with errors filled on failure.</summary>
        private static bool WipeAndCopy(string staging, string installDir, List<string> errors, List<string> warnings)
        {
            try
            {
                Directory.CreateDirectory(installDir);

                // Kill any stale staging folders from previous interrupted runs.
                // IMPORTANT: the *current* staging folder lives next to the
                // target under the same pattern, so never delete it here —
                // doing so wipes the freshly-extracted tree before the copy.
                string currentStaging = Path.GetFullPath(staging)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (string stale in Directory.GetDirectories(
                    Path.GetDirectoryName(installDir)!,
                    Path.GetFileName(installDir) + ".staging-*"))
                {
                    string full = Path.GetFullPath(stale)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(full, currentStaging, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(stale, recursive: true); } catch { }
                }

                try
                {
                    foreach (var entry in new DirectoryInfo(installDir).EnumerateFileSystemInfos().ToArray())
                    {
                        if (entry is DirectoryInfo dir) Directory.Delete(dir.FullName, recursive: true);
                        else File.Delete(entry.FullName);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not fully clear the existing installation: {ex.Message}");
                }

                CopyTree(staging, installDir, warnings);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"Could not write to '{installDir}': {ex.Message}");
                return false;
            }
        }

        private static void CopyTree(string source, string target, List<string> warnings)
        {
            Directory.CreateDirectory(target);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, target));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Copy(file, file.Replace(source, target), overwrite: true);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not copy '{Path.GetFileName(file)}': {ex.Message}");
                }
            }
        }

        private static void TryDeleteTree(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
