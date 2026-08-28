using System.Collections.Generic;
using System.Linq;
using KalOS.Models.Bios;
using KalOS.Services.Bios;
using Xunit;

namespace KalOS.Tests.Services;

public class ScewinParserTests
{
    private const string ExportExample = @"Setup Question	= Adjust USB4 Bridge Resource
Help String	= Adjust USB4 Bridge Resource when choose the corresponds controller
Token	=60	// Do NOT change this line
Offset	=78
Width	=01
BIOS Default	=[02]IUSB4_GPP0 + IUSB4_GPP1 
Options	=[00]IUSB4_GPP0	// Move ""*"" to the desired Option
         [01]IUSB4_GPP1
         *[02]IUSB4_GPP0 + IUSB4_GPP1
         [03]Disabled
";

    [Fact]
    public void TestParsingWithContinuationLines()
    {
        var settings = ScewinParser.Parse(ExportExample);
        Assert.Single(settings);

        var s = settings[0];
        Assert.Equal("Adjust USB4 Bridge Resource", s.Name);
        Assert.Equal(BiosDataType.Enum, s.DataType);

        // Options are exposed as human-readable labels only — no raw [NN] codes.
        Assert.NotNull(s.PossibleValues);
        Assert.Equal(4, s.PossibleValues.Count);
        Assert.Equal("IUSB4_GPP0", s.PossibleValues[0]);
        Assert.Equal("IUSB4_GPP1", s.PossibleValues[1]);
        Assert.Equal("IUSB4_GPP0 + IUSB4_GPP1", s.PossibleValues[2]);
        Assert.Equal("Disabled", s.PossibleValues[3]);

        // Current value = the label of the option marked with '*'.
        Assert.Equal("IUSB4_GPP0 + IUSB4_GPP1", s.CurrentValue);
    }

    [Fact]
    public void TestSerializingChanges()
    {
        // The UI sends the clean label (no [NN] code) — exactly what the
        // dropdown shows the user. The serializer must still move the '*'.
        var changes = new[] { new BiosSettingChange("Adjust USB4 Bridge Resource", "IUSB4_GPP1") };
        var output = ScewinParser.SerializeFull(ExportExample, changes);

        Assert.Contains("Options\t=[00]IUSB4_GPP0", output);
        Assert.Contains("        *[01]IUSB4_GPP1", output);
        Assert.Contains("          [02]IUSB4_GPP0 + IUSB4_GPP1", output);

        // Exactly one option entry is starred (the comment line containing
        // the literal "Move \"*\"" text must not be counted).
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(output, @"\*\s*\[").Count);
    }
}
