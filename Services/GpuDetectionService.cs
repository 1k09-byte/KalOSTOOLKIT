using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Tasks;
using KalOS.Models;

namespace KalOS.Services
{
    /// <summary>
    /// SMBIOS chassis types that mean the machine is a portable (laptop,
    /// notebook, convertible, tablet, etc.). Win32_SystemEnclosure
    /// ChassisTypes — the authoritative form-factor source on Windows.
    /// </summary>
    internal static class PortableChassis
    {
        internal static readonly HashSet<int> Types = new()
        {
            8,   // Portable (laptop)
            9,   // Laptop
            10,  // Notebook
            11,  // Hand Held
            12,  // Docking Station
            14,  // Sub Notebook
            30,  // Tablet
            31,  // Convertible
            32,  // Detachable
        };
    }

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

                // Form factor is a machine property, not a GPU property — read it
                // once and stamp it on every detected adapter.
                bool isLaptop = DetectIsLaptop();

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

                        // A GPU whose vendor driver isn't installed (fresh Windows, or a
                        // device in an error state) reports itself as "Microsoft Basic
                        // Display Adapter", hiding the AMD/NVIDIA/Intel hardware. Peel
                        // the real identity out of the device's registry node instead.
                        var (registryName, hardwareId, service) = ReadDeviceRegistryInfo(pnp);

                        // Vendor checks prefer the hardware ID (VEN_xxxx) so a generic
                        // WMI name can't mask an AMD adapter.
                        string vendorId = !string.IsNullOrWhiteSpace(hardwareId) ? hardwareId : pnp;

                        name = ResolveDisplayName(name, registryName, vendorId, service);

                        string finalVersion = wmiVersion;
                        if (VendorOf(vendorId, service) == "AMD"
                            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
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
                            // Keep the vendor's PNP id on the record so IsAmd/IsNvidia/IsIntel
                            // and the provider routing always see the real hardware.
                            PnpDeviceId = !string.IsNullOrWhiteSpace(pnp) ? pnp : vendorId,
                            Manufacturer = vendorId + (string.IsNullOrWhiteSpace(name) ? "" : " " + name),
                            IsLaptop = isLaptop
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

        /// <summary>
        /// Reads the real identity of a device from its registry node
        /// (HKLM\SYSTEM\CurrentControlSet\Enum\&lt;pnp&gt;): the human-friendly
        /// driver description, the hardware ID (VEN_xxxx), and the bound kernel
        /// driver service. All may be null when the node is missing.
        /// </summary>
        private static (string? DeviceDesc, string? HardwareId, string? Service) ReadDeviceRegistryInfo(string pnp)
        {
            if (string.IsNullOrWhiteSpace(pnp)) return (null, null, null);
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\{pnp}");
                if (key is null) return (null, null, null);

                string? hardwareId = null;
                if (key.GetValue("HardwareID") is string[] hwIds && hwIds.Length > 0)
                    hardwareId = hwIds[0];

                return (
                    key.GetValue("DeviceDesc") as string,
                    hardwareId,
                    key.GetValue("Service") as string);
            }
            catch
            {
                return (null, null, null);
            }
        }

        /// <summary>
        /// Produces the adapter name to show in the UI. A generic WMI name is
        /// replaced by the real device description when one exists; otherwise the
        /// vendor (from the hardware ID / driver service) labels the entry so an
        /// AMD/NVIDIA adapter never masquerades as "Microsoft Basic Display
        /// Adapter".
        /// </summary>
        public static string ResolveDisplayName(string wmiName, string? deviceDesc, string vendorId, string? service)
        {
            if (!IsGenericDisplayName(wmiName)) return wmiName;

            string? friendly = ParseDeviceDescFriendlyName(deviceDesc);
            // The inbox basic-display driver's own DeviceDesc is also a generic
            // placeholder, so re-check the resolved name before adopting it.
            if (!string.IsNullOrWhiteSpace(friendly) && !IsGenericDisplayName(friendly))
                return friendly;

            return VendorOf(vendorId, service) switch
            {
                "AMD" => "AMD Radeon (basic display — driver not installed)",
                "NVIDIA" => "NVIDIA GPU (basic display — driver not installed)",
                "Intel" => "Intel Graphics (basic display — driver not installed)",
                _ => wmiName
            };
        }

        /// <summary>
        /// True when a WMI adapter name is the generic placeholder Windows uses
        /// when no vendor driver is bound (these hide the real hardware).
        /// </summary>
        public static bool IsGenericDisplayName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            return name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Basic Display Adapter", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Microsoft Remote Display", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Unknown GPU", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts the human-friendly name from a driver <c>DeviceDesc</c> value.
        /// The value is typically <c>"@oem15.inf,%amdxxx%;AMD Radeon(TM) Graphics"</c>
        /// — everything after the first <c>;</c> is the readable name.
        /// </summary>
        public static string? ParseDeviceDescFriendlyName(string? deviceDesc)
        {
            if (string.IsNullOrWhiteSpace(deviceDesc)) return null;
            int semicolon = deviceDesc.IndexOf(';');
            if (semicolon < 0 || semicolon >= deviceDesc.Length - 1) return null;
            string candidate = deviceDesc[(semicolon + 1)..].Trim();
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }

        /// <summary>
        /// Resolves the GPU vendor from the hardware ID's <c>VEN_xxxx</c> token or
        /// the kernel service string, e.g. <c>amdkmdag</c>/<c>nvlddmkm</c>/<c>igfx</c>.
        /// Returns "AMD", "NVIDIA", "Intel", or "" when unknown.
        /// </summary>
        public static string VendorOf(string vendorId, string? service)
        {
            if (!string.IsNullOrWhiteSpace(vendorId))
            {
                if (vendorId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return "AMD";
                if (vendorId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
                if (vendorId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "Intel";
            }
            if (!string.IsNullOrWhiteSpace(service))
            {
                if (service.Equals("amdkmdag", StringComparison.OrdinalIgnoreCase)
                    || service.Equals("amdwddmg", StringComparison.OrdinalIgnoreCase)) return "AMD";
                if (service.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
                if (service.Equals("igfx", StringComparison.OrdinalIgnoreCase)
                    || service.Equals("igd", StringComparison.OrdinalIgnoreCase)
                    || service.Equals("igfxn", StringComparison.OrdinalIgnoreCase)) return "Intel";
            }
            return "";
        }

        /// <summary>
        /// True when the machine is a laptop/notebook/tablet. Reads the SMBIOS
        /// chassis type first (authoritative); falls back to battery presence,
        /// which distinguishes portables on OEM boxes that report a useless
        /// chassis type. Never throws — failure means "assume desktop".
        /// </summary>
        internal static bool DetectIsLaptop()
        {
            try
            {
                using var enclosure = new ManagementObjectSearcher(
                    "SELECT ChassisTypes FROM Win32_SystemEnclosure");
                foreach (var enc in enclosure.Get())
                {
                    using (enc)
                    {
                        if (enc["ChassisTypes"] is ushort[] types)
                        {
                            foreach (ushort t in types)
                            {
                                if (PortableChassis.Types.Contains((int)t)) return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Enclosure query unavailable — fall through to the battery check.
            }

            try
            {
                using var batteries = new ManagementObjectSearcher(
                    "SELECT BatteryStatus FROM Win32_Battery");
                return batteries.Get().Count > 0;
            }
            catch
            {
                return false;
            }
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