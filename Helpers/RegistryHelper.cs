using System;
using System.IO;
using Microsoft.Win32;

namespace KalOS.Helpers;

public static class RegistryHelper
{
    private static string BackupDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KalOS", "Backups");

    public static void BackupRegistryKey(string keyPath)
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);

            var sanitized = keyPath.Replace("\\", "_").Replace(":", "");
            var backupPath = Path.Combine(BackupDirectory, $"{sanitized}_{DateTime.Now:yyyyMMddHHmmss}.reg");

            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "reg.exe";
            process.StartInfo.Arguments = $"export \"{keyPath}\" \"{backupPath}\" /y";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to backup registry key '{keyPath}': {ex.Message}", ex);
        }
    }

    public static void RestoreRegistryKey(string backupPath)
    {
        try
        {
            if (!File.Exists(backupPath))
                throw new FileNotFoundException("Backup file not found", backupPath);

            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "reg.exe";
            process.StartInfo.Arguments = $"import \"{backupPath}\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidOperationException($"Failed to restore registry key from '{backupPath}': {ex.Message}", ex);
        }
    }

    public static object? GetRegistryValue(string keyPath, string valueName)
    {
        try
        {
            // keyPath points AT the key that contains the value (e.g.
            // "HKLM\...\PriorityControl"), so OpenBaseKey must receive the
            // full path — trimming the last segment would read the parent key.
            using var baseKey = OpenBaseKey(keyPath, out var subKeyPath);
            using var key = baseKey?.OpenSubKey(subKeyPath);
            return key?.GetValue(valueName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read registry value '{keyPath}\\{valueName}': {ex.Message}", ex);
        }
    }

    public static void SetRegistryValue(string keyPath, string valueName, object value, RegistryValueKind kind)
    {
        try
        {
            // keyPath points AT the key that will contain the value.
            using var baseKey = OpenBaseKey(keyPath, out var subKeyPath);
            using var key = baseKey?.CreateSubKey(subKeyPath, true);
            key?.SetValue(valueName, value, kind);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to set registry value '{keyPath}\\{valueName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Deletes a single value from a registry key (used when reverting a tweak that
    /// created a value which didn't exist before it was applied). Missing key/value is
    /// treated as already-reverted, not an error.
    /// </summary>
    public static void DeleteRegistryValue(string keyPath, string valueName)
    {
        try
        {
            // keyPath points AT the key that contains the value.
            using var baseKey = OpenBaseKey(keyPath, out var subKeyPath);
            using var key = baseKey?.OpenSubKey(subKeyPath, true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to delete registry value '{keyPath}\\{valueName}': {ex.Message}", ex);
        }
    }

    private static RegistryKey? OpenBaseKey(string hivePart, out string subKeyPath)
    {
        var parts = hivePart.Split('\\', 2);
        subKeyPath = parts.Length > 1 ? parts[1] : "";

        return parts[0].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Registry.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => Registry.CurrentConfig,
            _ => throw new ArgumentException($"Unknown registry hive: {parts[0]}")
        };
    }
}
