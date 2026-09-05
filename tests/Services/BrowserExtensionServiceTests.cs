using KaliteKit.Services;

namespace KaliteKit.Tests.Services;

/// <summary>
/// Pure tests for BrowserExtensionService — the shared catalog and the
/// policies.json builder. Registry/profile writes are intentionally not
/// exercised (they would mutate the live machine).
/// </summary>
public class BrowserExtensionServiceTests
{
    [Fact]
    public void CreateDefaultExtensions_ReturnsFourPrivacyExtensions()
    {
        var extensions = BrowserExtensionService.CreateDefaultExtensions();

        Assert.Equal(4, extensions.Count);
        Assert.All(extensions, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.ChromeId));
            Assert.False(string.IsNullOrWhiteSpace(e.FirefoxId));
            Assert.StartsWith("https://", e.FirefoxUrl, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void CreateDefaultExtensions_ReturnsFreshInstances()
    {
        var first = BrowserExtensionService.CreateDefaultExtensions();
        var second = BrowserExtensionService.CreateDefaultExtensions();

        Assert.NotSame(first, second);
        Assert.NotSame(first[0], second[0]);
    }

    [Fact]
    public void BuildFirefoxPoliciesJson_ForceInstallsEveryExtension()
    {
        var json = BrowserExtensionService.BuildFirefoxPoliciesJson(
            BrowserExtensionService.CreateDefaultExtensions());

        Assert.Contains("\"policies\"", json);
        Assert.Contains("\"ExtensionSettings\"", json);
        Assert.Contains("\"installation_mode\": \"force_installed\"", json);
        Assert.Contains("\"install_url\"", json);
        Assert.Contains("uBlock0@raymondhill.net", json);
        Assert.Contains("sponsorBlocker@ajay.app", json);
    }

    [Fact]
    public void BuildFirefoxPoliciesJson_IsBalancedJson()
    {
        var json = BrowserExtensionService.BuildFirefoxPoliciesJson(
            BrowserExtensionService.CreateDefaultExtensions());

        int opens = json.Count(c => c == '{');
        int closes = json.Count(c => c == '}');
        Assert.Equal(opens, closes);
    }
}