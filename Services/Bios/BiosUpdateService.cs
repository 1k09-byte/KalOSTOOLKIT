using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Models.Bios;

namespace KalOS.Services.Bios;

/// <summary>
/// Service that inspects the system BIOS version, motherboard details, and UEFI firmware
/// capsule records in Windows to determine if the BIOS is on the latest version.
/// </summary>
public sealed class BiosUpdateService
{
    private readonly IWmiClient _wmi;
    private readonly LoggingService _log;

    public BiosUpdateService(IWmiClient wmi, LoggingService log)
    {
        _wmi = wmi;
        _log = log;
    }

    /// <summary>
    /// Checks for BIOS updates by evaluating system hardware and UEFI firmware devices.
    /// </summary>
    public async Task<BiosUpdateCheckResult> CheckBiosVersionAsync(BiosSystemInfo systemInfo, CancellationToken ct = default)
    {
        try
        {
            var installedVersion = systemInfo.BiosVersion;
            var releaseDate = systemInfo.BiosReleaseDate;

            // Check Windows Device Manager UEFI Firmware capsules (Win32_PnPEntity ClassGuid {f2e7dd72-6468-4e36-b6f1-6488f42c1b52})
            var firmwarePnp = await QueryFirmwarePnpAsync(ct);
            if (firmwarePnp is not null)
            {
                var comparison = CompareBiosVersions(installedVersion, firmwarePnp.DriverVersion);
                if (comparison < 0 && !string.IsNullOrWhiteSpace(firmwarePnp.DriverVersion))
                {
                    return new BiosUpdateCheckResult(
                        Status: BiosUpdateStatus.UpdateAvailable,
                        InstalledVersion: installedVersion,
                        LatestVersion: firmwarePnp.DriverVersion,
                        ReleaseDate: releaseDate,
                        LatestReleaseDate: firmwarePnp.DriverDate,
                        StatusMessage: $"Newer UEFI firmware ({firmwarePnp.DriverVersion}) detected via Windows Update.",
                        Notes: firmwarePnp.DeviceName);
                }
            }

            return new BiosUpdateCheckResult(
                Status: BiosUpdateStatus.UpToDate,
                InstalledVersion: installedVersion,
                LatestVersion: installedVersion,
                ReleaseDate: releaseDate,
                StatusMessage: $"Running BIOS {installedVersion} (Released: {releaseDate}). No updates detected.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"BIOS update check error: {ex.Message}");
            return new BiosUpdateCheckResult(
                Status: BiosUpdateStatus.Error,
                InstalledVersion: systemInfo.BiosVersion,
                ReleaseDate: systemInfo.BiosReleaseDate,
                StatusMessage: $"Could not verify status: {ex.Message}");
        }
    }

    private sealed record PnpFirmwareInfo(string DeviceName, string DriverVersion, string? DriverDate);

    private async Task<PnpFirmwareInfo?> QueryFirmwarePnpAsync(CancellationToken ct)
    {
        try
        {
            // ClassGuid for Firmware devices in Windows is {f2e7dd72-6468-4e36-b6f1-6488f42c1b52}
            var rows = await _wmi.QueryAsync(
                @"root\cimv2",
                "SELECT Name, DriverVersion, DriverDate, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'Firmware' OR ClassGuid = '{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}'",
                ct);

            foreach (var row in rows)
            {
                var name = row.GetString("Name") ?? "";
                var driverVer = row.GetString("DriverVersion");
                var driverDate = row.GetString("DriverDate");

                if (!string.IsNullOrWhiteSpace(driverVer) && (name.Contains("System", StringComparison.OrdinalIgnoreCase) || name.Contains("Firmware", StringComparison.OrdinalIgnoreCase) || name.Contains("BIOS", StringComparison.OrdinalIgnoreCase)))
                {
                    return new PnpFirmwareInfo(name, driverVer.Trim(), FormatDate(driverDate));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Firmware PnP query failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Compares two BIOS versions.
    /// Returns: -1 if v1 &lt; v2, 0 if v1 == v2, 1 if v1 &gt; v2.
    /// </summary>
    public static int CompareBiosVersions(string? v1, string? v2)
    {
        if (string.IsNullOrWhiteSpace(v1) && string.IsNullOrWhiteSpace(v2)) return 0;
        if (string.IsNullOrWhiteSpace(v1)) return -1;
        if (string.IsNullOrWhiteSpace(v2)) return 1;

        v1 = v1.Trim();
        v2 = v2.Trim();

        if (string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase)) return 0;

        // Try System.Version parse
        if (Version.TryParse(CleanVersionString(v1), out var ver1) &&
            Version.TryParse(CleanVersionString(v2), out var ver2))
        {
            return ver1.CompareTo(ver2);
        }

        // Handle letter-prefixed versions like F.38 vs F.40 or F38 vs F40
        var match1 = Regex.Match(v1, @"^([A-Za-z]*)[\.\s_-]?(\d+)(?:[\.\s_-](\d+))?");
        var match2 = Regex.Match(v2, @"^([A-Za-z]*)[\.\s_-]?(\d+)(?:[\.\s_-](\d+))?");

        if (match1.Success && match2.Success)
        {
            var prefix1 = match1.Groups[1].Value;
            var prefix2 = match2.Groups[1].Value;

            if (string.Equals(prefix1, prefix2, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(match1.Groups[2].Value, out int n1) &&
                    int.TryParse(match2.Groups[2].Value, out int n2))
                {
                    if (n1 != n2) return n1.CompareTo(n2);

                    int sub1 = match1.Groups[3].Success && int.TryParse(match1.Groups[3].Value, out int s1) ? s1 : 0;
                    int sub2 = match2.Groups[3].Success && int.TryParse(match2.Groups[3].Value, out int s2) ? s2 : 0;
                    return sub1.CompareTo(sub2);
                }
            }
        }

        return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanVersionString(string ver)
    {
        var cleaned = Regex.Replace(ver, @"^[^\d]+", "");
        var parts = cleaned.Split(new[] { '.', '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var numericParts = parts.TakeWhile(p => int.TryParse(p, out _)).ToList();

        if (numericParts.Count == 0) return "0.0";
        if (numericParts.Count == 1) return $"{numericParts[0]}.0";
        return string.Join(".", numericParts.Take(4));
    }

    private static string FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        raw = raw.Trim();
        if (raw.Length >= 8 && char.IsDigit(raw[0]) && char.IsDigit(raw[1]))
        {
            if (DateTime.TryParseExact(raw[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        }
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

        return raw;
    }
}
