using FragForge.Actions.Files;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cs2;

/// <summary>
/// Capa 8.6 — inspector de SOLO LECTURA.
///
/// Los fixtures imitan los archivos reales de la PC (anonimizados): <c>cs2_video.txt</c> al mínimo,
/// <c>cs2_convars.vcfg</c> y un <c>autoexec.cfg</c> con <c>fps_max</c> distinto al guardado, que es el
/// conflicto que se quiere detectar.
///
/// Se comprueba además la propiedad que más importa: **nada se escribe**. Al terminar, los archivos tienen
/// exactamente el mismo contenido y no aparecen archivos nuevos.
/// </summary>
public sealed class Cs2InspectorTests : IDisposable
{
    private readonly string _temp;

    public Cs2InspectorTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ff-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- parsers puros

    [Fact]
    public void VideoSettings_AreReadByKey_NotByPosition()
    {
        const string video = """
            "video.cfg"
            {
                "settings"
                {
                    "setting.refreshrate_numerator"      "240"
                    "setting.refreshrate_denominator"    "1"
                    "setting.fullscreen"                 "1"
                    "setting.mat_vsync"                  "0"
                    "setting.r_low_latency"              "1"
                }
            }
            """;

        var settings = Cs2ConfigParser.ParseVideoSettings(video);

        // Por CLAVE: el orden no importa y los bloques intermedios tampoco.
        Assert.Equal("240", Cs2ConfigParser.FindLeaf(settings, "refreshrate_numerator"));
        // mat_vsync vale "0" en el fixture de arriba: se espera ESE valor. Estaba puesto "1", que no
        // aparece en ninguna parte del VDF de este test; el parser devolvia "0", que es lo correcto.
        Assert.Equal("0", Cs2ConfigParser.FindLeaf(settings, "mat_vsync"));
        Assert.Equal(240, Cs2ConfigParser.ParseRefreshHz(settings));
    }

    [Fact]
    public void RefreshHz_WithFraction_UsesNumeratorOverDenominator()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x.refreshrate_numerator"] = "240",
            ["x.refreshrate_denominator"] = "2"
        };
        Assert.Equal(120, Cs2ConfigParser.ParseRefreshHz(settings));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-60")]
    [InlineData("abc")]
    [InlineData("")]
    public void RefreshHz_WithNonPositiveOrGarbage_IsNull_NotAGuess(string value)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["y.refreshrate_numerator"] = value };
        Assert.Null(Cs2ConfigParser.ParseRefreshHz(settings));
    }

    [Fact]
    public void RefreshHz_UsesInvariantCulture_NotTheUiCulture()
    {
        // R-E: con una cultura que usa coma decimal, "240" debe seguir siendo 240.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["z.refreshrate_numerator"] = "240" };
            Assert.Equal(240, Cs2ConfigParser.ParseRefreshHz(settings));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Autoexec_IgnoresCommentsAndBlankLines()
    {
        const string cfg = """
            // config del jugador
            fps_max 0
            m_rawinput 1

            +cl_showfps 1
            sensitivity "1.5"
            """;

        var values = Cs2ConfigParser.ParseAutoexec(cfg);

        Assert.Equal("0", values["fps_max"]);
        Assert.Equal("1", values["m_rawinput"]);
        Assert.Equal("1", values["cl_showfps"]);
        Assert.Equal("1.5", values["sensitivity"]);
        Assert.Equal(4, values.Count);   // el comentario no cuenta
    }

    [Fact]
    public void Conflicts_AreDetectedByKey_WithBothValuesInTheMessage()
    {
        const string vcfg = """
            "convars"
            {
                "fps_max"     "0"
                "m_rawinput"  "1"
            }
            """;
        const string autoexec = """
            fps_max 64
            m_rawinput 1
            """;

        var stored = Cs2ConfigParser.ParseVcfg(vcfg);
        var player = Cs2ConfigParser.ParseAutoexec(autoexec);
        var conflicts = Cs2ConfigParser.FindConvarConflicts(stored, player);

        // Solo fps_max discrepa; m_rawinput coincide y NO se reporta.
        var conflict = Assert.Single(conflicts);
        Assert.Contains("fps_max", conflict, StringComparison.Ordinal);
        Assert.Contains("'0'", conflict, StringComparison.Ordinal);
        Assert.Contains("'64'", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicts_WithNoAutoexec_AreEmpty()
    {
        var stored = Cs2ConfigParser.ParseVcfg("""
            "convars" { "fps_max" "0" }
            """);
        Assert.Empty(Cs2ConfigParser.FindConvarConflicts(stored, new Dictionary<string, string>()));
    }

    // ---------------------------------------------------------------- inspector completo

    [Fact]
    public async Task Inspect_ReadsTheActiveAccountAndItsConfig()
    {
        var root = Path.Combine(_temp, "Steam");
        var account = 12345678u;
        var cfg = Path.Combine(root, "userdata", account.ToString(System.Globalization.CultureInfo.InvariantCulture), "730", "local", "cfg");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "cs2_video.txt"), """
            "video.cfg"
            {
                "settings"
                {
                    "setting.refreshrate_numerator"   "240"
                    "setting.refreshrate_denominator" "1"
                    "setting.mat_vsync"               "0"
                }
            }
            """);
        File.WriteAllText(Path.Combine(cfg, "cs2_convars.vcfg"), """
            "convars" { "fps_max" "0" }
            """);
        File.WriteAllText(Path.Combine(cfg, "autoexec.cfg"), "fps_max 64");

        PlaceExecutable(root);

        var registry = new FakeRegistry();
        registry.Set(@"HKCU\Software\Valve\Steam\ActiveProcess", "ActiveUser", "DWord", unchecked((int)account));

        var inspection = await new Cs2Inspector(new FixedResolver(root), registry).InspectAsync();

        Assert.Equal(account.ToString(System.Globalization.CultureInfo.InvariantCulture), inspection.ActiveSteamAccountId);
        Assert.EndsWith(Path.Combine("userdata", account.ToString(System.Globalization.CultureInfo.InvariantCulture)), inspection.ActiveUserDataDirectory);
        Assert.Equal(240, inspection.ConfiguredRefreshHz);
        Assert.Equal("0", inspection.StoredConvars["fps_max"]);
        Assert.Equal("64", inspection.AutoexecConvars["fps_max"]);
        var conflict = Assert.Single(inspection.Conflicts);
        Assert.Contains("fps_max", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_WithNoResolver_ReturnsNotes_AndDoesNotThrow()
    {
        var inspection = await new Cs2Inspector(new NoCs2Resolver(), new FakeRegistry()).InspectAsync();

        Assert.Null(inspection.ExecutablePath);
        Assert.True(!inspection.HasAnyData);
        Assert.True(inspection.Notes.Count > 0, "sin CS2 tiene que haber un motivo escrito");
    }

    /// <summary>
    /// La propiedad central de la capa: inspeccionar NO escribe. Se comprueba comparando el contenido y la
    /// lista de archivos antes y después.
    /// </summary>
    [Fact]
    public async Task Inspect_DoesNotModifyOrCreateAnything()
    {
        var root = Path.Combine(_temp, "Steam");
        var account = 555u;
        var cfg = Path.Combine(root, "userdata", account.ToString(System.Globalization.CultureInfo.InvariantCulture), "730", "local", "cfg");
        Directory.CreateDirectory(cfg);
        var videoPath = Path.Combine(cfg, "cs2_video.txt");
        var autoexecPath = Path.Combine(cfg, "autoexec.cfg");
        File.WriteAllText(videoPath, "\"video.cfg\" { \"settings\" { \"setting.mat_vsync\" \"0\" } }");
        File.WriteAllText(autoexecPath, "fps_max 300");

        // El escenario se prepara ANTES de la foto: el cs2.exe es parte del "antes", no algo que cree el inspector.
        PlaceExecutable(root);
        var beforeFiles = Directory.GetFiles(_temp, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var beforeVideo = File.ReadAllText(videoPath);
        var beforeAutoexec = File.ReadAllText(autoexecPath);

        var registry = new FakeRegistry();
        registry.Set(@"HKCU\Software\Valve\Steam\ActiveProcess", "ActiveUser", "DWord", unchecked((int)account));
        await new Cs2Inspector(new FixedResolver(root), registry).InspectAsync();

        // Nada nuevo, y los archivos byte a byte iguales.
        var afterFiles = Directory.GetFiles(_temp, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(beforeFiles, afterFiles);
        Assert.Equal(beforeVideo, File.ReadAllText(videoPath));
        Assert.Equal(beforeAutoexec, File.ReadAllText(autoexecPath));
    }

    [Fact]
    public void RecommendedLaunchOptions_AreTextOnly_AndNeverApplied()
    {
        // Se expone como constante para MOSTRARLA; el inspector no la escribe en Steam.
        Assert.Equal("-novid -nojoy -console", Cs2Inspector.RecommendedLaunchOptions);
    }

    // ---------------------------------------------------------------- dobles

    /// <summary>
    /// Crea el cs2.exe de mentira que el doble espera encontrar. Se llama desde el test, ANTES de tomar
    /// cualquier medida, para que crear el escenario no cuente como un efecto de la inspeccion.
    /// </summary>
    private static void PlaceExecutable(string steamRoot)
    {
        var executable = Path.Combine(steamRoot, "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin", "win64", "cs2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
    }

    private sealed class FixedResolver(string executableRoot) : ICs2Resolver
    {
        public Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default)
        {
            var executable = Path.Combine(executableRoot, "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin", "win64", "cs2.exe");
            // Este doble NO escribe: solo dice donde estaria el exe. Antes lo creaba aqui, y como ResolveAsync
            // se invoca DENTRO de InspectAsync(), el fichero aparecia despues de tomar la foto del test
            // Inspect_DoesNotModifyOrCreateAnything, que entonces veia 3 entradas donde hay 2 y acusaba al
            // inspector de crear un archivo. La creacion (cuando hace falta) la hace el propio test con
            // PlaceExecutable, antes de medir nada.
            if (!File.Exists(executable)) throw new InvalidOperationException("El test no preparo cs2.exe: llamar a PlaceExecutable antes de inspeccionar.");
            return Task.FromResult(new Cs2Resolution
            {
                ExecutablePath = executable,
                Source = "doble de test",
                ProbedPaths = [Path.Combine(executableRoot, "steamapps", "libraryfolders.vdf"), executable],
                Candidates = [executable]
            });
        }
    }

    private sealed class NoCs2Resolver : ICs2Resolver
    {
        public Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cs2Resolution { Source = "No se encontró la carpeta de Steam.", ProbedPaths = [] });
    }
}
