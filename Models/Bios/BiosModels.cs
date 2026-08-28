using System.Collections.Generic;

namespace KalOS.Models.Bios;

/// <summary>
/// The storage formats a specific BIOS setting exposes. Drives which editor
/// control the UI renders and how "PossibleValues" is interpreted.
/// </summary>
public static class BiosDataType
{
    public const string Enum = "Enum";
    public const string String = "String";
    public const string Integer = "Integer";
    public const string Password = "Password";
    public const string Boolean = "Boolean";
    public const string Unknown = "Unknown";
}

/// <summary>
/// One BIOS/UEFI setting as reported by a vendor WMI provider.
/// Immutable by design; the bindable wrapper for the UI lives in the view model.
/// <see cref="IsSensitive"/> marks boot/security-critical attributes that need an
/// extra acknowledgement before applying.
/// </summary>
public sealed record BiosSetting(
    string Name,
    string CurrentValue,
    string DataType,
    IReadOnlyList<string>? PossibleValues,
    int? MinValue = null,
    int? MaxValue = null,
    bool IsSensitive = false,
    bool IsReadOnly = false,
    IReadOnlyDictionary<string, string>? RawFields = null,
    string? Description = null);

/// <summary>A proposed single-value change, produced by the UI and applied by a provider.</summary>
public sealed record BiosSettingChange(string Name, string NewValue);

/// <summary>Outcome of an apply attempt. Some vendors stage the change and need a reboot to flash.</summary>
public sealed record ApplyResult(bool Success, IReadOnlyList<string> Errors, bool RequiresReboot);

/// <summary>Which OEM BIOS backend this provider talks to.</summary>
public enum BiosVendor
{
    Unknown,
    Dell,
    Lenovo,
    Hp,
    AmiGeneric,
    Insyde,
    Unsupported,
}

/// <summary>
/// Immutable description of the machine the BIOS provider is running on.
/// Used both to pick a provider and to stamp / validate export files.
/// <see cref="FirmwareVendor"/> is the real firmware vendor read from
/// <c>Win32_BIOS.Manufacturer</c> (e.g. "American Megatrends Inc." / "Insyde Corp."),
/// which can differ from the system-integrator <see cref="Manufacturer"/> used to
/// pick the OEM WMI provider.
/// </summary>
public sealed record BiosSystemInfo(
    string Manufacturer,
    string Model,
    string BiosVersion,
    bool IsVirtualMachine,
    string FirmwareVendor = "Unknown",
    string BaseBoardManufacturer = "Unknown",
    string BaseBoardProduct = "Unknown")
{
    public static BiosSystemInfo Unknown { get; } = new("Unknown", "Unknown", "Unknown", false, "Unknown", "Unknown", "Unknown");
}