using KaliteKit.Helpers;
using Microsoft.Win32;
using Xunit;

namespace KaliteKit.Tests.Helpers;

public class RegistryHelperTests
{
    private const string TestBasePath = @"HKEY_CURRENT_USER\Software\KaliteKit\Tests";

    [Fact]
    public void SetAndGetRegistryValue_StringValue_RoundTrips()
    {
        const string valueName = "TestString";
        const string expected = "Hello, World!";

        RegistryHelper.SetRegistryValue(TestBasePath, valueName, expected, RegistryValueKind.String);
        var result = RegistryHelper.GetRegistryValue(TestBasePath, valueName);

        Assert.Equal(expected, result);

        RegistryHelper.DeleteRegistryValue(TestBasePath, valueName);
    }

    [Fact]
    public void SetAndGetRegistryValue_DWordValue_RoundTrips()
    {
        const string valueName = "TestDWord";
        const int expected = 42;

        RegistryHelper.SetRegistryValue(TestBasePath, valueName, expected, RegistryValueKind.DWord);
        var result = RegistryHelper.GetRegistryValue(TestBasePath, valueName);

        Assert.Equal(expected, result);

        RegistryHelper.DeleteRegistryValue(TestBasePath, valueName);
    }

    [Fact]
    public void GetRegistryValue_NonExistentKey_ReturnsNull()
    {
        var result = RegistryHelper.GetRegistryValue(@"HKEY_CURRENT_USER\Software\KaliteKit\NonExistentKey12345", "Value");
        Assert.Null(result);
    }

    [Fact]
    public void DeleteRegistryValue_NonExistentValue_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            RegistryHelper.DeleteRegistryValue(TestBasePath, "NonExistentValue12345"));

        Assert.Null(exception);
    }

    /// <summary>
    /// Regression: the helper used to drop the last path segment and operate on
    /// the PARENT key, so reads of real values (e.g. Win32PrioritySeparation
    /// under ...\PriorityControl) silently came back null. The value must be
    /// written to and read from the exact key named in the path.
    /// </summary>
    [Fact]
    public void SetAndGetRegistryValue_WritesToTheGivenKey_NotItsParent()
    {
        const string path = @"HKEY_CURRENT_USER\Software\KaliteKit\TestsNested";
        const string valueName = "NestedValue";
        const string expected = "nested";

        RegistryHelper.SetRegistryValue(path, valueName, expected, RegistryValueKind.String);

        // The value must live on the key named in the path, not on its parent.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\KaliteKit\TestsNested");
        Assert.Equal(expected, key?.GetValue(valueName));
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\KaliteKit");
        Assert.Null(parent?.GetValue(valueName));

        // And the helper's own read must find it too.
        Assert.Equal(expected, RegistryHelper.GetRegistryValue(path, valueName));

        RegistryHelper.DeleteRegistryValue(path, valueName);
    }

    [Fact]
    public void SetRegistryValue_InvalidKeyPath_ThrowsException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RegistryHelper.SetRegistryValue("InvalidKeyPath", "Value", "data", RegistryValueKind.String));
    }

    [Fact]
    public void OpenBaseKey_HKCU_AliasWorks()
    {
        const string path = @"HKCU\Software\KaliteKit\TestsAlias";
        const string valueName = "AliasTest";
        const string expected = "AliasWorks";

        RegistryHelper.SetRegistryValue(path, valueName, expected, RegistryValueKind.String);
        var result = RegistryHelper.GetRegistryValue(path, valueName);

        Assert.Equal(expected, result);

        RegistryHelper.DeleteRegistryValue(path, valueName);
    }
}
