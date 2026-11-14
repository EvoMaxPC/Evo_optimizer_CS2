using FragForge.Core.Cs2Profile;
using Xunit;

namespace FragForge.Tests.Cs2Profile;

/// <summary>
/// El texto esperado se escribe A MANO en cada test. No se copia la salida del generador: si el formato
/// cambia, el test debe fallar y obligar a decidir si el cambio era intencionado.
/// </summary>
public sealed class Cs2ProfileGeneratorTests
{
    private static readonly Cs2OptionCatalog Catalog = Cs2OptionCatalog.LoadEmbedded();
    private static readonly DateTimeOffset FixedDate = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    private static GeneratedProfile Generate(Cs2ProfilePreferences preferences) =>
        Cs2ProfileGenerator.Generate(preferences, Catalog, FixedDate, version: "0.1.0");

    [Fact]
    public void HeaderAndBody_MatchTheHandWrittenText()
    {
        var profile = Generate(new Cs2ProfilePreferences
        {
            FpsCap = FpsCap.Fixed(240),
            VSync = false,
            Objective = Cs2ProfileObjective.LatenciaMinima
        });

        // Texto esperado, escrito a mano línea por línea:
        //   encabezado de 7 líneas + línea en blanco + fps_max + mat_vsync
        // fps_max va primero (índice 0 del catálogo) y mat_vsync después (índice 4). VSync=false -> "0".
        const string expected =
            "// FragForge — perfil de arranque de CS2 generado por FragForge\n" +
            "// Versión: 0.1.0\n" +
            "// Generado: 2026-11-13 09:30:00 UTC\n" +
            "// Objetivo declarado: LatenciaMinima\n" +
            "//\n" +
            "// Este archivo no promete FPS ni mejora medible: fija valores de configuración que el\n" +
            "// usuario eligió. Si algo no encaja, cambia el valor o borra la línea.\n" +
            "\n" +
            "fps_max 240\n" +
            "mat_vsync 0\n";

        Assert.Equal(expected, profile.CfgText);
    }

    [Fact]
    public void IsDeterministic_SameInputSameBytes()
    {
        var preferences = new Cs2ProfilePreferences { FpsCap = FpsCap.Fixed(144), VSync = true };

        var first = Generate(preferences);
        var second = Generate(preferences);

        Assert.Equal(first.CfgText, second.CfgText);
        Assert.Equal(first.LaunchOptions, second.LaunchOptions);
        Assert.Equal(first.Warnings, second.Warnings);
    }

    [Fact]
    public void DateIsInjected_NotReadFromTheClock()
    {
        var preferences = new Cs2ProfilePreferences { FpsCap = FpsCap.Fixed(60) };

        var early = Cs2ProfileGenerator.Generate(preferences, Catalog, new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), "1.0.0");
        var late = Cs2ProfileGenerator.Generate(preferences, Catalog, new DateTimeOffset(2031, 12, 31, 23, 59, 59, TimeSpan.Zero), "1.0.0");

        Assert.Contains("// Generado: 2020-01-02 03:04:05 UTC", early.CfgText, StringComparison.Ordinal);
        Assert.Contains("// Generado: 2031-12-31 23:59:59 UTC", late.CfgText, StringComparison.Ordinal);
        Assert.NotEqual(early.CfgText, late.CfgText);
    }

    [Fact]
    public void LaunchOptions_AreCommentedInTheCfg_AndJoinedForSteam()
    {
        var profile = Generate(new Cs2ProfilePreferences
        {
            FpsCap = FpsCap.Fixed(120),
            Options = [new SelectedOption { Id = "-novid" }, new SelectedOption { Id = "-nojoy" }]
        });

        // En el .cfg solo pueden aparecer como comentario: si se escribieran sueltos, el juego los leería
        // como convars inválidas.
        Assert.Contains("// Opciones de lanzamiento (NO van en el .cfg; se pegan en Steam):\n//   -novid\n//   -nojoy\n", profile.CfgText, StringComparison.Ordinal);
        // Y para Steam, separadas por un espacio, en el orden del catálogo.
        Assert.Equal("-novid -nojoy", profile.LaunchOptions);
        // La línea real del .cfg es solo fps_max.
        Assert.Contains("\nfps_max 120\n", profile.CfgText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n-novid", profile.CfgText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDuplicates_AndNoContradictions()
    {
        // Pide fps_max a mano, VSync dos veces por vías distintas y tope fijo: debe quedar una sola línea de
        // fps_max (con el tope) y una sola de mat_vsync.
        var profile = Generate(new Cs2ProfilePreferences
        {
            FpsCap = FpsCap.Fixed(180),
            VSync = true,
            Options =
            [
                new SelectedOption { Id = "fps_max", Value = "60" },
                new SelectedOption { Id = "mat_vsync", Value = "0" }
            ]
        });

        var lines = profile.CfgText.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "fps_max 180", "mat_vsync 1" }, lines);
        // Ni una sola repetición de comando en todo el archivo.
        Assert.Equal(1, profile.CfgText.Split("fps_max ").Length - 1);
        Assert.Equal(1, profile.CfgText.Split("mat_vsync ").Length - 1);
    }

    [Fact]
    public void NoOptions_LeavesAnExplicitEmptyMarker()
    {
        var profile = Generate(new Cs2ProfilePreferences());

        Assert.Contains("// (sin opciones de .cfg seleccionadas)\n", profile.CfgText, StringComparison.Ordinal);
        Assert.Equal("", profile.LaunchOptions);
    }

    [Fact]
    public void WarningsFromThePlanner_SurviveToTheGeneratedProfile()
    {
        // Refresco desconocido: el generador no inventa fps_max y la advertencia llega al resultado.
        var profile = Generate(new Cs2ProfilePreferences { FpsCap = FpsCap.DerivedFromRefresh(1.0), MeasuredRefreshHz = null });

        Assert.DoesNotContain("fps_max ", profile.CfgText, StringComparison.Ordinal);
        Assert.Contains(profile.Warnings, w => w.Contains("No se pudo medir la tasa de refresco", StringComparison.Ordinal));
    }

    [Fact]
    public void WrapInBlock_UsesTheExactMarkersAndTrailingNewline()
    {
        var block = Cs2ProfileGenerator.WrapInBlock("fps_max 144\n");

        // Cálculo a mano: marca de apertura + salto + contenido sin el salto final duplicado + salto +
        // marca de cierre + salto.
        Assert.Equal(
            "// >>> FragForge (no editar a mano)\nfps_max 144\n// <<< FragForge\n",
            block);
        Assert.Equal("// >>> FragForge (no editar a mano)", Cs2ProfileGenerator.BlockStart);
        Assert.Equal("// <<< FragForge", Cs2ProfileGenerator.BlockEnd);
    }

    [Fact]
    public void NumbersInOutput_UseInvariantCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // Bajo es-AR el separador decimal es la coma: si el factor se colara en el texto, cambiaría.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-AR");
            var profile = Generate(new Cs2ProfilePreferences { FpsCap = FpsCap.DerivedFromRefresh(0.5), MeasuredRefreshHz = 165 });

            // 165 * 0.5 = 82.5 -> AwayFromZero -> 83. Entero, sin separador decimal que localizar.
            Assert.Contains("\nfps_max 83\n", profile.CfgText, StringComparison.Ordinal);
            Assert.Contains("// Generado: 2026-11-13 09:30:00 UTC", profile.CfgText, StringComparison.Ordinal);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
