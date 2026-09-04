using System;
using System.Collections.Generic;
using System.Linq;
using KalOS.Models;

namespace KalOS.Services
{
    /// <summary>
    /// The installer's final connectivity-repair pass — the LAST registry
    /// writes the wizard performs, after every tweak, driver, software and
    /// Windhawk step.
    ///
    /// Two jobs, in order:
    ///
    /// 1. <b>Keep-alives</b> — re-assert Start=2/3 on everything the debloat
    ///    batches must never take down (Xbox, anti-cheat, audio, Bluetooth,
    ///    Store deps, the whole Wi-Fi/network keep list). Any stale Disabled
    ///    value left by an earlier debloat run gets corrected here.
    /// 2. <b>Wi-Fi fix</b> — the machine-generated KalOS_WiFi_Fix restore:
    ///    firewall service + filter driver back to Auto, firewall ON for all
    ///    three profiles. Structurally guaranteed to be the plan's final
    ///    entries, so nothing can re-break the firewall afterwards.
    ///
    /// These writes intentionally bypass <see cref="TweaksService"/>: the
    /// engine's <see cref="WifiSafety"/> guard refuses ALL firewall/WLAN
    /// registry writes (that is what protects the tweaks catalog), while this
    /// pass is the restoration half — turning the firewall back ON, never off.
    /// Every entry here only ever re-enables; nothing in this plan can disable
    /// a service or flip a protection off.
    /// </summary>
    public static class ConnectivityRepairPlan
    {
        /// <summary>One ordered registry write in the plan.</summary>
        public sealed record PlanEntry(
            string Name,
            string Key,
            string ValueName,
            TweakValueKind Kind,
            string Data);

        /// <summary>The ordered plan. Never reorder — the Wi-Fi fix is last.</summary>
        public static IReadOnlyList<PlanEntry> OrderedPlan { get; } = Build();

        private static IReadOnlyList<PlanEntry> Build()
        {
            var plan = new List<PlanEntry>();

            // ── 1. Keep-alives: only ever write Start=2/3 on EXISTING services ──
            void KeepAlive(string service, int start) =>
                plan.Add(new PlanEntry(
                    $"Keep alive: {service} = {(start == 2 ? "Auto" : "Manual")}",
                    $@"HKLM\SYSTEM\CurrentControlSet\Services\{service}",
                    "Start", TweakValueKind.Dword, start.ToString()));

            // Xbox stack (sign-in, save, networking — anti-cheat depends on these)
            KeepAlive("XblAuthManager", 3);
            KeepAlive("XblGameSave", 3);
            KeepAlive("XboxGipSvc", 3);
            KeepAlive("XboxNetApiSvc", 3);
            // Anti-cheat / gaming
            KeepAlive("EasyAntiCheat_EOS", 3);
            KeepAlive("EpicOnlineServices", 3);
            KeepAlive("GameInputSvc", 3);
            KeepAlive("GraphicsPerfSvc", 3);
            KeepAlive("NVDisplay.ContainerLocalSystem", 3);
            KeepAlive("Steam Client Service", 3);
            KeepAlive("BcastDVRUserService", 3);
            // Store / Xbox dependencies
            KeepAlive("StateRepository", 2);
            KeepAlive("ClipSVC", 3);
            KeepAlive("TokenBroker", 3);
            KeepAlive("LicenseManager", 3);
            KeepAlive("AppXSvc", 3);
            KeepAlive("sppsvc", 3);
            // Audio
            KeepAlive("AudioEndpointBuilder", 2);
            KeepAlive("Audiosrv", 2);
            KeepAlive("RtkAudioUniversalService", 3);
            // Display
            KeepAlive("DispBrokerDesktopSvc", 3);
            // Network / Wi-Fi (the WiFi-safe guarantee: these stay ON)
            KeepAlive("WlanSvc", 2);
            KeepAlive("WwanSvc", 3);
            KeepAlive("NlaSvc", 3);
            KeepAlive("Wcmsvc", 2);
            KeepAlive("Dhcp", 2);
            KeepAlive("Dnscache", 2);
            KeepAlive("Nsi", 2);
            KeepAlive("netprofm", 3);
            KeepAlive("bfe", 2);
            KeepAlive("LanmanServer", 3);
            KeepAlive("SharedAccess", 3);
            KeepAlive("icssvc", 3);
            KeepAlive("RasMan", 3);
            KeepAlive("EapHost", 3);
            // Bluetooth (enforce ON — the batches keep the BT stack intact)
            KeepAlive("bthserv", 2);
            KeepAlive("BluetoothUserService", 3);
            KeepAlive("BTAGService", 3);
            KeepAlive("BthAvctpSvc", 3);
            KeepAlive("hidserv", 3);

            // ── 2. KalOS Wi-Fi fix — LAST, structurally guaranteed ─────────
            // Restores the firewall stack exactly as KalOS_WiFi_Fix.reg does:
            // re-enable the firewall service + filter driver, then turn the
            // firewall back ON per profile. These are the final entries of the
            // final step, so no earlier write can undo them.
            plan.Add(new PlanEntry(
                "Wi-Fi fix: Windows Defender Firewall service (mpssvc) = Auto",
                @"HKLM\SYSTEM\CurrentControlSet\Services\mpssvc",
                "Start", TweakValueKind.Dword, "2"));
            plan.Add(new PlanEntry(
                "Wi-Fi fix: Firewall filter driver (mpsdrv) = Auto",
                @"HKLM\SYSTEM\CurrentControlSet\Services\mpsdrv",
                "Start", TweakValueKind.Dword, "2"));
            plan.Add(new PlanEntry(
                "Wi-Fi fix: firewall ON (Domain profile)",
                @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile",
                "EnableFirewall", TweakValueKind.Dword, "1"));
            plan.Add(new PlanEntry(
                "Wi-Fi fix: firewall ON (Private profile)",
                @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PrivateProfile",
                "EnableFirewall", TweakValueKind.Dword, "1"));
            plan.Add(new PlanEntry(
                "Wi-Fi fix: firewall ON (Public profile)",
                @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile",
                "EnableFirewall", TweakValueKind.Dword, "1"));

            return plan;
        }

        /// <summary>
        /// The Wi-Fi repair block: the final five entries of the plan (mpssvc,
        /// mpsdrv, and the three EnableFirewall profile flips).
        /// </summary>
        public static IReadOnlyList<PlanEntry> WifiRepair =>
            OrderedPlan.TakeLast(5).ToList();

        /// <summary>True when the plan's very last entry is a Wi-Fi-repair write.</summary>
        public static bool WifiRepairIsLast =>
            OrderedPlan.Count > 0
            && OrderedPlan[^1].Name.StartsWith("Wi-Fi fix:", StringComparison.Ordinal);

        /// <summary>
        /// True when every entry in the plan only ever sets data that turns a
        /// protection ON (Start=2/3, EnableFirewall=1) — the invariant that
        /// makes bypassing WifiSafety safe for this pass.
        /// </summary>
        public static bool AllEntriesAreRestorative()
        {
            foreach (var e in OrderedPlan)
            {
                bool restorative =
                    (e.ValueName == "Start" && e.Data is "2" or "3")
                    || (e.ValueName == "EnableFirewall" && e.Data == "1");
                if (!restorative) return false;
            }
            return true;
        }
    }
}
