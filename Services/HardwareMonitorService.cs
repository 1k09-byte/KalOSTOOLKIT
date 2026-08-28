using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LibreHardwareMonitor.Hardware;

namespace KalOS.Services;

public sealed class HardwareMonitorService
{
    public const string WingetPackageId = "LibreHardwareMonitor.LibreHardwareMonitor";
    private static readonly string[] ExecutableNames = { "LibreHardwareMonitor.exe", "OpenHardwareMonitor.exe" };
    private readonly LoggingService _log;

    public HardwareMonitorService(LoggingService log) => _log = log;

    /// <summary>True when the bundled HardwareMonitorWorker (which embeds LibreHardwareMonitor) is deployed next to the app.</summary>
    public bool WorkerAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "HardwareMonitorWorker.exe")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "HardwareMonitorWorker.dll"));

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The bundled worker can scan on its own - no standalone installation needed.
        if (WorkerAvailable) return true;
        return await IsStandaloneInstalledAsync(cancellationToken);
    }

    private async Task<bool> IsStandaloneInstalledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool found = await Task.Run(() => FindExecutable() != null || IsMonitorProcessRunning() || IsRegisteredInUninstallKeys(), cancellationToken);
        if (found) return true;
        try
        {
            var result = await WingetHelper.RunAsync($"list --id {WingetPackageId} -e --accept-source-agreements", false, cancellationToken);
            return result.Success && result.StandardOutput.Contains(WingetPackageId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { _log.Warn($"Hardware monitor detection failed: {ex.Message}"); return false; }
    }

    public async Task<bool> InstallAsync(CancellationToken cancellationToken = default, IProgress<HardwareInstallProgress>? progress = null)
    {
        try
        {
            progress?.Report(new("Preparing winget installation...", 0, true));
            progress?.Report(new("Downloading LibreHardwareMonitor...", 20, true));
            var result = await WingetHelper.RunAsync($"install --id {WingetPackageId} --source winget -e --accept-package-agreements --accept-source-agreements --silent", true, cancellationToken);
            if (!result.Success) return false;
            progress?.Report(new("Verifying the installation...", 85, true));
            await Task.Delay(750, cancellationToken);
            bool installed = await IsStandaloneInstalledAsync(cancellationToken);
            progress?.Report(new(installed ? "LibreHardwareMonitor is ready." : "Installation could not be verified.", installed ? 100 : 0, false));
            return installed;
        }
        catch (OperationCanceledException) { progress?.Report(new("Installation cancelled.", 0, false)); throw; }
        catch (Exception ex) { _log.Error($"Hardware monitor installation failed: {ex.Message}"); progress?.Report(new("Installation failed.", 0, false)); return false; }
    }

    public async Task<bool> UninstallAsync(CancellationToken cancellationToken = default, IProgress<HardwareInstallProgress>? progress = null)
    {
        try
        {
            progress?.Report(new("Uninstalling LibreHardwareMonitor...", 0, true));
            var result = await WingetHelper.RunAsync($"uninstall --id {WingetPackageId} -e --silent --accept-source-agreements", false, cancellationToken);
            // Fallback: kill processes and remove portable/manual files if winget didn't fully clean up
            try
            {
                foreach (var proc in Process.GetProcessesByName("LibreHardwareMonitor"))
                {
                    try { proc.Kill(); proc.WaitForExit(2000); } catch { }
                }
                foreach (var proc in Process.GetProcessesByName("OpenHardwareMonitor"))
                {
                    try { proc.Kill(); proc.WaitForExit(2000); } catch { }
                }

                // Try to remove portable executable and its folder (common for manual installs)
                var exe = FindExecutable();
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    var dir = Path.GetDirectoryName(exe);
                    // Avoid deleting the app's own install folder (contains KalOS.exe)
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !File.Exists(Path.Combine(dir, "KalOS.exe")))
                    {
                        try { Directory.Delete(dir, recursive: true); } catch { try { File.Delete(exe); } catch { } }
                    }
                    else if (!string.IsNullOrEmpty(exe))
                    {
                        try { File.Delete(exe); } catch { }
                    }
                }
            }
            catch (Exception ex) { _log.Warn($"Manual cleanup during uninstall failed: {ex.Message}"); }
            progress?.Report(new("Verifying uninstallation...", 80, true));
            await Task.Delay(500, cancellationToken);
            bool stillInstalled = await IsStandaloneInstalledAsync(cancellationToken);
            bool success = !stillInstalled;
            if (success)
            {
                progress?.Report(new("LibreHardwareMonitor uninstalled.", 100, false));
            }
            else
            {
                // If still detected, check if it's just the worker (part of KalOS) vs real monitor
                var exeStill = FindExecutable();
                if (string.IsNullOrEmpty(exeStill) || !File.Exists(exeStill))
                {
                    // No executable found, but IsInstalled still true via process or registry stale — treat as success
                    progress?.Report(new("LibreHardwareMonitor uninstalled (process/registry stale, will clear on next scan).", 100, false));
                    return true;
                }
                progress?.Report(new("Uninstall could not be verified. It may have been installed manually.", 0, false));
            }
            return success;
        }
        catch (OperationCanceledException) { progress?.Report(new("Uninstall cancelled.", 0, false)); throw; }
        catch (Exception ex) { _log.Error($"Hardware monitor uninstall failed: {ex.Message}"); progress?.Report(new("Uninstall failed.", 0, false)); return false; }
    }

    public async Task<HardwareScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        string worker = Path.Combine(AppContext.BaseDirectory, "HardwareMonitorWorker.exe");
        if (!File.Exists(worker)) return new HardwareScanResult(0, Array.Empty<SystemSensorReading>());
        try
        {
            var psi = new ProcessStartInfo(worker)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process == null) return new HardwareScanResult(0, Array.Empty<SystemSensorReading>());
            string json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var raw = JsonSerializer.Deserialize<List<WorkerReading>>(json) ?? new();
            var readings = raw.Select(r => new SystemSensorReading(r.Category, r.Hardware, r.Sensor, FormatWorkerValue(r), r.SensorType) { NumericValue = r.NumericValue }).ToList();
            return new HardwareScanResult(readings.Select(r => r.Hardware).Distinct(StringComparer.OrdinalIgnoreCase).Count(), SelectEssentialReadings(readings));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Warn($"Hardware worker unavailable: {ex.Message}"); return new HardwareScanResult(0, Array.Empty<SystemSensorReading>()); }
    }

    private static string FormatWorkerValue(WorkerReading reading)
    {
        string unit = reading.SensorType switch
        {
            "Temperature" => "°C",
            "Fan" => "RPM",
            "Load" or "Control" or "Level" => "%",
            "Power" => "W",
            "Clock" => "MHz",
            "Data" => "GB",
            "SmallData" => "MB",
            _ => ""
        };
        return $"{reading.NumericValue:0.##}{(unit.Length == 0 ? "" : " " + unit)}";
    }

    public static DiskUsageInfo GetDiskUsage()
    {
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.TotalSize > 0).ToArray();
            if (drives.Length == 0) return DiskUsageInfo.Empty;
            long total = drives.Sum(d => d.TotalSize);
            long used = drives.Sum(d => Math.Max(0, d.TotalSize - d.AvailableFreeSpace));
            return new DiskUsageInfo(used, total);
        }
        catch { return DiskUsageInfo.Empty; }
    }

    internal static IReadOnlyList<SystemSensorReading> SelectEssentialReadings(IReadOnlyList<SystemSensorReading> readings)
    {
        var selected = new List<SystemSensorReading>();
        foreach (var group in readings.GroupBy(r => new { r.Category, r.Hardware }))
        {
            switch (group.Key.Category)
            {
                case "CPU": AddBest(group, selected, "Load", 1); AddBest(group, selected, "Temperature", 1); break;
                case "GPU": AddBest(group, selected, "Load", 1); AddBest(group, selected, "Temperature", 1); AddBest(group, selected, "Data", 2); AddBest(group, selected, "SmallData", 2); AddBest(group, selected, "Fan", 1); break;
                case "Memory": AddBest(group, selected, "Load", 1); AddBest(group, selected, "Temperature", 2); AddBest(group, selected, "Data", 6); break;
            }
        }
        return selected;
    }

    private static void AddBest(IEnumerable<SystemSensorReading> group, ICollection<SystemSensorReading> selected, string type, int count)
    {
        foreach (var item in group.Where(r => r.SensorType.Equals(type, StringComparison.OrdinalIgnoreCase)).OrderByDescending(GetPriority).Take(count)) selected.Add(item);
    }

    private static int GetPriority(SystemSensorReading r) => (r.Sensor.Contains("Total", StringComparison.OrdinalIgnoreCase) ? 50 : 0) + (r.Sensor.Contains("Package", StringComparison.OrdinalIgnoreCase) ? 45 : 0) + (r.Sensor.Contains("Used", StringComparison.OrdinalIgnoreCase) ? 35 : 0);

    private static string? FindExecutable()
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { var match = Directory.EnumerateFiles(root, "LibreHardwareMonitor.exe", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(root, "OpenHardwareMonitor.exe", SearchOption.AllDirectories)).FirstOrDefault(); if (match != null) return match; } catch { }
        }
        return null;
    }

    private static bool IsMonitorProcessRunning()
    {
        foreach (string name in ExecutableNames)
        {
            try { using var process = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)).FirstOrDefault(); if (process != null) return true; } catch { }
        }
        return false;
    }

    private static bool IsRegisteredInUninstallKeys()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }) foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (string keyName in uninstall.GetSubKeyNames()) using (var key = uninstall.OpenSubKey(keyName))
                {
                    string? display = key?.GetValue("DisplayName") as string;
                    if (display?.Contains("LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase) == true || display?.Contains("OpenHardwareMonitor", StringComparison.OrdinalIgnoreCase) == true) return true;
                }
            }
            catch { }
        }
        return false;
    }
}

public sealed record SystemSensorReading(string Category, string Hardware, string Sensor, string Value, string SensorType) { public double NumericValue { get; init; } }
public sealed record WorkerReading(string Category, string Hardware, string Sensor, double NumericValue, string SensorType);
public sealed record DiskUsageInfo(long UsedBytes, long TotalBytes)
{
    public static DiskUsageInfo Empty => new(0, 0);
    public double UsagePercent => TotalBytes <= 0 ? 0 : Math.Clamp(UsedBytes * 100d / TotalBytes, 0, 100);
    public string UsedText => TotalBytes <= 0 ? "N/A" : $"{Format(UsedBytes)} used";
    public string TotalText => TotalBytes <= 0 ? "N/A" : $"of {Format(TotalBytes)}";
    private static string Format(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.#} GB";
}
public sealed record HardwareScanResult(int HardwareCount, IReadOnlyList<SystemSensorReading> Readings);
public sealed record HardwareInstallProgress(string Message, double Percent, bool IsIndeterminate);
