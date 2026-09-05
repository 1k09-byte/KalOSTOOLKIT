using System;
using System.Linq;
using System.Threading.Tasks;
using KaliteKit.Models.Bios;

namespace KaliteKit.Services.Bios;

public sealed class BiosProviderFactory
{
    private readonly LoggingService _log;
    private readonly ScewinService _scewin;
    private BiosSystemInfo? _cachedInfo;
    private readonly IWmiClient _wmi;

    public BiosProviderFactory(IWmiClient wmi, ScewinService scewin, LoggingService log)
    {
        _wmi = wmi;
        _scewin = scewin;
        _log = log;
    }

    public async Task<BiosSystemInfo> GetSystemInfoAsync()
    {
        if (_cachedInfo is not null) return _cachedInfo;

        var info = BiosSystemInfo.Unknown;
        try
        {
            var rows = await _wmi.QueryAsync(@"root\cimv2", "SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem", default);
            var comp = rows.FirstOrDefault();
            string manuf = comp?.GetString("Manufacturer") ?? "Unknown";
            string model = comp?.GetString("Model") ?? "Unknown";
            string sku = comp?.GetString("SystemSKUNumber")?.Trim() ?? "Unknown";

            var biosRows = await _wmi.QueryAsync(@"root\cimv2", "SELECT SMBIOSBIOSVersion, Manufacturer, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion, Version FROM Win32_BIOS", default);
            var biosRow = biosRows.FirstOrDefault();
            string bios = biosRow?.GetString("SMBIOSBIOSVersion") ?? biosRow?.GetString("Version") ?? "Unknown";
            string firmwareVendor = biosRow?.GetString("Manufacturer")?.Trim() ?? "Unknown";
            string? rawDate = biosRow?.GetString("ReleaseDate");
            string releaseDate = FormatBiosDate(rawDate);

            int? smbiosMajor = biosRow?.GetInt("SMBIOSMajorVersion");
            int? smbiosMinor = biosRow?.GetInt("SMBIOSMinorVersion");
            string smbiosVersion = (smbiosMajor.HasValue && smbiosMinor.HasValue)
                ? $"{smbiosMajor.Value}.{smbiosMinor.Value}"
                : (!string.IsNullOrEmpty(biosRow?.GetString("SMBIOSMajorVersion")) && !string.IsNullOrEmpty(biosRow?.GetString("SMBIOSMinorVersion")))
                    ? $"{biosRow.GetString("SMBIOSMajorVersion")}.{biosRow.GetString("SMBIOSMinorVersion")}"
                    : "Unknown";

            var boardRows = await _wmi.QueryAsync(@"root\cimv2", "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", default);
            var board = boardRows.FirstOrDefault();
            string boardManufacturer = board?.GetString("Manufacturer")?.Trim() ?? "Unknown";
            string boardProduct = board?.GetString("Product")?.Trim() ?? "Unknown";
            string boardVersion = board?.GetString("Version")?.Trim() ?? "Unknown";

            _cachedInfo = new BiosSystemInfo(
                manuf.Trim(),
                model.Trim(),
                bios.Trim(),
                false,
                firmwareVendor,
                boardManufacturer,
                boardProduct,
                releaseDate,
                smbiosVersion,
                sku,
                boardVersion);
        }
        catch (Exception ex)
        {
            _log.Warn($"BIOS system-info detection failed: {ex.Message}");
            _cachedInfo = BiosSystemInfo.Unknown;
        }

        return _cachedInfo;
    }

    private static string FormatBiosDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        raw = raw.Trim();

        // CIM datetime format: YYYYMMDD******
        if (raw.Length >= 8 && char.IsDigit(raw[0]) && char.IsDigit(raw[1]) && char.IsDigit(raw[2]) && char.IsDigit(raw[3]))
        {
            if (System.DateTime.TryParseExact(raw.Substring(0, 8), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var cimDt))
            {
                return cimDt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (System.DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }

        return raw;
    }

    public Task<IBiosProvider> CreateAsync()
    {
        return Task.FromResult<IBiosProvider>(new ScewinProvider(_scewin, _log));
    }
}