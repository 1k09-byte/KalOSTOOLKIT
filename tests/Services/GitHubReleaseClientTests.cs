using KaliteKit.Services;

namespace KaliteKit.Tests.Services;

/// <summary>
/// Backend contract tests for <see cref="GitHubReleaseClient"/>: tag parsing
/// from the /releases/latest redirect, and zip-asset selection from the
/// expanded-assets fragment — including the rule that a release carrying BOTH
/// the app zip and the KaliteKit-Setup wizard payload must never make the installer
/// download itself. No network is touched.
/// </summary>
public class GitHubReleaseClientTests
{
    // ── Tag parsing ───────────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/1k09-byte/KaliteKit/releases/tag/v1.0.0.6", "v1.0.0.6")]
    [InlineData("/1k09-byte/KaliteKit/releases/tag/v1.2.3", "v1.2.3")]
    [InlineData("/1k09-byte/KaliteKit/releases/tag/1.2.3", "v1.2.3")]
    [InlineData("https://github.com/1k09-byte/KaliteKit/releases/tag/v1.0.0.6/", "v1.0.0.6")]
    public void ParseTagFromRedirect_ExtractsTagWithVPrefix(string location, string expected)
    {
        Assert.Equal(expected, GitHubReleaseClient.ParseTagFromRedirect(location));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://github.com/1k09-byte/KaliteKit/releases")]
    [InlineData("https://example.com/anything")]
    public void ParseTagFromRedirect_ReturnsNullForNonTagLocations(string location)
    {
        Assert.Null(GitHubReleaseClient.ParseTagFromRedirect(location));
    }

    [Fact]
    public void ParseTagFromRedirect_NullLocationReturnsNull()
    {
        Assert.Null(GitHubReleaseClient.ParseTagFromRedirect(null!));
    }

    // ── Asset selection ───────────────────────────────────────────────

    private const string BaseHref = "/1k09-byte/KaliteKit/releases/download";

    [Fact]
    public void SelectZipAssetUrl_PrefersTheVersionedAppZip()
    {
        string html = $"""
            <a href="{BaseHref}/v1.0.0.6/KaliteKit.zip">KaliteKit.zip</a>
            <a href="{BaseHref}/v1.0.0.6/KaliteKit-v1.0.0.6-win-x64.zip">KaliteKit-v1.0.0.6-win-x64.zip</a>
            """;

        var url = GitHubReleaseClient.SelectZipAssetUrl(html, "v1.0.0.6");

        Assert.NotNull(url);
        Assert.Contains("KaliteKit-v1.0.0.6-win-x64.zip", url);
        Assert.StartsWith("https://github.com", url);
    }

    [Fact]
    public void SelectZipAssetUrl_FallsBackToPlainKaliteKitZip()
    {
        string html = $"""<a href="{BaseHref}/v1.0.0.6/KaliteKit.zip">KaliteKit.zip</a>""";

        Assert.Contains("/KaliteKit.zip", GitHubReleaseClient.SelectZipAssetUrl(html, "v1.0.0.6"));
    }

    [Fact]
    public void SelectZipAssetUrl_NeverDownloadsTheSetupPayload()
    {
        // A release that ships the wizard payload alongside (or instead of)
        // the app zip: the installer must never select its own artifact.
        string html = $"""
            <a href="{BaseHref}/v1.0.0.6/KaliteKit-Setup-v1.0.0.6-win-x64.zip">setup</a>
            """;

        Assert.Null(GitHubReleaseClient.SelectZipAssetUrl(html, "v1.0.0.6"));
    }

    [Fact]
    public void SelectZipAssetUrl_PicksAppZipOverSetupWhenBothPresent()
    {
        string html = $"""
            <a href="{BaseHref}/v1.0.0.6/KaliteKit-Setup-v1.0.0.6-win-x64.zip">setup</a>
            <a href="{BaseHref}/v1.0.0.6/KaliteKit.zip">app</a>
            """;

        var url = GitHubReleaseClient.SelectZipAssetUrl(html, "v1.0.0.6");

        Assert.NotNull(url);
        Assert.DoesNotContain("Setup", url, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/KaliteKit.zip", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectZipAssetUrl_TakesAnyOtherNonSetupZipAsLastResort()
    {
        string html = $"""<a href="{BaseHref}/v1.0.0.6/KaliteKit-v1.0.0.6-win-arm64.zip">arm</a>""";

        var url = GitHubReleaseClient.SelectZipAssetUrl(html, "v1.0.0.6");

        Assert.NotNull(url);
        Assert.Contains("win-arm64", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>No assets</body></html>")]
    [InlineData("""<a href="/1k09-byte/KaliteKit/releases/download/v1.0.0.6/notes.txt">notes</a>""")]
    public void SelectZipAssetUrl_ReturnsNullWithoutZipAssets(string html)
    {
        Assert.Null(GitHubReleaseClient.SelectZipAssetUrl(html, "v1.0.0.6"));
    }
}
