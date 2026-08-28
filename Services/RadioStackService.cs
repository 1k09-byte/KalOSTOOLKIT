using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Helpers;
using Windows.Devices.Radios;

namespace KalOS.Services
{
    /// <summary>
    /// Deep radio control. Unlike the soft on/off from
    /// <see cref="Windows.Devices.Radios.Radio"/>, disabling a radio here:
    /// <list type="bullet">
    ///   <item>sets every related service and driver to Disabled (registry Start=4) and stops it,</item>
    ///   <item>disables the adapter at the PnP device level (device ConfigFlags in the registry),</item>
    /// </list>
    /// so the radio stays off across reboots and can't be flipped back on by airplane
    /// mode. Enabling restores the original start values (snapshotted the first
    /// time, falling back to each service's default when unknown) and re-enables
    /// the adapter. Service tables mirror the classic Wi-Fi/Bluetooth toggle
    /// script (including the driver stack: NativeWifiP, BTHPORT, BTHUSB, BthEnum,
    /// HidBth, RFCOMM, ...) — natively, with exact restore.
    /// </summary>
    public class RadioStackService
    {
        private const string ConfigFile = "app-radiostack.json";

        private readonly LoggingService _log;
        private readonly ProcessManager _processManager;

        /// <summary>
        /// Bluetooth services/drivers → default start type used when restoring
        /// without a snapshot: 2 = auto, 3 = demand (the script's mapping).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int> BluetoothServices =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["bthserv"] = 2,                            // Bluetooth Support Service
                ["BluetoothUserService"] = 2,               // per-user; handled by pattern too
                ["BTAGService"] = 3,                        // Bluetooth Audio Gateway Service
                ["BthAvctpSvc"] = 3,                        // AVCTP service
                ["HidBth"] = 3,                             // Bluetooth HID driver
                ["Microsoft_Bluetooth_AvrcpTransport"] = 3, // AVRCP transport driver
                ["BthEnum"] = 3,                            // Bluetooth enumerator
                ["BthHFEnum"] = 3,                          // Handsfree enumerator
                ["BthLEEnum"] = 3,                          // Bluetooth LE enumerator
                ["BthMini"] = 3,                            // Bluetooth Mini driver
                ["BTHMODEM"] = 3,                           // Bluetooth modem
                ["BTHPORT"] = 3,                            // Bluetooth port driver
                ["BTHUSB"] = 3,                             // Bluetooth USB driver
                ["RFCOMM"] = 3,                             // Bluetooth RFCOMM protocol
            };

        /// <summary>Wi-Fi services/drivers → default start type when restoring without a snapshot.</summary>
        public static readonly IReadOnlyDictionary<string, int> WifiServices =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["WlanSvc"] = 2,      // WLAN AutoConfig
                ["WwanSvc"] = 2,      // WWAN AutoConfig
                ["NativeWifiP"] = 2,  // NativeWiFi protocol driver
            };

        public const string BluetoothUserServicePattern = "BluetoothUserService*";

        public RadioStackService(LoggingService log, ProcessManager processManager)
        {
            _log = log;
            _processManager = processManager;
        }

        public record RadioStackResult(bool Success, string Detail);

        public async Task<RadioStackResult> DisableAsync(RadioKind kind, CancellationToken cancellationToken = default)
        {
            var name = kind == RadioKind.Bluetooth ? "Bluetooth" : "Wi-Fi";
            var services = kind == RadioKind.Bluetooth ? BluetoothServices : WifiServices;
            var errors = new List<string>();

            // 1. Snapshot original start values so enable can restore exactly.
            var snapshot = SnapshotStartValues(services, errors);

            // 2. Disable + stop every service/driver (sc config writes the registry Start value too).
            foreach (var service in services.Keys)
            {
                var (_, _, exit) = await _processManager.RunWithOutputAndErrorAsync(
                    "sc", $"config \"{service}\" start= disabled", TimeSpan.FromSeconds(30), cancellationToken);
                if (exit == 0)
                {
                    await _processManager.RunAsync("sc", $"stop \"{service}\"", TimeSpan.FromSeconds(30), cancellationToken);
                    _log.Success($"Disabled service: {service}");
                }
                else
                {
                    errors.Add($"could not disable service {service}");
                }
            }

            // 3. Per-user Bluetooth service (name carries a suffix) via PowerShell.
            if (kind == RadioKind.Bluetooth)
            {
                await RunPowerShellAsync(
                    "Get-Service -Name 'BluetoothUserService*' -ErrorAction SilentlyContinue | " +
                    "ForEach-Object { Set-Service -Name $_.Name -StartupType Disabled -ErrorAction SilentlyContinue; " +
                    "Stop-Service -Name $_.Name -Force -ErrorAction SilentlyContinue }",
                    cancellationToken);
            }

            // 4. Disable the adapter at the PnP device level (device ConfigFlags in the registry).
            await DisablePnpDevicesAsync(kind, cancellationToken);

            SaveSnapshot(kind, snapshot);

            return new RadioStackResult(
                errors.Count == 0,
                errors.Count == 0 ? string.Empty : $"{name} disabled, but: {string.Join("; ", errors)}.");
        }

        public async Task<RadioStackResult> EnableAsync(RadioKind kind, CancellationToken cancellationToken = default)
        {
            var name = kind == RadioKind.Bluetooth ? "Bluetooth" : "Wi-Fi";
            var services = kind == RadioKind.Bluetooth ? BluetoothServices : WifiServices;
            var errors = new List<string>();

            // 1. Restore the snapshotted start values; fall back to the service's
            //    default (auto/demand) when we never snapshotted it.
            var snapshot = LoadSnapshot(kind);
            foreach (var (service, defaultStart) in services)
            {
                int start = snapshot.TryGetValue(service, out var original) ? original : defaultStart;
                var (_, _, exit) = await _processManager.RunWithOutputAndErrorAsync(
                    "sc", $"config \"{service}\" start= {StartValueName(start)}", TimeSpan.FromSeconds(30), cancellationToken);
                if (exit == 0)
                {
                    _log.Success($"Restored service startup: {service}");
                    await _processManager.RunAsync("sc", $"start \"{service}\"", TimeSpan.FromSeconds(30), cancellationToken);
                }
                else
                {
                    errors.Add($"could not restore service {service}");
                }
            }

            // 2. Per-user Bluetooth service back to Manual.
            if (kind == RadioKind.Bluetooth)
            {
                await RunPowerShellAsync(
                    "Get-Service -Name 'BluetoothUserService*' -ErrorAction SilentlyContinue | " +
                    "Set-Service -Name $_.Name -StartupType Manual -ErrorAction SilentlyContinue",
                    cancellationToken);
            }

            // 3. Re-enable the adapter at the PnP device level.
            await EnablePnpDevicesAsync(kind, cancellationToken);

            return new RadioStackResult(
                errors.Count == 0,
                errors.Count == 0 ? string.Empty : $"{name} enabled, but: {string.Join("; ", errors)}.");
        }

        // ── Snapshot / restore of original service Start values ───────

        private static Dictionary<string, int> SnapshotStartValues(IReadOnlyDictionary<string, int> services, List<string> errors)
        {
            var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var service in services.Keys)
            {
                try
                {
                    var value = RegistryHelper.GetRegistryValue(
                        $@"HKLM\SYSTEM\CurrentControlSet\Services\{service}", "Start");
                    if (value is int start)
                    {
                        snapshot[service] = start;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"could not read {service} start value ({ex.Message})");
                }
            }
            return snapshot;
        }

        private void SaveSnapshot(RadioKind kind, Dictionary<string, int> snapshot)
        {
            try
            {
                var config = JsonConfigHelper.LoadSync<RadioStackConfig>(ConfigFile) ?? new RadioStackConfig();
                config.Services ??= new Dictionary<string, Dictionary<string, int>>();
                var key = kind == RadioKind.Bluetooth ? "Bluetooth" : "Wifi";
                config.Services.TryGetValue(key, out var map);
                if (map == null)
                {
                    map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    config.Services[key] = map;
                }

                // First disable wins: never clobber an original value with a
                // later "already disabled" state.
                foreach (var (service, start) in snapshot)
                {
                    if (!map.ContainsKey(service) && start != 4)
                    {
                        map[service] = start;
                    }
                }

                _ = JsonConfigHelper.SaveAsync(ConfigFile, config);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not save radio stack snapshot: {ex.Message}");
            }
        }

        private static Dictionary<string, int> LoadSnapshot(RadioKind kind)
        {
            try
            {
                var config = JsonConfigHelper.LoadSync<RadioStackConfig>(ConfigFile);
                var key = kind == RadioKind.Bluetooth ? "Bluetooth" : "Wifi";
                if (config?.Services != null && config.Services.TryGetValue(key, out var map))
                {
                    return new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        internal static string StartValueName(int start) => start switch
        {
            2 => "auto",
            4 => "disabled",
            _ => "demand",
        };

        // ── PnP device-level adapter control ───────────────────────────

        private async Task DisablePnpDevicesAsync(RadioKind kind, CancellationToken cancellationToken)
        {
            string script = kind == RadioKind.Bluetooth
                ? "Get-PnpDevice -Class Bluetooth -PresentOnly -ErrorAction SilentlyContinue | Disable-PnpDevice -Confirm:$false"
                : "Get-PnpDevice -Class Net -PresentOnly -ErrorAction SilentlyContinue | " +
                  "Where-Object { $_.FriendlyName -match 'wireless|wlan|wi-fi|802\\.11' } | Disable-PnpDevice -Confirm:$false";
            await RunPowerShellAsync(script, cancellationToken);
        }

        private async Task EnablePnpDevicesAsync(RadioKind kind, CancellationToken cancellationToken)
        {
            string script = kind == RadioKind.Bluetooth
                ? "Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue | Enable-PnpDevice -Confirm:$false"
                : "Get-PnpDevice -Class Net -ErrorAction SilentlyContinue | " +
                  "Where-Object { $_.FriendlyName -match 'wireless|wlan|wi-fi|802\\.11' } | Enable-PnpDevice -Confirm:$false";
            await RunPowerShellAsync(script, cancellationToken);
        }

        private async Task RunPowerShellAsync(string script, CancellationToken cancellationToken)
        {
            var bytes = Encoding.Unicode.GetBytes(script);
            var encoded = Convert.ToBase64String(bytes);
            await _processManager.RunWithOutputAndErrorAsync(
                "powershell", $"-NoProfile -EncodedCommand {encoded}", TimeSpan.FromMinutes(2), cancellationToken);
        }

        private sealed class RadioStackConfig
        {
            public Dictionary<string, Dictionary<string, int>>? Services { get; set; }
        }
    }
}
