using System.Linq;
using KalOS.ViewModels;

namespace KalOS.Tests.ViewModels;

/// <summary>
/// Guards the Win32PrioritySeparation preset table shown in the Additional
/// Tweaks dropdown. A wrong value silently changes scheduler behavior, so the
/// table is contract-tested.
/// </summary>
public class PrioritySeparationPresetsTests
{
    [Fact]
    public void Presets_ContainTheKeptValues()
    {
        var values = PrioritySeparationPresets.Presets.Select(p => p.Value).ToList();

        Assert.Contains(0x02, values);  // Windows default
        Assert.Contains(0x16, values);  // 22 — Long, Variable, High (the "16" some guides quote)
        Assert.Contains(0x18, values);  // 24 — Long, Fixed, No boost
        Assert.Contains(0x1A, values);  // 26 — Long, Fixed, High boost
        Assert.Contains(0x24, values);  // 36 — Short, Variable, No boost
        Assert.Contains(0x28, values);  // 40 — Short, Fixed, No boost (best response time)
        Assert.Contains(0x2A, values);  // 42 — Short, Fixed, High boost
    }

    [Fact]
    public void Presets_ExcludeTheRemovedValues()
    {
        var values = PrioritySeparationPresets.Presets.Select(p => p.Value).ToList();

        Assert.DoesNotContain(0x14, values);  // 20
        Assert.DoesNotContain(0x15, values);  // 21
        Assert.DoesNotContain(0x19, values);  // 25
        Assert.DoesNotContain(0x25, values);  // 37
        Assert.DoesNotContain(0x26, values);  // 38
        Assert.DoesNotContain(0x29, values);  // 41
    }

    [Fact]
    public void Presets_HaveSevenEntries()
    {
        Assert.Equal(7, PrioritySeparationPresets.Presets.Count);
    }

    [Fact]
    public void Presets_AreAscendingAndUnique()
    {
        var values = PrioritySeparationPresets.Presets.Select(p => p.Value).ToList();

        Assert.Equal(values.OrderBy(v => v), values);
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void Labels_ShowDecimalAndHex()
    {
        Assert.Equal("22 (0x16) — Long, Variable, High boost", PrioritySeparationPresets.LongVariableHighBoost.Label);
        Assert.Equal("40 (0x28) — Short, Fixed, No boost", PrioritySeparationPresets.ShortFixedNoBoost.Label);
        Assert.Equal("2 (0x02) — Windows default", PrioritySeparationPresets.WindowsDefault.Label);
    }

    [Fact]
    public void PresetToString_ReturnsTheLabel()
    {
        Assert.Equal(PrioritySeparationPresets.WindowsDefault.Label, PrioritySeparationPresets.WindowsDefault.ToString());
    }
}
