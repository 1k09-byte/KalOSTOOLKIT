using System;
using System.Linq;
using System.Threading.Tasks;
using KalOS.Models.Bios;

namespace KalOS.Services.Bios;

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
            var rows = await _wmi.QueryAsync(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem", default);
            var comp = rows.FirstOrDefault();
            string manuf = comp?.GetString("Manufacturer") ?? "Unknown";
            string model = comp?.GetString("Model") ?? "Unknown";

            var biosRows = await _wmi.QueryAsync(@"root\cimv2", "SELECT SMBIOSBIOSVersion, Manufacturer FROM Win32_BIOS", default);
            var biosRow = biosRows.FirstOrDefault();
            string bios = biosRow?.GetString("SMBIOSBIOSVersion") ?? "Unknown";
            string firmwareVendor = biosRow?.GetString("Manufacturer")?.Trim() ?? "Unknown";

            var boardRows = await _wmi.QueryAsync(@"root\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard", default);
            var board = boardRows.FirstOrDefault();
            string boardManufacturer = board?.GetString("Manufacturer")?.Trim() ?? "Unknown";
            string boardProduct = board?.GetString("Product")?.Trim() ?? "Unknown";

            _cachedInfo = new BiosSystemInfo(manuf.Trim(), model.Trim(), bios.Trim(), false, firmwareVendor, boardManufacturer, boardProduct);
        }
        catch (Exception ex)
        {
            _log.Warn($"BIOS system-info detection failed: {ex.Message}");
            _cachedInfo = BiosSystemInfo.Unknown;
        }

        return _cachedInfo;
    }

    public Task<IBiosProvider> CreateAsync()
    {
        return Task.FromResult<IBiosProvider>(new ScewinProvider(_scewin, _log));
    }
}