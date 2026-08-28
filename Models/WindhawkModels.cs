using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KalOS.Models;

/// <summary>
/// The mod manifest (Assets/windhawk_mods.json): which mods to deploy, where
/// their source comes from, and the settings to apply. Pure data — the engine
/// that reads it lives in WindhawkManagerService.
/// </summary>
public sealed class WindhawkModManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Pinned upstream mods repo so sources are reproducible (never "main").</summary>
    [JsonPropertyName("modsRepo")]
    public WindhawkModsRepoPin ModsRepo { get; set; } = new();

    [JsonPropertyName("mods")]
    public List<WindhawkModEntry> Mods { get; set; } = new();
}

public sealed class WindhawkModsRepoPin
{
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "ramensoftware";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "windhawk-mods";

    /// <summary>Commit SHA — sources are fetched from this exact revision.</summary>
    [JsonPropertyName("commit")]
    public string Commit { get; set; } = string.Empty;

    [JsonPropertyName("rawRoot")]
    public string RawRoot { get; set; } = "https://raw.githubusercontent.com";
}

/// <summary>One mod to deploy.</summary>
public sealed class WindhawkModEntry
{
    /// <summary>Windhawk mod id, e.g. "windows-11-taskbar-styler".</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// "windhawk" = fetch the .wh.cpp source from the pinned upstream repo;
    /// "local"   = copy from <see cref="SourcePath"/>.
    /// </summary>
    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "windhawk";

    /// <summary>Path to a local .wh.cpp file when SourceType == "local".</summary>
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Optional theme setting (e.g. "Fluid 2", "Dock Lite"). Written to ModsWritable.</summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    /// <summary>Version stamped into the compiled DLL and registry (defaults to "1.0.0").</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>Arbitrary extra key/value settings written to the mod's writable settings.</summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, string> Settings { get; set; } = new();

    /// <summary>Target process(es), e.g. "explorer.exe" — mirrors a UI-installed mod's Include value.</summary>
    [JsonPropertyName("targetProcess")]
    public string TargetProcess { get; set; } = "explorer.exe";

    /// <summary>Whether the mod should be enabled after deploy (Disabled=0).</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>Structured per-mod deploy result — never a bare boolean.</summary>
public sealed class WindhawkDeployResult
{
    public WindhawkDeployResult(string modId)
    {
        ModId = modId;
    }

    public string ModId { get; }

    public bool Success { get; set; }

    /// <summary>True when the engine produced a compiled artifact for the mod after deploy.</summary>
    public bool Verified { get; set; }

    public string Detail { get; set; } = string.Empty;

    /// <summary>Internal: whether the engine was stopped for this deploy.</summary>
    internal bool EngineStopped { get; set; }

    /// <summary>Internal: whether this run wrote source/registry and needs the post-batch engine restart + verification.</summary>
    internal bool NeedsEngineRestart { get; set; }

    /// <summary>Internal: whether the mod was already registered before this deploy.</summary>
    internal bool WasRegistered { get; set; }

    public string Summary =>
        $"{ModId}: {(Success ? "OK" : "FAILED")} ({(Verified ? "verified" : "unverified")}) — {Detail}";
}

/// <summary>Windhawk application install pin (Assets/windhawk_pins.json).</summary>
public sealed class WindhawkInstallerPin
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Expected SHA-256 of the installer (uppercase hex).</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
