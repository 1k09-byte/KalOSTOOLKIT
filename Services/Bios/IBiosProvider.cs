using System.Collections.Generic;
using System.Threading.Tasks;
using KalOS.Models.Bios;

namespace KalOS.Services.Bios;

/// <summary>
/// Abstraction over one OEM's BIOS/UEFI configuration backend. The WinUI view
/// model only ever talks to this interface — never to WMI directly.
///
/// Contract notes:
///  - <see cref="GetSettingsAsync"/> is a read and should work unelevated.
///  - <see cref="ApplySettingsAsync"/> writes and may require elevation; it MUST
///    both check .NET exceptions AND the vendor's return-code property, because
///    Dell/Lenovo/HP commonly return a non-zero status code in the result object
///    without throwing.
///  - Some vendors stage changes and only flash them on reboot
///    (<see cref="ApplyResult.RequiresReboot"/>).
/// </summary>
public interface IBiosProvider
{
    BiosVendor SupportedVendor { get; }

    /// <summary>Human-friendly label, e.g. "Dell … syssysmgmt WMI".</summary>
    string DisplayName { get; }

    /// <summary>Reads every setting the vendor exposes (enumerations, strings, integers).</summary>
    Task<IReadOnlyList<BiosSetting>> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="changes"/> using <paramref name="supervisorPassword"/>
    /// if the vendor requires it. Returns success plus whether a reboot is needed.
    /// </summary>
    Task<ApplyResult> ApplySettingsAsync(
        IEnumerable<BiosSettingChange> changes,
        string? supervisorPassword,
        CancellationToken ct = default);
}

/// <summary>
/// A provider that detected no usable BIOS backend. Always exposes an empty
/// setting list and reports a clear reason for the UI to surface.
/// </summary>
public interface IUnsupportedBiosProvider : IBiosProvider
{
    string Reason { get; }
}