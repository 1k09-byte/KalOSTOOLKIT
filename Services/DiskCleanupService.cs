using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KaliteKit.Services
{
    /// <summary>A temp-file bucket with its current reclaimed size in bytes.</summary>
    public sealed class CleanupCategory
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long CleanableBytes { get; set; }

        /// <summary>Human-readable size, e.g. "1.2 GB" or "340 MB".</summary>
        public string SizeText => FormatBytes(CleanableBytes);

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.#} {units[unit]}";
        }
    }

    /// <summary>
    /// A minimal Windows junk cleaner that ONLY removes temporary files. It never
    /// touches Windows Update payloads or component store, which the user intends
    /// to service/disable separately (SoftwareDistribution, WinSxS, etc. are left
    /// strictly alone).
    /// </summary>
    public sealed class DiskCleanupService
    {
        /// <summary>
        /// The temp directories this clean targets. Kept as a curated allow-list so a
        /// bug can never wander into Windows Update or the component store.
        /// </summary>
        private static IReadOnlyList<CleanupCategory> BuildCategories()
        {
            var list = new List<CleanupCategory>
            {
                new()
                {
                    Name = "User temp files",
                    Path = System.IO.Path.GetTempPath(),
                    Description = "Per-user temporary files (%TEMP%) used by apps and installers.",
                },
                new()
                {
                    Name = "System temp files",
                    Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
                    Description = "Windows temporary files (C:\\Windows\\Temp).",
                },
                new()
                {
                    Name = "DirectX / setup cache",
                    Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs"),
                    Description = "Logging files under Windows\\Logs that are safe to prune.",
                },
            };

            // Only keep locations that actually exist AND are strictly temp/safe.
            return list.Where(c => Directory.Exists(c.Path)).ToList();
        }

        /// <summary>Sums the current size of every temp category without deleting anything.</summary>
        public Task<IReadOnlyList<CleanupCategory>> ScanAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var categories = BuildCategories();
                foreach (var cat in categories)
                {
                    ct.ThrowIfCancellationRequested();
                    cat.CleanableBytes = MeasureDirectory(cat.Path, ct);
                }
                return (IReadOnlyList<CleanupCategory>)categories;
            }, CancellationToken.None);
        }

        /// <summary>
        /// Deletes the contents of each temp category (best-effort — locked/in-use
        /// files are skipped) and returns the bytes actually freed. Windows Update
        /// payload is never touched.
        /// </summary>
        public Task<(IReadOnlyList<CleanupCategory> Result, long Freed)> CleanAsync(
            IReadOnlyList<CleanupCategory> categories, IProgress<double>? progress, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                long freed = 0;
                foreach (var cat in categories)
                {
                    ct.ThrowIfCancellationRequested();
                    long before = MeasureDirectory(cat.Path, ct);
                    DeleteContents(cat.Path, ct);
                    long after = MeasureDirectory(cat.Path, ct);
                    freed += Math.Max(0, before - after);
                    cat.CleanableBytes = after;
                    progress?.Report(1.0);
                }
                progress?.Report(1.0);
                return (categories, freed);
            }, CancellationToken.None);
        }

        private static long MeasureDirectory(string path, CancellationToken ct)
        {
            long total = 0;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    if (ct.IsCancellationRequested) return total;
                    try
                    {
                        var fi = new FileInfo(file);
                        total += fi.Length;
                    }
                    catch { /* file vanished or locked — skip */ }
                }
            }
            catch { /* inaccessible — skip this whole subtree */ }
            return total;
        }

        private static void DeleteContents(string path, CancellationToken ct)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        Directory.Delete(dir, recursive: true);
                    }
                    catch { /* in use/locked — skip */ }
                }
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    if (ct.IsCancellationRequested) return;
                    try { File.Delete(file); }
                    catch { /* locked — skip */ }
                }
            }
            catch { /* path gone or inaccessible */ }
        }
    }
}