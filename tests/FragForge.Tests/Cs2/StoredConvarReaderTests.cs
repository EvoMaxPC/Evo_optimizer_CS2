using FragForge.Actions.Files;
using FragForge.Core.Analysis.Live;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Cs2;

public sealed class StoredConvarReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-vcfg-" + Guid.NewGuid().ToString("N"));
    private string Cfg => Path.Combine(_root, "userdata", "123", "730", "local", "cfg");
    private string Machine => Path.Combine(Cfg, "cs2_machine_convars.vcfg");
    private string User => Path.Combine(Cfg, "cs2_user_convars_0_slot0.vcfg");
    public StoredConvarReaderTests() => Directory.CreateDirectory(Cfg);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private static string Config(string lines) => "\"config\"\n{\n\"convars\"\n{\n" + lines + "\n}\n}";

    [Fact]
    public void WhitelistHasExactNamesAndOrder()
    {
        Assert.Equal(new[] { "fps_max", "fps_max_ui", "cl_showfps", "m_rawinput", "m_customaccel", "m_mouse_accel1", "m_mouse_accel2", "sensitivity", "zoom_sensitivity_ratio", "cl_interp", "cl_interp_ratio", "cl_updaterate", "cl_cmdrate", "rate", "engine_low_latency_sleep_after_client_tick", "mat_vsync", "r_fullscreen_gamma", "cl_autohelp", "viewmodel_fov" }, StoredConvarNames.Ordered);
    }

    [Fact]
    public async Task OnlyWhitelistedValuesFromTwoLocalFilesAreRead()
    {
        File.WriteAllText(Machine, Config("\"fps_max\"\t\t\"0\"\n\"sensitivity\"\t\t\"3.9\"\n\"cachedvalue_count_x\" \"secreto1\"\n\"c_ruido\" \"secreto2\""));
        File.WriteAllText(User, Config("\"fps_max_ui\"\t\t\"60\"\n\"cl_showfps\" \"false\""));
        File.WriteAllText(Path.Combine(Cfg, "cs2_user_keys_0_slot0.vcfg"), Config("\"fps_max\" \"999\""));
        File.WriteAllText(Path.Combine(Cfg, "cs2_convars.vcfg"), Config("\"fps_max\" \"998\""));
        var remote = Path.Combine(_root, "userdata", "123", "730", "remote", "cfg"); Directory.CreateDirectory(remote);
        File.WriteAllText(Path.Combine(remote, "cs2_user_convars_0_slot0.vcfg"), Config("\"fps_max\" \"997\""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.True(result.Known);
        Assert.Equal("0", result.Values["fps_max"]); Assert.Equal("3.9", result.Values["sensitivity"]);
        Assert.Equal("60", result.Values["fps_max_ui"]); Assert.Equal("false", result.Values["cl_showfps"]);
        Assert.Equal(new[] { "cl_showfps", "fps_max", "fps_max_ui", "sensitivity" }, result.Values.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(notes); Assert.DoesNotContain("secreto", string.Join(" ", result.Values.Values));
    }

    [Fact]
    public async Task SameValueIsDeduplicatedAndDifferentValueIsOmitted()
    {
        File.WriteAllText(Machine, Config("\"fps_max\" \"0\"\n\"rate\" \"100\""));
        File.WriteAllText(User, Config("\"fps_max\" \"0\"\n\"rate\" \"200\""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.False(result.Known); Assert.Equal("0", Assert.Single(result.Values).Value);
        Assert.Equal("conflicto entre archivos guardados para rate", Assert.Single(notes));
    }

    [Fact]
    public async Task MissingFileIsNotAnExceptionAndMakesComparisonUnknown()
    {
        File.WriteAllText(Machine, Config("\"fps_max\" \"0\""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.False(result.Known); Assert.Equal("0", result.Values["fps_max"]);
        Assert.Equal("cs2_user_convars_0_slot0.vcfg: no se encontró.", Assert.Single(notes));
    }

    [Theory]
    [InlineData("\"wrong\" { \"convars\" { \"fps_max\" \"0\" } }")]
    [InlineData("\"config\" { \"other\" { \"fps_max\" \"0\" } }")]
    [InlineData("\"config\" { \"convars\" { \"fps_max\" \"0\" }")]
    [InlineData("\"config\" { \"convars\" { \"fps_max\" \"0\" } } }")]
    public async Task InvalidStructureIsIgnored(string content)
    {
        File.WriteAllText(Machine, content); File.WriteAllText(User, Config(""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.False(result.Known); Assert.Empty(result.Values);
        Assert.Equal("cs2_machine_convars.vcfg: no se pudo leer o está malformado.", Assert.Single(notes));
    }

    [Fact]
    public async Task OversizedFileIsIgnored()
    {
        using (var stream = new FileStream(Machine, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) stream.SetLength(2 * 1024 * 1024 + 1);
        File.WriteAllText(User, Config("\"fps_max\" \"20\""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        // 2 MiB + 1 byte excede el límite de 2 MiB.
        Assert.False(result.Known); Assert.Equal("20", Assert.Single(result.Values).Value);
        Assert.Equal("cs2_machine_convars.vcfg: no se pudo leer (supera 2 MiB).", Assert.Single(notes));
    }

    [Fact]
    public async Task ReadWorksWithAnotherSharedFileHandle()
    {
        File.WriteAllText(Machine, Config("\"fps_max\" \"300\"")); File.WriteAllText(User, Config(""));
        using var held = new FileStream(Machine, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.True(result.Known); Assert.Equal("300", result.Values["fps_max"]); Assert.Empty(notes);
    }

    [Fact]
    public async Task ControlAndLongValuesAreOmittedWithoutLeakingThem()
    {
        File.WriteAllText(Machine, Config("\"sensitivity\" \"a\u0001b\"\n\"fps_max\" \"" + new string('x', 65) + "\"\n\"rate\" \"128\""));
        File.WriteAllText(User, Config("\"sensitivity\" \"2\""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.False(result.Known); Assert.Equal("128", Assert.Single(result.Values).Value);
        Assert.Contains("cs2_machine_convars.vcfg: valor malformado para sensitivity.", notes);
        Assert.Contains("cs2_machine_convars.vcfg: valor malformado para fps_max.", notes);
        Assert.DoesNotContain(new string('x', 65), string.Join(" ", notes));
    }

    [Fact]
    public async Task UnreadablePathIsNotAnException()
    {
        Directory.CreateDirectory(Machine); File.WriteAllText(User, Config(""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.False(result.Known); Assert.Empty(result.Values);
        Assert.Equal("cs2_machine_convars.vcfg: no se pudo leer o está malformado.", Assert.Single(notes));
    }

    [Fact]
    public async Task NamesAreExactAndValuesRemainLiteral()
    {
        File.WriteAllText(Machine, Config("\"FPS_MAX\" \"999\"\n\"fps_max\" \" 3.9 \"\n\"viewmodel_fov\" \"60\""));
        File.WriteAllText(User, Config(""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.True(result.Known); Assert.Equal(" 3.9 ", result.Values["fps_max"]);
        Assert.Equal("60", result.Values["viewmodel_fov"]); Assert.DoesNotContain("FPS_MAX", result.Values.Keys); Assert.Empty(notes);
    }

    [Fact]
    public async Task ValidEmptyFilesAndOtherBlocksProduceNoValues()
    {
        File.WriteAllText(Machine, "\"config\" { // comentario\n\"other\" { \"fps_max\" \"999\" } \"convars\" { \"cachedvalue_count_x\" \"secret\" } }");
        File.WriteAllText(User, Config(""));
        var notes = new List<string>(); var result = await StoredConvarReader.ReadAsync(Cfg, notes);
        Assert.True(result.Known); Assert.Empty(result.Values); Assert.Empty(notes);
    }

    [Fact]
    public void MatchSectionUsesWhitelistOrderAndExactLabel()
    {
        var inspection = new Cs2Inspection { StoredConvarsKnown = true, StoredConvars = new Dictionary<string, string> { ["rate"] = "3", ["fps_max_ui"] = "2", ["fps_max"] = "1" } };
        var sheet = MatchConfigSheetBuilder.From(inspection);
        var text = MatchReportFormatter.Format(new MatchReportInput { Sheet = sheet });
        Assert.Equal(new[] { "fps_max", "fps_max_ui", "rate" }, sheet.StoredConvars.Select(x => x.Key));
        Assert.Contains("Convars guardados por el juego (pueden ser pisados por autoexec.cfg y las opciones de lanzamiento):\nfps_max: 1\nfps_max_ui: 2\nrate: 3\n", text);
    }

    [Fact]
    public void UnknownReadDoesNotReportRemovalInDiff()
    {
        var first = new MatchConfigSheet { StoredConvarsKnown = true, StoredConvars = [new("fps_max", "0")] };
        var end = new MatchConfigSheet { StoredConvarsKnown = false };
        Assert.Empty(MatchConfigDiff.Compare(first, end));
        Assert.Empty(MatchConfigDiff.Compare(end, first));
    }
}
