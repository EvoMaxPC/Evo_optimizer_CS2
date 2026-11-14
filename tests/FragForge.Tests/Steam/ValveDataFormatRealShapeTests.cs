using FragForge.Actions.Files;
using Xunit;

namespace FragForge.Tests.Steam;

public class ValveDataFormatRealShapeTests
{
    [Fact]
    public void ParsesTheShapeOfARealLibraryFoldersFile_WithoutOuterBraces()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"    "C:\\Program Files (x86)\\Steam"
                    "label"   ""
                    "apps"
                    {
                        "730"   "74265067316"
                    }
                }
            }
            """;
        var node = ValveDataFormat.Parse(vdf);
        var path = node.Children["libraryfolders"].Children["0"].Children["path"].Value;
        Assert.Equal(@"C:\Program Files (x86)\Steam", path);
    }

    [Fact]
    public void AnUnclosedNestedObject_StillThrows()
    {
        Assert.Throws<InvalidDataException>(() => ValveDataFormat.Parse("\"a\" { \"b\" \"c\""));
    }
}