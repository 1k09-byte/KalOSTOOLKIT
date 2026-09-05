using System;
using System.IO;
using System.Linq;
using KaliteKit.Services;

namespace KaliteKit.Tests.Services;

/// <summary>
/// Tests for the clean-install pipeline in <see cref="DriverInstallService"/>:
/// package stripping, locale-proof pnputil output parsing, newest-package
/// selection, and display INF discovery. No real installs or driver-store
/// mutations happen — everything runs against temp directories and strings.
/// </summary>
public class DriverInstallServiceTests : IDisposable
{
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly string _tempDir;
    private readonly DriverInstallService _service;

    public DriverInstallServiceTests()
    {
        var log = new LoggingService(new LogService());
        _service = new DriverInstallService(
            log,
            new ProcessManager(log),
            new DriverDownloadService(log));
        _tempDir = Path.Combine(Path.GetTempPath(), "kalitekit-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // ── Package stripping ─────────────────────────────────────────────

    [Fact]
    public void SelectiveExtractIncludes_CoversNvidiaAndAmdDisplayLayouts()
    {
        // Regression guard: if a layout mask ever drops out, the selective
        // extraction silently extracts nothing and installs fall back to the
        // full (storage-heavy) path.
        Assert.Contains("Display.Driver\\*", DriverInstallService.SelectiveExtractIncludes);
        Assert.Contains("NVI2\\*", DriverInstallService.SelectiveExtractIncludes);
        Assert.Contains("Packages\\Drivers\\Display\\*", DriverInstallService.SelectiveExtractIncludes);
        Assert.Contains("Drivers\\Display\\*", DriverInstallService.SelectiveExtractIncludes);
        Assert.Contains("setup.cfg", DriverInstallService.SelectiveExtractIncludes);
        Assert.Contains("setup.exe", DriverInstallService.SelectiveExtractIncludes);
        Assert.Contains("ListDevices.txt", DriverInstallService.SelectiveExtractIncludes);
    }

    [Fact]
    public void StripPackageContents_DeletesBloatButKeepsAllowlist()
    {
        var extractDir = Path.Combine(_tempDir, "pkg");
        Directory.CreateDirectory(Path.Combine(extractDir, "Display.Driver"));
        Directory.CreateDirectory(Path.Combine(extractDir, "HDAudio"));
        Directory.CreateDirectory(Path.Combine(extractDir, "NVI2", "UI"));
        Directory.CreateDirectory(Path.Combine(extractDir, "PhysX"));
        File.WriteAllText(Path.Combine(extractDir, "Display.Driver", "nv_disp.inf"), "[strings]");
        File.WriteAllText(Path.Combine(extractDir, "setup.exe"), "MZ");
        File.WriteAllText(Path.Combine(extractDir, "setup.cfg"), "[config]");
        File.WriteAllText(Path.Combine(extractDir, "ListDevices.txt"), "devices");
        File.WriteAllText(Path.Combine(extractDir, "EULA.txt"), "legal");
        File.WriteAllText(Path.Combine(extractDir, "license.txt"), "legal2");

        _service.StripPackageContents(extractDir);

        // Allowlisted items survive
        Assert.True(Directory.Exists(Path.Combine(extractDir, "Display.Driver")));
        Assert.True(File.Exists(Path.Combine(extractDir, "Display.Driver", "nv_disp.inf")));
        Assert.True(Directory.Exists(Path.Combine(extractDir, "NVI2")));
        Assert.True(Directory.Exists(Path.Combine(extractDir, "NVI2", "UI")));
        Assert.True(File.Exists(Path.Combine(extractDir, "setup.exe")));
        Assert.True(File.Exists(Path.Combine(extractDir, "setup.cfg")));
        Assert.True(File.Exists(Path.Combine(extractDir, "ListDevices.txt")));

        // Bloat is deleted
        Assert.False(Directory.Exists(Path.Combine(extractDir, "HDAudio")));
        Assert.False(Directory.Exists(Path.Combine(extractDir, "PhysX")));
        Assert.False(File.Exists(Path.Combine(extractDir, "EULA.txt")));
        Assert.False(File.Exists(Path.Combine(extractDir, "license.txt")));
    }

    [Fact]
    public void StripPackageContents_ToleratesMissingDirectory()
    {
        _service.StripPackageContents(Path.Combine(_tempDir, "does-not-exist"));
    }

    [Fact]
    public void StripPackageContents_KeepsSelectedComponentsAndStripsRest()
    {
        var extractDir = Path.Combine(_tempDir, "pkg2");
        Directory.CreateDirectory(Path.Combine(extractDir, "Display.Driver"));
        Directory.CreateDirectory(Path.Combine(extractDir, "HDAudio"));
        Directory.CreateDirectory(Path.Combine(extractDir, "PhysX"));
        Directory.CreateDirectory(Path.Combine(extractDir, "GFExperience"));
        Directory.CreateDirectory(Path.Combine(extractDir, "NVIDIA App"));
        Directory.CreateDirectory(Path.Combine(extractDir, "EULA"));

        _service.StripPackageContents(extractDir, new NvidiaInstallComponents
        {
            KeepHDAudio = true,
            KeepPhysX = true,
        });

        // Selected components survive
        Assert.True(Directory.Exists(Path.Combine(extractDir, "HDAudio")));
        Assert.True(Directory.Exists(Path.Combine(extractDir, "PhysX")));
        // The display driver is always kept
        Assert.True(Directory.Exists(Path.Combine(extractDir, "Display.Driver")));
        // Unselected components are stripped
        Assert.False(Directory.Exists(Path.Combine(extractDir, "GFExperience")));
        Assert.False(Directory.Exists(Path.Combine(extractDir, "NVIDIA App")));
        Assert.False(Directory.Exists(Path.Combine(extractDir, "EULA")));
    }

    [Fact]
    public void StripAmdPackageContents_KeepsSelectedComponentsAndStripsRest()
    {
        var extractDir = Path.Combine(_tempDir, "amd");
        Directory.CreateDirectory(Path.Combine(extractDir, "Packages", "Drivers", "Display"));
        Directory.CreateDirectory(Path.Combine(extractDir, "Packages", "Drivers", "Display2"));
        Directory.CreateDirectory(Path.Combine(extractDir, "Packages", "Drivers", "Audio"));
        Directory.CreateDirectory(Path.Combine(extractDir, "Packages", "CNext"));     // Adrenalin UI
        Directory.CreateDirectory(Path.Combine(extractDir, "Packages", "UEP"));      // telemetry
        Directory.CreateDirectory(Path.Combine(extractDir, "Packages", "Branding")); // bloat
        File.WriteAllText(Path.Combine(extractDir, "Packages", "Drivers", "Display", "u0xxx.inf"), "x");
        Directory.CreateDirectory(Path.Combine(extractDir, "Config"));
        File.WriteAllText(Path.Combine(extractDir, "Config", "InstallManifest.json"), "{}");

        _service.StripAmdPackageContents(extractDir, new AmdInstallComponents
        {
            KeepRadeonSoftware = true,
            KeepAudio = true,
        });

        // The display driver is always kept
        Assert.True(Directory.Exists(Path.Combine(extractDir, "Packages", "Drivers", "Display")));
        Assert.True(Directory.Exists(Path.Combine(extractDir, "Packages", "Drivers", "Display2")));
        // Selected components survive
        Assert.True(Directory.Exists(Path.Combine(extractDir, "Packages", "Drivers", "Audio")));
        Assert.True(Directory.Exists(Path.Combine(extractDir, "Packages", "CNext")));
        // Unselected components are stripped
        Assert.False(Directory.Exists(Path.Combine(extractDir, "Packages", "UEP")));
        Assert.False(Directory.Exists(Path.Combine(extractDir, "Packages", "Branding")));
    }

    // ── AMD strip ─────────────────────────────────────────────────────

    [Fact]
    public void StripAmdPackageContents_KeepsDisplayDriverOnly()
    {
        var root = Path.Combine(_tempDir, "amd-pkg");
        var displayDir = Path.Combine(root, "Packages", "Drivers", "Display", "WT6A_INF");
        Directory.CreateDirectory(displayDir);
        Directory.CreateDirectory(Path.Combine(root, "Packages", "Drivers", "Audio"));
        Directory.CreateDirectory(Path.Combine(root, "Packages", "Apps", "CNext"));
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        File.WriteAllText(Path.Combine(displayDir, "u0123456.inf"), "[strings]");
        File.WriteAllText(Path.Combine(root, "Setup.exe"), "MZ");
        File.WriteAllText(Path.Combine(root, "Branding.png"), "img");

        _service.StripAmdPackageContents(root);

        // Display driver content survives
        Assert.True(Directory.Exists(displayDir));
        Assert.True(File.Exists(Path.Combine(displayDir, "u0123456.inf")));
        Assert.True(Directory.Exists(Path.Combine(root, "Config")));

        // Audio and CNext are stripped
        Assert.False(Directory.Exists(Path.Combine(root, "Packages", "Drivers", "Audio")));
        Assert.False(Directory.Exists(Path.Combine(root, "Packages", "Apps")));
        Assert.False(File.Exists(Path.Combine(root, "Branding.png")));
    }

    // ── AMD display INF discovery ─────────────────────────────────────

    [Fact]
    public void FindAmdDisplayInf_LocatesInfInPackagesDriversDisplay()
    {
        var root = Path.Combine(_tempDir, "amd-inf");
        var infPath = Path.Combine(root, "Packages", "Drivers", "Display", "WT6A_INF", "u0123456.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(infPath)!);
        File.WriteAllText(infPath, "[strings]");

        Assert.Equal(infPath, DriverInstallService.FindAmdDisplayInf(root));
    }

    [Fact]
    public void FindAmdDisplayInf_ReturnsNullWhenNothingMatches()
    {
        var root = Path.Combine(_tempDir, "amd-inf-empty");
        Directory.CreateDirectory(root);

        Assert.Null(DriverInstallService.FindAmdDisplayInf(root));
        Assert.Null(DriverInstallService.FindAmdDisplayInf(Path.Combine(_tempDir, "never-created")));
    }

    // ── pnputil /enum-drivers parsing ─────────────────────────────────

    private const string EnumDriversEnUs = """
        Published Name:     oem7.inf
        Original Name:      nv_disp.inf
        Provider Name:      NVIDIA
        Class Name:         Display adapters
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Driver Version:     08/03/2024 32.0.15.5244
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem21.inf
        Original Name:      nv_disp.inf
        Provider Name:      NVIDIA
        Class Name:         Display adapters
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Driver Version:     12/05/2024 32.0.15.6636
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem9.inf
        Original Name:      u0139329.inf
        Provider Name:      AMD
        Class Name:         Display adapters
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Driver Version:     08/08/2024 31.0.24033.1002
        Signer Name:        Advanced Micro Devices, Inc.

        Published Name:     oem14.inf
        Original Name:      nvhm.inf
        Provider Name:      NVIDIA
        Class Name:         System devices
        Class GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}
        Driver Version:     08/03/2024 32.0.15.5244
        """;

    [Fact]
    public void ParseDriverPackages_ExtractsAllBlocks()
    {
        var packages = DriverInstallService.ParseDriverPackages(EnumDriversEnUs);

        Assert.Equal(4, packages.Count);
        Assert.All(packages, p => Assert.Matches(@"^oem\d+\.inf$", p.PublishedName));
    }

    [Fact]
    public void ParseDriverPackages_FlagsOnlyNvidiaBlocks()
    {
        var byPublished = DriverInstallService.ParseDriverPackages(EnumDriversEnUs)
            .ToDictionary(p => p.PublishedName);

        Assert.True(byPublished["oem7.inf"].IsNvidia);
        Assert.True(byPublished["oem21.inf"].IsNvidia);
        Assert.False(byPublished["oem9.inf"].IsNvidia);   // AMD display package
        Assert.True(byPublished["oem14.inf"].IsNvidia);   // NVIDIA but non-display class
    }

    [Fact]
    public void ParseDriverPackages_KeepsOriginalNameAndGuid()
    {
        var oem7 = DriverInstallService.ParseDriverPackages(EnumDriversEnUs)
            .Single(p => p.PublishedName == "oem7.inf");

        Assert.Equal("nv_disp.inf", oem7.OriginalName);
        Assert.Equal(DisplayClassGuid, oem7.ClassGuid);
        Assert.Equal(new[] { 32, 0, 15, 5244 }, oem7.Version);
    }

    [Fact]
    public void ParseDriverPackages_HandlesDotFormattedDates_LocaleProof()
    {
        // German-style pnputil prints the date with dots before the version on
        // the same line; the parser must pick the LAST dotted number (the actual
        // driver version), not the date.
        const string germanBlock = """
            Veröffentlichter Name:    oem5.inf
            Originalname:             nv_dispi.inf
            Anbietername:             NVIDIA
            Klassenname:              Anzeigegeräte
            Klassen-GUID:             {4D36E968-E325-11CE-BFC1-08002BE10318}
            Treiberversion:           08.08.2024 32.0.15.6094
            """;

        var package = DriverInstallService.ParseDriverPackages(germanBlock).Single();

        Assert.Equal(new[] { 32, 0, 15, 6094 }, package.Version);
        Assert.Equal(DisplayClassGuid, package.ClassGuid); // GUID match is case-insensitive downstream
        Assert.Equal("nv_dispi.inf", package.OriginalName);
    }

    [Fact]
    public void ParseDriverPackages_IgnoresTextWithoutPublishedNames()
    {
        Assert.Empty(DriverInstallService.ParseDriverPackages("no drivers\r\nfound at all"));
        Assert.Empty(DriverInstallService.ParseDriverPackages(""));
    }

    // ── Newest-package selection ──────────────────────────────────────

    [Fact]
    public void PickNewest_KeepsHighestVersion()
    {
        var packages = DriverInstallService.ParseDriverPackages(EnumDriversEnUs)
            .Where(p => p.IsNvidia && string.Equals(p.ClassGuid, DisplayClassGuid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var keep = DriverInstallService.PickNewest(packages);

        Assert.NotNull(keep);
        Assert.Equal("oem21.inf", keep!.PublishedName);   // 32.0.15.6636 beats 5244
        Assert.Single(packages, p => !p.PublishedName.Equals(keep.PublishedName)); // exactly one to delete
    }

    [Fact]
    public void PickNewest_EmptyListReturnsNull()
    {
        Assert.Null(DriverInstallService.PickNewest(System.Array.Empty<ParsedDriverPackage>()));
    }

    // ── Display INF discovery ─────────────────────────────────────────

    [Fact]
    public void FindDisplayInf_PrefersNvDispInDisplayDriver()
    {
        var root = Path.Combine(_tempDir, "inf-a");
        var infPath = Path.Combine(root, "Display.Driver", "nv_disp.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(infPath)!);
        File.WriteAllText(infPath, "");

        Assert.Equal(infPath, DriverInstallService.FindDisplayInf(root));
    }

    [Fact]
    public void FindDisplayInf_AcceptsNvDispiVariant()
    {
        var root = Path.Combine(_tempDir, "inf-b");
        var infPath = Path.Combine(root, "Display.Driver", "nv_dispi.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(infPath)!);
        File.WriteAllText(infPath, "");

        Assert.Equal(infPath, DriverInstallService.FindDisplayInf(root));
    }

    [Fact]
    public void FindDisplayInf_SearchesRecursivelyWhenNotAtRoot()
    {
        var root = Path.Combine(_tempDir, "inf-c");
        var infPath = Path.Combine(root, "unexpected", "subfolder", "nv_disp.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(infPath)!);
        File.WriteAllText(infPath, "");

        Assert.Equal(infPath, DriverInstallService.FindDisplayInf(root));
    }

    [Fact]
    public void FindDisplayInf_ReturnsNullWhenNothingMatches()
    {
        var root = Path.Combine(_tempDir, "inf-d");
        Directory.CreateDirectory(root);

        Assert.Null(DriverInstallService.FindDisplayInf(root));
        Assert.Null(DriverInstallService.FindDisplayInf(Path.Combine(_tempDir, "never-created")));
    }

    [Fact]
    public void FindNvidiaDisplayInfs_YieldsEveryCandidatePreferredFirst()
    {
        var root = Path.Combine(_tempDir, "inf-e");
        var disp = Path.Combine(root, "Display.Driver");
        var nested = Path.Combine(root, "Other", "Drivers");
        Directory.CreateDirectory(disp);
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(disp, "nv_disp.inf"), "");
        File.WriteAllText(Path.Combine(disp, "nv_dispi.inf"), "");
        File.WriteAllText(Path.Combine(nested, "nv_disp.inf"), "");

        var candidates = DriverInstallService.FindNvidiaDisplayInfs(root).ToList();

        // Preferred order: Display.Driver\nv_disp.inf, Display.Driver\nv_dispi.inf,
        // then the recursive finds. No duplicates from the recursive pass.
        Assert.Equal(new[]
        {
            Path.Combine(disp, "nv_disp.inf"),
            Path.Combine(disp, "nv_dispi.inf"),
            Path.Combine(nested, "nv_disp.inf"),
        }, candidates);
    }

    [Fact]
    public void FindNvidiaDisplayInfs_ReturnsEmptyWhenNothingMatches()
    {
        var root = Path.Combine(_tempDir, "inf-f");
        Directory.CreateDirectory(root);

        Assert.Empty(DriverInstallService.FindNvidiaDisplayInfs(root));
        Assert.Empty(DriverInstallService.FindNvidiaDisplayInfs(Path.Combine(_tempDir, "never-created")));
    }
}
