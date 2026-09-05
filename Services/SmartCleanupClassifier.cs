using System;
using System.Collections.Generic;
using System.Linq;
using KaliteKit.Models;

namespace KaliteKit.Services
{
    /// <summary>
    /// Smart Cleanup classifier (spec 5.5) — a PURE FUNCTION over enumerated
    /// packages. Groups OEM packages by their stable identity key (the
    /// vendor's original INF name + class, NOT the store's sequential
    /// oemNN.inf label), then flags cleanup candidates.
    ///
    /// Hard rules (tested in SmartCleanupClassifierTests, spec 10.3):
    ///  - A BootCritical package is NEVER a candidate, under any outcome.
    ///  - A package associated with a present device is NEVER a candidate,
    ///    even when a newer version exists in its group — "not newest" and
    ///    "not in use" are different conditions and both must hold.
    ///  - Candidates are returned for REVIEW ONLY; nothing here deletes.
    /// </summary>
    public static class SmartCleanupClassifier
    {
        /// <summary>
        /// Compute cleanup candidates from an enumeration.
        /// </summary>
        /// <param name="packages">All enumerated packages (inbox included or not — inbox packages are never candidates).</param>
        /// <returns>Candidates with per-candidate reasoning, newest-version-first within each group.</returns>
        public static List<CleanupCandidate> GetCandidates(IEnumerable<DriverPackageRecord> packages)
        {
            var candidates = new List<CleanupCandidate>();

            var groups = packages
                .Where(p => !p.IsInbox)
                .GroupBy(p => GroupKey(p));

            foreach (var group in groups)
            {
                // A single-package group has nothing to be superseded by.
                if (group.Count() < 2) continue;

                var ordered = group
                    .OrderByDescending(p => p.DriverVersion ?? new Version(0, 0, 0, 0))
                    .ThenByDescending(p => p.DriverDate ?? DateTime.MinValue)
                    .ToList();

                // The newest package in the group is the keeper — even if it
                // is not associated with any device (a newest-but-unused
                // package may be staged intentionally, e.g. for a dock).
                for (int i = 1; i < ordered.Count; i++)
                {
                    var p = ordered[i];

                    // HARD RULE 1: boot-critical packages are never candidates.
                    if (p.BootCritical) continue;

                    // HARD RULE 2: a package bound to a present device is never
                    // a candidate — an older driver still running working
                    // hardware is not "superseded" (spec 8.4: it may be needed
                    // the moment a dock/printer/USB device reconnects).
                    if (p.InUseByPresentDevice) continue;

                    string reason = BuildReason(p, ordered[0]);
                    candidates.Add(new CleanupCandidate(p, reason, PreChecked: true));
                }
            }

            return candidates;
        }

        /// <summary>
        /// The stable identity key: original vendor INF name + device class.
        /// The store's oemNN.inf name is a sequential label with no semantic
        /// meaning across versions of "the same" driver (spec 5.5.1).
        /// </summary>
        private static string GroupKey(DriverPackageRecord p) =>
            $"{p.DriverClass}|{p.OriginalInfName}".ToLowerInvariant();

        private static string BuildReason(DriverPackageRecord p, DriverPackageRecord newest)
        {
            string version = p.DriverVersion?.ToString() ?? "unknown version";
            string newestVersion = newest.DriverVersion?.ToString() ?? "unknown version";
            string baseReason = $"older version {version} of this driver — a newer version ({newestVersion}) is in the store, and no present device is using it";

            if (p.AssociatedDevices.Count > 0)
            {
                // Devices exist for this package but none are PRESENT right now —
                // flag distinctly rather than presenting both cases identically (spec 8.4).
                return baseReason + ", but it IS associated with currently-disconnected device(s): "
                    + string.Join(", ", p.AssociatedDevices.Select(d => string.IsNullOrEmpty(d.Description) ? d.InstanceId : d.Description).Distinct().Take(3));
            }
            return baseReason;
        }
    }
}
