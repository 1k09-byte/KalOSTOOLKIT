using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KalOS.Models;
using KalOS.Services;
using Microsoft.Win32;

namespace KalOS.Tests.Services;

/// <summary>
/// Tests for the native tweaks engine: catalog integrity (the generated list
/// must be well-formed and contain the source scripts' key actions) and the
/// executor's safe-to-run paths (HKCU registry + temp-folder deletions — no
/// admin, no processes, no system-wide changes).
/// </summary>
public class TweaksServiceTests
{
    // ── Catalog integrity ──────────────────────────────────────────────

    [Fact]
    public void Catalog_RegistryKeysUseKnownHives()
    {
        var bad = TweaksService.All
            .Select(t => t.Action)
            .OfType<RegistrySetAction>()
            .Where(a => !(a.Key.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
                          || a.Key.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
                          || a.Key.StartsWith("HKEY_USERS\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Catalog_NoDuplicateActions()
    {
        var dups = TweaksService.All
            .GroupBy(t => t.Action)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(dups);
    }

    // ── Hand-added sync/debloat tweaks (disablesync.reg + debloat.reg) ──

    [Fact]
    public void SyncAndDebloat_Tweaks_ArePresent()
    {
        var actions = TweaksService.All.Select(t => t.Action).ToList();

        // Offline Files / Sync Center / MSMQ services
        Assert.Contains(actions.OfType<DisableServiceAction>(),
            a => a.ServiceName == "CSC");
        Assert.Contains(actions.OfType<DisableServiceAction>(),
            a => a.ServiceName == "CscService");
        Assert.Contains(actions.OfType<DisableServiceAction>(),
            a => a.ServiceName == "MSMQ");

        // Sync Center policy + partner store + mobsync logon trigger
        Assert.Contains(actions.OfType<RegistrySetAction>(),
            a => a.Key.EndsWith(@"Policies\Microsoft\Windows\NetCache")
                 && a.ValueName == "Enabled" && a.Data == "0");
        Assert.Contains(actions.OfType<RegistrySetAction>(),
            a => a.Key.EndsWith(@"CurrentVersion\SyncMgr")
                 && a.ValueName == "KeepSyncPartners" && a.Data == "0");
        Assert.Contains(actions.OfType<RegistrySetAction>(),
            a => a.Key.EndsWith(@"CurrentVersion\SyncMgr")
                 && a.ValueName == "StartOnLogin" && a.Data == "0");

        // Taskbar / Explorer visuals (debloat)
        Assert.Contains(actions.OfType<RegistrySetAction>(),
            a => a.Key.EndsWith(@"Explorer\Advanced")
                 && a.ValueName == "ShowTaskViewButton" && a.Data == "0");
        Assert.Contains(actions.OfType<RegistrySetAction>(),
            a => a.Key.EndsWith(@"Explorer\Advanced")
                 && a.ValueName == "TaskbarAnimations" && a.Data == "0");
        Assert.Contains(actions.OfType<RegistrySetAction>(),
            a => a.Key.EndsWith(@"Explorer\Advanced")
                 && a.ValueName == "IconsOnly" && a.Data == "1");
    }

    [Fact]
    public void SyncAndDebloat_Tweaks_PassWifiSafetyGuard()
    {
        var newOnes = TweaksService.All.Where(t =>
            t.Action is DisableServiceAction d && (d.ServiceName is "CSC" or "CscService" or "MSMQ")
            || t.Action is RegistrySetAction r
                && (r.Key.Contains("NetCache", StringComparison.OrdinalIgnoreCase)
                    || r.Key.Contains("SyncMgr", StringComparison.OrdinalIgnoreCase)));

        Assert.NotEmpty(newOnes);
        Assert.All(newOnes, t => Assert.False(
            WifiSafety.IsWifiTouching(t),
            $"tween '{t.Name}' must never be refused as wifi-touching"));
    }

    // ── Executor: registry (HKCU, no admin) ────────────────────────────

    [Fact]
    public async Task Apply_RegistrySetThenClearThenDelete()
    {
        const string keyPath = @"Software\KalOS.Tests";
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            Assert.NotNull(key);
        }

        try
        {
            var service = new TweaksService();
            var set = new TweakDef("set", TweakGroup.Privacy,
                new RegistrySetAction(@"HKCU\" + keyPath, "TestValue", TweakValueKind.Dword, "1"));
            var (applied, failed) = await service.ApplyAsync(new[] { set });
            Assert.Equal(1, applied);
            Assert.Equal(0, failed);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal(1, Convert.ToInt32(key?.GetValue("TestValue")));
            }

            var clear = new TweakDef("clear", TweakGroup.History,
                new RegistryValuesClearAction(@"HKCU\" + keyPath, Recursive: false));
            (applied, failed) = await service.ApplyAsync(new[] { clear });
            Assert.Equal(1, applied);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Null(key?.GetValue("TestValue"));
            }

            var del = new TweakDef("del", TweakGroup.History,
                new RegistryKeyDeleteAction(@"HKCU\" + keyPath));
            (applied, failed) = await service.ApplyAsync(new[] { del });
            Assert.Equal(1, applied);
            Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); } catch { }
        }
    }

    // ── Executor: file deletion (temp dirs, no admin) ──────────────────

    [Fact]
    public async Task Apply_DeletePathContentsAndWildcard()
    {
        string root = Path.Combine(Path.GetTempPath(), "KalOS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "b");

        var service = new TweaksService();

        // Contents-only clears the folder but keeps the folder itself.
        var contents = new TweakDef("contents", TweakGroup.Logs,
            new DeletePathAction(root, ContentsOnly: true));
        var (applied, failed) = await service.ApplyAsync(new[] { contents });
        Assert.Equal(1, applied);
        Assert.Equal(0, failed);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));

        // Wildcard deletes matching dirs (OneDrive* style).
        string root2 = Path.Combine(Path.GetTempPath(), "KalOS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root2 + "-x");
        Directory.CreateDirectory(root2 + "-y");
        var wild = new TweakDef("wild", TweakGroup.OneDrive,
            new DeletePathAction(root2 + "-*", ContentsOnly: false));
        (applied, failed) = await service.ApplyAsync(new[] { wild });
        Assert.Equal(1, applied);
        Assert.False(Directory.Exists(root2 + "-x"));
        Assert.False(Directory.Exists(root2 + "-y"));

        try { Directory.Delete(root, true); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch { }
    }

    [Fact]
    public async Task Apply_MissingPathsAreNotFailures()
    {
        var service = new TweaksService();
        var tweaks = new[]
        {
            new TweakDef("missing-file", TweakGroup.Logs,
                new DeletePathAction(Path.Combine(Path.GetTempPath(), "definitely-not-here-KalOS"), ContentsOnly: false)),
            new TweakDef("missing-key", TweakGroup.Privacy,
                new RegistrySetAction(@"HKCU\Software\KalOS.Tests.Missing\Sub", "V", TweakValueKind.Dword, "1")),
        };
        var (applied, failed) = await service.ApplyAsync(tweaks);
        Assert.Equal(2, applied);
        Assert.Equal(0, failed);
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\KalOS.Tests.Missing", throwOnMissingSubKey: false);
        }
        catch { }
    }

    [Fact]
    public async Task Apply_SkipsAlreadyAppliedAndRepeatsIdempotently()
    {
        const string keyPath = @"Software\KalOS.Tests.Skip";
        using (Registry.CurrentUser.CreateSubKey(keyPath)) { } // ensure key exists
        var service = new TweaksService();
        var tweak = new TweakDef("skip-value", TweakGroup.Privacy,
            new RegistrySetAction(@"HKCU\" + keyPath, "SkipValue", TweakValueKind.Dword, "7"));

        var lines = new List<string>();
        void Report(string s) => lines.Add(s);

        try
        {
            // First run sets the value.
            var (a1, f1) = await service.ApplyAsync(new[] { tweak }, report: Report);
            Assert.Equal(1, a1);
            Assert.Equal(0, f1);
            using (var k = Registry.CurrentUser.OpenSubKey(keyPath))
                Assert.Equal(7, Convert.ToInt32(k?.GetValue("SkipValue")));
            Assert.DoesNotContain(lines, l => l.Contains("already applied"));

            // Second run detects it's already set and short-circuits.
            lines.Clear();
            var (a2, f2) = await service.ApplyAsync(new[] { tweak }, report: Report);
            Assert.Equal(1, a2); // still counted as applied (state is correct)
            Assert.Equal(0, f2);
            Assert.Contains(lines, l => l.Contains("already applied, skipped"));
            using (var k = Registry.CurrentUser.OpenSubKey(keyPath))
                Assert.Equal(7, Convert.ToInt32(k?.GetValue("SkipValue")));
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); } catch { }
        }
    }

    [Fact]
    public async Task Apply_AlreadyAbsentDeleteIsSkipped()
    {
        const string keyPath = @"Software\KalOS.Tests.Absent";
        // Ensure the key is gone, then a "delete value" tweak should skip.
        try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); } catch { }
        var service = new TweaksService();
        var tweak = new TweakDef("absent-del", TweakGroup.Privacy,
            new RegistryKeyDeleteAction(@"HKCU\" + keyPath));
        var lines = new List<string>();
        var (applied, failed) = await service.ApplyAsync(new[] { tweak }, report: l => lines.Add(l));
        Assert.Equal(1, applied);
        Assert.Equal(0, failed);
        Assert.Contains(lines, l => l.Contains("already applied, skipped"));
    }
}
