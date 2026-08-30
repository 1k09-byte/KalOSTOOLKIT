using System;
using System.Collections.Generic;
using KalOS.Services;

namespace KalOS.Tests.Services;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.0.4", "1.0.0.4")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v2.3.4", "2.3.4")]
    [InlineData("V1.0.0.9", "1.0.0.9")]
    public void TryParseReleaseVersion_ParsesTags(string tag, string expected)
    {
        Assert.True(UpdateService.TryParseReleaseVersion(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseReleaseVersion_RejectsInvalidTags(string? tag)
    {
        Assert.False(UpdateService.TryParseReleaseVersion(tag, out _));
    }

    [Theory]
    [InlineData("1.0.0.4", "1.0.0.3", true)]   // upgrade
    [InlineData("1.0.0.3", "1.0.0.3", false)]  // same version: no update
    [InlineData("1.0.0.2", "1.0.0.3", true)]   // downgrade after version reset: still offered
    [InlineData("2.0.0.0", "1.9.9.9", true)]
    public void IsNewer_OffersUpdateWheneverVersionDiffers(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(Version.Parse(latest), Version.Parse(current)));
    }

    [Fact]
    public void SelectZipAsset_PrefersExactVersionedPackage()
    {
        var assets = new List<ReleaseAsset>
        {
            new("KalOS-v1.0.0.4-setup.zip", "https://example.com/setup.zip"),
            new("KalOS-v1.0.0.4-win-x64.zip", "https://example.com/exact.zip"),
            new("readme.txt", "https://example.com/readme.txt")
        };

        Assert.Equal("https://example.com/exact.zip",
            UpdateService.SelectZipAsset(assets, Version.Parse("1.0.0.4")));
    }

    [Fact]
    public void SelectZipAsset_FallsBackToAnyKalOsZip()
    {
        var assets = new List<ReleaseAsset>
        {
            new("KalOS-1.0.0.4.zip", "https://example.com/fallback.zip"),
            new("notes.txt", "https://example.com/notes.txt")
        };

        Assert.Equal("https://example.com/fallback.zip",
            UpdateService.SelectZipAsset(assets, Version.Parse("1.0.0.4")));
    }

    [Fact]
    public void SelectZipAsset_ReturnsNullWithoutPackage()
    {
        var assets = new List<ReleaseAsset> { new("readme.txt", "https://example.com/readme.txt") };
        Assert.Null(UpdateService.SelectZipAsset(assets, Version.Parse("1.0.0.4")));
    }

    [Fact]
    public void ParseRelease_ReturnsUpdateInfoForNewerVersionWithPackage()
    {
        string json = """
        {
          "tag_name": "v1.0.0.4",
          "html_url": "https://github.com/1k09-byte/KalOSTOOLKIT/releases/tag/v1.0.0.4",
          "assets": [
            { "name": "KalOS-v1.0.0.4-win-x64.zip", "browser_download_url": "https://example.com/pkg.zip" },
            { "name": "checksums.txt", "browser_download_url": "https://example.com/checksums.txt" }
          ]
        }
        """;

        var info = UpdateService.ParseRelease(json, Version.Parse("1.0.0.3"));

        Assert.NotNull(info);
        Assert.Equal(Version.Parse("1.0.0.4"), info!.Version);
        Assert.Equal("v1.0.0.4", info.Tag);
        Assert.Equal("https://example.com/pkg.zip", info.ZipAssetUrl);
    }

    [Fact]
    public void ParseRelease_OffersDowngradeWhenLatestIsLower()
    {
        // After the version line was reset to 1.0.0.1, installs on 1.0.0.7
        // must still detect the latest release (lower version) as an update.
        string json = """
        {
          "tag_name": "v1.0.0.1",
          "assets": [ { "name": "KalOS-v1.0.0.1-win-x64.zip", "browser_download_url": "https://example.com/pkg.zip" } ]
        }
        """;

        var info = UpdateService.ParseRelease(json, Version.Parse("1.0.0.7"));

        Assert.NotNull(info);
        Assert.Equal(Version.Parse("1.0.0.1"), info!.Version);
    }

    [Fact]
    public void ParseRelease_ReturnsNullWhenSameVersion()
    {
        string json = """
        {
          "tag_name": "v1.0.0.3",
          "assets": [ { "name": "KalOS-v1.0.0.3-win-x64.zip", "browser_download_url": "https://example.com/pkg.zip" } ]
        }
        """;

        Assert.Null(UpdateService.ParseRelease(json, Version.Parse("1.0.0.3")));
    }

    [Fact]
    public void ParseRelease_ReturnsNullWhenNoPackageAsset()
    {
        string json = """
        {
          "tag_name": "v1.0.0.4",
          "assets": [ { "name": "checksums.txt", "browser_download_url": "https://example.com/checksums.txt" } ]
        }
        """;

        Assert.Null(UpdateService.ParseRelease(json, Version.Parse("1.0.0.3")));
    }

    [Fact]
    public void ParseRelease_ReturnsNullForInvalidTag()
    {
        string json = """{ "tag_name": "release-candidate", "assets": [] }""";
        Assert.Null(UpdateService.ParseRelease(json, Version.Parse("1.0.0.3")));
    }

    [Fact]
    public void ParseReleaseHistory_SortsNewestFirstAndMarksCurrent()
    {
        string json = """
        [
          { "tag_name": "v1.0.0.4", "name": "v1.0.0.4", "published_at": "2026-08-29T10:00:00Z", "html_url": "https://github.com/1k09-byte/KalOSTOOLKIT/releases/tag/v1.0.0.4", "body": "Fixed the thing" },
          { "tag_name": "v1.0.0.3", "name": "v1.0.0.3", "published_at": "2026-08-28T10:00:00Z", "html_url": "https://github.com/1k09-byte/KalOSTOOLKIT/releases/tag/v1.0.0.3", "body": "" },
          { "tag_name": "v1.0.0.5", "name": "v1.0.0.5", "published_at": "2026-08-30T10:00:00Z", "html_url": "https://github.com/1k09-byte/KalOSTOOLKIT/releases/tag/v1.0.0.5", "body": "Newest" }
        ]
        """;

        var history = UpdateService.ParseReleaseHistory(json, Version.Parse("1.0.0.3"));

        Assert.Equal(3, history.Count);
        Assert.Equal(Version.Parse("1.0.0.5"), history[0].Version);   // newest first
        Assert.Equal(Version.Parse("1.0.0.4"), history[1].Version);
        Assert.Equal(Version.Parse("1.0.0.3"), history[2].Version);
        Assert.True(history[2].IsCurrent);                            // running build marked
        Assert.False(history[1].IsCurrent);
        Assert.NotNull(history[0].PublishedAt);
        Assert.Equal("Fixed the thing", history[1].Notes);
    }

    [Fact]
    public void ParseReleaseHistory_SkipsInvalidTagsAndNonArrayPayloads()
    {
        string json = """[ { "tag_name": "nightly", "published_at": "2026-08-29T10:00:00Z" } ]""";
        Assert.Empty(UpdateService.ParseReleaseHistory(json, Version.Parse("1.0.0.3")));

        Assert.Empty(UpdateService.ParseReleaseHistory("{}", Version.Parse("1.0.0.3")));
    }

    [Fact]
    public void ParseReleaseHistory_StripsDiscordMarkerFromNotes()
    {
        string json = """[ { "tag_name": "v1.0.0.4", "body": "<!-- discord-msg:12 -->\nFixed MMCSS" } ]""";

        var history = UpdateService.ParseReleaseHistory(json, Version.Parse("1.0.0.3"));

        Assert.Single(history);
        Assert.Equal("Fixed MMCSS", history[0].Notes);
    }

    [Fact]
    public void BuildApplyScript_WaitsForOldProcessAndRelocatesFiles()
    {
        string script = UpdateService.BuildApplyScript(
            1234, @"C:\temp\v1.0.0.4", @"C:\Apps\KalOS", @"C:\temp\KalOS-1.0.0.4.zip", @"C:\temp\update.log");

        Assert.Contains("Get-Process -Id 1234", script);
        Assert.Contains("WaitForExit", script);
        Assert.Contains("Copy-Item", script);
        Assert.Contains(@"'C:\Apps\KalOS'", script);
        Assert.Contains("Start-Process", script);
        Assert.Contains("KalOS.exe", script);
    }
}
