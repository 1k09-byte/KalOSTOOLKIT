using System.IO;
using System.IO.Compression;
using KalOS.Services;

namespace KalOS.Tests.Services;

/// <summary>
/// Backend contract tests for <see cref="ZipPackageInstaller"/>: staging
/// extraction with the zip-slip guard, the required-file checklist, the
/// wipe-and-copy upgrade path, and the error reporting the wizard relies on
/// to decide between the native and script install paths. Everything runs in
/// temp directories — %LOCALAPPDATA% is never touched.
/// </summary>
public class ZipPackageInstallerTests : IDisposable
{
    private readonly string _root;

    public ZipPackageInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kalos-ziptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Builds a KalOS-shaped package zip with the required files (worker under Tools\).</summary>
    private static string BuildZip(string path, Action<ZipArchive>? extra = null, bool complete = true)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        void AddFile(string name, string content = "data")
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        if (complete)
        {
            AddFile("KalOS.exe");
            AddFile("hostfxr.dll");
            AddFile("hostpolicy.dll");
            AddFile("coreclr.dll");
            AddFile(@"Tools\HardwareMonitorWorker.exe");
            AddFile("os-changes.json", "{}");
        }

        extra?.Invoke(zip);
        return path;
    }

    // ── Extraction ────────────────────────────────────────────────────

    [Fact]
    public void ExtractToStaging_LandsEveryFileAndDirectory()
    {
        string zip = BuildZip(Path.Combine(_root, "pkg.zip"));
        string staging = Path.Combine(_root, "staging");

        ZipPackageInstaller.ExtractToStaging(zip, staging);

        Assert.True(File.Exists(Path.Combine(staging, "KalOS.exe")));
        Assert.True(File.Exists(Path.Combine(staging, "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(staging, "Tools", "HardwareMonitorWorker.exe")));
        Assert.True(Directory.Exists(Path.Combine(staging, "Tools")));
    }

    [Fact]
    public void ExtractToStaging_SkipsMacJunkEntries()
    {
        string zip = BuildZip(Path.Combine(_root, "pkg-mac.zip"), extra: zip =>
        {
            var junk = zip.CreateEntry("__MACOSX/._KalOS.exe");
            using var w = new StreamWriter(junk.Open());
            w.Write("junk");
        });
        string staging = Path.Combine(_root, "staging-mac");

        ZipPackageInstaller.ExtractToStaging(zip, staging);

        Assert.False(Directory.Exists(Path.Combine(staging, "__MACOSX")));
        Assert.True(File.Exists(Path.Combine(staging, "KalOS.exe")));
    }

    [Fact]
    public void ExtractToStaging_RefusesZipSlipEntries()
    {
        // A crafted archive whose entry escapes the staging root.
        string zip = Path.Combine(_root, "evil.zip");
        using (var fs = new FileStream(zip, FileMode.Create, FileAccess.Write))
        using (var zipArchive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zipArchive.CreateEntry("../escaped.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("malicious");
        }

        Assert.Throws<InvalidOperationException>(() =>
            ZipPackageInstaller.ExtractToStaging(zip, Path.Combine(_root, "staging-slip")));
    }

    // ── Validation ────────────────────────────────────────────────────

    [Fact]
    public void ValidateRequiredFiles_PassesForCompletePackage()
    {
        string zip = BuildZip(Path.Combine(_root, "ok.zip"));
        string staging = Path.Combine(_root, "staging-ok");
        ZipPackageInstaller.ExtractToStaging(zip, staging);

        ZipPackageInstaller.ValidateRequiredFiles(staging); // must not throw
    }

    [Fact]
    public void ValidateRequiredFiles_ListsEveryMissingFile()
    {
        string staging = Path.Combine(_root, "staging-partial");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "KalOS.exe"), "MZ");
        // hostfxr / hostpolicy / coreclr / HardwareMonitorWorker are missing.

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ZipPackageInstaller.ValidateRequiredFiles(staging));

        Assert.Contains("hostfxr.dll", ex.Message);
        Assert.Contains("coreclr.dll", ex.Message);
        Assert.Contains("HardwareMonitorWorker.exe", ex.Message);
    }

    // ── Install pipeline ──────────────────────────────────────────────

    [Fact]
    public void Install_CompletePackage_CopiesEverythingAndReportsSuccess()
    {
        string zip = BuildZip(Path.Combine(_root, "good.zip"));
        string installDir = Path.Combine(_root, "install");

        var result = ZipPackageInstaller.Install(zip, installDir);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(Path.Combine(installDir, "KalOS.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "Tools", "HardwareMonitorWorker.exe")));
        // Staging folder must be gone after a successful install.
        Assert.Empty(Directory.GetDirectories(_root, "install.staging-*"));
    }

    [Fact]
    public void Install_IncompletePackage_ReportsErrorsAndNeverTouchesTarget()
    {
        string zip = BuildZip(Path.Combine(_root, "bad.zip"), complete: false);
        string installDir = Path.Combine(_root, "install-bad");

        var result = ZipPackageInstaller.Install(zip, installDir);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("hostfxr.dll"));
        Assert.False(Directory.Exists(installDir));
        Assert.Empty(Directory.GetDirectories(_root, "install-bad.staging-*"));
    }

    [Fact]
    public void Install_UpgradePath_WipesOldFilesAndKeepsNewOnes()
    {
        // First install (v1 layout).
        string zipA = BuildZip(Path.Combine(_root, "v1.zip"), extra: zip =>
        {
            var entry = zip.CreateEntry("StaleFile.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("old");
        });
        string installDir = Path.Combine(_root, "install-upgrade");
        var first = ZipPackageInstaller.Install(zipA, installDir);
        Assert.True(first.Success);
        Assert.True(File.Exists(Path.Combine(installDir, "StaleFile.txt")));

        // Second install (v2 layout without StaleFile, with a new marker).
        string zipB = BuildZip(Path.Combine(_root, "v2.zip"), extra: zip =>
        {
            var entry = zip.CreateEntry("NewMarker.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("new");
        });
        var second = ZipPackageInstaller.Install(zipB, installDir);

        Assert.True(second.Success);
        Assert.False(File.Exists(Path.Combine(installDir, "StaleFile.txt")));
        Assert.True(File.Exists(Path.Combine(installDir, "NewMarker.txt")));
    }

    [Fact]
    public void Install_MissingZip_FailsGracefully()
    {
        var result = ZipPackageInstaller.Install(
            Path.Combine(_root, "does-not-exist.zip"),
            Path.Combine(_root, "install-missing"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("not found"));
    }

    [Fact]
    public void GetInstalledVersion_ReturnsNullForEmptyOrMissingTrees()
    {
        Assert.Null(ZipPackageInstaller.GetInstalledVersion(Path.Combine(_root, "nope")));
        Assert.Null(ZipPackageInstaller.TryGetStagedVersion(Path.Combine(_root, "also-nope")));
    }
}
