using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KalOS.Services
{
    /// <summary>One feature in the self-check manifest and whether the running build contains it.</summary>
    public sealed record FeatureStatus(string Name, string Marker, bool Present);

    /// <summary>
    /// In-app equivalent of the manual .kalos-feature-check.ps1 probe: scans the
    /// running KalOS assembly for the symbols each feature needs and reports any
    /// that are missing, alongside the app version. A feature counts as present
    /// when its marker (a type or member name) resolves via reflection, so a
    /// build compiled without a feature — e.g. update plumbing stripped from a
    /// dev build — shows up immediately instead of failing silently later.
    /// </summary>
    public static class FeatureSelfCheck
    {
        /// <summary>Every feature the current version of KalOS is expected to ship with.</summary>
        public static IReadOnlyList<(string Name, string Marker)> Manifest { get; } = new[]
        {
            ("System overview", "SystemOverviewViewModel"),
            ("Hardware monitoring", "HardwareMonitorService"),
            ("GPU drivers", "GpuDriversViewModel"),
            ("NVIDIA driver install", "NvidiaDriverProvider"),
            ("AMD driver install", "AmdDriverProvider"),
            ("AMD Radeon Slimmer", "RadeonSlimmerService"),
            ("Driver cleanup", "DriverCleanupService"),
            ("CPU per-core scheduling", "CoreSpreadingService"),
            ("Affinity manager", "AffinityManagerViewModel"),
            ("BIOS", "BiosViewModel"),
            ("Windhawk mods", "WindhawkManagerService"),
            ("Browser & software install", "PackageManagerService"),
            ("Privacy extensions", "BrowserExtensionService"),
            ("Personalization", "PersonalizationViewModel"),
            ("Window backdrop", "BackdropService"),
            ("Visual effects", "VisualEffectsPage"),
            ("Additional tweaks", "AdditionalTweaksViewModel"),
            ("OS changes", "OsChangeService"),
            ("Disk cleanup", "DiskCleanupService"),
            ("Startup banner", "StartupBannerWindow"),
            ("Startup tasks", "StartupTasksService"),
            ("UniGetUI", "WingetUiViewModel"),
            ("Self-update", "UpdateService"),
            ("Release history", "GetReleaseHistoryAsync"),
            ("Update log", "ShowUpdateLogIfAny"),
            ("Diagnostics log", "LoggingService"),
            ("Theme", "ThemeService"),
            ("Settings", "SettingsViewModel"),
        };

        /// <summary>
        /// Scans the running assembly and returns one status per manifest entry,
        /// in manifest order.
        /// </summary>
        public static IReadOnlyList<FeatureStatus> Run()
        {
            var symbols = CollectSymbols(typeof(FeatureSelfCheck).Assembly);
            return Manifest
                .Select(f => new FeatureStatus(f.Name, f.Marker, symbols.Contains(f.Marker)))
                .ToList();
        }

        /// <summary>
        /// Collects every type, method, property, field and event name declared
        /// in the assembly. Tolerates partially-loadable types so one broken
        /// type can't fail the whole check.
        /// </summary>
        private static HashSet<string> CollectSymbols(Assembly assembly)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (var type in types)
            {
                names.Add(type.Name);
                try
                {
                    foreach (var member in type.GetMembers(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.DeclaredOnly))
                    {
                        names.Add(member.Name);
                    }
                }
                catch
                {
                    // A single broken type must not fail the whole check.
                }
            }

            return names;
        }
    }
}
