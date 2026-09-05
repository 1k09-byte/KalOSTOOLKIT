using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaliteKit.Models;
using KaliteKit.Services;

namespace KaliteKit.Tests.Services;

/// <summary>
/// Backend contract tests for the driver stack: version comparison across
/// vendor numbering schemes, NVIDIA lookup-response parsing, and every
/// <see cref="DriverService.CheckForUpdateAsync"/> outcome the UI can render.
/// No network, WMI, or processes are touched.
/// </summary>
public class DriverServiceTests
{
    // ── Version comparison ────────────────────────────────────────────

    [Theory]
    [InlineData("32.0.15.5244", "552.44", 0)]   // WMI 4-group form ↔ marketing form
    [InlineData("32.0.15.7216", "572.16", 0)]
    [InlineData("32.0.15.6108", "561.08", 0)]
    [InlineData("32.0.15.5244", "566.36", -1)]  // installed older → update available
    [InlineData("32.0.15.6636", "552.44", 1)]   // installed newer
    [InlineData("31.0.15.5244", "552.44", 0)]   // leading groups are irrelevant
    public void Compare_NvidiaMapsWmiSuffixToMarketingNumber(string installed, string latest, int expected)
    {
        int? result = DriverVersionComparer.Compare("NVIDIA", installed, latest);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("31.0.101.5768", "32.0.101.6002", -1)]
    [InlineData("32.0.101.6002", "32.0.101.6002", 0)]
    [InlineData("33.0.101.6002", "32.0.101.6002", 1)]
    [InlineData("32.0.101.5768", "32.0.101.5768.1", -1)] // shorter side pads with zeros
    public void Compare_IntelUsesSegmentWiseNumericComparison(string installed, string latest, int expected)
    {
        int? result = DriverVersionComparer.Compare("Intel", installed, latest);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Compare_AmdReturnsNull_AdrenalinNumbersDoNotMapToWmi()
    {
        Assert.Null(DriverVersionComparer.Compare("AMD", "32.0.21013", "25.10.1"));
    }

    [Theory]
    [InlineData("NVIDIA", "Unknown", "566.36")]
    [InlineData("NVIDIA", "", "566.36")]
    [InlineData("Intel", "not a version", "32.0.101.6002")]
    public void Compare_UnparseableInputReturnsNullInsteadOfGuessing(string vendor, string installed, string latest)
    {
        Assert.Null(DriverVersionComparer.Compare(vendor, installed, latest));
    }

    // ── NVIDIA lookup response parsing ────────────────────────────────

    private const string NvidiaLookupJson = """
        {"IDS":[{"downloadInfo":{"Version":"566.36","DownloadURL":"https://us.download.nvidia.com/Windows/566.36/566.36-desktop-win10-win11-64bit-international-dch-whql.exe","ReleaseDateTime":"2024-12-05"}}]}
        """;

    [Fact]
    public void ParseLookupResponse_ExtractsVersionUrlAndDate()
    {
        var info = NvidiaDriverProvider.ParseLookupResponse(NvidiaLookupJson);

        Assert.NotNull(info);
        Assert.Equal("566.36", info!.Version);
        Assert.Contains("566.36-desktop-win10-win11-64bit", info.DownloadUrl);
        Assert.NotNull(info.ReleaseDate);
        Assert.Equal(2024, info.ReleaseDate!.Value.Year);
        Assert.Contains("566.36", info.DisplayString);
    }

    [Fact]
    public void ParseLookupResponse_ReturnsNullForJunkAndIncompletePayloads()
    {
        Assert.Null(NvidiaDriverProvider.ParseLookupResponse("<html>blocked</html>"));
        Assert.Null(NvidiaDriverProvider.ParseLookupResponse("""{"IDS":[]}"""));
    }

    /// <summary>The live API emits spaced JSON — the old compact-marker parse
    /// never matched, so this shape must parse to the newest entry.</summary>
    private const string NvidiaLookupJsonSpaced = """
        { "IDS" : [{ "downloadInfo": { "Success" : "1", "Name" : "GeForce%20Game%20Ready%20Driver",
          "Version" : "616.56", "GFE_DisplayVersion" : "11.0.8.299", "DisplayVersion" : "",
          "IsWHQL" : "1", "ReleaseDateTime" : "Wed Aug 26, 2026",
          "DetailsURL" : "https://www.nvidia.com/en-us/drivers/details/278153/",
          "DownloadURL" : "https://us.download.nvidia.com/Windows/616.56/616.56-desktop-win10-win11-64bit-international-dch-whql.exe" } }] }
        """;

    [Fact]
    public void ParseLookupResponse_HandlesSpacedLiveApiJson()
    {
        var info = NvidiaDriverProvider.ParseLookupResponse(NvidiaLookupJsonSpaced);

        Assert.NotNull(info);
        Assert.Equal("616.56", info!.Version);
        Assert.Contains("616.56-desktop-win10-win11-64bit", info.DownloadUrl);
        Assert.NotNull(info.ReleaseDate);
        Assert.Equal(2026, info.ReleaseDate!.Value.Year);
    }

    [Fact]
    public void ParseLookupVersions_ParsesEveryEntryNewestFirst()
    {
        const string json = """
            {"IDS":[
              {"downloadInfo":{"Version":"616.56","ReleaseDateTime":"Wed Aug 26, 2026","DownloadURL":"https://us.download.nvidia.com/Windows/616.56/616.56-desktop-win10-win11-64bit-international-dch-whql.exe"}},
              {"downloadInfo":{"Version":"580.97","ReleaseDateTime":"Wed Jul 01, 2026","DownloadURL":"https://us.download.nvidia.com/Windows/580.97/580.97-desktop-win10-win11-64bit-international-dch-whql.exe"}},
              {"downloadInfo":{"Version":"566.36","DownloadURL":"https://us.download.nvidia.com/Windows/566.36/566.36-desktop-win10-win11-64bit-international-dch-whql.exe"}}
            ]}
            """;

        var versions = NvidiaDriverProvider.ParseLookupVersions(json);

        Assert.Equal(3, versions.Count);
        Assert.Equal("616.56", versions[0].Version);
        Assert.Equal("580.97", versions[1].Version);
        Assert.Equal("566.36", versions[2].Version);
        Assert.All(versions, v => Assert.StartsWith("https://us.download.nvidia.com/Windows/", v.DownloadUrl));
        Assert.Equal("580.97", versions[1].ReleaseDate!.Value.Year is 2026 ? "580.97" : "wrong");
    }

    [Fact]
    public void ParseLookupVersions_SkipsEntriesWithoutDownloadUrl()
    {
        const string json = """
            {"IDS":[
              {"downloadInfo":{"Version":"616.56"}},
              {"downloadInfo":{"Version":"580.97","DownloadURL":"https://us.download.nvidia.com/Windows/580.97/x.exe"}}
            ]}
            """;

        var versions = NvidiaDriverProvider.ParseLookupVersions(json);

        // 616.56 has no URL — version-without-URL entries can't be installed.
        // Its segment leaks no URL to 580.97 either (segmenting by Version
        // occurrence guarantees that).
        var entry = Assert.Single(versions);
        Assert.Equal("580.97", entry.Version);
        Assert.Contains("580.97", entry.DownloadUrl);
    }

    // ── NVIDIA notebook (laptop) package selection ────────────────────

    [Fact]
    public void GetCuratedLatest_DesktopVariantPointsAtDesktopPackage()
    {
        var driver = NvidiaDriverProvider.GetCuratedLatest(isNotebook: false);

        Assert.Contains("-desktop-", driver.DownloadUrl);
        Assert.DoesNotContain("notebook", driver.DownloadUrl);
    }

    [Fact]
    public void GetCuratedLatest_NotebookVariantPointsAtNotebookPackage()
    {
        // NVIDIA's desktop installer refuses notebook hardware — the curated
        // fallback for laptops must resolve the notebook package family.
        var driver = NvidiaDriverProvider.GetCuratedLatest(isNotebook: true);

        Assert.Contains("-notebook-", driver.DownloadUrl);
        Assert.Contains("Notebook", driver.DisplayString);
    }

    [Fact]
    public void GetCuratedLatest_ParameterlessStaysDesktop()
    {
        // Existing callers (installer wizard, version-compare fallback) keep
        // desktop semantics unless they explicitly opt into the notebook variant.
        Assert.Contains("-desktop-", NvidiaDriverProvider.GetCuratedLatest().DownloadUrl);
    }

    // ── CheckForUpdateAsync outcomes ──────────────────────────────────

    private sealed class StubProvider : IDriverProvider
    {
        private readonly Func<GpuInfo, bool> _canHandle;
        private readonly DriverInfo? _latest;
        private readonly Exception? _failure;

        public StubProvider(string vendor, Func<GpuInfo, bool> canHandle, DriverInfo? latest = null, Exception? failure = null)
        {
            Vendor = vendor;
            _canHandle = canHandle;
            _latest = latest;
            _failure = failure;
        }

        public string Vendor { get; }

        public bool CanHandle(GpuInfo gpu) => _canHandle(gpu);

        public Task<DriverInfo?> GetLatestDriverAsync(GpuInfo gpu, CancellationToken cancellationToken = default)
        {
            if (_failure != null) return Task.FromException<DriverInfo?>(_failure);
            return Task.FromResult(_latest);
        }
    }

    private static GpuInfo GpuOfVendor(string vendor) => new()
    {
        Name = $"{vendor} Test GPU",
        DriverVersion = vendor == "Intel" ? "31.0.101.5768" : "32.0.15.5244",
        PnpDeviceId = vendor switch
        {
            "NVIDIA" => @"PCI\VEN_10DE&DEV_2684",
            "AMD" => @"PCI\VEN_1002&DEV_744C",
            "Intel" => @"PCI\VEN_8086&DEV_A780",
            _ => @"PCI\VEN_1234&DEV_5678",
        },
    };

    private static DriverService BuildService(params IDriverProvider[] providers)
    {
        var log = new LoggingService(new LogService());
        return new DriverService(
            providers,
            new DriverDownloadService(log),
            new DriverInstallService(log, new ProcessManager(log), new DriverDownloadService(log)),
            log);
    }

    [Fact]
    public async Task Check_NoProviderHandlesGpu_ReturnsUnsupported()
    {
        var service = BuildService(new StubProvider("NVIDIA", gpu => gpu.IsNvidia));

        var result = await service.CheckForUpdateAsync(GpuOfVendor("Matrox"));

        Assert.Equal(DriverStatus.Unsupported, result.Status);
        Assert.Null(result.LatestDriver);
    }

    [Fact]
    public async Task Check_LatestNewerThanInstalled_ReturnsUpdateAvailable()
    {
        var service = BuildService(new StubProvider(
            "NVIDIA",
            gpu => gpu.IsNvidia,
            latest: new DriverInfo { Version = "566.36", DownloadUrl = "https://example.com/dl" }));

        var result = await service.CheckForUpdateAsync(GpuOfVendor("NVIDIA"));

        Assert.Equal(DriverStatus.UpdateAvailable, result.Status);
        Assert.Equal("566.36", result.LatestDriver!.Version);
    }

    [Fact]
    public async Task Check_LatestMatchesInstalled_ReturnsUpToDate()
    {
        var service = BuildService(new StubProvider(
            "NVIDIA",
            gpu => gpu.IsNvidia,
            latest: new DriverInfo { Version = "552.44", DownloadUrl = "https://example.com/dl" }));

        var result = await service.CheckForUpdateAsync(GpuOfVendor("NVIDIA"));

        Assert.Equal(DriverStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Check_VersionsIncomparable_ReturnsUnknownButSurfacesLatest()
    {
        var service = BuildService(new StubProvider(
            "AMD",
            gpu => gpu.IsAmd,
            latest: new DriverInfo { Version = "25.10.1", DownloadUrl = "https://www.amd.com/en/support/download/drivers.html" }));

        var result = await service.CheckForUpdateAsync(GpuOfVendor("AMD"));

        Assert.Equal(DriverStatus.Unknown, result.Status);
        Assert.NotNull(result.LatestDriver);
    }

    [Fact]
    public async Task Check_ProviderFindsNothing_ReturnsError()
    {
        var service = BuildService(new StubProvider("Intel", gpu => gpu.IsIntel, latest: null));

        var result = await service.CheckForUpdateAsync(GpuOfVendor("Intel"));

        Assert.Equal(DriverStatus.Error, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Check_ProviderThrows_IsConvertedToErrorResult()
    {
        var service = BuildService(new StubProvider(
            "NVIDIA",
            gpu => gpu.IsNvidia,
            failure: new HttpRequestException("offline")));

        var result = await service.CheckForUpdateAsync(GpuOfVendor("NVIDIA"));

        Assert.Equal(DriverStatus.Error, result.Status);
        Assert.Contains("offline", result.Error);
    }

    [Fact]
    public async Task Check_FirstMatchingProviderWins()
    {
        var newer = new StubProvider("NVIDIA", gpu => gpu.IsNvidia,
            latest: new DriverInfo { Version = "580.88" });
        var older = new StubProvider("NVIDIA-generic", gpu => gpu.IsNvidia,
            latest: new DriverInfo { Version = "552.44" });

        var service = BuildService(newer, older);

        var result = await service.CheckForUpdateAsync(GpuOfVendor("NVIDIA"));

        Assert.Equal("580.88", result.LatestDriver!.Version);
        Assert.Same(newer, service.FindProvider(GpuOfVendor("NVIDIA")));
    }

    // ── Provider routing sanity ───────────────────────────────────────

    [Fact]
    public void VendorProviders_CanHandleOnlyTheirOwnVendors()
    {
        var nvidia = GpuOfVendor("NVIDIA");
        var amd = GpuOfVendor("AMD");
        var intel = GpuOfVendor("Intel");

        Assert.True(new NvidiaDriverProvider().CanHandle(nvidia));
        Assert.False(new NvidiaDriverProvider().CanHandle(intel));
        Assert.True(new AmdDriverProvider().CanHandle(amd));
        Assert.False(new AmdDriverProvider().CanHandle(nvidia));
        Assert.True(new IntelDriverProvider().CanHandle(intel));
        Assert.False(new IntelDriverProvider().CanHandle(amd));
    }

    [Fact]
    public async Task UpdateAsync_NonNvidiaGpu_OpensVendorPageWithoutDownloading()
    {
        var log = new LoggingService(new LogService());
        var service = new DriverService(
            Enumerable.Empty<IDriverProvider>(),
            new DriverDownloadService(log),
            new DriverInstallService(log, new ProcessManager(log), new DriverDownloadService(log)),
            log);

        // An unparseable URL keeps the test hermetic: OpenInBrowser refuses to
        // shell out, but the AMD path still completes as "handled".
        bool ok = await service.UpdateAsync(
            GpuOfVendor("AMD"),
            new DriverInfo { Version = "25.10.1", DownloadUrl = "not a url" });

        Assert.True(ok);
    }

    // ── Leftover cleanup (interrupted installs) ───────────────────────

    private static DriverService BuildServiceWithWorkDir(string workDir)
    {
        var log = new LoggingService(new LogService());
        return new DriverService(
            Enumerable.Empty<IDriverProvider>(),
            new DriverDownloadService(log),
            new DriverInstallService(log, new ProcessManager(log), new DriverDownloadService(log)),
            log,
            workDir);
    }

    [Fact]
    public void CleanStaleDownloads_RemovesOldLeftoversFromInterruptedInstalls()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "kalitekit-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDir);

            // A finished download + a partial one, plus a fully extracted folder
            // — all from a previous, interrupted session (old timestamps).
            var exe = Path.Combine(workDir, "nvidia-driver-566.36.exe");
            var tmp = Path.Combine(workDir, "nvidia-driver-566.36.exe.tmp");
            var extracted = Path.Combine(workDir, "extracted");
            File.WriteAllText(exe, "MZ...");
            File.WriteAllText(tmp, "MZ-partial");
            Directory.CreateDirectory(Path.Combine(extracted, "Display.Driver"));
            File.WriteAllText(Path.Combine(extracted, "Display.Driver", "nv_disp.inf"), "[strings]");
            File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddHours(-2));
            Directory.SetLastWriteTimeUtc(extracted, DateTime.UtcNow.AddHours(-2));

            BuildServiceWithWorkDir(workDir).CleanStaleDownloads(minAge: TimeSpan.FromMinutes(30));

            Assert.False(File.Exists(exe));
            Assert.False(File.Exists(tmp));
            Assert.False(Directory.Exists(extracted));
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CleanStaleDownloads_KeepsFreshFiles_SoInFlightInstallsAreSafe()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "kalitekit-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDir);

            var exe = Path.Combine(workDir, "nvidia-driver-566.36.exe");
            File.WriteAllText(exe, "MZ..."); // fresh — an install may be using it

            BuildServiceWithWorkDir(workDir).CleanStaleDownloads(minAge: TimeSpan.FromMinutes(30));

            Assert.True(File.Exists(exe));
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }
}
