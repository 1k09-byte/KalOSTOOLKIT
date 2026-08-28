using System;

namespace KalOS.Models
{
    /// <summary>A graphics adapter and its installed driver, as reported by WMI.</summary>
    public sealed class GpuInfo
    {
        public string Name { get; init; } = "Unknown GPU";
        public string Manufacturer { get; init; } = "Unknown";
        public string DriverVersion { get; init; } = "Unknown";
        public string DriverDate { get; init; } = "Unknown";
        public string PnpDeviceId { get; init; } = "";

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