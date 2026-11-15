using FragForge.Actions.Files;
using FragForge.Core.Abstractions;
using FragForge.Core.Analysis.Live;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cs2;

public sealed class LocalConfigLaunchOptionsTests
{
    private const string Account = "1138166200";
    private const string Prefix = "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { ";
    private const string Suffix = " } } } } }";

    private sealed class Resolver(string root) : ICs2Resolver
    {
        public Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Cs2Resolution
        {
            ExecutablePath = Path.Combine(root, "steamapps", "common", "CS2", "game", "bin", "win64", "cs2.exe"),
            Source = "fixture", ProbedPaths = [Path.Combine(root, "steamapps", "libraryfolders.vdf")]
        });
    }

    private static string Prepare(string root)
    {
        var config = Path.Combine(root, "userdata", Account, "config");
        Directory.CreateDirectory(config);
        return Path.Combine(config, "localconfig.vdf");
    }

    private static Task<Cs2Inspection> Inspect(string root)
    {
        var registry = new FakeRegistry();
        registry.Set(@"HKCU\Software\Valve\Steam\ActiveProcess", "ActiveUser", "DWord", 1138166200);
        return new Cs2Inspector(new Resolver(root), registry).InspectAsync();
    }

    private static string Report(Cs2Inspection inspection) => MatchReportFormatter.Format(new MatchReportInput
    {
        Sheet = MatchConfigSheetBuilder.From(inspection), GeneratedAtLocal = DateTimeOffset.UnixEpoch
    });

    [Theory]
    [InlineData("-novid -nojoy")]
    [InlineData("-novid -nojoy ")]
    public async Task ValuePresent_IsPreservedExactly(string value)
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), Prefix + "\"730\" { \"LaunchOptions\" \"" + value + "\" }" + Suffix);
        var inspection = await Inspect(temp.Path);
        // Un valor para 730, incluidos los espacios finales, se muestra sin recortarlo.
        Assert.Equal(value, inspection.LaunchOptions);
        Assert.False(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains("Opciones de lanzamiento: " + value + "\n", Report(inspection));
    }

    [Theory]
    [InlineData("Apps", "LaunchOptions")]
    [InlineData("apps", "launchoptions")]
    public async Task ObservedPathSpelling_PreservesTrailingSpace(string apps, string key)
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"" + apps + "\" { \"730\" { \"" + key + "\" \"-novid -nojoy \" } } } } } }");
        var inspection = await Inspect(temp.Path);
        // El valor sintético contiene un espacio antes de la comilla de cierre: se conserva.
        Assert.Equal("-novid -nojoy ", inspection.LaunchOptions);
        Assert.False(inspection.LaunchOptionsKnownAbsent);
    }

    [Fact]
    public async Task ObservedAppsSpelling_WithoutLaunchOptions_IsKnownAbsent()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"Apps\" { \"730\" { } } } } } }");
        var inspection = await Inspect(temp.Path);
        // La ruta con Apps contiene 0 claves LaunchOptions para 730.
        Assert.Null(inspection.LaunchOptions);
        Assert.True(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains("Opciones de lanzamiento: sin opciones configuradas", Report(inspection));
        Assert.DoesNotContain("Opciones de lanzamiento: no se sabe", Report(inspection));
    }

    [Fact]
    public async Task ObservedAppsSpelling_OnlyOtherAppHasOptions_DoesNotLeak()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"Apps\" { \"228980\" { \"LaunchOptions\" \"valor_sintetico_ajeno\" } \"730\" { } } } } } }");
        var inspection = await Inspect(temp.Path);
        var report = Report(inspection);
        // Hay 1 valor en otra app y 0 en 730; no se publica el valor ajeno.
        Assert.Null(inspection.LaunchOptions);
        Assert.True(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains("Opciones de lanzamiento: sin opciones configuradas", report);
        Assert.DoesNotContain("Opciones de lanzamiento: no se sabe", report);
        Assert.DoesNotContain("valor_sintetico_ajeno", System.Text.Json.JsonSerializer.Serialize(inspection));
        Assert.DoesNotContain("valor_sintetico_ajeno", report);
        Assert.DoesNotContain(inspection.Notes, note => note.Contains("valor_sintetico_ajeno", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingKey_IsKnownAbsent()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), Prefix + "\"730\" { \"Other\" \"personal\" }" + Suffix);
        var inspection = await Inspect(temp.Path);
        // Archivo válido, 0 claves LaunchOptions en 730: ausencia conocida, no desconocida.
        Assert.Null(inspection.LaunchOptions);
        Assert.True(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains("Opciones de lanzamiento: sin opciones configuradas", Report(inspection));
        Assert.DoesNotContain("Opciones de lanzamiento: no se sabe", Report(inspection));
    }

    [Fact]
    public async Task MissingFile_IsUnknownWithNote()
    {
        using var temp = new TempDirectory();
        Prepare(temp.Path);
        var inspection = await Inspect(temp.Path);
        // No existe el archivo: 0 valores leídos y un motivo de ausencia.
        Assert.Null(inspection.LaunchOptions);
        Assert.False(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains("No se encontró localconfig.vdf.", inspection.Notes);
        Assert.Contains("Opciones de lanzamiento: no se sabe", Report(inspection));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"UserLocalConfigStore\" { \"Software\" {")]
    [InlineData("\"UserLocalConfigStore\" { \"Software\" \"sin cerrar")]
    public async Task EmptyOrCorrupt_IsUnknownWithNote(string contents)
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), contents);
        var inspection = await Inspect(temp.Path);
        // Los tres archivos carecen de estructura completa; ninguna ausencia se presume conocida.
        Assert.Null(inspection.LaunchOptions);
        Assert.False(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains(inspection.Notes, note => note.Contains("localconfig.vdf está vacío, truncado, corrupto"));
        Assert.Contains("Opciones de lanzamiento: no se sabe", Report(inspection));
    }

    [Fact]
    public async Task PathIsCaseInsensitive_AndOtherAppIsIgnored()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), "\"USERLOCALCONFIGSTORE\" { \"software\" { \"vAlVe\" { \"sTeAm\" { \"APPS\" { \"440\" { \"LaunchOptions\" \"-otra\" } \"730\" { \"launchoptions\" \"-novid -nojoy\" } } } } } }");
        var inspection = await Inspect(temp.Path);
        // App 440 = -otra, app 730 = -novid -nojoy: solo se toma 730.
        Assert.Equal("-novid -nojoy", inspection.LaunchOptions);
        Assert.DoesNotContain("-otra", Report(inspection));
    }

    [Fact]
    public async Task No730Options_DoesNotUseOtherApp()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), Prefix + "\"440\" { \"LaunchOptions\" \"-otra\" } \"730\" { }" + Suffix);
        var inspection = await Inspect(temp.Path);
        // Hay 1 opción de otra app y 0 de 730: ausencia conocida para 730.
        Assert.Null(inspection.LaunchOptions);
        Assert.True(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains("Opciones de lanzamiento: sin opciones configuradas", Report(inspection));
        Assert.DoesNotContain("-otra", Report(inspection));
    }

    [Fact]
    public async Task TooLarge_IsUnknown()
    {
        using var temp = new TempDirectory();
        var path = Prepare(temp.Path);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.SetLength(16L * 1024 * 1024 + 1);
        var inspection = await Inspect(temp.Path);
        // 16 * 1024 * 1024 + 1 = 16777217 bytes, uno más que el máximo.
        Assert.Null(inspection.LaunchOptions);
        Assert.False(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains("localconfig.vdf supera el límite de 16 MiB.", inspection.Notes);
        Assert.Contains("Opciones de lanzamiento: no se sabe", Report(inspection));
    }

    [Fact]
    public async Task OtherPrivateValues_NeverAppearInReportOrNotes()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), "\"UserLocalConfigStore\" { \"PersonaSecreta\" \"dato_privado_123\" \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"730\" { \"PersonaSecreta\" \"dato_privado_456\" \"LaunchOptions\" \"-novid\" } } } } } }");
        var inspection = await Inspect(temp.Path);
        var report = Report(inspection);
        // Solo una clave permitida se extrae; las otras 2 claves y sus valores no salen.
        Assert.Equal("-novid", inspection.LaunchOptions);
        Assert.Contains("Opciones de lanzamiento: -novid", report);
        Assert.DoesNotContain("PersonaSecreta", report);
        Assert.DoesNotContain("dato_privado_123", report);
        Assert.DoesNotContain("dato_privado_456", report);
        Assert.DoesNotContain(inspection.Notes, n => n.Contains("dato_privado"));
    }

    [Fact]
    public async Task DuplicateTarget_IsUnknownAndNeverLeaksValues()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), Prefix + "\"730\" { \"LaunchOptions\" \"-uno\" \"launchoptions\" \"-dos\" }" + Suffix);
        var inspection = await Inspect(temp.Path);
        // Dos claves objetivo, sin regla de precedencia: no se elige ninguna.
        Assert.Null(inspection.LaunchOptions);
        Assert.False(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains(inspection.Notes, n => n.Contains("claves ambiguas"));
        Assert.DoesNotContain("-uno", Report(inspection));
        Assert.DoesNotContain("-dos", Report(inspection));
    }

    [Fact]
    public async Task DuplicateUnrelatedKeys_AreAllowed()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Prepare(temp.Path), Prefix + "\"440\" { \"x\" \"1\" \"x\" \"2\" } \"730\" { \"LaunchOptions\" \"-novid\" }" + Suffix);
        var inspection = await Inspect(temp.Path);
        // Dos claves ajenas repetidas no alteran el único valor de 730.
        Assert.Equal("-novid", inspection.LaunchOptions);
        Assert.False(inspection.LaunchOptionsKnownAbsent);
    }
    [Fact]
    public async Task ActiveAccountOnly_DoesNotReadOtherAccountOrWrite()
    {
        using var temp = new TempDirectory();
        var activePath = Prepare(temp.Path);
        const string activeText = Prefix + "\"730\" { \"LaunchOptions\" \"-novid\" }" + Suffix;
        File.WriteAllText(activePath, activeText);
        var other = Path.Combine(temp.Path, "userdata", "672087634", "config");
        Directory.CreateDirectory(other);
        var otherPath = Path.Combine(other, "localconfig.vdf");
        const string otherText = Prefix + "\"730\" { \"LaunchOptions\" \"-otra\" }" + Suffix;
        File.WriteAllText(otherPath, otherText);
        var inspection = await Inspect(temp.Path);
        // La cuenta activa tiene -novid; la otra -otra. Ambos archivos permanecen iguales.
        Assert.Equal("-novid", inspection.LaunchOptions);
        Assert.Equal(activeText, File.ReadAllText(activePath));
        Assert.Equal(otherText, File.ReadAllText(otherPath));
        Assert.DoesNotContain("-otra", Report(inspection));
    }

    [Fact]
    public async Task InvalidUtf8_IsUnknownWithNote()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Prepare(temp.Path), [0xff, 0xfe, 0xff]);
        var inspection = await Inspect(temp.Path);
        // Tres bytes que no forman UTF-8 válido: no se infiere ausencia de la clave.
        Assert.Null(inspection.LaunchOptions);
        Assert.False(inspection.LaunchOptionsKnownAbsent);
        Assert.Contains("localconfig.vdf no tiene codificación UTF-8 válida.", inspection.Notes);
        Assert.Contains("Opciones de lanzamiento: no se sabe", Report(inspection));
    }

}
