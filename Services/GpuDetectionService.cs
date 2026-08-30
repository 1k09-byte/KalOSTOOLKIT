using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Tasks;
using KalOS.Models;

namespace KalOS.Services
{
    /// <summary>
    /// Enumerates installed graphics adapters and their driver details through
    /// WMI/CIM (<c>Win32_VideoController</c>). Pure backend — the UI never sees
    /// WMI. All queries run off the UI thread.
    /// </summary>
    public sealed class GpuDetectionService
    {
        /// <summary>
        /// Returns every installed video controller. The first entry is usually
        /// the primary (active) GPU. Empty when nothing is reportable.
        /// </summary>
        public async Task<List<GpuInfo>> GetGpusAsync()
        {
            return await Task.Run(() =>
            {
                var gpus = new List<GpuInfo>();

                try
                {
                    // DriverDate surfaces as a WMI DateTime (yyyyMMddHHmmss.ffffff+ooo);
                    // format it to a short readable date when we can.
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name, DriverVersion, DriverDate, PNPDeviceID, AdapterRAM FROM Win32_VideoController");

                    foreach (ManagementObject gpu in searcher.Get())
                    {
                        var date = FormatDriverDate(Convert.ToString(gpu["DriverDate"]));
                        var name = Convert.ToString(gpu["Name"]) ?? "Unknown GPU";
                        var pnp = Convert.ToString(gpu["PNPDeviceID"]) ?? "";
                        var wmiVersion = Convert.ToString(gpu["DriverVersion"]) ?? "Unknown";

                        string finalVersion = wmiVersion;
                        bool isAmd = pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("AMD", StringComparison.OrdinalIgnoreCase);

                        if (isAmd)
                        {
                            string? amdMarketingVer = GetAmdMarketingVersion();
                            if (!string.IsNullOrWhiteSpace(amdMarketingVer))
                            {
                                finalVersion = amdMarketingVer;
                            }
                        }

                        gpus.Add(new GpuInfo
                        {
                            Name = string.IsNullOrWhiteSpace(name) ? "Unknown GPU" : name,
                            DriverVersion = finalVersion,
                            DriverDate = date,
                            PnpDeviceId = pnp,
                            Manufacturer = pnp + (string.IsNullOrWhiteSpace(name) ? "" : " " + name)
                        });
                        gpu.Dispose();
                    }
                }
                catch
                {
                    // WMI can be unavailable (very trimmed installs / locked down
                    // environments). Return an empty list so callers show state.
                }

                return gpus;
            });
        }

        private static string? GetAmdMarketingVersion()
        {
            try
            {
                // 1. Check Video Adapter Class ReleaseVersion (e.g. "25.10.45.05-260808a-203303C-AMD-Software-Adrenalin-Edition")
                using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (classKey != null)
                {
                    foreach (var subName in classKey.GetSubKeyNames())
                    {
                        if (subName.StartsWith("000", StringComparison.OrdinalIgnoreCase))
                        {
                            using var sub = classKey.OpenSubKey(subName);
                            var catVer = sub?.GetValue("Catalyst_Version")?.ToString();
                            if (!string.IsNullOrWhiteSpace(catVer) && catVer.Contains('.'))
                                return catVer.Trim();

                            var relVer = sub?.GetValue("ReleaseVersion")?.ToString();
                            if (!string.IsNullOrWhiteSpace(relVer))
                            {
                                // Match AMD build timestamp e.g. "-260808a-" -> 26.8.1
                                var match = System.Text.RegularExpressions.Regex.Match(relVer, @"-(\d{2})(\d{2})\d{2}[a-zA-Z]?-");
                                if (match.Success)
                                {
                                    int yy = int.Parse(match.Groups[1].Value);
                                    int mm = int.Parse(match.Groups[2].Value);
                                    return $"{yy}.{mm}.1";
                                }
                            }
                        }
                    }
                }

                // 2. Check AMD Catalyst Install Manager
                using var uKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AMD Catalyst Install Manager");
                var dispVer = uKey?.GetValue("DisplayVersion")?.ToString();
                if (!string.IsNullOrWhiteSpace(dispVer)) return dispVer.Trim();

                // 3. Check other AMD uninstall entries
                using var uParent = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uParent != null)
                {
                    foreach (var subName in uParent.GetSubKeyNames())
                    {
                        if (subName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || subName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                        {
                            using var sub = uParent.OpenSubKey(subName);
                            var v = sub?.GetValue("DisplayVersion")?.ToString();
                            if (!string.IsNullOrWhiteSpace(v) && v.Contains('.'))
                                return v.Trim();
                        }
                    }
                }

                // 4. Check AMDInstallManager CheckForUpdates
                using var imKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AMD\AMDInstallManager\CheckForUpdates");
                var instDriver = imKey?.GetValue("InstalledDriver")?.ToString();
                if (!string.IsNullOrWhiteSpace(instDriver)) return instDriver.Trim();
            }
            catch
            {
            }
            return null;
        }


        private static string FormatDriverDate(string? wmiDate)
        {
            if (string.IsNullOrWhiteSpace(wmiDate)) return "Unknown";

            // WMI datetime: YYYYMMDDHHMMSS.mmmmmm±UUU
            if (wmiDate.Length >= 8
                && int.TryParse(wmiDate.AsSpan(0, 4), out int y)
                && int.TryParse(wmiDate.AsSpan(4, 2), out int mo)
                && int.TryParse(wmiDate.AsSpan(6, 2), out int d))
            {
                try
                {
                    return new DateTime(y, mo, d).ToString("MMM d, yyyy");
                }
                catch
                {
                    // fall through to raw
                }
            }

            return wmiDate;
        }
    }
}