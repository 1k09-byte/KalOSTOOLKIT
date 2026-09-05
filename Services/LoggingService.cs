using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace KaliteKit.Services
{
    /// <summary>
    /// Session-scoped in-memory log (what the UI binds to) plus persistence.
    ///
    /// Every entry is mirrored to the shared <see cref="LogService"/> background
    /// writer, so there is exactly one file-I/O path in the app. Safe to construct
    /// directly (falls back to resolving the singleton) or via DI.
    /// </summary>
    public class LoggingService
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KaliteKit", "Logs");

        private readonly LogService? _fileLog;
        private readonly List<Models.CleanupLog> _logs = new();
        public event Action<Models.CleanupLog>? LogAdded;

        public IReadOnlyList<Models.CleanupLog> Logs => _logs;

        // Lazily-created fallback file logger for callers that construct this
        // service without DI (tests, tools, the Setup wizard's composition
        // root). Previously this fell back through App.Services, which made
        // this file uncompilable outside the WinUI app — the KaliteKit Setup
        // installer source-shares it, so the fallback must stay App-free.
        private static LogService? _fallbackFileLog;

        public LoggingService(LogService? logService = null)
        {
            _fileLog = logService ?? (_fallbackFileLog ??= new LogService());
            Directory.CreateDirectory(LogDir);
        }

        private void Log(string level, string message)
        {
            var entry = new Models.CleanupLog { Message = message, Level = level, Timestamp = DateTime.Now };
            _logs.Add(entry);
            LogAdded?.Invoke(entry);

            // Mirror to the persistent file through the shared background writer.
            _ = _fileLog?.WriteAsync(level, "Session", message, isError: level is "Error" or "Warn");
        }

        public void Info(string message) => Log("Info", message);
        public void Warn(string message) => Log("Warn", message);
        public void Error(string message) => Log("Error", message);
        public void Success(string message) => Log("Success", message);

        public async Task SaveToFileAsync(string fileName = "cleanup_log.txt")
        {
            try
            {
                var path = Path.Combine(LogDir, fileName);
                using var writer = new StreamWriter(path, append: false);
                foreach (var log in _logs)
                {
                    await writer.WriteLineAsync($"[{log.Timestamp:yyyy-MM-dd HH:mm:ss}] [{log.Level}] {log.Message}");
                }
            }
            catch { }
        }

        public void Clear()
        {
            _logs.Clear();
        }
    }
}