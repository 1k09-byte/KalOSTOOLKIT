using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services.Bios;

/// <summary>
/// Known SCEWIN failure codes mapped from stderr/exit-code patterns.
/// </summary>
public enum ScewinErrorCode
{
    None,
    BinaryNotFound,
    DriverMissing,
    NotElevated,
    BiosIncompatible,
    NvramWriteProtected,
    HiiDatabaseFailure,
    AccessDenied,
    UnknownError,
}

/// <summary>
/// Outcome of a single SCEWIN invocation.
/// </summary>
public sealed record ScewinResult(
    bool Success,
    string Stdout,
    string Stderr,
    int ExitCode,
    ScewinErrorCode Code,
    string HumanMessage);

/// <summary>
/// Process wrapper around AMI's SCEWIN_64.exe.  Never talks to the driver
/// directly — shells out to the real binary and parses its text I/O.
/// </summary>
public sealed class ScewinService
{
    private readonly LoggingService _log;

    // Under the user's profile — never a hardcoded drive root. A hardcoded
    // "C:\PostInstall" broke on machines where that folder does not exist and
    // turned every backup attempt into a DirectoryNotFoundException.
    private static readonly string BackupDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KalOS", "BiosBackups");

    private static readonly string[] SearchPaths = new[]
    {
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "SCEWIN_64.exe"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "SCEWIN", "SCEWIN_64.exe"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "SCEWIN2", "SCEWIN_64.exe"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SCEWIN_64.exe"),
        @"c:\PostInstall\Tools\SCEWIN_64.exe",
        @"c:\PostInstall\Tools\SCEWIN\SCEWIN_64.exe",
        @"c:\PostInstall\Tools\SCEWIN2\SCEWIN_64.exe",
        @"c:\PostInstall\SCEWIN_64.exe",
    };

    public ScewinService(LoggingService log)
    {
        _log = log;
        BinaryPath = AutoDetect();
        if (BinaryPath is not null)
            _log.Success($"SCEWIN auto-detected: {BinaryPath}");
    }

    /// <summary>Persisted path to the SCEWIN_64.exe binary.</summary>
    public string? BinaryPath { get; set; }

    private static string? AutoDetect()
    {
        foreach (var p in SearchPaths)
            if (File.Exists(p)) return p;
        return null;
    }

    // ── Preflight ──────────────────────────────────────────────────────

    public bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

    public bool IsBinaryConfigured =>
        !string.IsNullOrWhiteSpace(BinaryPath) && File.Exists(BinaryPath);

    /// <summary>
    /// Checks that the companion driver files sit next to the binary.
    /// SCEWIN needs amifldrv64.sys / amigendrv64.sys alongside it.
    /// </summary>
    public IReadOnlyList<string> MissingDriverFiles()
    {
        if (!IsBinaryConfigured) return Array.Empty<string>();
        var dir = Path.GetDirectoryName(BinaryPath)!;
        var missing = new List<string>();
        foreach (var name in new[] { "amifldrv64.sys", "amigendrv64.sys" })
        {
            if (!File.Exists(Path.Combine(dir, name)))
                missing.Add(name);
        }
        return missing;
    }

    // ── Export  ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>SCEWIN_64.exe /o /s &lt;file&gt;</c> to export every current
    /// NVRAM/BIOS setup variable to a text file in a temp location,
    /// then returns the file path and the raw result.
    /// </summary>
    public async Task<(string? FilePath, ScewinResult Result)> ExportAsync(CancellationToken ct = default)
    {
        var preflight = PreflightCheck();
        if (preflight is not null)
            return (null, preflight);

        var outPath = Path.Combine(Path.GetTempPath(), $"scewin_export_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var result = await RunScewinAsync($"/o /s \"{outPath}\"", ct);

        if (result.Success && File.Exists(outPath))
        {
            _log.Success($"SCEWIN export completed: {outPath}");
            return (outPath, result);
        }

        _log.Error($"SCEWIN export failed: {result.HumanMessage}");
        return (null, result);
    }

    // ── Import ─────────────────────────────────────────────────────────

    /// <summary>
    /// Auto-backs up current state, then runs <c>SCEWIN_64.exe /i /s &lt;file&gt;</c>
    /// to write changed values back to NVRAM.
    /// </summary>
    public async Task<ScewinResult> ImportAsync(string importFilePath, CancellationToken ct = default)
    {
        var preflight = PreflightCheck();
        if (preflight is not null) return preflight;

        // Always backup first
        var (backupPath, backupResult) = await ExportAsync(ct);
        if (backupPath is not null)
        {
            Directory.CreateDirectory(BackupDir);
            var dest = Path.Combine(BackupDir, $"backup_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.Copy(backupPath, dest, true);
            _log.Success($"Pre-import backup saved: {dest}");
        }
        else
        {
            _log.Warn($"Could not create pre-import backup: {backupResult.HumanMessage}");
        }

        var result = await RunScewinAsync($"/i /s \"{importFilePath}\"", ct);
        if (result.Success)
            _log.Success("SCEWIN import completed successfully.");
        else
            _log.Error($"SCEWIN import failed: {result.HumanMessage}");

        return result;
    }

    // ── Lock / password detection ─────────────────────────────────────

    /// <summary>
    /// Whether the export produced no settings AND no explicit error — a
    /// common signature of a firmware that is locked behind a supervisor
    /// password and reports nothing.
    /// </summary>
    public static bool LooksLocked(ScewinResult result)
        => !result.Success &&
           (result.Code == ScewinErrorCode.HiiDatabaseFailure ||
            result.Code == ScewinErrorCode.AccessDenied);

    // ── Backup management ──────────────────────────────────────────────

    public IReadOnlyList<string> GetBackupFiles()
    {
        if (!Directory.Exists(BackupDir)) return Array.Empty<string>();
        return Directory.GetFiles(BackupDir, "backup_*.txt");
    }

    // ── Core process runner ────────────────────────────────────────────

    private async Task<ScewinResult> RunScewinAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BinaryPath!,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(BinaryPath)!,
        };

        try
        {
            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var code = MapErrorCode(process.ExitCode, stdout, stderr);
            var human = HumanMessage(code, stderr);

            return new ScewinResult(
                process.ExitCode == 0 && code == ScewinErrorCode.None,
                stdout, stderr, process.ExitCode, code, human);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            return new ScewinResult(false, "", ex.Message, -1,
                ScewinErrorCode.NotElevated,
                "SCEWIN requires administrator privileges. Relaunch KalOS as admin.");
        }
        catch (Exception ex)
        {
            _log.Error($"SCEWIN process launch failed ({ex.GetType().Name}): {ex.Message}");
            return new ScewinResult(false, "", ex.Message, -1,
                ScewinErrorCode.UnknownError,
                $"SCEWIN could not be started: {ex.Message}");
        }
    }

    private ScewinResult? PreflightCheck()
    {
        if (!IsBinaryConfigured)
            return new ScewinResult(false, "", "SCEWIN_64.exe path not configured.", -1,
                ScewinErrorCode.BinaryNotFound,
                "SCEWIN_64.exe was not found. Place it (with amifldrv64.sys and amigendrv64.sys) in a Tools folder next to KalOS.exe — e.g. Tools\\SCEWIN_64.exe — then restart the app.");

        if (!IsElevated)
            return new ScewinResult(false, "", "Not elevated.", -1,
                ScewinErrorCode.NotElevated,
                "KalOS must be running as Administrator to use SCEWIN.");

        return null;
    }

    private static ScewinErrorCode MapErrorCode(int exitCode, string stdout, string stderr)
    {
        var combined = (stdout + stderr).ToUpperInvariant();

        if (combined.Contains("BIOS NOT COMPATIBLE") || combined.Contains("NOT SUPPORTED"))
            return ScewinErrorCode.BiosIncompatible;
        if (combined.Contains("RETRIEVING HII DATABASE") && combined.Contains("FAIL"))
            return ScewinErrorCode.HiiDatabaseFailure;
        if (combined.Contains("WRITE PROTECT") || combined.Contains("WRITE-PROTECT"))
            return ScewinErrorCode.NvramWriteProtected;
        if (combined.Contains("ACCESS") && combined.Contains("DENIED"))
            return ScewinErrorCode.AccessDenied;
        if (combined.Contains("DRIVER") && (combined.Contains("NOT FOUND") || combined.Contains("MISSING")))
            return ScewinErrorCode.DriverMissing;

        return exitCode != 0 ? ScewinErrorCode.UnknownError : ScewinErrorCode.None;
    }

    private static string HumanMessage(ScewinErrorCode code, string stderr) => code switch
    {
        ScewinErrorCode.None => "Operation completed successfully.",
        ScewinErrorCode.BinaryNotFound => "SCEWIN_64.exe was not found at the configured path.",
        ScewinErrorCode.DriverMissing => "AMI driver files (amifldrv64.sys / amigendrv64.sys) are missing. Place them in the same folder as SCEWIN_64.exe.",
        ScewinErrorCode.NotElevated => "Administrator privileges are required. Relaunch KalOS as admin.",
        ScewinErrorCode.BiosIncompatible => "This BIOS is not compatible with the supplied SCEWIN binary. You may need a version matched to your motherboard vendor.",
        ScewinErrorCode.NvramWriteProtected => "NVRAM write-protection is enabled. Disable it in BIOS setup before importing settings.",
        ScewinErrorCode.HiiDatabaseFailure => "Failed to retrieve the HII database from the firmware. The AMI driver may not be loaded or the BIOS may be locked.",
        ScewinErrorCode.AccessDenied => "Access denied by the firmware. A BIOS supervisor password may be required.",
        ScewinErrorCode.UnknownError => $"SCEWIN failed without a recognized diagnostic (exit code may be unavailable). {(string.IsNullOrWhiteSpace(stderr) ? "Check the executable, companion files, and Windows Event Viewer." : stderr.Trim())}",
        _ => "Unknown error.",
    };
}
