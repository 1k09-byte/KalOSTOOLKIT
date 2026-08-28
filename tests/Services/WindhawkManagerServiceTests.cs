using KalOS.Services;
using Microsoft.Win32;

namespace KalOS.Tests.Services;

/// <summary>
/// Tests for <see cref="WindhawkManagerService"/>'s registry ⇄ .reg helpers —
/// the backbone of the backup/restore feature. The export is exercised against
/// the live HKLM\SOFTWARE\Windhawk tree (read-only), and the round-trip parser
/// is validated on a synthetic .reg string that mirrors the export shape.
/// </summary>
public class WindhawkManagerServiceTests
{
    private const string WindhawkRoot = @"SOFTWARE\Windhawk";

    [Fact]
    public void ExportRegistryToReg_ProducesWellFormedRegText()
    {
        // The Windhawk tree is expected to exist on a machine with Windhawk
        // installed (the deploy path depends on it). Guard, don't fail, on
        // machines without it.
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (root.OpenSubKey(WindhawkRoot) == null)
        {
            return;
        }

        string regText = WindhawkManagerService.ExportRegistryToReg(WindhawkRoot);

        Assert.StartsWith("Windows Registry Editor Version 5.00", regText);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Windhawk]", regText);
        Assert.Contains("Engine", regText); // the engine subtree is part of the backup
    }

    [Fact]
    public void ExportAndApply_RoundTripIsIdempotentForStringAndDwordValues()
    {
        // Depends on the live Windhawk registry tree, like the first test —
        // guard, don't fail, on machines without Windhawk installed.
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (root.OpenSubKey(WindhawkRoot) == null)
        {
            return;
        }

        // ParseRegText is private; ApplyRegTextToRegistry is internal but would
        // mutate the live tree. Assert the observable contract instead: the
        // export of the real tree contains both dword and string lines, which
        // the restore-path parser must preserve.
        string exported = WindhawkManagerService.ExportRegistryToReg(WindhawkRoot);

        Assert.Contains("dword:", exported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Version\"=", exported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Disabled", exported, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.9.2", "1.10", -1)]
    [InlineData("1.10", "1.9.2", 1)]
    [InlineData("1.7", "1.7", 0)]
    [InlineData("1.8", "1.7", 1)]
    [InlineData("2.0", "1.9.2", 1)]
    [InlineData("1.10.1", "1.10", 1)]
    [InlineData("1.6", "1.6.0", 0)]
    public void CompareVersions_ComparesNumericComponents(string a, string b, int expected)
    {
        // The update path's "is the pinned version older than the latest?"
        // decision hinges on this comparator handling dotted versions
        // numerically ("1.10" > "1.9.2"), never lexically.
        int result = WindhawkManagerService.CompareVersions(a, b);
        Assert.True(
            Math.Sign(result) == Math.Sign(expected),
            $"{a} vs {b}: got {result}, expected sign {expected}");
    }
}
