using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace KaliteKit.Services;

/// <summary>
/// Minimal Windhawk manager — fixed URL + windhawk.json import only.
/// <para>
/// Downloads from <c>https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.3/windhawk_setup.exe</c>
/// with <c>/S</c> and imports <c>Assets/windhawk.json</c> via <c>windhawk-cli.exe data import</c>.
/// No windhawk_pins.json / windhawk_mods.json / per-mod deploy logic.
/// </para>
/// </summary>
public sealed class WindhawkManagerService
{
    private const string InstallDirName = "Windhawk";
    private const string WindhawkRootRegistryPath = @"SOFTWARE\Windhawk";

    public const string FixedWindhawkUrl = "https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.3/windhawk_setup.exe";

    private static readonly HttpClient DownloadClient = CreateHttpClient();
    private readonly LoggingService _log;

    public WindhawkManagerService(LoggingService log) => _log = log;

    // ── Install check ───────────────────────────────────────────────

    public bool IsInstalled()
    {
        bool exeOk = File.Exists(GetExecutablePath());
        bool regOk = RootRegistryKeyExists();
        _log.Info($"Windhawk installed check: exe={exeOk}, registry={regOk}");
        return exeOk || regOk;
    }

    public string GetInstallDirectory()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(programFiles, InstallDirName),
            Path.Combine(programFilesX86, InstallDirName),
            Path.Combine(localAppData, "Programs", InstallDirName),
        };
        return candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "windhawk.exe")))
               ?? candidates.FirstOrDefault(p => Directory.Exists(p))
               ?? candidates[0];
    }

    public string GetExecutablePath() => Path.Combine(GetInstallDirectory(), "windhawk.exe");

    private bool RootRegistryKeyExists()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(WindhawkRootRegistryPath);
                if (key != null) return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Windhawk registry check failed ({view}): {ex.Message}");
            }
        }
        return false;
    }

    // ── Import windhawk.json (test button, no download) ────────────

    public async Task ImportWindhawkJsonAsync(
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsInstalled())
            throw new InvalidOperationException("Windhawk is not installed — install it first before importing windhawk.json.");

        string[] jsonCandidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "windhawk.json"),
            Path.Combine(AppContext.BaseDirectory, "windhawk.json"),
        };
        string? jsonSource = jsonCandidates.FirstOrDefault(File.Exists);
        if (jsonSource == null)
            throw new FileNotFoundException("Assets/windhawk.json not found. Ensure KaliteKit.csproj includes it as Content.");

        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"windhawk_{Guid.NewGuid():N}.json");
        try
        {
            File.Copy(jsonSource, tempJsonPath, overwrite: true);
            _log.Info($"Importing Windhawk settings from {jsonSource} (via temp {tempJsonPath})");
            status?.Report("Importing windhawk.json into Windhawk...");

            string? cliPath = FindWindhawkCliPath();
            if (cliPath == null || !File.Exists(cliPath))
                throw new FileNotFoundException($"windhawk-cli.exe not found. Checked GetInstallDirectory and Program Files. Install dir: {GetInstallDirectory()}");

            _log.Info($"Running: \"{cliPath}\" data import \"{tempJsonPath}\" --confirm-app-restart --yes");
            // Match AutoOS exactly: Hidden window, WorkingDirectory = Windhawk folder
            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = $"data import \"{tempJsonPath}\" --confirm-app-restart --yes",
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? GetInstallDirectory(),
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start windhawk-cli.exe.");
            string stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            string stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            _log.Info($"windhawk-cli import exit {proc.ExitCode} stdout: {stdout} stderr: {stderr}");
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"windhawk-cli import failed (exit {proc.ExitCode}): {stderr} {stdout}");

            // Windhawk's import with --confirm-app-restart restarts the app and
            // by default reopens the main window (bottom-right page the user saw).
            // Force a tray-only restart so the UI stays hidden — exactly what
            // `windhawk.exe -restart -tray-only` is for (see command-line.txt).
            try
            {
                string exe = GetExecutablePath();
                if (File.Exists(exe))
                {
                    _log.Info("Restarting Windhawk tray-only to hide UI after import...");
                    var psiRestart = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "-restart -tray-only",
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? GetInstallDirectory(),
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = true,
                        CreateNoWindow = true,
                    };
                    using var rp = Process.Start(psiRestart);
                    if (rp != null)
                    {
                        // Don't block forever — restart is quick, but don't hang the UI
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(10));
                        try { await rp.WaitForExitAsync(cts.Token); } catch { }
                    }
                    await Task.Delay(800, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Tray-only restart after import failed (non-fatal): {ex.Message}");
            }

            status?.Report("windhawk.json imported.");
            _log.Info("Windhawk json import completed.");
        }
        finally
        {
            try { if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath); } catch { }
        }
    }

    // ── Download + install + import (installer pipeline) ────────────

    public async Task InstallFixedWindhawkAndImportAsync(
        IProgress<double>? progress = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        string installerPath = Path.Combine(Path.GetTempPath(), $"windhawk_setup_{Guid.NewGuid():N}.exe");
        try
        {
            status?.Report("Downloading Windhawk 2.0.0-alpha.3...");
            _log.Info($"Downloading Windhawk fixed from {FixedWindhawkUrl}");
            progress?.Report(5);
            using (var response = await DownloadClient.GetAsync(FixedWindhawkUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(installerPath);
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;
                    if (total.HasValue && total.Value > 0)
                        progress?.Report(5 + (totalRead * 45d / total.Value));
                }
            }

            var info = new FileInfo(installerPath);
            if (info.Length < 100_000)
                throw new InvalidOperationException("The downloaded Windhawk installer is unexpectedly small.");
            _log.Info($"Downloaded Windhawk installer ({info.Length} bytes) to {installerPath}");
            progress?.Report(55);
            status?.Report("Running the silent installer (Windhawk /S)...");
            int exitCode = await RunFixedInstallerAsync(installerPath, cancellationToken);
            if (exitCode != 0)
                throw new InvalidOperationException($"The Windhawk installer exited with code {exitCode}.");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsInstalled()) break;
                await Task.Delay(1000, cancellationToken);
            }
            if (!IsInstalled())
                throw new InvalidOperationException("Windhawk installer finished, but windhawk.exe was not found afterwards.");

            progress?.Report(70);
            status?.Report("Windhawk installed — importing settings from windhawk.json...");
            await ImportWindhawkJsonAsync(status, cancellationToken);
            progress?.Report(100);
            status?.Report("Windhawk installed and settings imported.");
            _log.Info("Fixed Windhawk install + import completed.");
        }
        finally
        {
            try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
        }
    }

    private string? FindWindhawkCliPath()
    {
        var candidates = new[]
        {
            Path.Combine(GetInstallDirectory(), "windhawk-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windhawk", "windhawk-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windhawk", "windhawk-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Windhawk", "windhawk-cli.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<int> RunFixedInstallerAsync(string installerPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/S",
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath(),
            UseShellExecute = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windhawk installer.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("The Windhawk installer timed out after five minutes.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KaliteKit/1.0 (+windhawk)");
        return client;
    }

    // ── Kept for unit tests (LaunchProcessAsStandardUser etc.) ──────
    // Minimal shims so tests\Services\WindhawkManagerServiceTests still compile.
    // The full old engine logic is intentionally removed.

    internal static bool TryParseModStatusFileName(string fileName, string modId, out long sessionPid, out long processPid)
    {
        sessionPid = 0; processPid = 0;
        string suffix = "_" + modId;
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = fileName[..^suffix.Length].Split('_');
        if (parts.Length != 3
            || !long.TryParse(parts[0], out long parsedSessionPid) || parsedSessionPid <= 0
            || !long.TryParse(parts[2], out long parsedProcessPid) || parsedProcessPid <= 0) return false;
        sessionPid = parsedSessionPid; processPid = parsedProcessPid;
        return true;
    }

    internal static bool IsProcessAlive(long processId, string expectedImageName)
    {
        if (processId <= 0 || processId > int.MaxValue) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            process.Refresh();
            if (process.HasExited) return false;
            return string.IsNullOrEmpty(expectedImageName)
                || process.ProcessName.Equals(expectedImageName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool LaunchProcessAsStandardUser(string executable, string? arguments, out int processId, out int lastError, out string lastStage)
    {
        processId = 0; lastError = 0; lastStage = string.Empty;
        IntPtr hToken = IntPtr.Zero, duplicated = IntPtr.Zero, restricted = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenMaximumAllowed, out hToken))
            { lastStage = "OpenProcessToken"; lastError = Marshal.GetLastWin32Error(); return false; }
            if (!DuplicateTokenEx(hToken, TokenMaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out duplicated))
            { lastStage = "DuplicateTokenEx"; lastError = Marshal.GetLastWin32Error(); return false; }
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            byte[] sidBytes = new byte[adminSid.BinaryLength];
            adminSid.GetBinaryForm(sidBytes, 0);
            IntPtr sidPtr = Marshal.AllocHGlobal(sidBytes.Length);
            try
            {
                Marshal.Copy(sidBytes, 0, sidPtr, sidBytes.Length);
                var disable = new[] { new SidAndAttributes { Sid = sidPtr, Attributes = 0 } };
                if (!CreateRestrictedToken(duplicated, DisableMaxPrivilege | SandboxInert, 1, disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted))
                { lastStage = "CreateRestrictedToken"; lastError = Marshal.GetLastWin32Error(); return false; }
            }
            finally { Marshal.FreeHGlobal(sidPtr); }
            var mediumLabel = new SecurityIdentifier("S-1-16-8192");
            byte[] labelBytes = new byte[mediumLabel.BinaryLength];
            mediumLabel.GetBinaryForm(labelBytes, 0);
            IntPtr labelSid = Marshal.AllocHGlobal(labelBytes.Length);
            try
            {
                Marshal.Copy(labelBytes, 0, labelSid, labelBytes.Length);
                var label = new TokenMandatoryLabel { Label = new SidAndAttributes { Sid = labelSid, Attributes = SeGroupIntegrity } };
                if (!SetTokenInformation(restricted, TokenIntegrityLevel, ref label, (uint)Marshal.SizeOf<TokenMandatoryLabel>()))
                { lastStage = "SetTokenInformation"; lastError = Marshal.GetLastWin32Error(); return false; }
            }
            finally { Marshal.FreeHGlobal(labelSid); }
            var startupInfo = new StartUpInfo { cb = Marshal.SizeOf<StartUpInfo>(), lpDesktop = "winsta0\\default" };
            var processInfo = new ProcessInformation();
            if (!CreateProcessWithToken(restricted, 0, executable, arguments, 0, IntPtr.Zero, null, ref startupInfo, out processInfo))
            { lastStage = "CreateProcessWithToken"; lastError = Marshal.GetLastWin32Error(); return false; }
            processId = processInfo.dwProcessId;
            CloseHandle(processInfo.hProcess); CloseHandle(processInfo.hThread);
            return true;
        }
        finally
        {
            if (restricted != IntPtr.Zero) CloseHandle(restricted);
            if (duplicated != IntPtr.Zero) CloseHandle(duplicated);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
        }
    }

    private const uint TokenMaximumAllowed = 0x02000000;
    private const uint DisableMaxPrivilege = 0x1;
    private const uint SandboxInert = 0x2;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x20;
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenMandatoryLabel { public SidAndAttributes Label; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartUpInfo { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existingTokenHandle, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newTokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CreateRestrictedToken(IntPtr existingTokenHandle, uint flags, uint disableSidCount, [In] SidAndAttributes[]? sidsToDisable, uint deletePrivilegeCount, IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newTokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, [In] ref TokenMandatoryLabel tokenInformation, uint tokenInformationLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithToken(IntPtr token, uint logonFlags, string applicationName, string? commandLine, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartUpInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
