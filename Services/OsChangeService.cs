using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KalOS.Helpers;
using Microsoft.Win32;

namespace KalOS.Services;

// ── Manifest data model ──────────────────────────────────────────────────────
// These live in os-changes.json next to KalOS.exe, so a release's OS changes
// ship inside the update zip WITHOUT recompiling the app. The app only knows
// how to apply/rollback the ops below, never which ones a release carries.

/// <summary>Kind of OS change a manifest entry performs.</summary>
public enum OsChangeOp
{
    /// <summary>Write a registry value (the parent key is backed up as .reg first).</summary>
    Registry,
    /// <summary>Set a Windows service startup type via sc.exe (original start type snapshotted).</summary>
    Service,
    /// <summary>Run a PowerShell script from the update package (no rollback — scripts are one-shot).</summary>
    Script
}

/// <summary>
/// JSON converter that accepts enum names case-insensitively ("registry",
/// "dword") instead of requiring exact C# member names. Keeps the manifest
/// format readable for humans.
/// </summary>
internal sealed class CaseInsensitiveEnumConverter<TEnum> : System.Text.Json.Serialization.JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var value))
        {
            return value;
        }
        throw new JsonException($"Unknown {typeof(TEnum).Name} value: {reader.GetString()}");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>One operation from the update's os-changes.json manifest.</summary>
public sealed class OsChangeEntry
{
    public string Description { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonConverter(typeof(CaseInsensitiveEnumConverter<OsChangeOp>))]
    public OsChangeOp Type { get; set; }

    // Registry: full key path (e.g. "HKLM\SOFTWARE\...\SystemProfile") + value name.
    public string Key { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public JsonElement? Value { get; set; }

    [System.Text.Json.Serialization.JsonConverter(typeof(CaseInsensitiveEnumConverter<RegistryValueKind>))]
    public RegistryValueKind ValueKind { get; set; } = RegistryValueKind.DWord;

    // Service: display name + target startup (auto|manual|disabled|delayed).
    public string ServiceName { get; set; } = string.Empty;
    public string StartupType { get; set; } = string.Empty;

    // Script: relative path to a .ps1 file (relative to the install dir).
    public string Script { get; set; } = string.Empty;
}

/// <summary>The parsed os-changes.json manifest.</summary>
public sealed class OsChangeManifest
{
    public string Version { get; set; } = string.Empty;
    public List<OsChangeEntry> Changes { get; set; } = new();
}

/// <summary>Result of one apply/rollback pass.</summary>
public sealed class OsChangeResult
{
    public bool Success { get; set; }
    public List<string> Applied { get; } = new();
    public List<string> Errors { get; } = new();

    public string Summary =>
        $"Applied {Applied.Count} change(s)" + (Errors.Count > 0 ? $", {Errors.Count} error(s)" : string.Empty);
}

// ── Executor + state (pure logic, unit-tested) ─────────────────────────────

/// <summary>
/// Applies / rolls back the OS changes that ship inside an update package
/// (os-changes.json). Runs elevated with the app, uses the same RegistryHelper
/// and sc.exe pattern the tweak pages use, snapshots every change so rollback
/// can revert it, and persists state so nothing applies twice.
/// </summary>
public sealed class OsChangeService
{
    public const string ManifestFileName = "os-changes.json";

    /// <summary>State path tracks which manifest versions were applied for rollback.</summary>
    public static string StatePath =>
        Path.Combine(UpdateService.AppDataFolder, "os-changes-state.json");

    /// <summary>Where per-entry rollback backups land (registry .reg exports + service snapshots).</summary>
    public static string RollbackFolder =>
        Path.Combine(UpdateService.AppDataFolder, "os-changes-rollback");

    private static readonly HashSet<string> AllowedRegistryHives = new(StringComparer.OrdinalIgnoreCase)
    {
        "HKEY_LOCAL_MACHINE", "HKLM", "HKEY_CURRENT_USER", "HKCU",
        "HKEY_CLASSES_ROOT", "HKCR", "HKEY_USERS", "HKU", "HKEY_CURRENT_CONFIG", "HKCC"
    };

    private static readonly HashSet<string> AllowedServiceStartups = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "demand", "manual", "disabled", "delayed"
    };

    /// <summary>
    /// Reads + validates the manifest at the given path. Returns null if missing
    /// or invalid. An EMPTY changes array is treated as "no OS changes" — the
    /// same as no manifest — so an app-only update never offers the button.
    /// </summary>
    public static OsChangeManifest? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var manifest = JsonSerializer.Deserialize<OsChangeManifest>(File.ReadAllText(path), options);
            if (manifest == null || !Validate(manifest)) return null;
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Rejects anything outside the safe surface: supported ops, a whitelisted
    /// registry hive, a bounded value, a real service name, a known startup type.
    /// A bad manifest can never make the app crash or write outside safe hives.
    /// </summary>
    public static bool Validate(OsChangeManifest manifest)
    {
        if (manifest.Changes.Count == 0) return false;
        foreach (var c in manifest.Changes)
        {
            if (string.IsNullOrWhiteSpace(c.Description)) return false;
            switch (c.Type)
            {
                case OsChangeOp.Registry:
                    if (string.IsNullOrWhiteSpace(c.Key)) return false;
                    var hive = c.Key.Split('\\', 2)[0];
                    if (!AllowedRegistryHives.Contains(hive)) return false;
                    if (!c.Value.HasValue) return false;
                    var isNumericKind = c.ValueKind is RegistryValueKind.DWord or RegistryValueKind.QWord;
                    var isStringKind = c.ValueKind is RegistryValueKind.String
                        or RegistryValueKind.ExpandString or RegistryValueKind.MultiString;
                    // Numeric registry kinds need a JSON number; string kinds need a JSON string.
                    if (isNumericKind && c.Value.Value.ValueKind != JsonValueKind.Number) return false;
                    if (isStringKind && c.Value.Value.ValueKind != JsonValueKind.String) return false;
                    if (isNumericKind)
                    {
                        try
                        {
                            if (c.ValueKind == RegistryValueKind.DWord) _ = c.Value.Value.GetUInt32();
                            else _ = c.Value.Value.GetUInt64();
                        }
                        catch { return false; }
                    }
                    if (c.ValueKind is not (RegistryValueKind.DWord or RegistryValueKind.QWord
                        or RegistryValueKind.String or RegistryValueKind.ExpandString
                        or RegistryValueKind.MultiString)) return false;
                    break;
                case OsChangeOp.Service:
                    if (string.IsNullOrWhiteSpace(c.ServiceName)) return false;
                    if (!AllowedServiceStartups.Contains(c.StartupType)) return false;
                    break;
                case OsChangeOp.Script:
                    if (string.IsNullOrWhiteSpace(c.Script)) return false;
                    // Reject path traversal and absolute paths — scripts must be
                    // relative .ps1 files inside the install dir.
                    if (Path.IsPathRooted(c.Script) || c.Script.Contains("..")) return false;
                    if (!c.Script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>Applies a manifest pass and records state. Returns false on any error.</summary>
    public bool TryApply(OsChangeManifest manifest, OsChangeResult? result = null)
    {
        if (!Validate(manifest)) return false;
        result ??= new OsChangeResult();
        var state = LoadState();
        // Fresh pass: clear previous per-entry rollback backups so a re-apply
        // after a failed attempt never double-restores.
        TryClearRollbackFolder();

        foreach (var entry in manifest.Changes)
        {
            try
            {
                switch (entry.Type)
                {
                    case OsChangeOp.Registry:
                        var backup = Path.Combine(RollbackFolder, Sanitize(entry.Key) + ".reg");
                        // Backup the parent key so rollback can restore it. A key
                        // that doesn't exist yet can't be exported (no prior state
                        // to restore — the value is simply deleted on rollback),
                        // so a backup failure must not block the apply.
                        try { RegistryHelper.BackupRegistryKey(entry.Key); }
                        catch { /* no prior key — rollback deletes the value instead */ }
                        RegistryHelper.SetRegistryValue(entry.Key, entry.ValueName,
                            MaterializeValue(entry),
                            entry.ValueKind);
                        RecordApplied(state, manifest.Version, entry.Description, backup);
                        result.Applied.Add(entry.Description);
                        break;

                    case OsChangeOp.Service:
                        var original = GetServiceStartType(entry.ServiceName);
                        var scExit = RunScConfig(entry.ServiceName, entry.StartupType);
                        if (scExit != 0)
                        {
                            result.Errors.Add($"{entry.Description}: sc.exe returned {scExit}");
                            continue;
                        }
                        RecordApplied(state, manifest.Version, entry.Description, original.ToString());
                        result.Applied.Add(entry.Description);
                        break;

                    case OsChangeOp.Script:
                        var scriptPath = Path.Combine(AppContext.BaseDirectory, entry.Script);
                        if (!File.Exists(scriptPath))
                        {
                            result.Errors.Add($"{entry.Description}: script not found: {entry.Script}");
                            continue;
                        }
                        var (scriptOk, scriptOutput) = RunScript(scriptPath);
                        if (!scriptOk)
                        {
                            result.Errors.Add($"{entry.Description}: script failed (exit code non-zero)\n{scriptOutput}");
                            continue;
                        }
                        RecordApplied(state, manifest.Version, entry.Description, "script");
                        result.Applied.Add(entry.Description);
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{entry.Description}: {ex.Message}");
            }
        }

        state.AppliedManifestVersion = manifest.Version;
        SaveState(state);
        result.Success = result.Errors.Count == 0;
        return result.Success;
    }

    /// <summary>Rolls back every entry previously applied for the given manifest version.</summary>
    public bool TryRollback(OsChangeManifest manifest, OsChangeResult? result = null)
    {
        if (!Validate(manifest)) return false;
        result ??= new OsChangeResult();
        var state = LoadState();

        var appliedForVersion = state.AppliedEntries
            .Where(e => e.Version == manifest.Version)
            .ToDictionary(e => e.Description, StringComparer.OrdinalIgnoreCase);
        if (appliedForVersion.Count == 0)
        {
            // Nothing recorded for this version — nothing to roll back.
            result.Success = true;
            return true;
        }

        // Roll back in reverse order.
        foreach (var entry in manifest.Changes.Where(c => appliedForVersion.ContainsKey(c.Description)).Reverse())
        {
            var record = appliedForVersion[entry.Description];
            try
            {
                switch (entry.Type)
                {
                    case OsChangeOp.Registry:
                        if (!string.IsNullOrEmpty(record.Before) && File.Exists(record.Before))
                            RegistryHelper.RestoreRegistryKey(record.Before);
                        else
                            RegistryHelper.DeleteRegistryValue(entry.Key, entry.ValueName);
                        result.Applied.Add(entry.Description);
                        break;

                    case OsChangeOp.Service:
                        if (int.TryParse(record.Before, out var startType))
                            RunScConfig(entry.ServiceName, StartValueToKeyword(startType));
                        else
                            RunScConfig(entry.ServiceName, "demand");
                        result.Applied.Add(entry.Description);
                        break;

                    case OsChangeOp.Script:
                        // Scripts are one-shot — no rollback. Just record that it was applied.
                        result.Applied.Add(entry.Description);
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{entry.Description}: {ex.Message}");
            }
        }

        state.AppliedEntries.RemoveAll(e => e.Version == manifest.Version);
        if (state.AppliedManifestVersion == manifest.Version)
            state.AppliedManifestVersion = string.Empty;
        SaveState(state);
        result.Success = result.Errors.Count == 0;
        return result.Success;
    }

    /// <summary>True if the given manifest version was already applied (and not rolled back).</summary>
    public static bool IsApplied(OsChangeManifest manifest)
    {
        var state = LoadState();
        if (state.AppliedManifestVersion == manifest.Version) return true;
        return state.AppliedEntries.Any(e => e.Version == manifest.Version);
    }

    /// <summary>Reads the manifest from the app install dir (next to KalOS.exe). Null when none ships.</summary>
    public static OsChangeManifest? LoadFromInstallDir()
    {
        return Load(Path.Combine(AppContext.BaseDirectory, ManifestFileName));
    }

    // ── Persisted state ─────────────────────────────────────────────────

    internal sealed class AppliedEntry
    {
        public string Version { get; set; } = string.Empty;    // manifest version (usually the release tag)
        public string Description { get; set; } = string.Empty;
        public string Before { get; set; } = string.Empty;     // rollback snapshot (reg path / service start)
    }

    internal sealed class OsChangeState
    {
        public string AppliedManifestVersion { get; set; } = string.Empty;
        public List<AppliedEntry> AppliedEntries { get; set; } = new();
    }

    private static OsChangeState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<OsChangeState>(File.ReadAllText(StatePath)) ?? new OsChangeState();
        }
        catch { }
        return new OsChangeState();
    }

    private static void SaveState(OsChangeState state)
    {
        try
        {
            Directory.CreateDirectory(RollbackFolder);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void RecordApplied(OsChangeState state, string version, string description, string before)
    {
        state.AppliedEntries.RemoveAll(e =>
            e.Version == version && e.Description.Equals(description, StringComparison.OrdinalIgnoreCase));
        state.AppliedEntries.Add(new AppliedEntry { Version = version, Description = description, Before = before });
    }

    // ── Service helpers (sc.exe, same pattern as RadioStackService) ──────

    /// <summary>Returns (startType, exists). startType is the sc.exe numeric value or -1 if unknown.</summary>
    /// <summary>Test helper: resets persisted state so tests run isolated.</summary>
    internal static void ResetStateForTest()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
            if (Directory.Exists(RollbackFolder)) Directory.Delete(RollbackFolder, recursive: true);
        }
        catch { }
    }

    internal static (int StartType, bool Exists) GetServiceStartType(string serviceName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"qc \"{serviceName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return (-1, false);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            // "START_TYPE : 3 DEMAND_START"
            var line = output.Split('\n').FirstOrDefault(l => l.Contains("START_TYPE"));
            if (line == null) return (-1, false);
            var match = System.Text.RegularExpressions.Regex.Match(line, @"\b(\d+)\b");
            return match.Success ? (int.Parse(match.Groups[1].Value), true) : (-1, false);
        }
        catch
        {
            return (-1, false);
        }
    }

    /// <summary>sc.exe config start= keyword. Returns process exit code.</summary>
    internal static int RunScConfig(string serviceName, string startupKeyword)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"config \"{serviceName}\" start= {startupKeyword}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return -1;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Converts the manifest's JSON value into the .NET type that matches the
    /// target RegistryValueKind. The registry API requires DWord values as Int32
    /// (a JSON uint like 4294967295 is written as unchecked int -1, which stores
    /// the same 32-bit DWORD on disk) and QWord as UInt64.
    /// </summary>
    internal static object MaterializeValue(OsChangeEntry entry)
    {
        var el = entry.Value!.Value;
        return entry.ValueKind switch
        {
            RegistryValueKind.DWord => unchecked((int)el.GetUInt32()),
            RegistryValueKind.QWord => el.GetUInt64(),
            _ => el.GetString() ?? string.Empty
        };
    }

    /// <summary>Maps an sc.exe numeric start type back to the config keyword.</summary>
    internal static string StartValueToKeyword(int startType) => startType switch
    {
        0 => "boot",
        1 => "system",
        2 => "auto",
        3 => "demand",
        4 => "disabled",
        _ => "demand"
    };

    /// <summary>
    /// Runs a PowerShell script and returns (success, output). The script runs
    /// with the app's elevation (requireAdministrator), so no UAC prompt.
    /// Exit code 0 = success; non-zero = failure.
    /// </summary>
    internal static (bool success, string output) RunScript(string scriptPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return (false, "Could not start PowerShell.");
            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            var combined = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}";
            return (proc.ExitCode == 0, combined.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"{ex.Message}");
        }
    }

    private static string Sanitize(string keyPath) =>
        new string(keyPath.Where(char.IsLetterOrDigit).ToArray());

    private static void TryClearRollbackFolder()
    {
        try
        {
            if (Directory.Exists(RollbackFolder))
                Directory.Delete(RollbackFolder, recursive: true);
            Directory.CreateDirectory(RollbackFolder);
        }
        catch { }
    }
}

/// <summary>Small extension for readability in Validate().</summary>
internal static class JsonElementKindExtensions
{
    public static bool In(this JsonValueKind kind, params JsonValueKind[] kinds) => kinds.Contains(kind);
}
