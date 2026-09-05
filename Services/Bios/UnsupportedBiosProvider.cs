using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KaliteKit.Models.Bios;

namespace KaliteKit.Services.Bios;

/// <summary>
/// Fallback provider for machines where no supported vendor WMI BIOSS backend
/// exists (generic AMI/Insyde boards, unrecognized OEMs, or virtual machines).
/// It exposes no settings and always returns a clear read-only reason so the UI
/// can tell the user why BIOS configuration is unavailable without pretending.
/// </summary>
public sealed class UnsupportedBiosProvider : BiosProviderBase, IUnsupportedBiosProvider
{
    private readonly string _reason;

    public UnsupportedBiosProvider(string reason) => _reason = reason;

    public string Reason => _reason;

    public override BiosVendor SupportedVendor => BiosVendor.Unsupported;
    public override string DisplayName => "Unsupported hardware — read-only";

    public override Task<IReadOnlyList<BiosSetting>> GetSettingsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BiosSetting>>(Array.Empty<BiosSetting>());

    public override Task<ApplyResult> ApplySettingsAsync(
        IEnumerable<BiosSettingChange> changes,
        string? supervisorPassword,
        CancellationToken ct = default)
        => Task.FromResult(new ApplyResult(false, new[] { _reason }, RequiresReboot: false));
}