using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace KalOS.Services
{
    /// <summary>
    /// Persistent file logger.
    ///
    /// All entries are funneled through a single background writer so no caller
    /// (including the UI thread) ever blocks on disk I/O — the old implementation
    /// was "async" in name only and did synchronous AppendAllText on the caller's
    /// thread. Session files are capped in size and rotated; never grows unbounded.
    /// </summary>
    public class LogService : IDisposable
    {
        private const long MaxFileBytes = 2L * 1024 * 1024; // 2 MB per session file

        private static readonly string LogDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KalOS", "Logs");

        private readonly string _logPath;
        private readonly Channel<string> _queue =
            Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _writer;
        private bool _disposed;

        public LogService()
        {
            Directory.CreateDirectory(LogDirectoryPath);
            _logPath = Path.Combine(LogDirectoryPath, $"KalOS_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _writer = Task.Run(WriteLoopAsync);
        }

        /// <summary>Directory that holds all session log files.</summary>
        public static string GetLogDirectory() => LogDirectoryPath;

        /// <summary>Full path to the current session's log file.</summary>
        public string GetLogPath() => _logPath;

        /// <summary>
        /// Enqueues a log entry. Never throws and never blocks on disk I/O;
        /// the background writer performs the actual append.
        /// </summary>
        public Task WriteAsync(string category, string operation, string result, bool isError = false)
        {
            string level = isError ? "ERROR" : "INFO";
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{category}] {operation}: {result}";
            _queue.Writer.TryWrite(line);
            return Task.CompletedTask;
        }

        /// <summary>Flushes pending entries and completes the writer (call at shutdown).</summary>
        public async Task FlushAsync()
        {
            _queue.Writer.TryComplete();
            try { await _writer; } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _ = FlushAsync(); } catch { }
        }

        private async Task WriteLoopAsync()
        {
            await foreach (var line in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never take the app down; drop the entry.
                }
            }
        }

        private void RotateIfNeeded()
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length <= MaxFileBytes) return;

            string backup = _logPath + ".1";
            try { File.Delete(backup); } catch { }
            try { File.Move(_logPath, backup); } catch { }
        }
    }
}