using KaliteKit.Models;
using Windows.UI;

namespace KaliteKit.Tests.Models;

public class TintPresetsTests
{
    [Fact]
    public void All_ContainsDefaultPlusSixtyNineColors()
    {
        Assert.Equal(70, TintPresets.All.Count);
        Assert.Equal("Default", TintPresets.All[0].Name);
        Assert.Null(TintPresets.All[0].Hex);
    }

    [Fact]
    public void All_EveryNonDefaultHexParses()
    {
        foreach (var preset in TintPresets.All.Skip(1))
        {
            var color = TintPresets.ParseHex(preset.Hex);
            Assert.NotNull(color);
            Assert.Equal(0xFF, color!.Value.A);
            Assert.Equal(preset.Color, color.Value);
        }
    }

    [Fact]
    public void All_HasUniqueNames()
    {
        var names = TintPresets.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void All_FamiliesStayInGradientOrder()
    {
        // The wrap grid reads as a gradient: neutrals → blues/teals/greens →
        // warm tones → reds/pinks → purples, so family markers must appear
        // in that order and the palette must end with the last purple.
        var names = TintPresets.All.Select(p => p.Name).ToList();
        Assert.Equal("Orchid", names[^1]);
        Assert.True(names.IndexOf("Camouflage") < names.IndexOf("Cool Blue Bright")); // neutral < blue
        Assert.True(names.IndexOf("Camouflage") < names.IndexOf("Brick Red"));      // neutral < warm
        Assert.True(names.IndexOf("Brick Red") < names.IndexOf("Red"));             // warm < red
        Assert.True(names.IndexOf("Red") < names.IndexOf("Violet Red Light"));      // pink < purple
    }

    [Fact]
    public void ParseHex_HandlesHashAndBareHex()
    {
        Assert.Equal(Color.FromArgb(0xFF, 0xA8, 0x90, 0x3C), TintPresets.ParseHex("#A8903C"));
        Assert.Equal(Color.FromArgb(0xFF, 0xA8, 0x90, 0x3C), TintPresets.ParseHex("A8903C"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12")]       // too short
    [InlineData("#12345")]    // wrong length
    [InlineData("#GGGGGG")]   // not hex
    public void ParseHex_ReturnsNullForInvalid(string? hex)
        => Assert.Null(TintPresets.ParseHex(hex));

    [Fact]
    public void ToHex_RoundTrips()
    {
        var color = Color.FromArgb(0xFF, 0x3E, 0x6F, 0xB8);
        Assert.Equal("3E6FB8", TintPresets.ToHex(color));
    }

    [Fact]
    public void All_KeepsOriginalWindowColors()
    {
        // The classic picker names users already know must stay present.
        var names = TintPresets.All.Select(p => p.Name).ToHashSet();
        foreach (var known in new[] { "Seafoam", "Mint Light", "Violet Red Light", "Iris Pastel", "Cool Blue Bright", "Camouflage" })
        {
            Assert.True(names.Contains(known), $"Missing original color: {known}");
        }
    }
}