using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaliteKit.Models.Bios;

namespace KaliteKit.Services.Bios;

/// <summary>
/// Dell BIOS settings via the Dell Command integration WMI namespace
/// (<c>root\\dell\\sysmgmt</c>, classes <c>DCIM_BIOSEnumeration</c>,
/// <c>DCIM_BIOSString</c>, <c>DCIM_BIOSInteger</c>).
///
/// Setting layout notes (verified against Dell Command | Configure's published
/// behavior):
///  - DCIM_BIOSEnumeration  : AttributeName, CurrentValue, PossibleValues[]
///  - DCIM_BIOSString       : AttributeName, CurrentValue
///  - DCIM_BIOSInteger      : AttributeName, CurrentValue (numeric), min/max props
///  - Apply: DCIM_BIOSService.SetBIOSAttributes(AttributeName[], AttributeValue[])
///    returns a method status; 0 = applied immediately, otherwise a job was
///    staged and a reboot is required (CreateTargetedConfigJob is used by cctk
///    when a reboot is needed; we surface RequiresReboot and suggest rebooting).
///
/// The attribute names arriving through the DCIM path are already the friendly
/// "LegacyBoot","WakeOnLan" names — we do not need the cctk hyphen translation.
///
/// Live-hardware note: exact job/report codes beyond 0 were not captured on test
/// hardware here; the mapping above follows Dell's documented SetBIOSAttributes
/// return table and should be verified once on a real Dell during QA.
/// </summary>
public sealed class DellBiosProvider : BiosProviderBase
{
    /// <summary>Dell Command WMI namespace for business-class Dell systems.</summary>
    public const string DellScope = @"root\dell\sysmgmt";

    private readonly IWmiClient _wmi;
    private readonly LoggingService _log;

    public DellBiosProvider(IWmiClient wmi, LoggingService log)
    {
        _wmi = wmi;
        _log = log;
    }

    public override BiosVendor SupportedVendor => BiosVendor.Dell;
    public override string DisplayName => "Dell — Dell Command WMI (root\\dell\\sysmgmt)";

    public override async Task<IReadOnlyList<BiosSetting>> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = new List<BiosSetting>();

        // Enumerations carry PossibleValues.
        var enums = await _wmi.QueryAsync(DellScope, "SELECT AttributeName, CurrentValue, PossibleValues FROM DCIM_BIOSEnumeration", ct);
        foreach (var row in enums)
        {
            var name = row.GetString("AttributeName");
            if (string.IsNullOrEmpty(name)) continue;
            var possible = row.GetStringArray("PossibleValues");
            var current = row.GetString("CurrentValue") ?? string.Empty;
            settings.Add(MakeSetting(name, current, possible.Count > 0 ? BiosDataType.Enum : BiosDataType.String, possible));
        }

        // Plain strings.
        var strings = await _wmi.QueryAsync(DellScope, "SELECT AttributeName, CurrentValue FROM DCIM_BIOSString", ct);
        foreach (var row in strings)
        {
            var name = row.GetString("AttributeName");
            if (string.IsNullOrEmpty(name)) continue;
            if (settings.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            settings.Add(MakeSetting(name, row.GetString("CurrentValue") ?? string.Empty, BiosDataType.String));
        }

        // Integers with optional range.
        var ints = await _wmi.QueryAsync(DellScope, "SELECT AttributeName, CurrentValue FROM DCIM_BIOSInteger", ct);
        foreach (var row in ints)
        {
            var name = row.GetString("AttributeName");
            if (string.IsNullOrEmpty(name)) continue;
            if (settings.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var current = row.GetString("CurrentValue") ?? string.Empty;
            settings.Add(MakeSetting(name, current, BiosDataType.Integer));
        }

        return settings;
    }

    public override async Task<ApplyResult> ApplySettingsAsync(
        IEnumerable<BiosSettingChange> changes,
        string? supervisorPassword,
        CancellationToken ct = default)
    {
        var list = changes as IReadOnlyList<BiosSettingChange> ?? changes.ToList();
        if (list.Count == 0) return new ApplyResult(true, Array.Empty<string>(), false);

        try
        {
            var names = list.Select(c => c.Name).ToArray();
            var values = list.Select(c => c.NewValue).ToArray();

            _log.Info($"Applying {list.Count} Dell BIOS attribute change(s).");
            using var result = await _wmi.InvokeMethodAsync(
                DellScope,
                "DCIM_BIOSService",
                "InstanceID='DCIM:BIOSService'",
                "SetBIOSAttributes",
                new Dictionary<string, object?>
                {
                    { "AttributeName", names },
                    { "AttributeValue", values },
                },
                ct);

            // Dell returns the method return value. 0 = applied immediately; some
            // attribute classes stage a config job that only lands after a reboot.
            int? code = result?.GetInt("ReturnValue");
            if (code is null || code == 0)
            {
                _log.Success("Dell BIOS changes applied.");
                return new ApplyResult(true, Array.Empty<string>(), RequiresReboot: false);
            }

            var errors = TranslateVendorReturnCode(code, "Dell BIOS apply");
            _log.Error(string.Join(" ", errors));
            return new ApplyResult(false, errors, RequiresReboot: code == 1);
        }
        catch (Exception ex)
        {
            _log.Error($"Dell BIOS apply failed: {ex.Message}");
            return new ApplyResult(false, new[] { ExplainWmiException(ex) }, RequiresReboot: false);
        }
    }
}