using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaliteKit.Models.Bios;

namespace KaliteKit.Services.Bios;

/// <summary>
/// In-memory BIOS provider seeded with realistic sample settings. Used for UI
/// development and the unit tests so the page can be exercised without real
/// Dell/Lenovo/HP hardware. Does not touch WMI.
/// </summary>
public sealed class FakeBiosProvider : BiosProviderBase
{
    private sealed class FakeAttr
    {
        public required BiosSetting Setting;
        public required string Value;
    }

    private readonly List<FakeAttr> _attrs;

    public FakeBiosProvider()
    {
        _attrs = new List<FakeAttr>
        {
            New("LegacyBoot", "Disabled", BiosDataType.Enum, new[] { "Enabled", "Disabled" }, sensitive: true),
            New("WakeOnLan", "LANOnly", BiosDataType.Enum, new[] { "Disabled", "LANOnly", "WLANOnly", "All" }),
            New("SataOperation", "AHCI", BiosDataType.Enum, new[] { "AHCI", "RaidOn", "Disabled" }),
            New("UsbDebugsupport", "Enabled", BiosDataType.Enum, new[] { "Enabled", "Disabled" }),
            New("TpmType", "Ptt", BiosDataType.Enum, new[] { "Ptt", "FirwareTpm", "None" }, sensitive: true),
            New("SecureBoot", "Disabled", BiosDataType.Enum, new[] { "Enabled", "Disabled" }, sensitive: true),
            New("AssetTag", "ABC123", BiosDataType.String),
            New("ServiceTag", "X1234", BiosDataType.String),
            New("CpuWattWatts", "45", BiosDataType.Integer, null, 15, 90),
            New("AdminPassword", "Not Set", BiosDataType.Password, sensitive: true),

        };
    }

    private static FakeAttr New(string name, string value, string type, IReadOnlyList<string>? possible = null, int? min = null, int? max = null, bool sensitive = false)
        => new()
        {
            Setting = new BiosSetting(name, value, type, possible, min, max, sensitive),
            Value = value,
        };

    public override BiosVendor SupportedVendor => BiosVendor.Dell;
    public override string DisplayName => "Fake in-memory BIOS provider (development)";

    public override Task<IReadOnlyList<BiosSetting>> GetSettingsAsync(CancellationToken ct = default)
    {
        var snapshot = _attrs
            .Select(a => new BiosSetting(a.Setting.Name, a.Value, a.Setting.DataType, a.Setting.PossibleValues, a.Setting.MinValue, a.Setting.MaxValue, a.Setting.IsSensitive))
            .ToList();
        return Task.FromResult<IReadOnlyList<BiosSetting>>(snapshot);
    }

    public override Task<ApplyResult> ApplySettingsAsync(
        IEnumerable<BiosSettingChange> changes,
        string? supervisorPassword,
        CancellationToken ct = default)
    {
        var list = changes as IReadOnlyList<BiosSettingChange> ?? changes.ToList();
        foreach (var change in list)
        {
            var attr = _attrs.FirstOrDefault(a => string.Equals(a.Setting.Name, change.Name, StringComparison.OrdinalIgnoreCase));
            if (attr is not null) attr.Value = change.NewValue;
        }
        return Task.FromResult(new ApplyResult(true, Array.Empty<string>(), RequiresReboot: false));
    }
}