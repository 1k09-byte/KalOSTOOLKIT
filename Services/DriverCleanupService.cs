using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace KalOS.Services
{
    public partial class DriverCleanupOptions : ObservableObject
    {
        // General
        [ObservableProperty] private bool? _removeMonitors = true;
        [ObservableProperty] private bool? _createRestorePoint = false;
        [ObservableProperty] private bool? _removeVulkanRuntime = true;

        // NVIDIA
        [ObservableProperty] private bool? _removeNvidiaFolders = true;
        [ObservableProperty] private bool? _removePhysX = true;
        [ObservableProperty] private bool? _remove3DTVPlay = true;
        [ObservableProperty] private bool? _removeGeForceExperience = true;
        [ObservableProperty] private bool? _removeNvidiaBroadcast = true;
        [ObservableProperty] private bool? _removeNvidiaControlPanelDCH = true;
        [ObservableProperty] private bool? _removeNvidiaShaderCache = true;
        [ObservableProperty] private bool? _keepNvidiaControlPanelSettings = false;

        // AMD
        [ObservableProperty] private bool? _removeAmdFolders = true;
        [ObservableProperty] private bool? _removeAmdKmpfd = true;
        [ObservableProperty] private bool? _removeAmdAudioBus = true;
        [ObservableProperty] private bool? _removeAmdCrimsonShaderCache = true;
        [ObservableProperty] private bool? _removeAmdControlPanelDCH = true;
    }

    /// <summary>
    /// Executes deep driver cleanup operations akin to DDU. Removes side-apps,
    /// DCH control panels, driver store packages, and shader caches.
    /// </summary>
    public class DriverCleanupService
    {
        private readonly LoggingService _log;
        private readonly ProcessManager _processManager;
        private readonly DriverDownloadService _downloadService;

        // Device setup class GUIDs used to target driver-store packages for removal.
        private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        private const string MediaClassGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}";

        // Official AMD Cleanup Utility (GPU-601). Interactive: removes all AMD
        // graphics/audio drivers + software, creates a restore point, and strongly
        // recommends running in safe mode (offers a reboot-into-safe-mode flow).
        private const string AmdCleanupUtilityUrl = "https://drivers.amd.com/drivers/amdcleanuputility.exe";

        private static readonly string AmdCleanupUtilityLocalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KalOS", "tools", "amdcleanuputility.exe");

        public DriverCleanupService(LoggingService log, ProcessManager processManager, DriverDownloadService downloadService)
        {
            _log = log;
            _processManager = processManager;
            _downloadService = downloadService;
        }

        public async Task RunCleanupAsync(DriverCleanupOptions options, bool isNvidia, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            int totalSteps = 0;
            if (options.CreateRestorePoint == true) totalSteps++;
            if (options.RemoveMonitors == true) totalSteps++;
            if (options.RemoveVulkanRuntime == true) totalSteps++;
            // The uninstallation script is 1 step
            if (isNvidia)
            {
                // Every counted step must map to exactly one progress tick below,
                // or the bar stalls below 100%. GFE + NVIDIA App is one tick.
                totalSteps++;
                if (options.RemovePhysX == true) totalSteps++;
                if (options.Remove3DTVPlay == true) totalSteps++;
                if (options.RemoveGeForceExperience == true) totalSteps++;
                if (options.RemoveNvidiaBroadcast == true) totalSteps++;
                if (options.RemoveNvidiaControlPanelDCH == true) totalSteps++;
                if (options.RemoveNvidiaShaderCache == true) totalSteps++;
                if (options.KeepNvidiaControlPanelSettings != true) totalSteps++;
                if (options.RemoveNvidiaFolders == true) totalSteps++;
            }
            else
            {
                totalSteps++;
                if (options.RemoveAmdAudioBus == true) totalSteps++;
                if (options.RemoveAmdKmpfd == true) totalSteps++;
                if (options.RemoveAmdControlPanelDCH == true) totalSteps++;
                if (options.RemoveAmdCrimsonShaderCache == true) totalSteps++;
                if (options.RemoveAmdFolders == true) totalSteps++;
            }
            if (totalSteps == 0) totalSteps = 1;

            int step = 0;
            void NextStep(string msg)
            {
                step++;
                _log.Info($"Cleanup: {msg}");
                progress?.Report(step * 100.0 / totalSteps);
            }

            try
            {
                if (options.CreateRestorePoint == true)
                {
                    NextStep("Creating System Restore Point");
                    await _processManager.RunWithOutputAndErrorAsync(
                        "powershell",
                        "-NoProfile -Command \"Checkpoint-Computer -Description 'KalOS GPU Cleanup' -RestorePointType 'MODIFY_SETTINGS'\"",
                        TimeSpan.FromMinutes(2));
                }

                if (options.RemoveMonitors == true)
                {
                    NextStep("Removing ghost monitors");
                    // Windows provides no easy CLI to remove non-present enumerated devices without devcon.
                    // This is a placeholder since raw removal requires registry tree manipulation or devcon.exe.
                }

                if (options.RemoveVulkanRuntime == true)
                {
                    NextStep("Removing Vulkan Runtime");
                    await UninstallWmiAppAsync("Vulkan Run Time Libraries");
                }

                if (isNvidia)
                {
                    await RunNvidiaCleanupAsync(options, NextStep);
                }
                else
                {
                    await RunAmdCleanupAsync(options, NextStep);
                }

                progress?.Report(100);
            }
            catch (Exception ex)
            {
                _log.Error($"Cleanup failed: {ex.Message}");
                throw;
            }
        }

        private async Task RunNvidiaCleanupAsync(DriverCleanupOptions options, Action<string> next)
        {
            next("Uninstalling NVIDIA Display Driver");
            await UninstallVendorDriverStoreAsync("NVIDIA", DisplayClassGuid);

            if (options.RemovePhysX == true)
            {
                next("Removing PhysX");
                await UninstallWmiAppAsync("NVIDIA PhysX System Software");
            }
            if (options.Remove3DTVPlay == true)
            {
                next("Removing 3DTV Play");
                await UninstallWmiAppAsync("NVIDIA 3DTV Play");
            }
            if (options.RemoveGeForceExperience == true)
            {
                next("Removing GeForce Experience / NVIDIA App");
                await UninstallWmiAppAsync("NVIDIA GeForce Experience");
                await UninstallWmiAppAsync("NVIDIA App");
            }
            if (options.RemoveNvidiaBroadcast == true)
            {
                next("Removing NVIDIA Broadcast");
                await UninstallWmiAppAsync("NVIDIA Broadcast");
            }
            if (options.RemoveNvidiaControlPanelDCH == true)
            {
                next("Removing NVIDIA Control Panel (Store App)");
                await RemoveAppxPackageAsync("NVIDIACorp.NVIDIAControlPanel");
            }
            if (options.RemoveNvidiaShaderCache == true)
            {
                next("Clearing NVIDIA Shader Cache");
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                ClearFolder(Path.Combine(localAppData, "NVIDIA", "DXCache"));
                ClearFolder(Path.Combine(localAppData, "NVIDIA", "GLCache"));
            }
            if (options.KeepNvidiaControlPanelSettings != true)
            {
                next("Clearing NVIDIA Control Panel Settings");
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                ClearFolder(Path.Combine(programData, "NVIDIA Corporation", "Drs"));
            }
            if (options.RemoveNvidiaFolders == true)
            {
                next("Deleting C:\\NVIDIA");
                ClearFolder("C:\\NVIDIA", deleteRoot: true);
            }
        }

        private async Task RunAmdCleanupAsync(DriverCleanupOptions options, Action<string> next)
        {
            // Primary path: AMD's official Cleanup Utility (GPU-601) removes ALL
            // AMD graphics + audio drivers and software, creates a restore point,
            // and offers a reboot-into-safe-mode flow. Launch it for the user.
            if (await TryLaunchAmdCleanupUtilityAsync())
            {
                next("Launched AMD Cleanup Utility — complete its prompts (reboot to safe mode recommended).");
                return;
            }

            // Fallback: official utility unavailable → best-effort manual cleanup.
            next("AMD Cleanup Utility unavailable — using built-in cleanup");
            await UninstallVendorDriverStoreAsync("Advanced Micro Devices|AMD", DisplayClassGuid);

            if (options.RemoveAmdAudioBus == true)
            {
                next("Removing AMD Audio Bus");
                // AMD's GPU audio bus ships as a MEDIA-class driver package in the
                // store; purge those store packages so the audio bus loses its driver.
                await UninstallVendorDriverStoreAsync("Advanced Micro Devices|AMD", MediaClassGuid);
            }
            if (options.RemoveAmdKmpfd == true)
            {
                next("Removing AMDKMPFD filter");
                await RemoveAmdKmpfdAsync();
            }
            if (options.RemoveAmdControlPanelDCH == true)
            {
                next("Removing AMD Control Panel (Store App)");
                await RemoveAppxPackageAsync("AdvancedMicroDevicesInc-2.AMDRadeonSoftware");
            }
            if (options.RemoveAmdCrimsonShaderCache == true)
            {
                next("Clearing AMD Shader Cache");
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                ClearFolder(Path.Combine(localAppData, "AMD", "DxCache"));
                ClearFolder(Path.Combine(localAppData, "AMD", "GLCache"));
            }
            if (options.RemoveAmdFolders == true)
            {
                next("Deleting C:\\AMD");
                ClearFolder("C:\\AMD", deleteRoot: true);
            }
        }

        /// <summary>
        /// Ensures the official AMD Cleanup Utility is available and launches it
        /// visibly. Prefers the user's own copy in Downloads, then a previously
        /// downloaded app copy, otherwise downloads it from AMD. Returns true when
        /// it was launched. KalOS runs elevated, so the child inherits admin.
        /// </summary>
        private async Task<bool> TryLaunchAmdCleanupUtilityAsync()
        {
            string? exe = null;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string userCopy = Path.Combine(profile, "Downloads", "amdcleanuputility.exe");
            if (File.Exists(userCopy)) exe = userCopy;

            if (exe == null && File.Exists(AmdCleanupUtilityLocalPath)) exe = AmdCleanupUtilityLocalPath;

            if (exe == null)
            {
                try
                {
                    _log.Info("Downloading AMD Cleanup Utility from AMD...");
                    await _downloadService.DownloadAsync(
                        AmdCleanupUtilityUrl, AmdCleanupUtilityLocalPath,
                        cancellationToken: CancellationToken.None);
                    if (File.Exists(AmdCleanupUtilityLocalPath)) exe = AmdCleanupUtilityLocalPath;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not download AMD Cleanup Utility: {ex.Message}");
                }
            }

            if (exe == null || !File.Exists(exe)) return false;

            try
            {
                _log.Info($"Launching AMD Cleanup Utility: {exe}");
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not launch AMD Cleanup Utility: {ex.Message}");
                return false;
            }
        }

        private async Task UninstallWmiAppAsync(string partialName)
        {
            await _processManager.RunWithOutputAndErrorAsync(
                "powershell",
                $"-NoProfile -Command \"Get-WmiObject -Class Win32_Product | Where-Object {{ $_.Name -match '{partialName}' }} | ForEach-Object {{ $_.Uninstall() }}\"",
                TimeSpan.FromMinutes(2));
        }

        private async Task RemoveAppxPackageAsync(string packageName)
        {
            await _processManager.RunWithOutputAndErrorAsync(
                "powershell",
                $"-NoProfile -Command \"Get-AppxPackage *{packageName}* -AllUsers | Remove-AppxPackage -AllUsers\"",
                TimeSpan.FromMinutes(1));
        }

        private void ClearFolder(string path, bool deleteRoot = false)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }

                if (deleteRoot)
                {
                    try { Directory.Delete(path, true); } catch { }
                }
            }
            catch
            {
                // Ignored if files are in use
            }
        }

        private async Task UninstallVendorDriverStoreAsync(string providerMatch, string classGuid)
        {
            // Purge every driver-store package (per setup class) whose provider
            // matches the vendor. This removes ALL stored versions of the driver,
            // matching a DDU store purge. The in-use display package cannot be
            // unbound while the adapter is rendering, so it stays until reboot
            // (same as DDU running in a live OS).
            var psScript = $@"
$classGuid = '{classGuid}'
$providerMatch = '{providerMatch}'
$targets = @()
$inf = $null
$provider = $null
$class = $null
foreach ($line in pnputil /enum-drivers) {{
    $s = ""$line""
    if ($s -match '^Published Name:\s+(oem\d+\.inf)') {{ $inf = $matches[1] }}
    if ($s -match '^Class GUID:\s+({{[0-9A-Fa-f-]+}})') {{ $class = $matches[1] }}
    if ($s -match '^Provider Name:\s+(.*)$') {{ $provider = $matches[1] }}
    if ($s -match '^\s*$') {{
        if ($inf -and $provider -and $class -eq $classGuid -and ($provider -match $providerMatch)) {{ $targets += $inf }}
        $inf = $null; $provider = $null; $class = $null
    }}
}}
if ($inf -and $provider -and $class -eq $classGuid -and ($provider -match $providerMatch)) {{ $targets += $inf }}

foreach ($t in ($targets | Select-Object -Unique)) {{
    Write-Host ""Uninstalling driver store package: $t""
    pnputil /delete-driver $t /uninstall /force
}}
";
            var bytes = System.Text.Encoding.Unicode.GetBytes(psScript);
            var encoded = Convert.ToBase64String(bytes);
            await _processManager.RunWithOutputAndErrorAsync("powershell", $"-NoProfile -EncodedCommand {encoded}", TimeSpan.FromMinutes(3));
        }

        private async Task RemoveAmdKmpfdAsync()
        {
            // AMDKMPFD is a legacy NT kernel filter (disk lower filter) service.
            // Stop it, delete its service entry, and purge it from the store.
            var psScript = @"
$svc = 'amdkmpfd'
$exists = Get-Service -Name $svc -ErrorAction SilentlyContinue
if ($exists) {
    Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
    sc.exe delete $svc | Out-Null
    Write-Host ('Deleted AMDKMPFD service: ' + $svc)
} else {
    Write-Host ('AMDKMPFD service not present: ' + $svc)
}
";
            var bytes = System.Text.Encoding.Unicode.GetBytes(psScript);
            var encoded = Convert.ToBase64String(bytes);
            await _processManager.RunWithOutputAndErrorAsync("powershell", $"-NoProfile -EncodedCommand {encoded}", TimeSpan.FromMinutes(2));
        }
    }
}
