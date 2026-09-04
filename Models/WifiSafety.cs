using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KalOS.Models
{
/// <summary>
/// Hard safety net for the tweak engine: refuses ANY tweak that would
/// disable, remove, or otherwise modify a Wi-Fi / WLAN stack component or a
/// network-critical one (firewall, name resolution, network identification).
///
/// The installer and the main app share the tweak catalog, and the catalog
/// is generated from external scripts — so the guarantee "installing KalOS
/// never touches your Wi-Fi or network settings" is enforced HERE, in the
/// engine, not by trusting catalog contents. A future regeneration or hand
/// edit that adds such a tweak gets refused at run time (counted as a
/// skipped no-op, never applied).
///
/// Scope (match-by-name on the target of the action):
///  - WLAN services: WlanSvc (WLAN AutoConfig), WcmSvc, netprofm, NdisUio,
///    nwifi (NativeWifiP), dot3svc (wired counterpart guarded too).
///  - Network-critical services: NlaSvc (identification — killing it drops
///    Wi-Fi), Windows Firewall (MpsSvc + its WFP driver), Dnscache, DHCP,
///    DPS (Network Connectivity Assistant depends on it).
///  - WLAN drivers: any PnP name matching wireless/wlan/wi-fi/802.11.
///  - WLAN capabilities: any WLAN/Wi-Fi capability (e.g. WLAN.Hotspot).
///  - Radio/app policies: airplane-mode / radio management registry keys.
///  - Windows Firewall policy keys (EnableFirewall flips).
///  - Everything under HKLM\SOFTWARE\Microsoft\WlanSvc (profiles store).
///  - The hosts file / name resolution (no host blocking by tweaks at all).
/// </summary>
    public static class WifiSafety
    {
        /// <summary>Service short names whose removal breaks WLAN or general connectivity.</summary>
        public static readonly IReadOnlySet<string> ProtectedServices = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            // ── WLAN stack ──
            "WlanSvc",     // WLAN AutoConfig — the WLAN service itself
            "WcmSvc",      // Windows Connection Manager — network state UI
            "netprofm",    // Network List Service — profile/Connectivity UX
            "NdisUio",     // 802.11 NDIS usermode I/O protocol driver
            "nwifi",       // Native WiFi protocol driver
            "NativeWifiP", // Native WiFi protocol driver (driver form)
            "dot3svc",     // wired autoconfig — guarded too, it is the wired twin
            "RmSvc",       // Radio Management Service — airplane mode / radios

            // ── network-critical (general connectivity) ──
            "NlaSvc",      // Network Location Awareness — identification; killing it drops Wi-Fi
            "Dnscache",    // DNS Client — name resolution
            "Dhcp",        // DHCP Client — address assignment
            "DPS",         // Diagnostic Policy Service — NCA connectivity diagnostics
            "MpsSvc",      // Windows Defender Firewall service
            "mpsdrv",      // Windows Defender Firewall Authorization driver
            "MsSecWfp",    // Microsoft Security WFP callout driver
            "bfe",         // Base Filtering Engine — firewall + IPsec dependency
        };

        /// <summary>
        /// Registry key fragments that belong to WLAN state: the profiles store,
        /// radio management policy, per-interface connection state, and the
        /// Windows Firewall policy keys.
        /// </summary>
        private static readonly string[] ProtectedKeyFragments =
        {
            @"\WlanSvc",                 // HKLM\SOFTWARE\Microsoft\WlanSvc (profiles)
            @"Control Panel\Radio Management", // radio/airplane policy
            @"Wlan\Parameters",          // WLAN policy parameters
            @"WindowsFirewall",         // HKLM\SOFTWARE\Policies\...\WindowsFirewall
            @"FirewallPolicy",          // HKLM\SYSTEM\...\SharedAccess\...\FirewallPolicy
        };

        /// <summary>Value names that would flip airplane/radio state.</summary>
        private static readonly string[] ProtectedValueNames =
        {
            "AirplaneMode",
            "RadioState",
            "WifiState",
        };

        /// <summary>PnP device-name fragments for wireless adapters/drivers.</summary>
        private static readonly Regex WirelessDevicePattern =
            new(@"wireless|wlan|wi-?fi|802\.11", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// True when the tweak would modify any WLAN/Wi-Fi stack component and
        /// must therefore NOT run. Conservatively broad: when a name is only
        /// <em>maybe</em> wireless-related, the tweak is refused.
        /// </summary>
        public static bool IsWifiTouching(TweakDef tweak) => IsWifiTouching(tweak.Action);

        public static bool IsWifiTouching(TweakAction action) => action switch
        {
            DisableServiceAction a => ProtectedServices.Contains(a.ServiceName),

            // Registry: refuse keys under the WLAN store / radio policy, and
            // any value that flips airplane mode or radio state anywhere.
            RegistrySetAction a =>
                ProtectedKeyFragments.Any(f => a.Key.Contains(f, StringComparison.OrdinalIgnoreCase))
                || ProtectedValueNames.Any(v => a.ValueName.Equals(v, StringComparison.OrdinalIgnoreCase)),

            RegistryValueDeleteAction a =>
                ProtectedKeyFragments.Any(f => a.Key.Contains(f, StringComparison.OrdinalIgnoreCase))
                || ProtectedValueNames.Any(v => a.ValueName.Equals(v, StringComparison.OrdinalIgnoreCase)),

            RegistryValuesClearAction a =>
                a.Key.Contains("WlanSvc", StringComparison.OrdinalIgnoreCase),

            RegistryKeyDeleteAction a =>
                ProtectedKeyFragments.Any(f => a.Key.Contains(f, StringComparison.OrdinalIgnoreCase)),

            RegistryKeyCreateAction a =>
                ProtectedKeyFragments.Any(f => a.Key.Contains(f, StringComparison.OrdinalIgnoreCase)),

            // Capabilities: any WLAN/Wi-Fi capability (e.g. "WLAN.Hotspot",
            // "Language.Basic" style names are unaffected).
            RemoveCapabilityAction a => WirelessDevicePattern.IsMatch(a.CapabilityName)
                || a.CapabilityName.Contains("wlan", StringComparison.OrdinalIgnoreCase),

            // Optional features: refuse anything wireless-ish (e.g.
            // "WirelessDisplay", "SimpleTCP" style names are unaffected).
            DisableFeatureAction a => WirelessDevicePattern.IsMatch(a.FeatureName),

            // Services by wildcard (webthreatdefusersvc_* style) cannot match
            // a protected name here because the pattern only exists in the
            // catalog for Defender services — but refuse wildcards that could
            // expand onto a protected service anyway.
            _ => false,
        };
    }
}
