using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KalOS.Models;
using Microsoft.Win32;

namespace KalOS.Services;

/// <summary>
/// Installs Windhawk and deploys/backs up/restores a curated mod set — all in
/// native C# (no PowerShell): registry via Microsoft.Win32, files via System.IO,
/// the installer via Process (the only "shelling out" is running the official
/// NSIS installer, which C# does directly with no script needed).
///
/// Verified ground truth (Windhawk 1.7.3 on this machine):
///   • Installer is NSIS, NOT Inno Setup — silent flags are /S /STANDARD
///     (uninstall.exe embeds "Nullsoft Install System v3.11").
///   • Mods are NOT precompiled binaries. Each mod is a .wh.cpp source file the
///     engine compiles the first time it loads.
///   • There is no windhawk-cli.exe and no public mod-install verb (issue #154
///     is an open feature request) — deployment registers mods with the engine
///     and lets it compile from source.
///   • Installed-mod state lives under HKLM\SOFTWARE\Windhawk\Engine\Mods\&lt;id&gt;
///     (schema captured from a real UI-installed mod: Disabled DWORD, Include,
///     Exclude, Architecture, Version, LibraryFileName ...), writable settings
///     (Theme) under ...\Engine\ModsWritable\&lt;id&gt;, cached sources under
///     %ProgramData%\Windhawk\ModsSource, and compiled artifacts under
///     %ProgramData%\Windhawk\Engine\Mods\&lt;arch&gt;.
///   • Documented engine control: windhawk.exe -exit [-wait], -restart [-tray-only].
///
/// The deploy path writes source + registry and lets the engine compile — every
/// deploy is VERIFIED against a fresh compiled artifact before it is reported
/// as success, so an unsupported registry quirk surfaces as a clear per-mod
/// failure instead of a silent no-op.
/// </summary>
public sealed class WindhawkManagerService
{
    // ── Layout (verified on 1.7.3) ─────────────────────────────────────────
    private const string InstallDirName = "Windhawk";

    /// <summary>
    /// Settle window after a cold engine start: long enough for the engine to
    /// finish booting (process spawn, mods-tree scan, DLL load) before we
    /// verify mods or fire the disable→enable reload kick. Kicking earlier
    /// means the engine misses the toggle — the disable/re-enable bug.
    /// </summary>
    private const int EngineSettleDelayMs = 5000;
    private const string EngineModsRegistryPath = @"SOFTWARE\Windhawk\Engine\Mods";
    private const string ModsWritableRegistryPath = @"SOFTWARE\Windhawk\Engine\ModsWritable";
    private const string WindhawkRootRegistryPath = @"SOFTWARE\Windhawk";

    // NSIS silent install. /STANDARD = default (non-portable) layout; portable
    // mode would be "/S /PORTABLE /D=<path>".
    private const string InstallerSilentArgs = "/S /STANDARD";

    // Baked engine registry schema for a mod entry. Mirrored from a real
    // UI-installed mod (always-on-top) on this machine. When another mod is
    // already registered, its exact value names/kinds are mirrored instead.
    private static readonly (string Name, RegistryValueKind Kind)[] BakedModSchema =
    {
        ("Disabled", RegistryValueKind.DWord),
        ("Version", RegistryValueKind.String),
        ("Include", RegistryValueKind.String),
        ("Exclude", RegistryValueKind.String),
        ("Architecture", RegistryValueKind.String),
        ("LoggingEnabled", RegistryValueKind.DWord),
        ("DebugLoggingEnabled", RegistryValueKind.DWord),
        ("SettingsChangeTime", RegistryValueKind.DWord),
        ("LibraryFileName", RegistryValueKind.String),
    };

    private const string ModSourceMarkerBegin = "// ==WindhawkMod==";
    private const string ModSourceMarkerEnd = "// ==/WindhawkMod==";

    private static readonly HttpClient DownloadClient = CreateHttpClient();

    private readonly LoggingService _log;

    public WindhawkManagerService(LoggingService log)
    {
        _log = log;
    }

    // ═══════════════════════════ Part 1: Windhawk install ═══════════════════════════

    /// <summary>True when windhawk.exe exists and the HKLM root registry key is present.</summary>
    public bool IsInstalled()
    {
        bool exeOk = File.Exists(GetExecutablePath());
        bool regOk = RootRegistryKeyExists();
        _log.Info($"Windhawk installed check: exe={exeOk}, registry={regOk}");
        return exeOk || regOk;
    }

    public string GetInstallDirectory()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new[]
        {
            Path.Combine(programFiles, InstallDirName),
            Path.Combine(programFilesX86, InstallDirName),
            Path.Combine(localAppData, "Programs", InstallDirName),
        };

        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "windhawk.exe")))
               ?? candidates.FirstOrDefault(path => Directory.Exists(path))
               ?? candidates[0];
    }

    public string GetExecutablePath() => Path.Combine(GetInstallDirectory(), "windhawk.exe");

    public string GetModsSourceDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        InstallDirName, "ModsSource");

    public string GetEngineModsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        InstallDirName, "Engine", "Mods");

    private bool RootRegistryKeyExists()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(WindhawkRootRegistryPath);
                if (key != null) return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Windhawk registry check failed ({view}): {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// Downloads the pinned NSIS installer, verifies its SHA-256, runs it
    /// silently with /S /STANDARD, and verifies the install. Throws on failure
    /// so the caller surfaces a clear error.
    /// </summary>
    public async Task InstallWindhawkAsync(
        IProgress<double>? progress = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var pin = LoadInstallerPin();
        if (string.IsNullOrWhiteSpace(pin.Url))
        {
            throw new InvalidOperationException("windhawk_pins.json has no installer URL.");
        }

        string installerPath = Path.Combine(Path.GetTempPath(), $"windhawk_setup_{Guid.NewGuid():N}.exe");
        try
        {
            status?.Report("Downloading the official Windhawk installer...");
            _log.Info($"Downloading Windhawk {pin.Version} from {pin.Url}");
            using (var response = await DownloadClient.GetAsync(pin.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(installerPath);
                await source.CopyToAsync(target, cancellationToken);
            }

            var info = new FileInfo(installerPath);
            if (info.Length < 100_000)
            {
                throw new InvalidOperationException("The downloaded Windhawk installer is unexpectedly small.");
            }

            status?.Report("Verifying the installer's hash...");
            string actual = await Task.Run(() => ComputeSha256(installerPath), cancellationToken);
            if (!string.IsNullOrWhiteSpace(pin.Sha256) &&
                !string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Installer hash mismatch — expected {pin.Sha256}, got {actual}. " +
                    "Refresh windhawk_pins.json deliberately if the pinned version changed upstream.");
            }
            _log.Info("Installer SHA-256 matches the pin.");

            progress?.Report(60);
            status?.Report("Running the silent installer (NSIS /S /STANDARD)...");
            int exitCode = await RunInstallerAsync(installerPath, cancellationToken);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"The Windhawk installer exited with code {exitCode}.");
            }

            // The installer can return 0 while the install is still settling —
            // poll for the real evidence (exe + registry key).
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsInstalled())
                {
                    progress?.Report(100);
                    status?.Report($"Windhawk {pin.Version} installed.");
                    return;
                }
                await Task.Delay(1000, cancellationToken);
            }

            throw new InvalidOperationException(
                "The Windhawk installer finished, but windhawk.exe was not found afterwards.");
        }
        finally
        {
            try { File.Delete(installerPath); } catch { }
        }
    }

    private async Task<int> RunInstallerAsync(string installerPath, CancellationToken cancellationToken)
    {
        // The app runs requireAdministrator (app.manifest), so no UAC relaunch
        // is needed — the installer inherits elevation.
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = InstallerSilentArgs,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath(),
            UseShellExecute = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windhawk installer.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("The Windhawk installer timed out after five minutes.");
        }
    }

    // ═══════════════════════════ Part 2: mod deploy ═══════════════════════════

    /// <summary>
    /// Deploys one mod: source → ModsSource, registry entry → Engine\Mods,
    /// theme → ModsWritable. Idempotent — an already-deployed (registered and
    /// enabled) mod is skipped. When called as part of a batch, pass
    /// <paramref name="engineStopped"/> so the batch can stop the engine once
    /// up front and restart it once at the end; standalone calls stop and
    /// restart around this mod and verify it themselves.
    /// </summary>
    public async Task<WindhawkDeployResult> DeployModAsync(
        WindhawkModEntry entry,
        WindhawkModManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        bool engineStopped = false)
    {
        var result = new WindhawkDeployResult(entry.Id)
        {
            WasRegistered = ModRegistryEntryExists(entry.Id),
        };

        // The engine keeps the mods folder open while running, so the source
        // must be written with the engine stopped.

        try
        {
            // ── Idempotency: registered, enabled, compiled, AND themed? ──
            // A registry entry alone is NOT enough — the mod may still need its
            // library or its theme settings, so the short-circuit requires all
            // three to be in place.
            if (result.WasRegistered && IsModReady(entry.Id) && HasRequiredSettings(entry))
            {
                result.Success = true;
                result.Verified = true;
                result.Detail = "Already deployed (registered, enabled, compiled, and themed).";
                _log.Info(result.Detail + " " + entry.Id);
                return result;
            }

            progress?.Report(15);
            cancellationToken.ThrowIfCancellationRequested();

            // The engine holds the Mods folder open; stop it before writing.
            if (!engineStopped)
            {
                await StopEngineAsync(cancellationToken);
                result.EngineStopped = true;
            }

            // ── Source: fetch from the pinned repo commit, or copy a local file. ──
            progress?.Report(25);
            string source = await GetModSourceAsync(entry, manifest, cancellationToken);
            ValidateModSource(entry.Id, source);

            string sourceDir = GetModsSourceDirectory();
            Directory.CreateDirectory(sourceDir);
            string sourcePath = Path.Combine(sourceDir, $"{entry.Id}.wh.cpp");
            await File.WriteAllTextAsync(sourcePath, source, cancellationToken);
            _log.Info($"Wrote mod source: {sourcePath}");

            // ── Fetch the compiled library the same way the Windhawk UI does:
            // the UI downloads a precompiled DLL from the mods server by
            // default (the engine itself never compiles — it only loads DLLs).
            // The DLL lands in Engine\Mods\<arch>\<id>_<version>_<random>.dll.
            progress?.Report(55);
            string version = await ResolveModVersionAsync(entry.Id, entry.Version, cancellationToken);
            string? dllName = await DownloadPrecompiledModAsync(entry.Id, version, cancellationToken);
            if (string.IsNullOrWhiteSpace(dllName))
            {
                result.Detail = "Deploy failed: no precompiled library was available for this mod/version.";
                _log.Error($"{entry.Id}: {result.Detail}");
                return result;
            }
            _log.Info($"Downloaded compiled library for {entry.Id} (v{version}) → {dllName}");

            // ── Registry entry: Disabled/Include/Version/LibraryFileName, the
            // last being the engine's pointer to the compiled DLL.
            progress?.Report(75);
            WriteModRegistryEntry(entry, version, dllName);

            // ── Writable settings (Theme + extras) + settings-change kick. ──
            WriteModSettings(entry);

            // The engine must be restarted for the new/updated mod to load;
            // the batch loop does one restart after ALL mods are written.
            progress?.Report(90);
            result.NeedsEngineRestart = true;
            result.Detail = "Deployed (compiled + registered); awaiting engine reload.";
            _log.Info($"Deployed {entry.Id} (compiled + registered).");
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Detail = "Cancelled.";
            return result;
        }
        catch (Exception ex)
        {
            _log.Error($"Deploy of {entry.Id} failed: {ex.Message}");
            result.Detail = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Deploys every selected mod with the engine stopped ONCE and restarted
    /// ONCE at the end, then verifies each deployed mod. Per-mod restarts were
    /// observed to interrupt the engine's compile queue and make it drop mods,
    /// so the whole batch shares a single engine lifecycle.
    /// </summary>
    public async Task<List<WindhawkDeployResult>> DeployModsAsync(
        IReadOnlyList<WindhawkModEntry> mods,
        WindhawkModManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WindhawkDeployResult>();
        bool engineStopped = false;
        int index = 0;

        foreach (var entry in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modProgress = new Progress<double>(value =>
                progress?.Report((index * 100d + value) / mods.Count));

            var result = await DeployModAsync(entry, manifest, modProgress, cancellationToken, engineStopped);
            engineStopped |= result.EngineStopped;
            results.Add(result);
            index++;
        }

        // ONE clean restart so every deployed mod loads together.
        if (results.Any(r => r.NeedsEngineRestart))
        {
            await StartEngineAsync(cancellationToken);

            // Give the engine time to finish booting (spawn workers, scan the
            // mods tree, compile/load DLLs) BEFORE we inspect or toggle the
            // registry. Without this settle window, our own deploy writes
            // (LibraryFileName + DLL) already satisfy IsModReady, verification
            // returns instantly, and the disable→enable kick below fires while
            // the engine is still initializing — it misses the toggle and the
            // mods end up loaded-but-not-injected (the disable/re-enable bug).
            await Task.Delay(EngineSettleDelayMs, cancellationToken);

            // The engine accepts a mod by keeping its registry entry (and
            // deletes entries for mods it cannot load), so survival of the
            // entry in an enabled state is the acceptance signal.
            foreach (var result in results.Where(r => r.NeedsEngineRestart))
            {
                bool verified = await WaitForModAcceptedAsync(result.ModId, cancellationToken);
                result.Verified = verified;
                result.Success = verified;
                result.Detail = verified
                    ? "Deployed (source + registry); the engine accepted and enabled it."
                    : "Deployed, but the engine did not load it within 3 minutes — check the mod in the Windhawk UI.";
                _log.Info(result.Summary);
            }

            // After a cold engine start, freshly registered mods are often
            // loaded-but-not-injected into processes that were already running.
            // Kick a disable→enable cycle so the engine re-injects them — this
            // is what users previously had to do by hand in the Windhawk UI.
            var kickedIds = results
                .Where(r => r.NeedsEngineRestart && r.Verified)
                .Select(r => r.ModId)
                .ToList();
            if (kickedIds.Count > 0)
            {
                await KickModsReloadAsync(kickedIds, cancellationToken);
            }

            // The disable→enable kick is not always enough for mods that live
            // inside Explorer: the old shell keeps running the mod — often with
            // stale or empty theme settings — until the mod is fully reloaded
            // in it. Restarting Explorer makes the freshly written registry
            // (theme settings included) take effect deterministically: the
            // running engine hooks the NEW shell as it starts and loads the
            // mods from current state. The taskbar flickers briefly while the
            // shell comes back.
            if (kickedIds.Count > 0 && TargetsExplorer(mods, kickedIds))
            {
                await RestartExplorerAsync(cancellationToken);
            }
        }

        return results;
    }

    /// <summary>
    /// Bumps a deployed mod to the latest published version: downloads the new
    /// precompiled library (the same endpoint the UI uses), updates the
    /// registry entry (Version + LibraryFileName), restarts the engine, and
    /// verifies. Returns a result whose Detail says "already up to date" when
    /// there is nothing newer to install.
    /// </summary>
    public async Task<WindhawkDeployResult> UpdateModAsync(
        WindhawkModEntry entry,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new WindhawkDeployResult(entry.Id);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            string latest = await GetLatestModVersionAsync(entry.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(latest))
            {
                result.Detail = "Update check failed — could not reach mods.windhawk.net.";
                _log.Error($"{entry.Id}: {result.Detail}");
                return result;
            }

            if (CompareVersions(latest, entry.Version) <= 0)
            {
                result.Success = true;
                result.Verified = true;
                result.Detail = $"Already up to date (v{entry.Version}).";
                return result;
            }

            progress?.Report(20);
            await StopEngineAsync(cancellationToken);
            result.EngineStopped = true;

            // Drop the previous compiled library so only the new one remains.
            DeleteModLibraries(entry.Id);

            progress?.Report(45);
            string? dllName = await DownloadPrecompiledModAsync(entry.Id, latest, cancellationToken);
            if (string.IsNullOrWhiteSpace(dllName))
            {
                result.Detail = $"Update failed: no precompiled library available for v{latest}.";
                _log.Error($"{entry.Id}: {result.Detail}");
                return result;
            }

            progress?.Report(70);
            WriteModRegistryEntry(entry, latest, dllName);
            WriteModSettings(entry);

            progress?.Report(85);
            await StartEngineAsync(cancellationToken);

            // Same settle window as the batch deploy: let the engine finish
            // booting before verifying / kicking, or it misses the toggle.
            await Task.Delay(EngineSettleDelayMs, cancellationToken);

            bool verified = await WaitForModAcceptedAsync(entry.Id, cancellationToken);
            if (verified)
            {
                // Same cold-start injection gap as a batch deploy: kick a
                // disable→enable cycle so the updated mod actually re-injects.
                await KickModsReloadAsync(new[] { entry.Id }, cancellationToken);

                // Explorer-targeted mods additionally need a fresh shell: the
                // disable→enable cycle alone can leave the old Explorer running
                // the updated library half-applied.
                if (TargetsExplorer(new[] { entry }, new[] { entry.Id }))
                {
                    await RestartExplorerAsync(cancellationToken);
                }
            }
            result.Verified = verified;
            result.Success = verified;
            result.Detail = verified
                ? $"Updated to v{latest} and the engine loaded it."
                : $"Updated to v{latest}, but the engine did not load it within 3 minutes.";
            _log.Info(result.Summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Detail = "Cancelled.";
            return result;
        }
        catch (Exception ex)
        {
            _log.Error($"Update of {entry.Id} failed: {ex.Message}");
            result.Detail = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Removes a mod completely: registry entry (incl. its Settings subkey),
    /// ModsWritable leftovers, cached source, and compiled libraries — with the
    /// engine stopped so no DLL is locked. Idempotent: a mod that isn't
    /// registered and has no cached source is a no-op success.
    /// </summary>
    public async Task<WindhawkDeployResult> UninstallModAsync(string modId, CancellationToken cancellationToken = default)
    {
        var result = new WindhawkDeployResult(modId);
        try
        {
            if (!ModRegistryEntryExists(modId) && !HasSourceFile(modId))
            {
                result.Success = true;
                result.Verified = true;
                result.Detail = "Not installed — nothing to remove.";
                return result;
            }

            await StopEngineAsync(cancellationToken);

            // Registry tree (incl. Settings subkey) + stale ModsWritable entry.
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                if (baseKey.OpenSubKey($@"{EngineModsRegistryPath}\{modId}") != null)
                {
                    baseKey.DeleteSubKeyTree($@"{EngineModsRegistryPath}\{modId}", throwOnMissingSubKey: false);
                }
                if (baseKey.OpenSubKey($@"{ModsWritableRegistryPath}\{modId}") != null)
                {
                    baseKey.DeleteSubKeyTree($@"{ModsWritableRegistryPath}\{modId}", throwOnMissingSubKey: false);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not delete registry entries for {modId}: {ex.Message}");
            }

            // Cached source.
            try
            {
                string sourcePath = Path.Combine(GetModsSourceDirectory(), $"{modId}.wh.cpp");
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not delete cached source for {modId}: {ex.Message}");
            }

            // Compiled libraries: any &lt;mod-id&gt;_*.dll under Engine\Mods (any arch).
            DeleteModLibraries(modId);

            await StartEngineAsync(cancellationToken);

            result.Success = !ModRegistryEntryExists(modId);
            result.Verified = result.Success;
            result.Detail = result.Success
                ? "Uninstalled."
                : "Uninstall incomplete — the mod is still registered.";
            _log.Info($"{modId}: {result.Detail}");
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Detail = "Cancelled.";
            return result;
        }
        catch (Exception ex)
        {
            _log.Error($"Uninstall of {modId} failed: {ex.Message}");
            result.Detail = ex.Message;
            return result;
        }
    }

    private bool HasSourceFile(string modId)
    {
        try
        {
            return File.Exists(Path.Combine(GetModsSourceDirectory(), $"{modId}.wh.cpp"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes every &lt;mod-id&gt;_*.dll under Engine\Mods (any arch).</summary>
    private void DeleteModLibraries(string modId)
    {
        try
        {
            string root = GetEngineModsDirectory();
            if (!Directory.Exists(root)) return;

            foreach (string dll in Directory.EnumerateFiles(root, $"{modId}_*.dll", SearchOption.AllDirectories).ToList())
            {
                File.Delete(dll);
                _log.Info($"Deleted compiled library: {dll}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not delete compiled libraries for {modId}: {ex.Message}");
        }
    }

    private async Task<string> GetModSourceAsync(
        WindhawkModEntry entry, WindhawkModManifest manifest, CancellationToken cancellationToken)
    {
        if (string.Equals(entry.SourceType, "local", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(entry.SourcePath) || !File.Exists(entry.SourcePath))
            {
                throw new InvalidOperationException($"Local source not found: '{entry.SourcePath}'.");
            }
            return await File.ReadAllTextAsync(entry.SourcePath, cancellationToken);
        }

        // "windhawk": fetch from the pinned repo commit — never from main.
        if (string.IsNullOrWhiteSpace(manifest.ModsRepo.Commit))
        {
            throw new InvalidOperationException("The mod manifest has no pinned repo commit — refusing to follow main.");
        }

        string url = $"{manifest.ModsRepo.RawRoot}/{manifest.ModsRepo.Owner}/{manifest.ModsRepo.Name}/{manifest.ModsRepo.Commit}/mods/{entry.Id}.wh.cpp";
        _log.Info($"Fetching mod source: {url}");
        using var response = await DownloadClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to download mod source (HTTP {(int)response.StatusCode}).");
        }
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static void ValidateModSource(string modId, string source)
    {
        if (!source.Contains(ModSourceMarkerBegin, StringComparison.Ordinal) ||
            !source.Contains(ModSourceMarkerEnd, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Downloaded source for '{modId}' is not a valid Windhawk mod.");
        }
    }

    /// <summary>
    /// Writes HKLM\SOFTWARE\Windhawk\Engine\Mods\&lt;id&gt;, mirroring the value
    /// names/kinds of an already-registered mod when one exists (never guessed
    /// blind), else the baked captured schema. <paramref name="dllName"/> is the
    /// compiled library the engine should load (its LibraryFileName pointer).
    /// </summary>
    private void WriteModRegistryEntry(WindhawkModEntry entry, string version, string dllName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var modKey = baseKey.CreateSubKey($@"{EngineModsRegistryPath}\{entry.Id}", writable: true)
            ?? throw new InvalidOperationException($"Could not create {EngineModsRegistryPath}\\{entry.Id}.");

        var schema = TryGetReferenceSchema(entry.Id) ?? BakedModSchema;

        foreach (var (name, kind) in schema)
        {
            if (modKey.GetValue(name) != null) continue; // preserve pre-existing values
            SetDefaultValue(modKey, name, kind);
        }

        // Values that reflect "installed + enabled + current". LibraryFileName
        // is the engine's pointer to the compiled DLL — the whole deploy hinges
        // on it pointing at a file that actually exists.
        modKey.SetValue("Disabled", entry.Enabled ? 0 : 1, RegistryValueKind.DWord);
        modKey.SetValue("Include", string.IsNullOrWhiteSpace(entry.TargetProcess) ? "" : entry.TargetProcess, RegistryValueKind.String);
        modKey.SetValue("Exclude", string.Empty, RegistryValueKind.String);
        modKey.SetValue("Architecture", string.Empty, RegistryValueKind.String);
        modKey.SetValue("Version", version, RegistryValueKind.String);
        modKey.SetValue("LibraryFileName", dllName, RegistryValueKind.String);
        modKey.SetValue("SettingsChangeTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RegistryValueKind.DWord);

        _log.Info($"Registered {entry.Id} under {EngineModsRegistryPath}\\{entry.Id} " +
                  (schema == BakedModSchema ? "(baked schema)" : "(mirrored from a reference mod)"));
    }

    // ── Precompiled library download (the UI's default mechanism) ──

    /// <summary>
    /// Downloads the precompiled mod DLL from Windhawk's mods server — the same
    /// endpoint the Windhawk UI's install flow uses — into
    /// Engine\Mods\&lt;arch&gt;\&lt;id&gt;_&lt;version&gt;_&lt;random&gt;.dll, and returns the
    /// produced file name (or null on failure).
    /// </summary>
    private async Task<string?> DownloadPrecompiledModAsync(
        string modId, string version, CancellationToken cancellationToken)
    {
        string archFolder = GetArchitectureFolder();
        string url = $"https://mods.windhawk.net/mods/{modId}/{version}_{archFolder}.dll";

        string outputDir = Path.Combine(GetEngineModsDirectory(), archFolder);
        Directory.CreateDirectory(outputDir);
        string dllName = $"{modId}_{version}_{Random.Shared.Next(100000, 999999)}.dll";
        string outputPath = Path.Combine(outputDir, dllName);

        try
        {
            using var response = await DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"Precompiled mod not available (HTTP {(int)response.StatusCode}): {url}");
                return null;
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(outputPath);
            await source.CopyToAsync(target, cancellationToken);

            if (new FileInfo(outputPath).Length < 10_000)
            {
                File.Delete(outputPath);
                _log.Warn($"Precompiled mod download suspiciously small: {url}");
                return null;
            }

            _log.Info($"Downloaded precompiled mod: {url} → {outputPath}");
            return dllName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Precompiled mod download failed for {modId}: {ex.Message}");
            try { File.Delete(outputPath); } catch { }
            return null;
        }
    }

    /// <summary>Returns the pinned version, or the latest from versions.json when not pinned.</summary>
    private async Task<string> ResolveModVersionAsync(
        string modId, string pinned, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(pinned)) return pinned;

        string latest = await GetLatestModVersionAsync(modId, cancellationToken);
        return string.IsNullOrWhiteSpace(latest) ? "1.0.0" : latest;
    }

    /// <summary>Latest published version of a mod from mods.windhawk.net, or empty on failure.</summary>
    public async Task<string> GetLatestModVersionAsync(string modId, CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"https://mods.windhawk.net/mods/{modId}/versions.json";
            using var response = await DownloadClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return string.Empty;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            string? latest = null;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("version", out var v))
                {
                    latest = v.GetString();
                }
            }
            return latest ?? string.Empty;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not resolve the latest version of {modId}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Numeric component-wise version compare — "1.9.2" &lt; "1.10".</summary>
    internal static int CompareVersions(string a, string b)
    {
        string[] pa = a.Split('.');
        string[] pb = b.Split('.');
        int max = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < max; i++)
        {
            int x = i < pa.Length && int.TryParse(pa[i], out int n) ? n : 0;
            int y = i < pb.Length && int.TryParse(pb[i], out int m) ? m : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private string GetArchitectureFolder() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "32",
            _ => "64",
        };

    /// <summary>
    /// Writes Theme (and any extra settings) to the mod's Settings subkey
    /// (Engine\Mods\&lt;id&gt;\Settings — where the Windhawk UI stores them) and
    /// bumps SettingsChangeTime so the engine reloads them. The setting VALUE
    /// NAME must match the mod's Wh_GetStringSetting key exactly — these mods
    /// read lowercase "theme" (verified in the mod sources), so the Theme
    /// manifest property is written under that name. The earlier ModsWritable
    /// path and the wrongly-cased "Theme" value were misreads and are cleaned
    /// up.
    /// </summary>
    private void WriteModSettings(WindhawkModEntry entry)
    {
        // The theme setting key as the mods read it: Wh_GetStringSetting(L"theme").
        const string themeSettingName = "theme";

        bool anySetting = !string.IsNullOrWhiteSpace(entry.Theme) || entry.Settings.Count > 0;
        if (!anySetting) return;

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        // Correct location: a Settings subkey under the mod's Engine\Mods entry.
        using (var settingsKey = baseKey.CreateSubKey($@"{EngineModsRegistryPath}\{entry.Id}\Settings", writable: true))
        {
            if (settingsKey == null) throw new InvalidOperationException($"Could not create {EngineModsRegistryPath}\\{entry.Id}\\Settings.");
            if (!string.IsNullOrWhiteSpace(entry.Theme))
            {
                settingsKey.SetValue(themeSettingName, entry.Theme, RegistryValueKind.String);
            }
            foreach (var (name, value) in entry.Settings)
            {
                settingsKey.SetValue(name, value, RegistryValueKind.String);
            }
        }

        using (var modKey = baseKey.CreateSubKey($@"{EngineModsRegistryPath}\{entry.Id}", writable: true))
        {
            modKey?.SetValue("SettingsChangeTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RegistryValueKind.DWord);
        }

        // Remove the stale ModsWritable entry written by the earlier (wrong)
        // settings path — the engine does not read settings from there.
        try
        {
            using var baseKey2 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (baseKey2.OpenSubKey($@"{ModsWritableRegistryPath}\{entry.Id}") != null)
            {
                baseKey2.DeleteSubKeyTree($@"{ModsWritableRegistryPath}\{entry.Id}", throwOnMissingSubKey: false);
                _log.Info($"Removed stale ModsWritable entry for {entry.Id}.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not clean stale ModsWritable entry for {entry.Id}: {ex.Message}");
        }

        _log.Info($"Wrote settings for {entry.Id}" + (string.IsNullOrWhiteSpace(entry.Theme) ? "" : $" (Theme='{entry.Theme}')"));
    }

    /// <summary>True when the mod's registry settings match the manifest (Theme + extras).</summary>
    public bool HasRequiredSettings(WindhawkModEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Theme) && entry.Settings.Count == 0) return true;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var settingsKey = baseKey.OpenSubKey($@"{EngineModsRegistryPath}\{entry.Id}\Settings");
            if (settingsKey == null) return false;

            if (!string.IsNullOrWhiteSpace(entry.Theme) &&
                !string.Equals(settingsKey.GetValue("theme")?.ToString(), entry.Theme, StringComparison.Ordinal))
            {
                return false;
            }
            foreach (var (name, value) in entry.Settings)
            {
                if (!string.Equals(settingsKey.GetValue(name)?.ToString(), value, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetDefaultValue(RegistryKey key, string name, RegistryValueKind kind)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord: key.SetValue(name, 0, RegistryValueKind.DWord); break;
            case RegistryValueKind.QWord: key.SetValue(name, 0L, RegistryValueKind.QWord); break;
            case RegistryValueKind.Binary: key.SetValue(name, Array.Empty<byte>(), RegistryValueKind.Binary); break;
            case RegistryValueKind.MultiString: key.SetValue(name, Array.Empty<string>(), RegistryValueKind.MultiString); break;
            default: key.SetValue(name, string.Empty, kind); break;
        }
    }

    /// <summary>Mirrors the value names/kinds of the first other registered mod, or null.</summary>
    private (string Name, RegistryValueKind Kind)[]? TryGetReferenceSchema(string exceptModId)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var modsKey = baseKey.OpenSubKey(EngineModsRegistryPath);
            if (modsKey == null) return null;

            foreach (string name in modsKey.GetSubKeyNames())
            {
                if (name.Equals(exceptModId, StringComparison.OrdinalIgnoreCase)) continue;

                using var key = modsKey.OpenSubKey(name);
                if (key == null) continue;

                var schema = new List<(string, RegistryValueKind)>();
                foreach (string valueName in key.GetValueNames())
                {
                    try { schema.Add((valueName, key.GetValueKind(valueName))); }
                    catch { }
                }
                if (schema.Count > 0)
                {
                    _log.Info($"Mirroring registry schema from reference mod '{name}' for '{exceptModId}'.");
                    return schema.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read a reference mod schema: {ex.Message}");
        }
        return null;
    }

    // ═══════════════════════════ Part 3: backup / restore ═══════════════════════════

    /// <summary>
    /// Snapshot: zips the mod sources, the Windhawk registry tree (as a .reg
    /// export), and a metadata file into one .whbackup file (a zip). The
    /// engine's compiled DLL cache is intentionally NOT included — it is
    /// rebuilt from source on restore, so stale artifacts can never be planted.
    /// </summary>
    public async Task<string> BackupConfigAsync(
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string regText = ExportRegistryToReg(WindhawkRootRegistryPath);
        string modsSourceDir = GetModsSourceDirectory();
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "Assets", "windhawk_mods.json");

        var meta = JsonSerializer.Serialize(new
        {
            format = "whbackup-v1",
            createdAt = DateTimeOffset.UtcNow,
            windhawkInstalled = IsInstalled(),
            modCount = Directory.Exists(modsSourceDir) ? Directory.EnumerateFiles(modsSourceDir, "*.wh.cpp").Count() : 0,
        }, new JsonSerializerOptions { WriteIndented = true });

        string workDir = Path.Combine(Path.GetTempPath(), $"whbackup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            string regPath = Path.Combine(workDir, "windhawk-registry.reg");
            await File.WriteAllTextAsync(regPath, regText, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(workDir, "meta.json"), meta, cancellationToken);
            if (File.Exists(manifestPath))
            {
                File.Copy(manifestPath, Path.Combine(workDir, "windhawk_mods.json"), overwrite: true);
            }

            string sourceZipDir = Path.Combine(workDir, "ModsSource");
            if (Directory.Exists(modsSourceDir))
            {
                Directory.CreateDirectory(sourceZipDir);
                var files = Directory.EnumerateFiles(modsSourceDir, "*.wh.cpp").ToList();
                for (int i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(files[i], Path.Combine(sourceZipDir, Path.GetFileName(files[i])), overwrite: true);
                    progress?.Report((i + 1) * 50d / Math.Max(files.Count, 1));
                }
            }

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            ZipFile.CreateFromDirectory(workDir, destinationPath);
            _log.Info($"Backup written: {destinationPath}");
            progress?.Report(100);
            return destinationPath;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Restore: ensures Windhawk is installed, stops the engine, restores the
    /// mod sources and the HKLM\SOFTWARE\Windhawk registry tree from the backup,
    /// then restarts the engine so it recompiles and picks everything up.
    /// </summary>
    public async Task RestoreConfigAsync(
        string backupPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup file not found.", backupPath);
        }
        if (!IsInstalled())
        {
            throw new InvalidOperationException("Windhawk is not installed — install it before restoring a backup.");
        }

        string workDir = Path.Combine(Path.GetTempPath(), $"whrestore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            progress?.Report(5);
            ZipFile.ExtractToDirectory(backupPath, workDir);

            // Sanity check: the backup is one of ours.
            if (!File.Exists(Path.Combine(workDir, "windhawk-registry.reg")))
            {
                throw new InvalidOperationException("Not a valid Windhawk backup (missing windhawk-registry.reg).");
            }

            // Stop the engine so it is not watching the folders we replace.
            await StopEngineAsync(cancellationToken);
            progress?.Report(30);

            // Restore sources.
            string sourceZipDir = Path.Combine(workDir, "ModsSource");
            if (Directory.Exists(sourceZipDir))
            {
                string sourceDir = GetModsSourceDirectory();
                Directory.CreateDirectory(sourceDir);
                var files = Directory.EnumerateFiles(sourceZipDir, "*.wh.cpp").ToList();
                for (int i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(files[i], Path.Combine(sourceDir, Path.GetFileName(files[i])), overwrite: true);
                    progress?.Report(30 + (i + 1) * 30d / Math.Max(files.Count, 1));
                }
            }

            // Restore registry: replace the whole Windhawk tree with the snapshot.
            string regText = await File.ReadAllTextAsync(Path.Combine(workDir, "windhawk-registry.reg"), cancellationToken);
            ApplyRegTextToRegistry(regText);

            progress?.Report(80);

            // Let the engine recompile from the restored sources.
            await StartEngineAsync(cancellationToken);
            progress?.Report(100);
            _log.Info($"Restored configuration from {backupPath}");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // ═══════════════════════════ Registry ⇄ .reg ═══════════════════════════

    /// <summary>Exports a registry subtree under HKLM to .reg text (in-memory — no shelling out).</summary>
    internal static string ExportRegistryToReg(string basePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        WriteRegKey(sb, root, basePath);
        return sb.ToString();
    }

    private static void WriteRegKey(StringBuilder sb, RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        if (key == null) return;

        sb.AppendLine();
        sb.AppendLine($"[HKEY_LOCAL_MACHINE\\{path}]");
        foreach (string name in key.GetValueNames())
        {
            try
            {
                var kind = key.GetValueKind(name);
                object? value = key.GetValue(name);
                string regName = name.Length == 0 ? "@" : $"\"{EscapeRegString(name)}\"";
                sb.AppendLine($"{regName}={FormatRegValue(kind, value)}");
            }
            catch { }
        }

        foreach (string sub in key.GetSubKeyNames())
        {
            WriteRegKey(sb, root, path + "\\" + sub);
        }
    }

    private static string FormatRegValue(RegistryValueKind kind, object? value)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord:
                return $"dword:{Convert.ToUInt32(value):x8}";
            case RegistryValueKind.QWord:
                return $"hex(b):{BitConverter.GetBytes(Convert.ToInt64(value)).Reverse().Aggregate("", (a, b) => a + $"{b:x2},").TrimEnd(',')}";
            case RegistryValueKind.Binary when value is byte[] bytes:
                return $"hex:{bytes.Aggregate("", (a, b) => a + $"{b:x2},").TrimEnd(',')}";
            case RegistryValueKind.MultiString when value is string[] strings:
                var parts = strings.Select(s => EscapeRegString(s)).ToList();
                return $"hex(7):{string.Join(",", parts.Select(s => ToHexWithNulls(s)))}00,";
            case RegistryValueKind.ExpandString:
                return $"hex(2):{ToHexWithNulls(value?.ToString() ?? "")}00,";
            default:
                return $"\"{EscapeRegString(value?.ToString() ?? "")}\"";
        }
    }

    private static string ToHexWithNulls(string s) =>
        Encoding.Unicode.GetBytes(s).Aggregate("", (a, b) => a + $"{b:x2},");

    private static string EscapeRegString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Applies .reg text (HKLM\... sections) to the registry, replacing the Windhawk tree.</summary>
    internal static void ApplyRegTextToRegistry(string regText)
    {
        // Replace the whole tree first so removed keys/values disappear too.
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (baseKey.OpenSubKey(WindhawkRootRegistryPath) != null)
            {
                baseKey.DeleteSubKeyTree(WindhawkRootRegistryPath, throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not clear the current Windhawk registry state: {ex.Message}");
        }

        var sections = ParseRegText(regText);
        foreach (var (path, values) in sections)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(path, writable: true);
            if (key == null) continue;

            foreach (var (name, kind, data) in values)
            {
                switch (kind)
                {
                    case "dword": key.SetValue(name, Convert.ToUInt32(data, 16), RegistryValueKind.DWord); break;
                    case "string": key.SetValue(name, data, RegistryValueKind.String); break;
                    default: key.SetValue(name, data, RegistryValueKind.String); break;
                }
            }
        }
    }

    private static List<(string Path, List<(string Name, string Kind, string Data)> Values)> ParseRegText(string regText)
    {
        var sections = new List<(string, List<(string, string, string)>)>();
        List<(string, string, string)>? current = null;

        foreach (string rawLine in regText.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("Windows Registry Editor")) continue;

            if (line.StartsWith("[HKEY_LOCAL_MACHINE\\"))
            {
                string path = line.Substring("[HKEY_LOCAL_MACHINE\\".Length, line.Length - "[HKEY_LOCAL_MACHINE\\".Length - 1);
                current = new List<(string, string, string)>();
                sections.Add((path, current));
                continue;
            }

            if (current == null || !line.Contains('=')) continue;
            int eq = line.IndexOf('=');
            string rawName = line[..eq];
            string rawValue = line[(eq + 1)..];

            string name = rawName == "@" ? "" : ParseRegName(rawName);
            if (rawValue.StartsWith("dword:"))
            {
                current.Add((name, "dword", rawValue["dword:".Length..].Trim()));
            }
            else if (rawValue.StartsWith('"'))
            {
                current.Add((name, "string", ParseRegName(rawValue)));
            }
            // hex(2)/hex(7)/hex(b) values are skipped on restore — the engine
            // rebuilds binary state from source anyway; strings + dwords are
            // the whole mod schema we need.
        }

        return sections;
    }

    private static string ParseRegName(string raw) =>
        raw.Trim('"').Replace("\\\\", "\\").Replace("\\\"", "\"");

    // ═══════════════════════════ Engine control ═══════════════════════════

    public async Task StopEngineAsync(CancellationToken cancellationToken = default)
    {
        string exe = GetExecutablePath();
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException($"windhawk.exe not found at {exe}.");
        }

        int exitCode = await RunProcessAsync(exe, "-exit -wait", TimeSpan.FromSeconds(30), cancellationToken);
        _log.Info($"windhawk.exe -exit -wait completed (exit {exitCode}).");

        for (int i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessRunning("windhawk")) return;
            await Task.Delay(500, cancellationToken);
        }

        _log.Warn("windhawk.exe -exit did not stop the engine in time; continuing anyway.");
    }

    public async Task StartEngineAsync(CancellationToken cancellationToken = default)
    {
        string exe = GetExecutablePath();
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException($"windhawk.exe not found at {exe}.");
        }

        int exitCode = await RunProcessAsync(exe, "-restart -tray-only", TimeSpan.FromSeconds(30), cancellationToken);
        _log.Info($"windhawk.exe -restart -tray-only completed (exit {exitCode}).");
        await Task.Delay(1500, cancellationToken);
    }

    /// <summary>
    /// Forces the engine to reload freshly deployed mods by toggling Disabled
    /// 0→1→0 with SettingsChangeTime bumps — exactly what a manual disable +
    /// enable in the Windhawk UI does. The running engine watches these values:
    /// the disable makes it unload the mod everywhere, the enable makes it load
    /// the mod AND inject it into all matching processes (including ones that
    /// were already running). After a cold engine start a mod is often merely
    /// registered without being injected — the symptom users previously fixed
    /// by toggling the mod by hand — so every deploy ends with this kick.
    /// </summary>
    public async Task KickModsReloadAsync(IEnumerable<string> modIds, CancellationToken cancellationToken = default)
    {
        var ids = modIds.ToList();
        if (ids.Count == 0) return;

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        // Phase 1: disable everything at once → engine unloads the mods.
        foreach (string modId in ids)
        {
            try
            {
                using var modKey = baseKey.OpenSubKey($@"{EngineModsRegistryPath}\{modId}", writable: true);
                if (modKey == null) continue;
                modKey.SetValue("Disabled", 1, RegistryValueKind.DWord);
                modKey.SetValue("SettingsChangeTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                _log.Warn($"Reload kick (disable) failed for {modId}: {ex.Message}");
            }
        }

        // Give the engine time to notice and unload. Too short and the
        // disable and enable land within one engine poll — the engine sees
        // Disabled back at 0 and treats the whole toggle as a no-op.
        await Task.Delay(3000, cancellationToken);

        // Phase 2: re-enable everything at once → engine loads + re-injects.
        foreach (string modId in ids)
        {
            try
            {
                using var modKey = baseKey.OpenSubKey($@"{EngineModsRegistryPath}\{modId}", writable: true);
                if (modKey == null) continue;
                modKey.SetValue("Disabled", 0, RegistryValueKind.DWord);
                modKey.SetValue("SettingsChangeTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                _log.Warn($"Reload kick (enable) failed for {modId}: {ex.Message}");
            }
        }

        _log.Info($"Reload kick applied to {ids.Count} mod(s) — engine reloaded and re-injected them.");
    }

    // ═══════════════════════════ Part 4: shell (Explorer) restart ═══════════════════════════

    /// <summary>
    /// Restarts Windows Explorer in the current session so freshly deployed mods
    /// that target explorer.exe are loaded by the engine into a clean shell.
    ///
    /// This is the deterministic programmatic equivalent of the manual
    /// disable → enable toggle in the Windhawk UI. Mods registered while the
    /// engine was stopped can leave an OLD Explorer session running them
    /// half-applied (or with stale/empty theme settings) until the shell
    /// reloads them; a restarted Explorer is injected by the engine at startup
    /// and reads the current registry — theme settings included — so the
    /// customization shows up without any manual step. The taskbar flickers
    /// briefly while the shell comes back.
    ///
    /// Returns true when Explorer is confirmed running in this session
    /// afterwards (or no Explorer was running to begin with, e.g. in a
    /// non-interactive session, which is a legitimate no-op).
    /// </summary>
    public async Task<bool> RestartExplorerAsync(CancellationToken cancellationToken = default)
    {
        int sessionId = GetCurrentSessionId();
        string explorerExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        var explorers = GetSessionExplorers(sessionId);
        if (explorers.Count == 0)
        {
            _log.Info("Explorer restart skipped — no Explorer is running in this session.");
            return true;
        }

        _log.Info("Restarting Explorer so deployed Windhawk mods re-inject into a clean shell...");

        // ── Phase 1: end the old shell. Explorer does not auto-relaunch after
        // being killed, so we start the replacement ourselves in phase 2.
        foreach (var process in explorers)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                _log.Info($"Ended Explorer (pid {process.Id}).");
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not end Explorer (pid {process.Id}): {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        // Let the old shell actually exit before starting the new one.
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetSessionExplorers(sessionId).Count == 0) break;
            await Task.Delay(300, cancellationToken);
        }

        // ── Phase 2: relaunch the shell UNELEVATED. KalOS runs elevated, and an
        // Explorer started straight from this process would inherit the admin
        // token (degraded shell: broken drag & drop, elevated file windows). A
        // one-shot scheduled task with /RL LIMITED + /IT runs explorer.exe in
        // the interactive session under a filtered token — the mechanism other
        // debloat tools use to hand the shell back to the user.
        bool relaunched = await LaunchExplorerTaskAsync(explorerExe, sessionId, cancellationToken);
        if (!relaunched)
        {
            _log.Warn("Scheduled-task Explorer relaunch did not confirm; falling back to a direct start.");
            try
            {
                Process.Start(new ProcessStartInfo(explorerExe) { UseShellExecute = true });
                relaunched = true;
            }
            catch (Exception ex)
            {
                _log.Error($"Could not start Explorer: {ex.Message}");
            }
        }

        // ── Phase 3: wait for the new shell to appear.
        var appearDeadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < appearDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetSessionExplorers(sessionId).Count > 0)
            {
                _log.Info("Explorer restarted — deployed mods load in the new shell.");
                return true;
            }
            await Task.Delay(500, cancellationToken);
        }

        _log.Warn("Explorer restart did not confirm a new shell within 20 seconds.");
        return relaunched;
    }

    /// <summary>True when any of <paramref name="modIds"/> targets Windows Explorer.</summary>
    private static bool TargetsExplorer(IReadOnlyList<WindhawkModEntry> entries, IEnumerable<string> modIds)
    {
        var ids = new HashSet<string>(modIds, StringComparer.OrdinalIgnoreCase);
        return entries.Any(entry =>
            ids.Contains(entry.Id) &&
            !string.IsNullOrWhiteSpace(entry.TargetProcess) &&
            entry.TargetProcess
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(target => target.Contains("explorer.exe", StringComparison.OrdinalIgnoreCase)));
    }

    private static int GetCurrentSessionId()
    {
        try { return Process.GetCurrentProcess().SessionId; }
        catch { return 0; }
    }

    /// <summary>Explorer processes in <paramref name="sessionId"/> (each must be disposed by the caller).</summary>
    private static List<Process> GetSessionExplorers(int sessionId)
    {
        var explorers = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (process.SessionId == sessionId) explorers.Add(process);
                    else process.Dispose();
                }
                catch
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            // Static helper without logging access — an enumeration failure is
            // simply reported as "no Explorer" by callers.
            _ = ex;
        }
        return explorers;
    }

    /// <summary>
    /// Launches explorer.exe in the interactive session through a one-shot,
    /// least-privilege scheduled task (deleted afterwards) and reports whether
    /// an Explorer process showed up in the session.
    /// </summary>
    private async Task<bool> LaunchExplorerTaskAsync(
        string explorerExe, int sessionId, CancellationToken cancellationToken)
    {
        string taskName = $"KalOS-ExplorerRestart-{Process.GetCurrentProcess().Id}";
        string userName = $@"{Environment.UserDomainName}\{Environment.UserName}";
        string startTime = DateTime.Now.AddMinutes(1).ToString("HH:mm");

        string[] commands =
        {
            $"schtasks /Create /F /TN \"{taskName}\" /SC ONCE /ST {startTime} /RU \"{userName}\" /IT /RL LIMITED /TR \"{explorerExe}\"",
            $"schtasks /Run /TN \"{taskName}\"",
            $"schtasks /Delete /F /TN \"{taskName}\"",
        };

        try
        {
            foreach (string command in commands)
            {
                // schtasks runs under the current (elevated) token and its exit
                // code is checked rather than its stderr; failure is fine — the
                // caller falls back to launching Explorer directly.
                int exitCode = await RunProcessAsync("schtasks.exe", command, TimeSpan.FromSeconds(30), cancellationToken);
                if (exitCode != 0)
                {
                    _log.Warn($"schtasks exited {exitCode}: {command}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Scheduled-task Explorer relaunch failed: {ex.Message}");
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetSessionExplorers(sessionId).Count > 0) return true;
            await Task.Delay(300, cancellationToken);
        }
        return false;
    }

    private static bool IsProcessRunning(string name) =>
        Process.GetProcessesByName(name).Length > 0;

    private static async Task<int> RunProcessAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            CreateNoWindow = true,
        });
        if (process == null) return -1;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return -1;
        }
    }

    // ═══════════════════════════ Verification / state ═══════════════════════════

    /// <summary>Reads the Engine\Mods registry to mark which mods are ready (registered, enabled, and compiled).</summary>
    public Dictionary<string, bool> GetModsState()
    {
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var modsKey = baseKey.OpenSubKey(EngineModsRegistryPath);
            if (modsKey == null) return state;

            foreach (string id in modsKey.GetSubKeyNames())
            {
                state[id] = IsModReady(id);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read Windhawk mod state: {ex.Message}");
        }
        return state;
    }

    /// <summary>True when the mod is registered, enabled, AND the engine has a compiled library for it — i.e. it is actually running, not just booked in the registry.</summary>
    public bool IsModReady(string modId) =>
        ModRegistryEntryExists(modId) && !IsModDisabled(modId) && HasCompiledLibrary(modId);

    /// <summary>True when the mod is registered and compiled — even if it is currently disabled.</summary>
    public bool IsModDeployed(string modId) =>
        ModRegistryEntryExists(modId) && HasCompiledLibrary(modId);

    /// <summary>
    /// True when the engine produced a compiled library for the mod. The engine
    /// names compiled DLLs &lt;mod-id&gt;_&lt;version&gt;_&lt;random&gt;.dll and stores that
    /// name in the mod's LibraryFileName registry value after compiling; a mod
    /// with a registry entry but NO compiled library still needs compiling.
    /// </summary>
    private bool HasCompiledLibrary(string modId)
    {
        string root = GetEngineModsDirectory();
        if (!Directory.Exists(root)) return false;

        try
        {
            // Prefer the engine's own pointer in the registry.
            string? libraryFileName = ReadRegistryString(EngineModsRegistryPath, modId, "LibraryFileName");
            if (!string.IsNullOrWhiteSpace(libraryFileName) &&
                Directory.EnumerateFiles(root, libraryFileName, SearchOption.AllDirectories).Any())
            {
                return true;
            }

            // Fall back to any <mod-id>_*.dll the engine produced for it.
            return Directory.EnumerateFiles(root, $"{modId}_*.dll", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex)
        {
            _log.Warn($"Compiled-library check failed for {modId}: {ex.Message}");
            return false;
        }
    }

    private string? ReadRegistryString(string basePath, string modId, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{basePath}\{modId}");
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }



    public bool ModRegistryEntryExists(string modId)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{EngineModsRegistryPath}\{modId}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the mod's registry entry exists with Disabled != 1.</summary>
    private bool IsModDisabled(string modId)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{EngineModsRegistryPath}\{modId}");
            if (key == null) return true; // not registered at all

            object? disabled = key.GetValue("Disabled");
            return disabled != null && Convert.ToInt32(disabled) == 1;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Waits for the engine to accept a deployed mod: the engine keeps a mod's
    /// registry entry when it loads it and DELETES entries it cannot load, and
    /// only sets the compiled library after a successful compile. So readiness
    /// (registered + enabled + compiled library) is the acceptance signal.
    /// Compilation of these large mods can take a while, so the timeout is
    /// generous. NOTE: because our own deploy writes (LibraryFileName + DLL)
    /// already satisfy readiness, this cannot distinguish "we wrote it" from
    /// "the engine loaded it" — callers must give the engine a settle window
    /// (EngineSettleDelayMs) after StartEngineAsync before treating readiness
    /// as engine acceptance.
    /// </summary>
    private async Task<bool> WaitForModAcceptedAsync(string modId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsModReady(modId))
            {
                return true;
            }
            await Task.Delay(3000, cancellationToken);
        }
        return false;
    }

    // ═══════════════════════════ Manifest loading ═══════════════════════════

    public async Task<WindhawkModManifest> LoadManifestAsync(CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "windhawk_mods.json");
        if (!File.Exists(path))
        {
            _log.Warn($"Windhawk manifest not found at {path}");
            return new WindhawkModManifest();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WindhawkModManifest>(stream, cancellationToken: cancellationToken)
               ?? new WindhawkModManifest();
    }

    /// <summary>
    /// Persists a bumped version pin back into Assets/windhawk_mods.json so the
    /// update survives restarts. Best-effort — the app assets dir can be
    /// read-only in a packaged install, in which case the in-memory manifest
    /// still carries the new pin for the session.
    /// </summary>
    public void PersistVersionPin(string modId, string version)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "windhawk_mods.json");
        try
        {
            if (!File.Exists(path)) return;

            var manifest = JsonSerializer.Deserialize<WindhawkModManifest>(File.ReadAllText(path));
            var entry = manifest?.Mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            entry.Version = version;
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            _log.Info($"Bumped {modId} version pin to {version} in the manifest.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not persist the version bump for {modId}: {ex.Message}");
        }
    }

    private static WindhawkInstallerPin LoadInstallerPin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "windhawk_pins.json");
        if (!File.Exists(path)) return new WindhawkInstallerPin();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("installer", out var installer)
            ? installer.Deserialize<WindhawkInstallerPin>() ?? new WindhawkInstallerPin()
            : new WindhawkInstallerPin();
    }

    public static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS/1.0 (+windhawk deploy)");
        return client;
    }
}
