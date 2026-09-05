using System;
using System.Collections.Generic;
using System.Linq;

namespace KaliteKit.Models
{
    /// <summary>Which DriverStore a provider operates on.</summary>
    public enum DriverStoreTarget
    {
        /// <summary>The running system's store (C:\Windows\System32\DriverStore).</summary>
        Online,

        /// <summary>A mounted image / offline Windows folder.</summary>
        Offline,
    }

    /// <summary>One device currently associated with a driver package.</summary>
    public sealed record AssociatedDevice(string InstanceId, string Description, bool IsPresent);

    /// <summary>
    /// One driver package in a DriverStore. Mirrors RAPR's DriverStoreEntry
    /// fields (spec section 4). Mutable members (<see cref="SizeBytes"/>,
    /// <see cref="DriverFiles"/>, <see cref="AssociatedDevices"/>) are filled
    /// after the initial metadata enumeration — see spec 6.1/6.2.
    /// </summary>
    public sealed record DriverPackageRecord
    {
        public required string DriverClass { get; init; }
        public Guid ClassGuid { get; init; }

        /// <summary>Staged INF name inside the store, e.g. "oem12.inf".</summary>
        public required string InfName { get; init; }

        /// <summary>Published INF name (for published packages, same as <see cref="InfName"/>).</summary>
        public required string PublishedName { get; init; }

        /// <summary>
        /// The vendor's original INF filename — the stable identity key for
        /// grouping "the same driver" across versions (spec 5.5.1).
        /// </summary>
        public string OriginalInfName { get; init; } = string.Empty;

        public string ExtensionId { get; init; } = string.Empty;
        public required string Provider { get; init; }
        public required string Signer { get; init; }
        public DateTime? DriverDate { get; init; }
        public Version? DriverVersion { get; init; }
        public required string FolderLocation { get; init; }

        /// <summary>Folder size — computed asynchronously after listing (spec 6.1).</summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// Safety-critical: a package Windows depends on to start (spec 7.1).
        /// Unknown/false in the pnputil fallback provider — that limitation is
        /// surfaced in the delete UI.
        /// </summary>
        public bool BootCritical { get; init; }

        public DateTime? InstallDate { get; init; }

        /// <summary>Inbox (Microsoft-shipped) package — hidden by default (spec 7.4).</summary>
        public bool IsInbox { get; init; }

        /// <summary>Referenced binary files — lazily enumerated on demand (export / round-trip check).</summary>
        public IReadOnlyList<string>? DriverFiles { get; set; }

        public IReadOnlyList<AssociatedDevice> AssociatedDevices { get; set; } =
            Array.Empty<AssociatedDevice>();

        /// <summary>Non-empty when the record came from an offline image root.</summary>
        public string OfflineRoot { get; init; } = string.Empty;

        public bool IsOffline => !string.IsNullOrEmpty(OfflineRoot);

        /// <summary>Any PRESENT device bound to this package. A device that is associated but disconnected (dock, printer) does not count as in-use — but its presence on the record is surfaced in cleanup reasoning.</summary>
        public bool InUseByPresentDevice =>
            AssociatedDevices.Any(d => d.IsPresent);

        /// <summary>Human-readable identity for backups and confirmations.</summary>
        public string DisplayName =>
            string.IsNullOrEmpty(OriginalInfName) ? PublishedName : OriginalInfName;

        /// <summary>
        /// RAPR-style backup folder name: human-readable, collision-safe —
        /// "NVIDIA_nvlt.inf_32.0.15.6109", not just "oem12.inf" (spec 5.2).
        /// </summary>
        public string BackupFolderName
        {
            get
            {
                string version = DriverVersion?.ToString() ?? (DriverDate?.ToString("yyyy-MM-dd") ?? "unknown");
                string provider = Sanitize(Provider.Length > 0 ? Provider : "Unknown");
                return $"{provider}_{Sanitize(DisplayName)}_{Sanitize(version)}";
            }
        }

        private static string Sanitize(string s)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 || char.IsControl(chars[i]))
                {
                    chars[i] = '_';
                }
            }
            return new string(chars).Trim();
        }
    }

    /// <summary>
    /// One Smart Cleanup candidate with its human-readable reasoning
    /// (spec 5.5.6) and whether it is pre-checked in the review list.
    /// Boot-critical packages can NEVER become candidates (spec 5.5.3) —
    /// enforced and tested in <c>SmartCleanupClassifier</c>.
    /// </summary>
    public sealed record CleanupCandidate(DriverPackageRecord Package, string Reason, bool PreChecked);
}
