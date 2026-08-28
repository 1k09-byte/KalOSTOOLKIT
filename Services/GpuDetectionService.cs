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
                        var name = Convert.ToString(gpu["Name"]);
                        var pnp = Convert.ToString(gpu["PNPDeviceID"]);

                        gpus.Add(new GpuInfo
                        {
                            Name = string.IsNullOrWhiteSpace(name) ? "Unknown GPU" : name,
                            DriverVersion = Convert.ToString(gpu["DriverVersion"]) ?? "Unknown",
                            DriverDate = date,
                            PnpDeviceId = string.IsNullOrWhiteSpace(pnp) ? "" : pnp,
                            Manufacturer = (string.IsNullOrWhiteSpace(pnp) ? "" : pnp) +
                                           (string.IsNullOrWhiteSpace(name) ? "" : " " + name)
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