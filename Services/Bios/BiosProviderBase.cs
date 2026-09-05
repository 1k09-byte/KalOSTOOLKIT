using System;
using System.Collections.Generic;
using System.Linq;
using KaliteKit.Models.Bios;

namespace KaliteKit.Services.Bios;

/// <summary>
/// Shared helpers for concrete WMI BIOS providers: the "dangerous setting"
/// heuristic, WMI HRESULT translation, and normalized return-code checking.
/// </summary>
public abstract class BiosProviderBase : IBiosProvider
{
    public abstract BiosVendor SupportedVendor { get; }

    public abstract string DisplayName { get; }

    public abstract Task<IReadOnlyList<BiosSetting>> GetSettingsAsync(CancellationToken ct = default);

    public abstract Task<ApplyResult> ApplySettingsAsync(
        IEnumerable<BiosSettingChange> changes,
        string? supervisorPassword,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a setting dangerous when its name references anything that can prevent
    /// boot or lock a user out. Deriving this keeps the list from going stale as
    /// vendors add new attributes.
    /// </summary>
    protected static bool IsSensitiveName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.ToLowerInvariant();
        // Boot-critical / can prevent the machine from starting.
        if (n.Contains("secureboot") || n.Contains("boot order") || n.Contains("boot sequence")
            || n.Contains("bootmode") || n.Contains("failsafe") || n.Contains("uart mode"))
            return true;
        // Firmware-level security flags.
        if (n.Contains("tpm") || n.Contains("ptt") || n.Contains("bitlocker") || n.Contains("vt")
            || n.Contains("virtualization") || n.Contains("smm") || n.Contains("uefi")
            || n.Contains("legacy") || n.Contains("csm"))
            return true;
        // Anything that can lock the current user out of Setup.
        if (n.Contains("password") || n.Contains("admin") || n.Contains("supervisor")
            || n.Contains("master") || n.Contains("ownership tag") || n.Contains("asset tag thef"))
            return true;
        return false;
    }

    /// <summary>
    /// Inspects the vendor return-code property that WMI methods typically return
    /// in the method result without throwing. Returns a human error list to report,
    /// or an empty list when the code indicates success.
    /// </summary>
    protected static IReadOnlyList<string> TranslateVendorReturnCode(int? returnCode, string context)
    {
        if (returnCode is null) return Array.Empty<string>();
        return returnCode switch
        {
            0 => Array.Empty<string>(),
            1 => new[] { $"{context}: the change was staged but a system reboot is required to apply it." },
            4096 => new[] { $"{context}: this BIOS backend does not support the requested operation." },
            4 or 5 => new[] { $"{context}: permission denied. Run KaliteKit elevated (as Administrator)." },
            109 => new[] { $"{context}: the TPM is in use (e.g. by BitLocker) and rejected the change." },
            _ => new[] { $"{context}: the BIOS reported return code {returnCode}." },
        };
    }

    /// <summary>Translates a generic WMI COMException into a readable failure string.</summary>
    protected static string ExplainWmiException(Exception ex)
    {
        var h = System.Runtime.InteropServices.Marshal.GetHRForException(ex);
        return h switch
        {
            unchecked((int)0x80041002) or -2147217406 => "The requested BIOS attribute does not exist on this machine.",
            unchecked((int)0x80041003) or -2147217405 => "Access denied querying the BIOS provider. Try running elevated.",
            unchecked((int)0x8004100E) or -2147217394 => "The BIOS WMI namespace could not be reached. The vendor provider may not be installed.",
            unchecked((int)0x8004101A) or -2147217382 => "Invalid method parameter sent to the BIOS provider.",
            _ => ex.Message,
        };
    }

    /// <summary>Combines the current value with possible values for display/validation.</summary>
    protected static BiosSetting MakeSetting(
        string name,
        string currentValue,
        string dataType,
        IReadOnlyList<string>? possible = null,
        int? min = null,
        int? max = null,
        bool sensitive = false)
        => new(name, currentValue, dataType, possible, min, max, IsSensitiveName(name) || sensitive);
}