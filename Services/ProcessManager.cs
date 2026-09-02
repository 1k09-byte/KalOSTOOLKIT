using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace KalOS.Services
{
    public class ProcessManager
    {
        private readonly LoggingService _log;

        public ProcessManager(LoggingService log)
        {
            _log = log;
        }

        public async Task<int> RunAsync(string fileName, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var (_, exitCode) = await RunWithOutputAsync(fileName, arguments, timeout, cancellationToken);
            return exitCode;
        }

        public async Task<(string output, int exitCode)> RunWithOutputAsync(string fileName, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var (output, _, exitCode) = await RunWithOutputAndErrorAsync(fileName, arguments, timeout, cancellationToken);
            return (output, exitCode);
        }

        public async Task<(string output, string error, int exitCode)> RunWithOutputAndErrorAsync(string fileName, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default, string? workingDirectory = null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory
                };

                using var process = new Process { StartInfo = psi };
                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { process.Kill(true); } catch { }
                        _log.Warn($"Process '{fileName}' canceled by user");
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (sw.Elapsed > effectiveTimeout)
                    {
                        try { process.Kill(true); } catch { }
                        _log.Warn($"Process '{fileName}' timed out after {effectiveTimeout.TotalSeconds}s");
                        string partialOutput = outputTask.IsCompleted ? await outputTask : string.Empty;
                        string partialError = errorTask.IsCompleted ? await errorTask : string.Empty;
                        return (partialOutput, partialError, -1);
                    }
                    await Task.Delay(250);
                }

                string output = await outputTask;
                string error = await errorTask;
                return (output, error, process.ExitCode);
            }
            catch (Exception ex)
            {
                // A missing executable is the expected outcome when probing for
                // optional tools (choco, scoop, …) that aren't installed; callers
                // already treat an exit code of -1 as "unavailable". Record it
                // quietly instead of logging a red Error on every app launch on
                // machines without those tools.
                if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 }) // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
                {
                    _log.Info($"'{fileName}' not found — assumed not installed.");
                    return (string.Empty, string.Empty, -1);
                }
                _log.Error($"Failed to run '{fileName} {arguments}': {ex.Message}");
                return (string.Empty, string.Empty, -1);
            }
        }

        public async Task StopProcessByNameAsync(string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.Kill(true);
                        _log.Info($"Killed process: {processName} (PID {proc.Id})");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Could not kill {processName}: {ex.Message}");
                    }
                }
                await Task.Delay(500);
            }
            catch { }
        }

        public bool IsProcessRunning(string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                return processes.Length > 0;
            }
            catch { return false; }
        }
    }
}
