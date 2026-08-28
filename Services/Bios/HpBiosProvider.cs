using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Models.Bios;

namespace KalOS.Services.Bios;

/// <summary>
/// HP BIOS/UEFI settings via <c>root\\hp\\instrumentedBIOS</c>.
///
/// Layout notes (verified against HP's published Provider for BIOS Management):
///  - <c>HP_BIOSSetting</c>        : Name, Value, Path (Path is the bitness key: "user"/"hprp")
///  - <c>HP_BIOSEnumeration</c>    : Name, Value, PossibleValues[]
///  - <c>HP_BIOSString</c>         : Name, Value
///  - <c>HP_BIOSInteger</c>        : Name, Value
///  - <c>HP_BIOSPassword</c>       : password-backed attributes
///  - Apply: <c>HP_BIOSSettingInterface.SetBIOSSetting(Name, Value, Password)</c>.
///    The instance is commonly reached as "HPBIOS_BIOSSettingInterface.InstanceID
///    = 'HPBIOS_BIOSSettingInterface'". 0 on success; non-zero otherwise.
///
/// The older HP_BIOSSettingInterface singleton key differs subtly across firmware
/// generations — the WHERE clause below is the most widely reported one.
/// </summary>
public sealed class HpBiosProvider : BiosProviderBase
{
    public const string Scope = @"root\hp\instrumentedBIOS";

    private readonly IWmiClient _wmi;
    private readonly LoggingService _log;

    public HpBiosProvider(IWmiClient wmi, LoggingService log)
    {
        _wmi = wmi;
        _log = log;
    }

    public override BiosVendor SupportedVendor => BiosVendor.Hp;
    public override string DisplayName => "HP — root\\hp\\instrumentedBIOS";

    public override async Task<IReadOnlyList<BiosSetting>> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = new List<BiosSetting>();

        IReadOnlyList<IWmiRow> enums;
        try
        {
            enums = await _wmi.QueryAsync(Scope, "SELECT Name, Value, PossibleValues FROM HP_BIOSEnumeration", ct);
        }
        catch (Exception ex)
        {
            _log.Warn($"HP BIOSEnumeration read failed: {ex.Message}");
            enums = Array.Empty<IWmiRow>();
        }

        foreach (var row in enums)
        {
            var name = row.GetString("Name");
            if (string.IsNullOrEmpty(name)) continue;
            var possible = row.GetStringArray("PossibleValues");
            var value = row.GetString("Value") ?? string.Empty;
            settings.Add(MakeSetting(name, value, possible.Count > 0 ? BiosDataType.Enum : BiosDataType.String, possible));
        }

        // Strings not already covered by an enum of the same name.
        var strings = await QuerySafelyAsync("SELECT Name, Value FROM HP_BIOSString");
        foreach (var row in strings)
        {
            var name = row.GetString("Name");
            if (string.IsNullOrEmpty(name)) continue;
            if (settings.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            settings.Add(MakeSetting(name, row.GetString("Value") ?? string.Empty, BiosDataType.String));
        }

        // Integers.
        var ints = await QuerySafelyAsync("SELECT Name, Value FROM HP_BIOSInteger");
        foreach (var row in ints)
        {
            var name = row.GetString("Name");
            if (string.IsNullOrEmpty(name)) continue;
            if (settings.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var value = row.GetString("Value");
            var min = row.GetInt("MinValue");
            var max = row.GetInt("MaxValue");
            settings.Add(MakeSetting(name, value ?? string.Empty, BiosDataType.Integer, null, min, max));
        }

        return settings;
    }

    private async Task<IReadOnlyList<IWmiRow>> QuerySafelyAsync(string wql)
    {
        try
        {
            return await _wmi.QueryAsync(Scope, wql);
        }
        catch (Exception ex)
        {
            _log.Warn($"HP BIOS read failed for {wql}: {ex.Message}");
            return Array.Empty<IWmiRow>();
        }
    }

    public override async Task<ApplyResult> ApplySettingsAsync(
        IEnumerable<BiosSettingChange> changes,
        string? supervisorPassword,
        CancellationToken ct = default)
    {
        var list = changes as IReadOnlyList<BiosSettingChange> ?? changes.ToList();
        if (list.Count == 0) return new ApplyResult(true, Array.Empty<string>(), false);

        var errors = new List<string>();
        try
        {
            // HP SetBIOSSetting applies one attribute per call and requires the
            // current BIOS admin password as the "Password" parameter.
            foreach (var change in list)
            {
                var inParams = new Dictionary<string, object?>
                {
                    { "Name", change.Name },
                    { "Value", change.NewValue },
                    { "Password", supervisorPassword },
                };

                using var result = await _wmi.InvokeMethodAsync(
                    Scope,
                    "HP_BIOSSettingInterface",
                    "InstanceID='HPBIOS_BIOSSettingInterface'",
                    "SetBIOSSetting",
                    inParams,
                    ct);

                var rc = result?.GetInt("ReturnValue");
                var surplus = result?.GetString("Message");
                if (rc != 0)
                {
                    errors.AddRange(TranslateVendorReturnCode(rc, $"HP SetBIOSSetting({change.Name})"));
                    if (!string.IsNullOrEmpty(surplus)) errors.Add(surplus!);
                }
            }

            if (errors.Count > 0)
            {
                _log.Error("HP BIOS apply reported errors: " + string.Join("; ", errors));
                return new ApplyResult(false, errors, RequiresReboot: false);
            }

            _log.Success("HP BIOS changes applied.");
            // HP sets most attributes immediately; not flagged as requiring reboot.
            return new ApplyResult(true, Array.Empty<string>(), RequiresReboot: false);
        }
        catch (Exception ex)
        {
            _log.Error($"HP BIOS apply failed: {ex.Message}");
            return new ApplyResult(false, new[] { ExplainWmiException(ex) }, RequiresReboot: false);
        }
    }
}