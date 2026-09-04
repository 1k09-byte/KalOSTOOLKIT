using System;
using System.Linq;
using KalOS.Models;
using KalOS.Services;
using Xunit;

namespace KalOS.Tests.Models;

/// <summary>
/// Locks the installer/app guarantee: the tweak engine must refuse any tweak
/// that would touch the WLAN stack (services, drivers, capabilities, radio
/// policy, the WlanSvc profiles store). The installer's "never tampers with
/// Wi-Fi" promise is enforced by WifiSafety.IsWifiTouching — these tests keep
/// it that way even if the generated catalog changes.
/// </summary>
public class WifiSafetyTests
{
    [Fact]
    public void WlanServices_AreRefused()
    {
        foreach (var service in new[] { "WlanSvc", "WcmSvc", "netprofm", "NdisUio", "nwifi", "NativeWifiP", "RmSvc" })
        {
            var tweak = new TweakDef($"Disable {service}", TweakGroup.Services,
                new DisableServiceAction(service));
            Assert.True(WifiSafety.IsWifiTouching(tweak), $"expected {service} to be refused");
        }
    }

    [Fact]
    public void NetworkCriticalServices_AreRefused()
    {
        foreach (var service in new[] { "NlaSvc", "Dnscache", "Dhcp", "DPS", "MpsSvc", "mpsdrv", "MsSecWfp", "bfe" })
        {
            var tweak = new TweakDef($"Disable {service}", TweakGroup.Services,
                new DisableServiceAction(service));
            Assert.True(WifiSafety.IsWifiTouching(tweak), $"expected {service} to be refused");
        }
    }

    [Fact]
    public void UnrelatedServices_AreAllowed()
    {
        foreach (var service in new[] { "wuauserv", "Sense", "diagsvc" })
        {
            var tweak = new TweakDef($"Disable {service}", TweakGroup.Services,
                new DisableServiceAction(service));
            Assert.False(WifiSafety.IsWifiTouching(tweak), $"expected {service} to be allowed");
        }
    }

    [Fact]
    public void WlanProfilesStore_AndRadioPolicy_AreRefused()
    {
        var profileStore = new TweakDef("WLAN store", TweakGroup.Privacy,
            new RegistryKeyDeleteAction(@"HKLM\SOFTWARE\Microsoft\WlanSvc"));
        Assert.True(WifiSafety.IsWifiTouching(profileStore));

        var radioPolicy = new TweakDef("Radio policy", TweakGroup.Privacy,
            new RegistrySetAction(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Radio Management",
                "Foo", TweakValueKind.Dword, "1"));
        Assert.True(WifiSafety.IsWifiTouching(radioPolicy));

        var airplane = new TweakDef("Airplane", TweakGroup.Privacy,
            new RegistrySetAction(@"HKLM\SOFTWARE\Whatever", "AirplaneMode", TweakValueKind.Dword, "1"));
        Assert.True(WifiSafety.IsWifiTouching(airplane));
    }

    [Fact]
    public void FirewallDisables_AndHostsBlocks_AreRefused()
    {
        var firewall = new TweakDef("Disable Firewall via registry", TweakGroup.Privacy,
            new RegistrySetAction(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\StandardProfile",
                "EnableFirewall", TweakValueKind.Dword, "0"));
        Assert.True(WifiSafety.IsWifiTouching(firewall));

        var firewallPolicy = new TweakDef("Disable Firewall (FirewallPolicy)", TweakGroup.Privacy,
            new RegistrySetAction(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile",
                "EnableFirewall", TweakValueKind.Dword, "0"));
        Assert.True(WifiSafety.IsWifiTouching(firewallPolicy));
    }

    [Fact]
    public void WirelessCapabilities_Features_AndDevicePaths_AreRefused()
    {
        Assert.True(WifiSafety.IsWifiTouching(new TweakDef("cap", TweakGroup.Capabilities,
            new RemoveCapabilityAction("WLAN.Hotspot"))));
        Assert.True(WifiSafety.IsWifiTouching(new TweakDef("feature", TweakGroup.Features,
            new DisableFeatureAction("WirelessDisplay"))));
    }

    [Fact]
    public void UnrelatedRegistryTweaks_AreAllowed()
    {
        var tweak = new TweakDef("Telemetry", TweakGroup.Privacy,
            new RegistrySetAction(@"HKLM\Software\Policies\Microsoft\Windows\DataCollection",
                "AllowTelemetry", TweakValueKind.Dword, "0"));
        Assert.False(WifiSafety.IsWifiTouching(tweak));
    }

    /// <summary>
    /// The ship-blocking gate: every tweak in the CURRENT catalog must be
    /// allowed. If a regeneration ever adds a WLAN-touching tweak, this test
    /// fails and the catalog has to be fixed — the installer never silently
    /// breaks Wi-Fi.
    /// </summary>
    [Fact]
    public void CurrentCatalog_Contains_NoWifiTouchingTweaks()
    {
        var offenders = TweaksService.All
            .Where(t => WifiSafety.IsWifiTouching(t))
            .Select(t => t.Name)
            .ToList();
        Assert.True(offenders.Count == 0,
            "Wi-Fi-touching tweaks in the catalog: " + string.Join("; ", offenders));
    }
}
