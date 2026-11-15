using FragForge.Core.Analysis.Live;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

public sealed class MatchConfigSheetTests
{
    [Fact]
    public void Resolution_IsUnknownWithoutVerifiedSource()
    {
        var result = ConfiguredResolutionReader.TryRead(new Dictionary<string, string> { ["arbitraria"] = "1920" });
        // Sin fuente verificada, ambas dimensiones son desconocidas aunque existan datos.
        Assert.Null(result.Width);
        Assert.Null(result.Height);
    }

    [Fact]
    public void Builder_CopiesAllValuesInOrdinalOrder()
    {
        var source = new Cs2Inspection { ActiveSteamAccountId = "42", VideoSettings = new Dictionary<string, string> { ["z"] = "1", ["a"] = "2" },
            StoredConvars = new Dictionary<string, string> { ["fps"] = "64" }, AutoexecConvars = new Dictionary<string, string> { ["fps"] = "0" },
            Conflicts = ["conflicto"], LaunchOptions = "-novid", ConfiguredRefreshHz = 144, Notes = ["nota"] };
        var sheet = MatchConfigSheetBuilder.From(source);
        // Orden ordinal: a antes que z; se conservan los dos valores sin cambios.
        Assert.Equal(new[] { "a", "z" }, sheet.VideoSettings.Select(x => x.Key));
        Assert.Equal(new[] { "2", "1" }, sheet.VideoSettings.Select(x => x.Value));
        Assert.Equal("42", sheet.ActiveSteamAccountId);
        Assert.Equal("64", Assert.Single(sheet.StoredConvars).Value);
        Assert.Equal("0", Assert.Single(sheet.AutoexecConvars).Value);
        Assert.Equal("conflicto", Assert.Single(sheet.Conflicts));
        Assert.Equal("-novid", sheet.LaunchOptions);
        Assert.Equal(144, sheet.ConfiguredRefreshHz);
        Assert.Equal(new[] { "nota", "resolución configurada desconocida: falta o es inválida la clave video.cfg.setting.defaultres" }, sheet.Notes);
        Assert.Null(sheet.ConfiguredWidth);
        Assert.Null(sheet.ConfiguredHeight);
    }

    [Fact]
    public void Diff_EmptyAndChangesOrderedBySectionAndKey()
    {
        var old = new MatchConfigSheet { VideoSettings = [new("b", "1"), new("c", "3")], StoredConvarsKnown = true, StoredConvars = [new("x", "1")], AutoexecConvars = [new("a", "1")], LaunchOptions = "antes" };
        var updated = new MatchConfigSheet { VideoSettings = [new("a", "2"), new("b", "4")], StoredConvarsKnown = true, StoredConvars = [new("x", "2")], AutoexecConvars = [new("a", "2")], LaunchOptions = "después" };
        Assert.Empty(MatchConfigDiff.Compare(old, old));
        // Orden ordinal por sección y clave: Autoexec, Launch, Stored, Video a/b/c.
        Assert.Equal(new[] { "AutoexecConvars: a cambió de 1 a 2", "LaunchOptions: cambió de antes a después", "StoredConvars: x cambió de 1 a 2", "VideoSettings: a agregada: 2", "VideoSettings: b cambió de 1 a 4", "VideoSettings: c quitada: 3" }, MatchConfigDiff.Compare(old, updated));
    }
    [Theory]
    [InlineData("VideoSettings")]
    [InlineData("StoredConvars")]
    [InlineData("AutoexecConvars")]
    public void Diff_AddChangeRemoveInEachSection(string section)
    {
        var beforePairs = new KeyValuePair<string, string>[] { new("b", "viejo"), new("c", "quitado") };
        var afterPairs = new KeyValuePair<string, string>[] { new("a", "nuevo"), new("b", "nuevo") };
        var before = section switch
        {
            "VideoSettings" => new MatchConfigSheet { VideoSettings = beforePairs },
            "StoredConvars" => new MatchConfigSheet { StoredConvarsKnown = true, StoredConvars = beforePairs },
            _ => new MatchConfigSheet { AutoexecConvars = beforePairs },
        };
        var after = section switch
        {
            "VideoSettings" => new MatchConfigSheet { VideoSettings = afterPairs },
            "StoredConvars" => new MatchConfigSheet { StoredConvarsKnown = true, StoredConvars = afterPairs },
            _ => new MatchConfigSheet { AutoexecConvars = afterPairs },
        };
        // Tres claves en orden ordinal: a agregada, b cambiada, c quitada.
        Assert.Equal(new[] { section + ": a agregada: nuevo", section + ": b cambió de viejo a nuevo", section + ": c quitada: quitado" }, MatchConfigDiff.Compare(before, after));
    }

}
