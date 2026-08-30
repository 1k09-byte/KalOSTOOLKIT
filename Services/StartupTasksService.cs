using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KalOS.Services;

/// <summary>One user-configured startup command (a hidden shell command run at login).</summary>
public sealed record StartupTask(string Command, bool Enabled = true);

/// <summary>Persisted startup-banner configuration.</summary>
public sealed class StartupSettings
{
    /// <summary>Legacy field kept so old startup.json files deserialize cleanly.
    /// Startup is mandatory now, so nothing reads this value anymore.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool StartupEnabled { get; set; } = false;

    /// <summary>The user's startup command list, run hidden in order at login.</summary>
    public List<StartupTask> Tasks { get; set; } = new();

    /// <summary>Whether the banner also checks the toolkit for updates at login.</summary>
    public bool CheckUpdatesAtStartup { get; set; } = true;
}

/// <summary>
/// Stores and runs the user's startup command list. Commands execute hidden
/// (no terminal window) in order; progress is reported per task so the banner
/// can show a live progress bar and status text.
/// </summary>
public sealed class StartupTasksService
{
    private readonly LoggingService _log;

    public static string SettingsPath =>
        Path.Combine(UpdateService.AppDataFolder, "startup.json");

    /// <summary>Registry value name used in the HKCU Run key (shown in Task Manager's Startup tab).</summary>
    public const string RunKeyValueName = "KalOS";

    public StartupTasksService(LoggingService log)
    {
        _log = log;
    }

    // ── Persistence ──────────────────────────────────────────────────────

    public StartupSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<StartupSettings>(File.ReadAllText(SettingsPath)) ?? new StartupSettings();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Startup settings load failed: {ex.Message}");
        }
        return new StartupSettings();
    }

    public void Save(StartupSettings settings)
    {
        try
        {
            Directory.CreateDirectory(UpdateService.AppDataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            _log.Warn($"Startup settings save failed: {ex.Message}");
        }
    }

    // ── Autostart (HKCU Run key) ─────────────────────────────────────────

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>True when the HKCU Run key currently points at this exe with the --startup flag.</summary>
    public static bool IsRegisteredInRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunKeyValueName) is string value &&
                   value.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Writes (or updates) the HKCU Run entry: "KalOS.exe" --startup.</summary>
    public static void EnableAutostart()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(RunKeyValueName, $"\"{exePath}\" --startup");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EnableAutostart failed: {ex.Message}");
        }
    }

    /// <summary>Removes the HKCU Run entry. Kept for API compatibility —
    /// startup is mandatory, so this is never called in normal flow.</summary>
    public static void DisableAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunKeyValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DisableAutostart failed: {ex.Message}");
        }
    }

    // ── Execution ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs each enabled command hidden, in order. Reports (index, total,
    /// description, done) so the banner can drive its progress bar.
    /// </summary>
    public async Task RunTasksAsync(
        IReadOnlyList<StartupTask> tasks,
        Action<int, int, string>? onProgress = null)
    {
        var enabled = new List<StartupTask>();
        foreach (var t in tasks)
        {
            if (t.Enabled && !string.IsNullOrWhiteSpace(t.Command)) enabled.Add(t);
        }

        int total = enabled.Count;
        for (int i = 0; i < total; i++)
        {
            var task = enabled[i];
            onProgress?.Invoke(i, total, task.Command);
            try
            {
                await RunOneHiddenAsync(task.Command);
            }
            catch (Exception ex)
            {
                _log.Warn($"Startup task failed ({task.Command}): {ex.Message}");
            }
        }
        onProgress?.Invoke(total, total, string.Empty);
    }

    /// <summary>
    /// Runs one command with no visible window. Uses cmd.exe so shell
    /// constructs (quotes, &amp;&amp;, redirects) behave like a terminal,
    /// but CreateNoWindow keeps everything invisible.
    /// </summary>
    private static Task RunOneHiddenAsync(string command)
    {
        var tcs = new TaskCompletionSource<bool>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /s /c \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) => tcs.TrySetResult(true);
            if (!proc.Start())
            {
                tcs.TrySetResult(false);
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }
}
