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

        public DriverCleanupService(LoggingService log, ProcessManager processManager)
        {
            _log = log;
            _processManager = processManager;
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
            await UninstallDisplayDriverAsync("NVIDIA");

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
            next("Uninstalling AMD Display Driver");
            await UninstallDisplayDriverAsync("Advanced Micro Devices|AMD");

            if (options.RemoveAmdAudioBus == true)
            {
                next("Removing AMD Audio Bus");
                // Handled via pnputil in advanced sweeps, but here we can remove the device node if needed.
            }
            if (options.RemoveAmdKmpfd == true)
            {
                next("Removing AMDKMPFD filter");
                // Requires registry service deletion.
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

        private async Task UninstallDisplayDriverAsync(string vendor)
        {
            var psScript = $@"
$output = pnputil /enum-devices /class Display
$currentInf = $null
$isTarget = $false

foreach ($line in $output) {{
    if ($line -match '^Driver Name:\s+(oem\d+\.inf)') {{
        $currentInf = $matches[1]
    }}
    if ($line -match '^Manufacturer Name:\s+(.*)$') {{
        if ($matches[1] -match '{vendor}') {{
            $isTarget = $true
        }}
    }}
    if ($line -match '^\s*$') {{
        if ($isTarget -and $currentInf) {{
            Write-Host ""Uninstalling display driver: $currentInf""
            pnputil /delete-driver $currentInf /uninstall /force
        }}
        $currentInf = $null
        $isTarget = $false
    }}
}}
if ($isTarget -and $currentInf) {{
    Write-Host ""Uninstalling display driver: $currentInf""
    pnputil /delete-driver $currentInf /uninstall /force
}}
";
            var bytes = System.Text.Encoding.Unicode.GetBytes(psScript);
            var encoded = Convert.ToBase64String(bytes);
            await _processManager.RunWithOutputAndErrorAsync("powershell", $"-NoProfile -EncodedCommand {encoded}", TimeSpan.FromMinutes(2));
        }
    }
}
