using System;
using System.Threading.Tasks;

namespace KalOS.Services
{
    /// <summary>
    /// Creates a System Restore point before destructive DriverStore
    /// operations (spec 7.3 — the default is to protect, matching RAPR's
    /// NoRestorePoint flag semantics: restore points happen unless the user
    /// explicitly opts out).
    ///
    /// Uses Checkpoint-Computer (the supported PowerShell surface of
    /// SRSetRestorePointW). Best-effort: failure to create a restore point
    /// (disabled System Restore, no eligible volume, frequency-throttled)
    /// is logged and reported, never swallowed silently — the caller decides
    /// whether to proceed.
    /// </summary>
    public sealed class RestorePointService
    {
        private readonly ProcessManager _processManager;
        private readonly LoggingService _log;

        public RestorePointService(ProcessManager processManager, LoggingService log)
        {
            _processManager = processManager;
            _log = log;
        }

        /// <summary>True when a restore point was created (or already exists within the frequency window).</summary>
        public async Task<bool> CreateAsync(string description)
        {
            try
            {
                var (_, exitCode) = await _processManager.RunWithOutputAsync(
                    "powershell.exe",
                    $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description '{description}' -RestorePointType 'MODIFY_SETTINGS'\"");
                if (exitCode == 0)
                {
                    _log.Info($"Restore point '{description}' created.");
                    return true;
                }
                _log.Info($"Restore point creation returned exit code {exitCode} (System Restore may be disabled or throttled).");
                return false;
            }
            catch (Exception ex)
            {
                _log.Info($"Restore point creation failed: {ex.Message}");
                return false;
            }
        }
    }
}
