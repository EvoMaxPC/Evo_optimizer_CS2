using FragForge.Actions.Files;
using FragForge.Actions.Measurement;
using FragForge.Core.Abstractions;
using FragForge.Core.Analysis.Live;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

public sealed class ConfiguredResolutionTests
{
    // Muestra de la PC de desarrollo: las claves incluyen puntos y los pares están separados por TABs.
    private const string Video1024 = """
        "video.cfg"
        {
        "setting.defaultres"		"1024"
        "setting.defaultresheight"		"768"
        "setting.refreshrate_numerator"		"46800000"
        "setting.refreshrate_denominator"		"234000"
        "setting.fullscreen"		"1"
        "setting.mat_vsync"		"0"
        "setting.monitor_index"		"0"
        "Version"		"17"
        "VendorID"		"32902"
        "DeviceID"		"18099"
        "setting.nowindowborder"		"1"
        "setting.fullscreen_min_on_focus_loss"		"1"
        "setting.high_dpi"		"0"
        "setting.r_low_latency"		"1"
        "setting.aspectratiomode"		"0"
        "setting.videocfg_shadow_quality"		"0"
        }
        """;
    private const string Video640 = """
        "video.cfg"
        {
            "setting.defaultres" "640"
            "setting.defaultresheight" "480"
        }
        """;
    private const string Video180 = """
        "video.cfg"
        {
            "setting.defaultres" "1024"
            "setting.defaultresheight" "768"
            "setting.refreshrate_numerator" "44550000"
            "setting.refreshrate_denominator" "247500"
        }
        """;

    private sealed class FixedResolver(string root) : ICs2Resolver
    {
        public Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default)
        {
            var executable = Path.Combine(root, "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin", "win64", "cs2.exe");
            return Task.FromResult(new Cs2Resolution { ExecutablePath = executable, Source = "doble de test", ProbedPaths = [Path.Combine(root, "steamapps", "libraryfolders.vdf")] });
        }
    }

    private static void PlaceVideo(string root, string account, string text)
    {
        var cfg = Path.Combine(root, "userdata", account, "730", "local", "cfg");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "cs2_video.txt"), text);
    }

    private static Task<Cs2Inspection> Inspect(string root, int? active = null)
    {
        var registry = new FakeRegistry();
        if (active is int id) registry.Set(@"HKCU\Software\Valve\Steam\ActiveProcess", "ActiveUser", "DWord", id);
        return new Cs2Inspector(new FixedResolver(root), registry).InspectAsync();
    }

    [Fact]
    public async Task Active1024_RealRead_BuildsSheetAndCompares()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "1138166200", Video1024);
        var inspection = await Inspect(temp.Path, 1138166200);
        var sheet = MatchConfigSheetBuilder.From(inspection);
        var comparison = ResolutionComparison.Compare(sheet.ConfiguredWidth, sheet.ConfiguredHeight, 1024, 768);
        // 1024 = 1024 y 768 = 768; 46800000 / 234000 = 200.
        Assert.Equal("1024", inspection.VideoSettings["video.cfg.setting.defaultres"]);
        Assert.Equal("768", inspection.VideoSettings["video.cfg.setting.defaultresheight"]);
        Assert.Empty(inspection.VideoSettingsByAccount);
        Assert.Equal(1024, sheet.ConfiguredWidth);
        Assert.Equal(768, sheet.ConfiguredHeight);
        Assert.Equal(200, inspection.ConfiguredRefreshHz);
        Assert.Equal(ResolutionComparisonKind.Same, comparison.Kind);
        Assert.Equal("configurada 1024x768, observada 1024x768 (área cliente; no prueba el modo de pantalla ni la resolución interna de render)", comparison.Text);
    }

    [Fact]
    public async Task Active640_RealRead_Uses640By480()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "672087634", Video640);
        var inspection = await Inspect(temp.Path, 672087634);
        var pair = ConfiguredResolutionReader.TryRead(inspection.VideoSettings);
        // Las dos claves contienen literalmente 640 y 480.
        Assert.Equal(640, pair.Width);
        Assert.Equal(480, pair.Height);
        Assert.Empty(inspection.VideoSettingsByAccount);
    }

    [Fact]
    public async Task Active180_RefreshIsCalculated()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "645582440", Video180);
        var inspection = await Inspect(temp.Path, 645582440);
        // 44550000 / 247500 = 180 Hz, división entera exacta.
        Assert.Equal(180, inspection.ConfiguredRefreshHz);
        var sheet = MatchConfigSheetBuilder.From(inspection);
        Assert.Equal(1024, sheet.ConfiguredWidth);
        Assert.Equal(768, sheet.ConfiguredHeight);
    }

    [Fact]
    public async Task MissingHeight_LeavesBothUnknownAndExplains()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "1138166200", "\"video.cfg\" { \"setting.defaultres\" \"1024\" }");
        var sheet = MatchConfigSheetBuilder.From(await Inspect(temp.Path, 1138166200));
        // Ancho 1024 sin alto no forma un par conocido.
        Assert.Null(sheet.ConfiguredWidth);
        Assert.Null(sheet.ConfiguredHeight);
        Assert.Contains("resolución configurada desconocida: falta o es inválida la clave video.cfg.setting.defaultresheight", sheet.Notes);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1e3")]
    [InlineData("16385")]
    public async Task InvalidWidth_LeavesBothUnknown(string invalid)
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "1138166200", Video1024.Replace("\"1024\"", "\"" + invalid + "\""));
        var sheet = MatchConfigSheetBuilder.From(await Inspect(temp.Path, 1138166200));
        // abc y 1e3 no son enteros; 0 y -5 no son positivos; 16385 > 16384.
        Assert.Null(sheet.ConfiguredWidth);
        Assert.Null(sheet.ConfiguredHeight);
        Assert.Contains("resolución configurada desconocida: falta o es inválida la clave video.cfg.setting.defaultres", sheet.Notes);
    }

    [Fact]
    public async Task UnknownAccount_DifferentResolutions_AreAmbiguous_AndEmptyFolderIgnored()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "1138166200", Video1024);
        PlaceVideo(temp.Path, "672087634", Video640);
        Directory.CreateDirectory(Path.Combine(temp.Path, "userdata", "0"));
        var inspection = await Inspect(temp.Path);
        var sheet = MatchConfigSheetBuilder.From(inspection);
        // 1024x768 != 640x480; la carpeta 0 no tiene archivo.
        Assert.Null(inspection.ActiveSteamAccountId);
        Assert.Empty(inspection.VideoSettings);
        Assert.Equal(2, inspection.VideoSettingsByAccount.Count);
        Assert.False(inspection.VideoSettingsByAccount.ContainsKey("0"));
        Assert.Null(sheet.ConfiguredWidth);
        Assert.Null(sheet.ConfiguredHeight);
        Assert.Contains("las cuentas de Steam tienen resoluciones distintas y no se sabe cuál usa la partida", sheet.Notes);
    }

    [Fact]
    public async Task ActiveAccount_DifferentResolutions_UsesOnlyActive()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "1138166200", Video1024);
        PlaceVideo(temp.Path, "672087634", Video640);
        var inspection = await Inspect(temp.Path, 672087634);
        var sheet = MatchConfigSheetBuilder.From(inspection);
        // La cuenta activa 672087634 contiene 640x480; la otra no se selecciona.
        Assert.Equal("672087634", inspection.ActiveSteamAccountId);
        Assert.Empty(inspection.VideoSettingsByAccount);
        Assert.Equal(640, sheet.ConfiguredWidth);
        Assert.Equal(480, sheet.ConfiguredHeight);
    }

    [Fact]
    public async Task UnknownAccount_SameResolutions_ReportsConsensus()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "1138166200", Video1024);
        PlaceVideo(temp.Path, "645582440", Video180);
        var sheet = MatchConfigSheetBuilder.From(await Inspect(temp.Path));
        // 1024x768 = 1024x768; son 2 cuentas, aunque su refresco sea distinto.
        Assert.Equal(1024, sheet.ConfiguredWidth);
        Assert.Equal(768, sheet.ConfiguredHeight);
        Assert.Contains("cuenta activa desconocida; las 2 cuentas leídas coinciden", sheet.Notes);
    }

    [Fact]
    public async Task UnknownAccount_UnparseableAccount_PreventsConsensus()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "1138166200", Video1024);
        PlaceVideo(temp.Path, "672087634", "\"video.cfg\" { \"clave\" \"sin cerrar");
        var inspection = await Inspect(temp.Path);
        var sheet = MatchConfigSheetBuilder.From(inspection);
        // Una cuenta es legible y la otra falla al parsear: no hay consenso verificable.
        Assert.Null(inspection.VideoSettingsByAccount["672087634"]);
        Assert.Contains(inspection.Notes, n => n.Contains("672087634") && n.Contains("no se pudo interpretar"));
        Assert.Null(sheet.ConfiguredWidth);
        Assert.Null(sheet.ConfiguredHeight);
        Assert.Contains("no se pudo leer la cuenta 672087634; no se sabe qué resolución usa la partida", sheet.Notes);
    }

    [Fact]
    public async Task UnknownAccount_InvalidPair_PreventsConsensus()
    {
        using var temp = new TempDirectory();
        PlaceVideo(temp.Path, "1138166200", Video1024);
        PlaceVideo(temp.Path, "672087634", "\"video.cfg\" { \"setting.defaultres\" \"640\" }");
        var sheet = MatchConfigSheetBuilder.From(await Inspect(temp.Path));
        // 640 sin alto no es una resolución completa, aunque la otra cuenta sí lo sea.
        Assert.Null(sheet.ConfiguredWidth);
        Assert.Null(sheet.ConfiguredHeight);
        Assert.Contains("cuenta 672087634: resolución configurada desconocida: falta o es inválida la clave video.cfg.setting.defaultresheight", sheet.Notes);
    }

    [Fact]
    public async Task UnknownAccount_NoFiles_Explains()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "userdata", "0"));
        var inspection = await Inspect(temp.Path);
        var sheet = MatchConfigSheetBuilder.From(inspection);
        // Cero archivos de video: cero cuentas leídas y resolución desconocida.
        Assert.Empty(inspection.VideoSettingsByAccount);
        Assert.Null(sheet.ConfiguredWidth);
        Assert.Null(sheet.ConfiguredHeight);
        Assert.Contains("no se encontró cs2_video.txt", sheet.Notes);
    }
}
