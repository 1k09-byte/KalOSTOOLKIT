using System;
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
    public void Catalog_ContainsAllGroupsAndComposites()
    {
        var groups = TweaksService.All.Select(t => t.Group).Distinct().ToList();
        Assert.Contains(TweakGroup.Apps, groups);
        Assert.Contains(TweakGroup.OneDrive, groups);
        Assert.Contains(TweakGroup.Edge, groups);
        Assert.Contains(TweakGroup.Features, groups);
        Assert.Contains(TweakGroup.Capabilities, groups);
        Assert.Contains(TweakGroup.Privacy, groups);
        Assert.Contains(TweakGroup.Services, groups);
        Assert.Contains(TweakGroup.Tasks, groups);
        Assert.Contains(TweakGroup.History, groups);
        Assert.Contains(TweakGroup.Logs, groups);
    }

    [Fact]
    public void Catalog_HasSubstantialSize()
    {
        // The three privacy.sexy scripts add up to ~700 deduplicated actions.
        Assert.True(TweaksService.All.Count > 600,
            $"expected a full catalog, got {TweaksService.All.Count}");
    }

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
    public void Catalog_ContainsKnownTweaksFromTheScripts()
    {
        // Telemetry service disable (dataC.bat).
        Assert.Contains(TweaksService.All, t =>
            t.Action is DisableServiceAction s && s.ServiceName == "DiagTrack");

        // Appx removal (dataC.bat + removeapps.bat).
        Assert.Contains(TweaksService.All, t =>
            t.Action is AppxRemoveAction a && a.PackageName == "Microsoft.BingWeather");

        // Scheduled task disable (dataC.bat).
        Assert.Contains(TweaksService.All, t =>
            t.Action is DisableTaskAction task
            && task.TaskNamePattern == "KernelCeipTask");

        // The composites are hand-added on top of the generated catalog.
        Assert.Contains(TweaksService.All, t => t.Action is RemoveOneDriveAction);
        Assert.Contains(TweaksService.All, t => t.Action is RemoveEdgeAction);
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
}
