using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Models;
using Microsoft.Win32;

namespace KalOS.Services
{
    /// <summary>
    /// Runs the <see cref="TweakCatalog"/> natively — no batch scripts.
    ///
    /// Registry keys/values go through <see cref="Microsoft.Win32.Registry"/>,
    /// files through <see cref="System.IO"/>, services through the Services
    /// registry key (Start=4), scheduled tasks through schtasks, optional
    /// features/capabilities through DISM, event logs through wevtutil, and
    /// Store apps through PowerShell (the only API Windows exposes for Appx
    /// removal). OneDrive and Edge removals are hand-implemented composites
    /// that mirror the source scripts' full sequences.
    ///
    /// Every tweak is best-effort: a failure is counted and reported but never
    /// throws, so the install keeps going and the Finish page shows exactly
    /// what failed.
    /// </summary>
    public sealed class TweaksService
    {
        /// <summary>
        /// Every tweak: the generated catalog plus hand-added composites.
        /// Hand-added tweaks live here (not in TweakCatalog.g.cs) so re-running
        /// the generator never removes them.
        /// </summary>
        public static IReadOnlyList<TweakDef> All { get; } =
            TweakCatalog.All
            .Concat(new[]
            {
                new TweakDef("Enable \"End Task\" in the taskbar right-click menu (Windows 11)",
                    TweakGroup.Privacy,
                    new RegistrySetAction(
                        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings",
                        @"TaskbarEndTask", TweakValueKind.Dword, @"1")),
            })
            .ToList();

        public IReadOnlyList<TweakDef> Catalog => All;

        public static IReadOnlyList<TweakDef> ByGroup(TweakGroup group) =>
            All.Where(t => t.Group == group).ToList();

        /// <summary>
        /// Runs the given tweaks. Returns (applied, failed). <paramref name="progress"/>
        /// receives the fraction (0..1) of tweaks completed after each one.
        /// </summary>
        public async Task<(int Applied, int Failed)> ApplyAsync(
            IEnumerable<TweakDef> tweaks,
            Action<string>? report = null,
            Action<double>? progress = null,
            CancellationToken ct = default)
        {
            var list = tweaks.ToList();
            int applied = 0, failed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                report?.Invoke(list[i].Name);
                try
                {
                    await ExecuteAsync(list[i].Action, report, ct);
                    applied++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    report?.Invoke($"{list[i].Name} — {ex.Message}");
                }
                progress?.Invoke((double)(i + 1) / Math.Max(list.Count, 1));
            }
            return (applied, failed);
        }

        // ── dispatch ──────────────────────────────────────────────────────

        private Task ExecuteAsync(TweakAction action, Action<string>? report, CancellationToken ct)
        {
            return action switch
            {
                RegistrySetAction a => Task.Run(() => SetRegistryValue(a), ct),
                RegistryValueDeleteAction a => Task.Run(() => DeleteRegistryValue(a), ct),
                RegistryValuesClearAction a => Task.Run(() => ClearRegistryValues(a), ct),
                RegistryKeyCreateAction a => Task.Run(() => CreateRegistryKey(a), ct),
                RegistryKeyDeleteAction a => Task.Run(() => DeleteRegistryKey(a), ct),
                DeletePathAction a => Task.Run(() => DeletePath(a), ct),
                AppxRemoveAction a => RemoveAppxAsync(a, ct),
                DisableFeatureAction a => RunAsync("dism.exe",
                    $"/online /disable-feature /featurename:{a.FeatureName} /norestart", ct),
                RemoveCapabilityAction a => RemoveCapabilitiesAsync(a, ct),
                DisableServiceAction a => DisableServiceAsync(a, ct),
                DisableTaskAction a => DisableTasksAsync(a, ct),
                HostsBlockAction a => Task.Run(() => BlockHosts(a), ct),
                ClearEventLogsAction _ => ClearEventLogsAsync(ct),
                RunToolAction a => RunAsync(a.FileName, a.Arguments, ct),
                RemoveOneDriveAction _ => RemoveOneDriveAsync(ct),
                RemoveEdgeAction _ => RemoveEdgeAsync(ct),
                _ => Task.CompletedTask,
            };
        }

        // ── registry ──────────────────────────────────────────────────────

        private static (RegistryKey Root, string SubPath) SplitKey(string key)
        {
            key = key.Replace("$CURRENT_USER_SID", CurrentUserSid);
            var parts = key.Split(new[] { '\\' }, 2, StringSplitOptions.None);
            var root = parts[0].ToUpperInvariant() switch
            {
                "HKLM" => Registry.LocalMachine,
                "HKCU" => Registry.CurrentUser,
                "HKEY_USERS" or "HKU" => Registry.Users,
                _ => throw new InvalidOperationException($"Unknown registry hive: {parts[0]}"),
            };
            return (root, parts.Length > 1 ? parts[1] : string.Empty);
        }

        private static string CurrentUserSid
        {
            get
            {
                try
                {
                    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    return identity.User?.Value ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private static void SetRegistryValue(RegistrySetAction a)
        {
            var (root, sub) = SplitKey(a.Key);
            using var key = root.CreateSubKey(sub, writable: true);
            if (key is null) return;
            object value = a.Kind switch
            {
                TweakValueKind.Dword => int.TryParse(a.Data, out var n) ? n : 0,
                TweakValueKind.String => a.Data,
                TweakValueKind.MultiString => a.Data == "\\0"
                    ? new[] { string.Empty }
                    : a.Data.Split(';'),
                _ => a.Data,
            };
            var kind = a.Kind switch
            {
                TweakValueKind.Dword => RegistryValueKind.DWord,
                TweakValueKind.String => RegistryValueKind.String,
                TweakValueKind.MultiString => RegistryValueKind.MultiString,
                _ => RegistryValueKind.String,
            };
            key.SetValue(a.ValueName, value, kind);
        }

        private static void DeleteRegistryValue(RegistryValueDeleteAction a)
        {
            var (root, sub) = SplitKey(a.Key);
            using var key = root.OpenSubKey(sub, writable: true);
            key?.DeleteValue(a.ValueName, throwOnMissingValue: false);
        }

        private static void ClearRegistryValues(RegistryValuesClearAction a)
        {
            var (root, sub) = SplitKey(a.Key);
            using var key = root.OpenSubKey(sub, writable: true);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                try { key.DeleteValue(name); } catch { }
            }
            if (a.Recursive)
            {
                foreach (var child in key.GetSubKeyNames())
                {
                    using var childKey = root.OpenSubKey(sub + "\\" + child, writable: true);
                    ClearValuesRecursive(childKey);
                }
            }
        }

        private static void ClearValuesRecursive(RegistryKey? key)
        {
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                try { key.DeleteValue(name); } catch { }
            }
            foreach (var child in key.GetSubKeyNames())
            {
                using var c = key.OpenSubKey(child, writable: true);
                ClearValuesRecursive(c);
            }
        }

        private static void CreateRegistryKey(RegistryKeyCreateAction a)
        {
            var (root, sub) = SplitKey(a.Key);
            using var key = root.CreateSubKey(sub, writable: true);
        }

        private static void DeleteRegistryKey(RegistryKeyDeleteAction a)
        {
            var (root, sub) = SplitKey(a.Key);
            try { root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); } catch { }
        }

        // ── file system ───────────────────────────────────────────────────

        private static void DeletePath(DeletePathAction a)
        {
            string full = Environment.ExpandEnvironmentVariables(a.Path);
            if (a.ContentsOnly)
            {
                if (Directory.Exists(full))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(full))
                    {
                        try
                        {
                            if (Directory.Exists(entry)) Directory.Delete(entry, true);
                            else File.Delete(entry);
                        }
                        catch { }
                    }
                }
                else if (File.Exists(full))
                {
                    try { File.Delete(full); } catch { }
                }
                return;
            }

            string dir = Path.GetDirectoryName(full) ?? ".";
            string pattern = Path.GetFileName(full);
            bool wildcard = pattern.IndexOfAny(new[] { '*', '?' }) >= 0;
            if (!Directory.Exists(dir)) return;

            if (wildcard)
            {
                foreach (var file in Directory.EnumerateFiles(dir, pattern))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir, pattern))
                {
                    try { Directory.Delete(sub, true); } catch { }
                }
            }
            else if (Directory.Exists(full))
            {
                try { Directory.Delete(full, true); } catch { }
            }
            else if (File.Exists(full))
            {
                try { File.Delete(full); } catch { }
            }
        }

        // ── Store apps (PowerShell — the only removal API Windows exposes) ─

        /// <summary>Escapes a string for use inside a single-quoted PowerShell literal.</summary>
        private static string PsQuote(string s) => s.Replace("'", "''");

        private static async Task RemoveAppxAsync(AppxRemoveAction a, CancellationToken ct)
        {
            string name = PsQuote(a.PackageName);

            // The installer runs elevated, and on Windows 11 24H2+ an elevated
            // session only sees the interactive user's Appx packages through
            // -AllUsers — the old plain "Get-AppxPackage 'X' |
            // Remove-AppxPackage" matched nothing from that context, so apps
            // silently "survived" the cleanup. Remove for every user, drop the
            // provisioned (staged) copy so new accounts never receive it, and
            // mark every copy deprovisioned so Windows Update cannot reinstall
            // it later. The final check turns a failed removal into a reported
            // failure instead of a silent "success".
            string script =
                "$pkgs = @(Get-AppxPackage -AllUsers -Name '" + name + "'); " +
                "$pkgs | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue; " +
                "$pkgs | ForEach-Object { New-Item -Path ('HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore\\Deprovisioned\\' + $_.PackageFullName) -Force -ErrorAction SilentlyContinue | Out-Null }; " +
                "Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq '" + name + "' } | Remove-AppxProvisionedPackage -Online | Out-Null; " +
                "if (Get-AppxPackage -AllUsers -Name '" + name + "') { exit 1 }";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) throw new InvalidOperationException($"Could not launch PowerShell to remove '{a.PackageName}'.");

            // Drain both streams while waiting — a full pipe buffer would
            // otherwise deadlock WaitForExitAsync.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(ct);
            var errors = (await stderrTask).Trim();

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Package '{a.PackageName}' could not be removed" +
                    (errors.Length == 0 ? "." : $": {errors.Split('\n')[0].Trim()}"));
            }
        }

        // ── capabilities / features (DISM) ────────────────────────────────

        private static async Task RemoveCapabilitiesAsync(RemoveCapabilityAction a, CancellationToken ct)
        {
            string prefix = a.CapabilityName.TrimEnd('*');
            bool isPattern = a.CapabilityName.EndsWith("*");
            var names = new List<string>();
            if (!isPattern)
            {
                names.Add(a.CapabilityName);
            }
            else
            {
                // Enumerate via DISM and match the pattern (DISM has no wildcards).
                var output = await RunCaptureAsync("dism.exe", "/online /get-capabilities", ct);
                foreach (var line in output)
                {
                    const string marker = "Capability Name : ";
                    int i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    string name = line[(i + marker.Length)..].Trim();
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }

            foreach (var name in names.Distinct())
            {
                await RunAsync("dism.exe", $"/online /remove-capability /capabilityname:{name} /norestart", ct);
            }
        }

        // ── services (registry Start=4, then stop) ────────────────────────

        private static async Task DisableServiceAsync(DisableServiceAction a, CancellationToken ct)
        {
            var names = ExpandServicePattern(a.ServiceName);
            foreach (var name in names)
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
                key?.SetValue("Start", 4, RegistryValueKind.DWord);
                // net stop /y — NOT sc.exe stop. When a service has running
                // dependents, sc.exe prompts "Continue? (Y/N)" on stdin and
                // with no console input attached it waits forever, hanging
                // the whole tweaks step (seen on wlidsvc).
                await RunAsync("net.exe", $"stop \"{name}\" /y", ct, ignoreErrors: true);
            }
        }

        private static IEnumerable<string> ExpandServicePattern(string pattern)
        {
            if (pattern.IndexOf('*') < 0)
                return new[] { pattern };

            var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase);
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return Enumerable.Empty<string>();
            return services.GetSubKeyNames().Where(n => rx.IsMatch(n)).ToList();
        }

        // ── scheduled tasks (schtasks — the built-in tool) ────────────────

        private static async Task DisableTasksAsync(DisableTaskAction a, CancellationToken ct)
        {
            var rx = new Regex("^" + Regex.Escape(a.TaskNamePattern).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase);
            string folder = (a.TaskPath ?? "\\").Replace("\\\\", "\\");
            if (!folder.EndsWith("\\")) folder += "\\";

            // /FO CSV /V lists every task with a fully-qualified TaskName.
            var lines = await RunCaptureAsync("schtasks.exe", "/Query /FO CSV /V", ct);
            foreach (var line in lines.Skip(1)) // header
            {
                var name = ReadCsvField(line, 0);
                if (string.IsNullOrEmpty(name) || !name.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    continue;
                string taskName = name[folder.Length..];
                if (rx.IsMatch(taskName))
                {
                    await RunAsync("schtasks.exe", $"/Change /TN \"{name}\" /DISABLE", ct, ignoreErrors: true);
                }
            }
        }

        /// <summary>Parses one field from a schtasks CSV line (fields are quoted, commas inside quotes are kept).</summary>
        private static string ReadCsvField(string line, int index)
        {
            int pos = 0;
            for (int f = 0; f <= index; f++)
            {
                while (pos < line.Length && line[pos] == ' ') pos++;
                if (pos >= line.Length) return string.Empty;
                if (line[pos] != '"') return string.Empty;
                pos++; // opening quote
                int start = pos;
                while (pos < line.Length && line[pos] != '"')
                {
                    if (line[pos] == '"' && pos + 1 < line.Length && line[pos + 1] == '"')
                        pos++; // escaped quote
                    pos++;
                }
                string field = line[start..pos];
                pos++; // closing quote
                if (f == index) return field;
            }
            return string.Empty;
        }

        // ── hosts-file blocking (0.0.0.0 sinkhole entries) ───────────────

        /// <summary>
        /// Appends 0.0.0.0 entries for each domain to the hosts file, skipping
        /// any domain that is already blocked (by any address). Idempotent.
        /// </summary>
        private static void BlockHosts(HostsBlockAction a)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "etc", "hosts");

            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && !parts[0].StartsWith("#"))
                        blocked.Add(parts[1]);
                }
            }

            var toAdd = a.Domains.Where(d => !blocked.Contains(d)).ToList();
            if (toAdd.Count == 0) return;

            using var writer = File.AppendText(path);
            writer.WriteLine();
            writer.WriteLine("# Blocked by KalOS tweaks");
            foreach (var d in toAdd)
                writer.WriteLine($"0.0.0.0\t{d} # KalOS");
        }

        // ── event logs ────────────────────────────────────────────────────

        private static async Task ClearEventLogsAsync(CancellationToken ct)
        {
            var logs = await RunCaptureAsync("wevtutil.exe", "el", ct);
            foreach (var log in logs)
            {
                if (string.IsNullOrWhiteSpace(log)) continue;
                await RunAsync("wevtutil.exe", $"cl \"{log}\"", ct, ignoreErrors: true);
            }
        }

        // ── OneDrive (hand-written composite mirroring the source script) ─

        private static async Task RemoveOneDriveAsync(CancellationToken ct)
        {
            // 1. Kill the process.
            await RunAsync("taskkill.exe", "/IM OneDrive.exe /F", ct, ignoreErrors: true);

            // 2. Remove the startup entry.
            DeleteRegistryValue(new RegistryValueDeleteAction(
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "OneDrive"));

            // 3. Run the official uninstaller.
            foreach (var setup in new[]
                     {
                         @"%SYSTEMROOT%\System32\OneDriveSetup.exe",
                         @"%SYSTEMROOT%\SysWOW64\OneDriveSetup.exe",
                     })
            {
                string path = Environment.ExpandEnvironmentVariables(setup);
                if (File.Exists(path))
                    await RunAsync(path, "/uninstall", ct, ignoreErrors: true);
            }

            // 4. Delete data + installation folders.
            foreach (var dir in new[]
                     {
                         @"%USERPROFILE%\OneDrive*",
                         @"%LOCALAPPDATA%\Microsoft\OneDrive",
                         @"%PROGRAMDATA%\Microsoft OneDrive",
                         @"%SYSTEMDRIVE%\OneDriveTemp",
                     })
            {
                DeletePath(new DeletePathAction(dir, ContentsOnly: false));
            }

            // 5. Remove shortcuts.
            foreach (var lnk in new[]
                     {
                         @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\OneDrive.lnk",
                         @"%USERPROFILE%\Links\OneDrive.lnk",
                         @"%SYSTEMROOT%\ServiceProfiles\LocalService\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\OneDrive.lnk",
                         @"%SYSTEMROOT%\ServiceProfiles\NetworkService\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\OneDrive.lnk",
                     })
            {
                DeletePath(new DeletePathAction(lnk, ContentsOnly: false));
            }

            // 6. Disable sync via policy.
            foreach (var (key, value) in new[]
                     {
                         (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC"),
                         (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSync"),
                     })
            {
                SetRegistryValue(new RegistrySetAction(key, value, TweakValueKind.Dword, "1"));
            }

            // 7. Remove the OneDriveSetup auto-install entry.
            DeleteRegistryValue(new RegistryValueDeleteAction(
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "OneDriveSetup"));

            // 8. Hide OneDrive from File Explorer's navigation pane.
            foreach (var clsid in new[]
                     {
                         @"HKCU\Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}",
                         @"HKCU\Software\Classes\Wow6432Node\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}",
                     })
            {
                SetRegistryValue(new RegistrySetAction(
                    clsid, "System.IsPinnedToNameSpaceTree", TweakValueKind.Dword, "0"));
            }

            // 9. Disable OneDrive scheduled tasks.
            foreach (var (path, name) in new[]
                     {
                         (@"\", @"OneDrive Reporting Task-*"),
                         (@"\", @"OneDrive Standalone Update Task-*"),
                         (@"\", @"OneDrive Per-Machine Standalone Update"),
                     })
            {
                await DisableTasksAsync(new DisableTaskAction(path, name), ct);
            }

            // 10. Clear the OneDrive environment variable.
            DeleteRegistryValue(new RegistryValueDeleteAction(@"HKCU\Environment", "OneDrive"));
        }

        // ── Microsoft Edge (hand-written composite mirroring the source script)

        private static async Task RemoveEdgeAsync(CancellationToken ct)
        {
            // 1. Allow the official uninstaller to run.
            SetRegistryValue(new RegistrySetAction(
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdateDev",
                "AllowUninstall", TweakValueKind.Dword, "1"));

            // 2. Remove the appx packages + deprovision markers.
            foreach (var pkg in new[]
                     {
                         "Microsoft.MicrosoftEdge",
                         "Microsoft.MicrosoftEdge.Stable",
                         "Microsoft.MicrosoftEdgeDevToolsClient",
                     })
            {
                try { await RemoveAppxAsync(new AppxRemoveAction(pkg, null), ct); }
                catch { /* best-effort — the setup.exe uninstall below is the real remover */ }
                CreateRegistryKey(new RegistryKeyCreateAction(
                    $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Deprovisioned\{pkg}_8wekyb3d8bbwe"));
            }

            // 3. Placeholder so the system stub never re-launches Edge.
            var placeholder = @"%SYSTEMROOT%\SystemApps\Microsoft.MicrosoftEdge_8wekyb3d8bbwe\MicrosoftEdge.exe";
            string phPath = Environment.ExpandEnvironmentVariables(placeholder);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(phPath)!);
                File.WriteAllText(phPath, "privacy.sexy placeholder");
            }
            catch { }

            // 4. Run the official uninstaller for every installed Edge copy.
            var programDirs = new[] { "%ProgramFiles%", "%ProgramFiles(x86)%" }
                .Select(Environment.ExpandEnvironmentVariables)
                .Where(d => Directory.Exists(d));
            foreach (var dir in programDirs)
            {
                string appRoot = Path.Combine(dir, "Microsoft", "Edge", "Application");
                if (!Directory.Exists(appRoot)) continue;
                foreach (var version in Directory.EnumerateDirectories(appRoot))
                {
                    string setup = Path.Combine(version, "Installer", "setup.exe");
                    if (!File.Exists(setup)) continue;
                    var exit = await RunExitAsync(setup,
                        "--uninstall --system-level --verbose-logging --force-uninstall", ct);
                    if (exit != 0 && exit != 19)
                    {
                        // fall through — removal continues with shortcuts/assocs
                    }
                }
            }

            // 5. Remove shortcuts.
            foreach (var lnk in new[]
                     {
                         @"%ProgramData%\Microsoft\Windows\Start Menu\Programs\Microsoft Edge.lnk",
                         @"%APPDATA%\Microsoft\Internet Explorer\Quick Launch\Microsoft Edge.lnk",
                         @"%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Microsoft Edge.lnk",
                         @"%PUBLIC%\Desktop\Microsoft Edge.lnk",
                         @"%SYSTEMROOT%\System32\config\systemprofile\AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\Microsoft Edge.lnk",
                         @"%USERPROFILE%\Desktop\Microsoft Edge.lnk",
                     })
            {
                DeletePath(new DeletePathAction(lnk, ContentsOnly: false));
            }

            // 6. Clear Edge association-toast entries (harmless leftovers once
            //    the appx is gone; the UserChoice ProgId surgery from the script
            //    is intentionally skipped — those point at the removed package).
            try
            {
                var (root, sub) = SplitKey(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts");
                using var key = root.OpenSubKey(sub, writable: true);
                if (key is not null)
                {
                    foreach (var name in key.GetValueNames()
                                 .Where(n => n.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        try { key.DeleteValue(name); } catch { }
                    }
                }
            }
            catch { }
        }

        // ── process helpers ───────────────────────────────────────────────

        private static async Task RunAsync(string fileName, string arguments,
            CancellationToken ct, bool ignoreErrors = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return;

            // Drain both pipes while waiting. Output is redirected but was
            // never read — once a child fills its ~4 KB stdout buffer (DISM
            // progress, verbose tools, …) it blocks on its next write and
            // WaitForExitAsync never returns, hanging the tweaks step.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            // Last-resort timeout: a wedged child (interactive prompt, stuck
            // driver operation) must never hang the install indefinitely.
            // The thrown TimeoutException is caught per-tweak by ApplyAsync,
            // counted as failed, and the install moves on.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"\"{fileName} {arguments}\" did not finish within 10 minutes and was terminated.");
            }

            _ = await stdoutTask;
            _ = await stderrTask;
        }

        private static async Task<int> RunExitAsync(string fileName, string arguments, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            await p.WaitForExitAsync(ct);
            return p.ExitCode;
        }

        private static async Task<List<string>> RunCaptureAsync(string fileName, string arguments,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var lines = new List<string>();
            using var p = Process.Start(psi);
            if (p is null) return lines;
            var readTask = p.StandardOutput.ReadToEndAsync();
            // Drain stderr too — same pipe-buffer deadlock as RunAsync.
            var stderrTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(ct);
            _ = await stderrTask;
            var output = await readTask;
            foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.TrimEnd('\r'));
            }
            return lines;
        }
    }
}
