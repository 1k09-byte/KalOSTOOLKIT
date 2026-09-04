using System.Collections.Generic;

namespace KalOS.Models
{
    /// <summary>
    /// The user-facing buckets the installer's Tweaks page groups tweaks into.
    /// Each <see cref="TweakDef"/> belongs to exactly one group; the page shows
    /// one checkbox per group and the pipeline runs every selected group.
    /// </summary>
    public enum TweakGroup
    {
        /// <summary>Remove preinstalled / bloatware Store apps (incl. Widgets).</summary>
        Apps,
        /// <summary>Remove OneDrive (process, installer, data, tasks, nav-pane entry).</summary>
        OneDrive,
        /// <summary>Remove Microsoft Edge (appx, installer, shortcuts, associations).</summary>
        Edge,
        /// <summary>Disable optional Windows features (DISM).</summary>
        Features,
        /// <summary>Remove optional Windows capabilities (DISM).</summary>
        Capabilities,
        /// <summary>Privacy / telemetry registry policy tweaks.</summary>
        Privacy,
        /// <summary>Disable services.</summary>
        Services,
        /// <summary>Disable scheduled tasks.</summary>
        Tasks,
        /// <summary>Clear recent history / activity (MRU, recent files, search history).</summary>
        History,
        /// <summary>Clear logs, temp files, shadow copies, event logs, SRUM.</summary>
        Logs,

        /// <summary>Appearance: dark mode + transparency effects for the current user (HKCU Personalize key).</summary>
        Personalization,
    }

    /// <summary>One tweak: a human-readable name + a typed action to execute.</summary>
    public sealed record TweakDef(string Name, TweakGroup Group, TweakAction Action);

    /// <summary>Base of every executable tweak action.</summary>
    public abstract record TweakAction;

    /// <summary>Set a registry value (create the key if missing).</summary>
    public sealed record RegistrySetAction(string Key, string ValueName, TweakValueKind Kind, string Data) : TweakAction;

    /// <summary>Delete a registry value (no-op when missing).</summary>
    public sealed record RegistryValueDeleteAction(string Key, string ValueName) : TweakAction;

    /// <summary>
    /// Clear every value under a key (e.g. an MRU list); optionally recurse
    /// into subkeys. The key itself is left in place.
    /// </summary>
    public sealed record RegistryValuesClearAction(string Key, bool Recursive) : TweakAction;

    /// <summary>Create a registry key (no-op when present).</summary>
    public sealed record RegistryKeyCreateAction(string Key) : TweakAction;

    /// <summary>Delete a registry key tree (no-op when missing).</summary>
    public sealed record RegistryKeyDeleteAction(string Key) : TweakAction;

    /// <summary>
    /// Delete a file system path. <see cref="ContentsOnly"/> mirrors the
    /// scripts' "Clear directory contents" vs "Delete directory/file".
    /// The final path segment may contain * / ? wildcards.
    /// </summary>
    public sealed record DeletePathAction(string Path, bool ContentsOnly) : TweakAction;

    /// <summary>
    /// Uninstall a Store app (Get-AppxPackage | Remove-AppxPackage) and mark it
    /// deprovisioned so Windows Update does not reinstall it.
    /// </summary>
    public sealed record AppxRemoveAction(string PackageName, string? DeprovisionKey) : TweakAction;

    /// <summary>Disable an optional Windows feature via DISM.</summary>
    public sealed record DisableFeatureAction(string FeatureName) : TweakAction;

    /// <summary>Remove an optional Windows capability (name may end with *).</summary>
    public sealed record RemoveCapabilityAction(string CapabilityName) : TweakAction;

    /// <summary>Disable a service (Start=4) and stop it if running.</summary>
    public sealed record DisableServiceAction(string ServiceName) : TweakAction;

    /// <summary>Disable scheduled task(s) matching a folder path + name pattern.</summary>
    public sealed record DisableTaskAction(string TaskPath, string TaskNamePattern) : TweakAction;

    /// <summary>Clear every Windows event log.</summary>
    public sealed record ClearEventLogsAction() : TweakAction;

    /// <summary>Run a built-in Windows tool (dism, vssadmin, slmgr, …).</summary>
    public sealed record RunToolAction(string FileName, string Arguments, string Description) : TweakAction;

    /// <summary>The composite OneDrive removal (process, installer, data, tasks, nav entry).</summary>
    public sealed record RemoveOneDriveAction() : TweakAction;

    /// <summary>The composite Microsoft Edge removal (appx, installer, shortcuts, associations).</summary>
    public sealed record RemoveEdgeAction() : TweakAction;

    /// <summary>Registry value kinds the catalog uses.</summary>
    public enum TweakValueKind
    {
        Dword,
        String,
        MultiString,
    }
}
