using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services
{
    /// <summary>
    /// Robust wrapper around the Windows Package Manager (winget) CLI.
    /// </summary>
    public static class WingetHelper
    {
        private static bool _sourceInitialized;
        private static readonly SemaphoreSlim _sourceLock = new(1, 1);
        private static readonly SemaphoreSlim _repairLock = new(1, 1);
        private static readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

        /// <summary>
        /// Result of a winget invocation.
        /// </summary>
        public record WingetResult(int ExitCode, string StandardOutput, string StandardError, bool Success);

        /// <summary>
        /// Runs a winget command and returns the result.
        /// </summary>
        /// <param name="arguments">Arguments to pass to winget (without the leading "winget").</param>
        /// <param name="ensureSource">Whether to ensure the default winget source is present before running.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="WingetResult"/> containing exit code and output.</returns>
        public static async Task<WingetResult> RunAsync(
            string arguments,
            bool ensureSource = true,
            CancellationToken cancellationToken = default)
        {
            if (ensureSource)
            {
                await EnsureSourceAsync(cancellationToken);
            }

            string wingetPath = FindWingetExecutable();

            // Prefer the real executable path; fall back to the App Execution Alias through cmd.exe.
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!string.IsNullOrEmpty(wingetPath))
            {
                psi.FileName = wingetPath;
                psi.Arguments = arguments;
            }
            else
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c winget {arguments}";
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start cmd.exe to invoke winget.");
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            return new WingetResult(process.ExitCode, stdout, stderr, IsSuccessExitCode(process.ExitCode));
        }

        /// <summary>
        /// Runs a winget command and throws if it fails.
        /// </summary>
        public static async Task<WingetResult> RunOrThrowAsync(
            string arguments,
            bool ensureSource = true,
            CancellationToken cancellationToken = default)
        {
            var result = await RunAsync(arguments, ensureSource, cancellationToken);
            if (!result.Success)
            {
                string detail = !string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardError
                    : result.StandardOutput;
                throw new InvalidOperationException($"winget failed (exit {result.ExitCode}): {detail}");
            }
            return result;
        }

        /// <summary>
        /// Checks whether winget appears to be available on this machine.
        /// </summary>
        public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await RunAsync("--version", ensureSource: false, cancellationToken);
                return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Attempts to locate the winget executable on disk, bypassing the App Execution Alias.
        /// </summary>
        private static string FindWingetExecutable()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");

            if (Directory.Exists(windowsApps))
            {
                try
                {
                    // winget.exe is usually nested under the DesktopAppInstaller package folder.
                    string? candidate = Directory.EnumerateFiles(windowsApps, "winget.exe", SearchOption.AllDirectories)
                        .Where(f => new FileInfo(f).Length > 0)
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                    // Ignore access/unauthorized exceptions.
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Ensures the default winget source is present.
        /// </summary>
        private static async Task EnsureSourceAsync(CancellationToken cancellationToken)
        {
            if (_sourceInitialized) return;

            await _sourceLock.WaitAsync(cancellationToken);
            try
            {
                if (_sourceInitialized) return;

                var listResult = await RunAsync("source list", ensureSource: false, cancellationToken);
                bool hasWingetSource = listResult.StandardOutput.Contains("winget", StringComparison.OrdinalIgnoreCase);

                if (!hasWingetSource)
                {
                    await RunAsync(
                        "source add --name winget --arg https://cdn.winget.microsoft.com/cache --accept-source-agreements",
                        ensureSource: false,
                        cancellationToken);
                }

                _sourceInitialized = true;
            }
            catch (Exception ex)
            {
                // Best-effort; let the actual command surface real problems, but don't lose the reason.
                Debug.WriteLine($"WingetHelper.EnsureSourceAsync failed: {ex}");
            }
            finally
            {
                _sourceLock.Release();
            }
        }

        /// <summary>
        /// Attempts to install/repair the Windows Package Manager (App Installer) from the official Microsoft source.
        /// Returns true if winget appears to be available after the operation.
        /// </summary>
        public static async Task<bool> TryRepairAsync(CancellationToken cancellationToken = default, LogService? logService = null)
        {
            await _repairLock.WaitAsync(cancellationToken);

            string tempPath = Path.Combine(Path.GetTempPath(), $"Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle_{Guid.NewGuid():N}.msixbundle");

            try
            {
                // On fresh installs the Microsoft Store source certificate can be pinned incorrectly.
                // Allow winget to bypass that pinning so the source can be updated. We use the resolved
                // winget.exe path because the App Execution Alias ("winget.exe" on PATH) is precisely
                // what's broken in the repair scenario — relying on it would silently no-op.
                try
                {
                    string wingetPath = FindWingetExecutable();
                    if (!string.IsNullOrEmpty(wingetPath) && File.Exists(wingetPath))
                    {
                        var psiBypass = new ProcessStartInfo
                        {
                            FileName = wingetPath,
                            Arguments = "settings --enable BypassCertificatePinningForMicrosoftStore",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var bypassProcess = Process.Start(psiBypass);
                        if (bypassProcess != null)
                        {
                            await bypassProcess.WaitForExitAsync(cancellationToken);
                            logService?.WriteAsync("Winget", "Repair", $"Enabled BypassCertificatePinningForMicrosoftStore (exit {bypassProcess.ExitCode})", isError: bypassProcess.ExitCode != 0);
                        }
                    }
                    else
                    {
                        logService?.WriteAsync("Winget", "Repair", "Skipping BypassCertificatePinningForMicrosoftStore: winget.exe not found on disk (App Execution Alias is the reason winget is broken).", isError: false);
                    }
                }
                catch (Exception ex)
                {
                    logService?.WriteAsync("Winget", "Repair", $"Failed to enable bypass: {ex.Message}", isError: true);
                }

                logService?.WriteAsync("Winget", "Repair", "Downloading Microsoft.DesktopAppInstaller MSIX bundle from aka.ms/getwinget...", isError: false);
                byte[] fileBytes = await _httpClient.GetByteArrayAsync("https://aka.ms/getwinget", cancellationToken);
                await File.WriteAllBytesAsync(tempPath, fileBytes, cancellationToken);

                // Basic sanity check: an MSIX bundle starts with the ZIP signature "PK".
                if (fileBytes.Length < 2 || fileBytes[0] != 0x50 || fileBytes[1] != 0x4B)
                {
                    string msg = $"WingetHelper.TryRepairAsync: downloaded file is not an MSIX bundle (length={fileBytes.Length}).";
                    Debug.WriteLine(msg);
                    logService?.WriteAsync("Winget", "Repair", msg, isError: true);
                    return false;
                }
                logService?.WriteAsync("Winget", "Repair", $"Downloaded {fileBytes.Length} bytes. Installing via Add-AppxPackage...", isError: false);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command Add-AppxPackage -Path '{tempPath}'",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    try
                    {
                        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        throw;
                    }
                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;

                    if (process.ExitCode != 0)
                    {
                        string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                        logService?.WriteAsync("Winget", "Repair", $"Add-AppxPackage failed (exit {process.ExitCode}): {detail}", isError: true);
                    }
                    else
                    {
                        logService?.WriteAsync("Winget", "Repair", "Add-AppxPackage succeeded.", isError: false);
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = $"WingetHelper.TryRepairAsync failed: {ex}";
                Debug.WriteLine(msg);
                logService?.WriteAsync("Winget", "Repair", msg, isError: true);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
                _repairLock.Release();
            }

            bool available = await IsAvailableAsync(cancellationToken);
            if (!available)
            {
                logService?.WriteAsync("Winget", "Repair", "winget is STILL not available after Add-AppxPackage. The user may need to install the App Installer manually from the Microsoft Store.", isError: true);
            }
            return available;
        }

        /// <summary>
        /// Determines whether a winget exit code represents success.
        /// </summary>
        private static bool IsSuccessExitCode(int exitCode)
        {
            // 0 = success
            // -1978335189 = package already installed (0x8A150011)
            // -1978335184 = no applicable upgrade found (0x8A150010)
            return exitCode == 0 || exitCode == -1978335189 || exitCode == -1978335184;
        }

        /// <summary>
        /// Returns a user-friendly message for common winget error codes.
        /// Accepts long because some callers/reporting tools include an extra digit.
        /// </summary>
        public static string GetErrorMessage(long exitCode)
        {
            return exitCode switch
            {
                // 0x8A15005E APPINSTALLER_CLI_ERROR_PINNED_CERTIFICATE_MISMATCH
                -1978335138 or -19783335138 =>
                    "winget certificate pinning failed (0x8A15005E). Falling back to direct download.",
                -1978335189 => "Package is already installed.",
                -1978335184 => "No applicable upgrade found.",
                -1978334976 => "winget source data is missing. Try running 'winget source reset'.",
                -1978334969 => "winget source update failed. Check your internet connection.",
                _ => $"winget exited with code {exitCode}."
            };
        }
    }
}
