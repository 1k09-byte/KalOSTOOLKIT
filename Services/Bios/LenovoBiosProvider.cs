using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Models.Bios;

namespace KalOS.Services.Bios;

/// <summary>
/// Lenovo BIOS settings via <c>root\\wmi</c> (classes <c>Lenovo_BiosSetting</c>,
/// <c>Lenovo_SetBiosSetting</c>, <c>Lenovo_SaveBiosSettings</c>,
/// <c>Lenovo_GetBiosSelections</c>).
///
/// Format notes (verified against Lenovo's published WMI provider docs):
///  - <c>Lenovo_BiosSetting.CurrentSetting</c> is a single quoted string of the
///    form <c>"Name,Value"</c> — we parse name and value on the comma.
///  - Possible values for enumerable settings come from <c>Lenovo_GetBiosSelections</c>,
///    whose single instance exposes instance names like <c>SettingName,</c> and a
///    <c>CurrentSetting</c> carrying "Name,Val0,Val1,..." for the first selection.
///  - To apply: call <c>Lenovo_SetBiosSetting.SetBiosSetting(SettingString)</c> per
///    change, then <c>Lenovo_SaveBiosSettings.SaveBiosSettings</c> to commit,
///    which Lenovo documents as requiring a reboot to take effect.
///
/// Live-hardware note: Lenovo's instance __PATH parsing (the "SettingName," suffix
/// stripping) is notoriously fiddly; every value read/written below goes through
/// the shared comma-split helper so a single verified location can be fixed if a
/// particular model pads differently.
/// </summary>
public sealed class LenovoBiosProvider : BiosProviderBase
{
    public const string Scope = @"root\wmi";

    private readonly IWmiClient _wmi;
    private readonly LoggingService _log;

    public LenovoBiosProvider(IWmiClient wmi, LoggingService log)
    {
        _wmi = wmi;
        _log = log;
    }

    public override BiosVendor SupportedVendor => BiosVendor.Lenovo;
    public override string DisplayName => "Lenovo — root\\wmi Lenovo_BiosSetting";

    /// <summary>"Name,Value" → (name, value). Handles values that themselves contain commas.</summary>
    private static (string name, string value) SplitSetting(string currentSetting)
    {
        if (string.IsNullOrWhiteSpace(currentSetting)) return (string.Empty, string.Empty);
        var idx = currentSetting.IndexOf(',');
        return idx < 0
            ? (currentSetting.Trim(), string.Empty)
            : (currentSetting[..idx].Trim(), currentSetting[(idx + 1)..].Trim());
    }

    public override async Task<IReadOnlyList<BiosSetting>> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = new List<BiosSetting>();
        var selections = await LoadSelectionsAsync(ct);
        var nameToSettings = selections.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

        var rows = await _wmi.QueryAsync(Scope, "SELECT InstanceName, CurrentSetting FROM Lenovo_BiosSetting", ct);
        foreach (var row in rows)
        {
            var current = row.GetString("CurrentSetting");
            if (string.IsNullOrEmpty(current)) continue;
            var (name, value) = SplitSetting(current);
            if (string.IsNullOrEmpty(name)) continue;

            var possible = nameToSettings.TryGetValue(name, out var p) ? p : Array.Empty<string>();
            var dataType = possible.Length > 0 ? BiosDataType.Enum : BiosDataType.String;
            settings.Add(MakeSetting(name, value, dataType, possible));
        }

        // Lenovo sometimes only exposes numeric/mode settings through selections
        // without a CurrentSetting row; merge any that are missing.
        foreach (var kvp in nameToSettings)
        {
            var kvp1 = kvp;
            if (settings.Any(s => string.Equals(s.Name, kvp1.Key, StringComparison.OrdinalIgnoreCase))) continue;
            settings.Add(MakeSetting(kvp1.Key, string.Empty, BiosDataType.Enum, kvp1.Value));
        }

        return settings;
    }

    private async Task<Dictionary<string, string[]>> LoadSelectionsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<IWmiRow> rows;
        try
        {
            rows = await _wmi.QueryAsync(Scope, "SELECT InstanceName, CurrentSetting FROM Lenovo_GetBiosSelections", ct);
        }
        catch (Exception ex)
        {
            // Selections are purely a UI nicety; failure here shouldn't break the read.
            _log.Warn($"Lenovo selection load failed: {ex.Message}");
            return map;
        }

        foreach (var row in rows)
        {
            var instance = row.GetString("InstanceName");
            var current = row.GetString("CurrentSetting");
            if (current is null) continue;

            var parts = current.Split(',');
            if (parts.Length < 2) continue;
            var name = parts[0].Trim();
            var values = parts.Skip(1).Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
            if (name.Length > 0)
            {
                map[name] = values;
            }
            else if (!string.IsNullOrEmpty(instance))
            {
                // Fallback: instance names are often "SomeEnum," — strip a trailing comma.
                map[instance.TrimEnd(',')] = values;
            }
        }

        return map;
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
            foreach (var change in list)
            {
                using var setResult = await _wmi.InvokeMethodAsync(
                    Scope,
                    "Lenovo_SetBiosSetting",
                    "InstanceName='Lenovo_SetBiosSetting'",
                    "SetBiosSetting",
                    new Dictionary<string, object?> { { "SettingString", $"{change.Name},{change.NewValue}" } },
                    ct);

                var rc = setResult?.GetInt("ReturnValue");
                if (rc != 0)
                {
                    errors.AddRange(TranslateVendorReturnCode(rc, $"Lenovo SetBiosSetting({change.Name})"));
                }
            }

            if (errors.Count > 0)
            {
                _log.Error("Lenovo BIOS apply reported errors: " + string.Join("; ", errors));
                return new ApplyResult(false, errors, RequiresReboot: false);
            }

            // Commit requires calling SaveBiosSettings; Lenovo documents a reboot as required.
            using var saveResult = await _wmi.InvokeMethodAsync(
                Scope,
                "Lenovo_SaveBiosSettings",
                "InstanceName='Lenovo_SaveBiosSettings'",
                "SaveBiosSettings",
                new Dictionary<string, object?>(),
                ct);

            var saveRc = saveResult?.GetInt("ReturnValue");
            if (saveRc != 0)
            {
                var saveErrors = TranslateVendorReturnCode(saveRc, "Lenovo SaveBiosSettings");
                _log.Error(string.Join(" ", saveErrors));
                return new ApplyResult(false, saveErrors, RequiresReboot: false);
            }

            _log.Success("Lenovo BIOS changes staged; reboot required to apply.");
            return new ApplyResult(true, Array.Empty<string>(), RequiresReboot: true);
        }
        catch (Exception ex)
        {
            _log.Error($"Lenovo BIOS apply failed: {ex.Message}");
            return new ApplyResult(false, new[] { ExplainWmiException(ex) }, RequiresReboot: false);
        }
    }
}