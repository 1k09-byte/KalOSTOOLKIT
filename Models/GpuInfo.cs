using System;

namespace KaliteKit.Models
{
    /// <summary>A graphics adapter and its installed driver, as reported by WMI.</summary>
    public sealed class GpuInfo
    {
        public string Name { get; init; } = "Unknown GPU";
        public string Manufacturer { get; init; } = "Unknown";
        public string DriverVersion { get; init; } = "Unknown";
        public string DriverDate { get; init; } = "Unknown";
        public string PnpDeviceId { get; init; } = "";
        /// <summary>
        /// True when the machine is a laptop/notebook/tablet. Detected from the
        /// SMBIOS chassis type (and battery presence) — not from the GPU name —
        /// so an "NVIDIA GeForce RTX 4060 Laptop GPU" that WMI reports without
        /// the Laptop/Mobile/Notebook words still resolves to the notebook
        /// driver packages.
        /// </summary>
        public bool IsLaptop { get; init; }

        /// <summary>
        /// True when this adapter is a mobile/notebook variant — the laptop
        /// chassis flag, or the model name carrying the vendor's mobile marker
        /// ("Laptop GPU", Mobile, Notebook, or an "M"-suffixed GeForce model).
        /// NVIDIA notebook GPUs need the notebook series queries for version
        /// lookups even though the driver package itself is the same DCH build.
        /// </summary>
        public bool IsMobileGpu => IsLaptop || NameContainsMobileMarker(Name);

        public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || PnpDeviceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);
        public bool IsAmd => Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || PnpDeviceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase);
        public bool IsIntel => Name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || PnpDeviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase);

        /// <summary>The vendor key the provider registry uses to route this GPU.</summary>
        public string Vendor => IsNvidia ? "NVIDIA" : IsAmd ? "AMD" : IsIntel ? "Intel" : "Other";

        public static GpuInfo Unknown() => new();

        /// <summary>
        /// Model-name mobile marker. Vendor naming only — never queried from
        /// the system (WMI has no GPU-level form factor), so this is safe to
        /// call from unit tests. Covers "Laptop GPU"/Mobile/Notebook words, the
        /// classic "M"-suffixed GeForce models (GTX 860M), the Max-Q designs,
        /// and the notebook-only MX series.
        /// </summary>
        public static bool NameContainsMobileMarker(string name) =>
            name.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Notebook", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(
                name,
                @"(Max-?Q|MX\s*\d{3}|\b(GTX|RTX)\s*\d{3,4}M\b)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>The outcome of a driver check for a single GPU.</summary>
    public enum DriverStatus
    {
        Unknown,
        UpToDate,
        UpdateAvailable,
        Unsupported,
        Error
    }

    /// <summary>The latest driver a provider knows about for a given GPU.</summary>
    public sealed class DriverInfo
    {
        public string Version { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        /// <summary>Human-facing vendor driver/support page opened by the "Open download page" button.</summary>
        public string SupportUrl { get; init; } = "";
        public DateTime? ReleaseDate { get; init; }
        public string? DisplayString { get; set; }
    }

    /// <summary>Full result returned by <see cref="Services.DriverService.CheckForUpdateAsync"/>. Fills in the UI with no vendor knowledge.</summary>
    public sealed class DriverCheckResult
    {
        public DriverStatus Status { get; init; }
        public DriverInfo? LatestDriver { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>Stages of a driver download-and-install run.</summary>
    public enum DriverUpdatePhase
    {
        Downloading,
        Extracting,
        Installing,
        CleaningUp,
        Done
    }

    /// <summary>Progress snapshot pushed to the UI during a driver update run. Percent is only meaningful while Downloading.</summary>
    public sealed class DriverUpdateProgress
    {
        public DriverUpdatePhase Phase { get; init; }
        public double Percent { get; init; }
        public string Message { get; init; } = "";
    }
}