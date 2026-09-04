using KalOS.Models;
using KalOS.Services;

namespace KalOS.Tests.Services;

/// <summary>
/// Pure-helper tests for GpuDetectionService: the generic-name detection,
/// DeviceDesc friendly-name parsing, and vendor resolution that make an
/// AMD/NVIDIA adapter visible even when its driver isn't installed.
/// </summary>
public class GpuDetectionServiceTests
{
    // ── IsGenericDisplayName ─────────────────────────────────────────────

    [Theory]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("Microsoft Basic Display Adapter (Microsoft Corporation - WDDM)")]
    [InlineData("Microsoft Remote Display Adapter")]
    [InlineData("Unknown GPU")]
    [InlineData("")]
    [InlineData(null)]
    public void IsGenericDisplayName_DetectsPlaceholderNames(string? name)
        => Assert.True(GpuDetectionService.IsGenericDisplayName(name));

    [Theory]
    [InlineData("AMD Radeon RX 7800 XT")]
    [InlineData("NVIDIA GeForce RTX 4070 SUPER")]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Microsoft Hyper-V Video")]
    public void IsGenericDisplayName_LeavesRealNamesAlone(string name)
        => Assert.False(GpuDetectionService.IsGenericDisplayName(name));

    // ── ParseDeviceDescFriendlyName ─────────────────────────────────────

    [Fact]
    public void ParseDeviceDescFriendlyName_TakesPartAfterSemicolon()
    {
        string? name = GpuDetectionService.ParseDeviceDescFriendlyName(
            "@oem15.inf,%amdxxx%;AMD Radeon(TM) Graphics");
        Assert.Equal("AMD Radeon(TM) Graphics", name);
    }

    [Fact]
    public void ParseDeviceDescFriendlyName_TrimsWhitespace()
    {
        string? name = GpuDetectionService.ParseDeviceDescFriendlyName(
            "@oem20.inf,%nvxxx%; NVIDIA GeForce RTX 4070 SUPER ");
        Assert.Equal("NVIDIA GeForce RTX 4070 SUPER", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@oem15.inf,%amdxxx%;")]
    [InlineData("no semicolon here")]
    public void ParseDeviceDescFriendlyName_ReturnsNullWhenUnparsable(string? deviceDesc)
        => Assert.Null(GpuDetectionService.ParseDeviceDescFriendlyName(deviceDesc));

    // ── VendorOf ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"PCI\VEN_1002&DEV_744C", null, "AMD")]
    [InlineData(@"PCI\VEN_1002&DEV_164E&SUBSYS_08D51028", "amdwddmg", "AMD")]
    [InlineData(null, "amdkmdag", "AMD")]
    [InlineData(@"PCI\VEN_10DE&DEV_2684", null, "NVIDIA")]
    [InlineData(null, "nvlddmkm", "NVIDIA")]
    [InlineData(@"PCI\VEN_8086&DEV_4680", null, "Intel")]
    [InlineData(null, "igfx", "Intel")]
    [InlineData(null, "igd", "Intel")]
    [InlineData("", null, "")]
    [InlineData(@"PCI\VEN_1414&DEV_008E", null, "")]
    public void VendorOf_ResolvesFromHardwareIdOrService(string? vendorId, string? service, string expected)
        => Assert.Equal(expected, GpuDetectionService.VendorOf(vendorId ?? "", service));

    // ── Integrated naming ────────────────────────────────────────────────

    [Fact]
    public void VendorOf_CaseInsensitive()
        => Assert.Equal("AMD", GpuDetectionService.VendorOf(@"PCI\VEN_1002&DEV_13C0", "AMDWDDMG"));

    [Fact]
    public void ResolveDisplayName_RealNamePassesThrough()
    {
        string name = GpuDetectionService.ResolveDisplayName(
            "AMD Radeon RX 7800 XT", null, @"PCI\VEN_1002&DEV_744C", "amdwddmg");
        Assert.Equal("AMD Radeon RX 7800 XT", name);
    }

    [Fact]
    public void ResolveDisplayName_UsesDeviceDescWhenItIsReal()
    {
        string name = GpuDetectionService.ResolveDisplayName(
            "Microsoft Basic Display Adapter",
            "@oem15.inf,%amdxxx%;AMD Radeon(TM) Graphics",
            @"PCI\VEN_1002&DEV_164E", "amdwddmg");
        Assert.Equal("AMD Radeon(TM) Graphics", name);
    }

    [Fact]
    public void ResolveDisplayName_IgnoresGenericDeviceDescAndLabelsVendor()
    {
        // The inbox basic display driver's DeviceDesc is itself the generic
        // placeholder — the vendor label must win in that case.
        string name = GpuDetectionService.ResolveDisplayName(
            "Microsoft Basic Display Adapter",
            "@display.inf,%BasicDisplay%;Microsoft Basic Display Adapter",
            @"PCI\VEN_1002&DEV_164E", "amdwddmg");
        Assert.Equal("AMD Radeon (basic display — driver not installed)", name);
    }

    [Fact]
    public void ResolveDisplayName_LabelsNvidiaFromServiceWhenNoHardwareId()
    {
        string name = GpuDetectionService.ResolveDisplayName(
            "Microsoft Basic Display Adapter", null, "", "nvlddmkm");
        Assert.Equal("NVIDIA GPU (basic display — driver not installed)", name);
    }

    [Fact]
    public void ResolveDisplayName_KeepsNameWhenVendorUnknown()
    {
        string name = GpuDetectionService.ResolveDisplayName(
            "Microsoft Basic Display Adapter", null, "", null);
        Assert.Equal("Microsoft Basic Display Adapter", name);
    }

    // ── Laptop / mobile GPU detection ─────────────────────────────────

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce GTX 1660 Ti", false)]        // no M suffix — desktop card
    [InlineData("NVIDIA GeForce GTX 980M", true)]            // classic mobile M suffix
    [InlineData("NVIDIA GeForce RTX 3080 Ti Mobile", true)]
    [InlineData("NVIDIA GeForce RTX 2080 Super", false)]
    [InlineData("GeForce RTX 4090 Max-Q", true)]             // Max-Q designs are notebook-only
    [InlineData("NVIDIA GeForce MX550", true)]               // MX series is notebook-only
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("Intel(R) UHD Graphics", false)]
    public void NameContainsMobileMarker_ClassifiesMobileModelNames(string name, bool expected)
        => Assert.Equal(expected, GpuInfo.NameContainsMobileMarker(name));

    [Fact]
    public void IsMobileGpu_NameMarkerWinsEvenWhenChassisUnknown()
    {
        var gpu = new GpuInfo { Name = "NVIDIA GeForce RTX 4060 Laptop GPU" };
        Assert.True(gpu.IsMobileGpu);
    }

    [Fact]
    public void IsMobileGpu_LaptopChassisFlagsDesktopNamedCard()
    {
        // An OEM box WMI names without the Laptop/Mobile/Notebook words still
        // resolves via the machine's chassis detection — this is the case the
        // old name-only check silently got wrong.
        var gpu = new GpuInfo { Name = "NVIDIA GeForce RTX 4070", IsLaptop = true };
        Assert.True(gpu.IsMobileGpu);
    }

    [Fact]
    public void IsMobileGpu_DesktopChassisAndDesktopNameIsFalse()
    {
        var gpu = new GpuInfo { Name = "NVIDIA GeForce RTX 4070", IsLaptop = false };
        Assert.False(gpu.IsMobileGpu);
    }

    [Fact]
    public void PortableChassis_CoversLaptopAndTabletTypes()
    {
        Assert.Contains(9, KalOS.Services.PortableChassis.Types);   // Laptop
        Assert.Contains(10, KalOS.Services.PortableChassis.Types);  // Notebook
        Assert.Contains(31, KalOS.Services.PortableChassis.Types);  // Convertible
        Assert.Contains(32, KalOS.Services.PortableChassis.Types);  // Detachable
        Assert.DoesNotContain(3, KalOS.Services.PortableChassis.Types); // Desktop
    }
}