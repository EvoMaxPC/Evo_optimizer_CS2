using FragForge.Core.Cs2Profile;
using Xunit;

namespace FragForge.Tests.Cs2Profile;

public sealed class Cs2ProfileSerializerTests
{
    private static readonly Cs2OptionCatalog Catalog = Cs2OptionCatalog.LoadEmbedded();
    private static readonly DateTimeOffset FixedDate = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RoundTrip_ExportImportRegenerate_IsIdentical()
    {
        var original = new Cs2ProfilePreferences
        {
            Objective = Cs2ProfileObjective.FrametimeEstable,
            FpsCap = FpsCap.DerivedFromRefresh(0.5),
            MeasuredRefreshHz = 165,
            VSync = true,
            SleepWhenUnfocused = false,
            IncludeUnverified = true,
            Options = [new SelectedOption { Id = "-novid" }, new SelectedOption { Id = "r_low_latency", Value = "1" }]
        };

        var json = Cs2ProfileSerializer.Serialize(original);
        var imported = Cs2ProfileSerializer.Deserialize(json);
        var regenerated = Cs2ProfileGenerator.Generate(imported, Catalog, FixedDate, "0.1.0");

        // Ida y vuelta REAL: generar con el original y con el importado debe dar el mismo texto byte a byte.
        var first = Cs2ProfileGenerator.Generate(original, Catalog, FixedDate, "0.1.0");
        Assert.Equal(first.CfgText, regenerated.CfgText);
        Assert.Equal(first.LaunchOptions, regenerated.LaunchOptions);

        // Campo a campo. NO se usa Assert.Equal(original, imported) sobre los records: la igualdad de
        // record compara por REFERENCIA los miembros que son listas, así que un List<> y un ReadOnlyArray
        // con el mismo contenido nunca serían "iguales" y el test no probaría lo que dice probar.
        Assert.Equal(original.FormatVersion, imported.FormatVersion);
        Assert.Equal(original.Objective, imported.Objective);
        Assert.Equal(original.FpsCap.Mode, imported.FpsCap.Mode);
        Assert.Equal(original.FpsCap.FixedFps, imported.FpsCap.FixedFps);
        Assert.Equal(original.FpsCap.RefreshFactor, imported.FpsCap.RefreshFactor);
        Assert.Equal(original.VSync, imported.VSync);
        Assert.Equal(original.SleepWhenUnfocused, imported.SleepWhenUnfocused);
        Assert.Equal(original.IncludeUnverified, imported.IncludeUnverified);
        Assert.Equal(original.MeasuredRefreshHz, imported.MeasuredRefreshHz);

        // Y las opciones, elemento a elemento: mismo id y mismo valor, en el mismo orden.
        Assert.Equal(original.Options.Count, imported.Options.Count);
        for (var i = 0; i < original.Options.Count; i++)
        {
            Assert.Equal(original.Options[i].Id, imported.Options[i].Id);
            Assert.Equal(original.Options[i].Value, imported.Options[i].Value);
        }
    }

    [Fact]
    public void Json_UsesEnumMemberNames_NotNumbers()
    {
        var json = Cs2ProfileSerializer.Serialize(new Cs2ProfilePreferences
        {
            Objective = Cs2ProfileObjective.LatenciaMinima,
            FpsCap = FpsCap.DerivedFromRefresh(1.0),
            MeasuredRefreshHz = 144
        });

        // R-N: los enums viajan por NOMBRE. Un número rompería la lectura del perfil a mano.
        // Las claves van en camelCase porque se heredan de JsonDefaults (PropertyNamingPolicy.CamelCase).
        Assert.Contains("\"objective\": \"LatenciaMinima\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\": \"DerivadoDelRefresco\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"objective\": 0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyJson_IsRejectedWithAClearMessage()
    {
        Assert.Contains("vacío", Assert.Throws<Cs2ProfileImportException>(() => Cs2ProfileSerializer.Deserialize("")).Message, StringComparison.Ordinal);
        Assert.Contains("vacío", Assert.Throws<Cs2ProfileImportException>(() => Cs2ProfileSerializer.Deserialize("   ")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongVersion_IsRejected_NotSilentlyUpgraded()
    {
        const string json = """{ "FormatVersion": 99 }""";

        var error = Assert.Throws<Cs2ProfileImportException>(() => Cs2ProfileSerializer.Deserialize(json));
        Assert.Contains("Versión de perfil 99", error.Message, StringComparison.Ordinal);
        Assert.Contains("no soportada", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownField_IsRejected_StrictProfile()
    {
        const string json = """{ "FormatVersion": 1, "CampoQueNoExiste": 1 }""";

        var error = Assert.Throws<Cs2ProfileImportException>(() => Cs2ProfileSerializer.Deserialize(json));
        Assert.Contains("no es un perfil de FragForge válido", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedCapWithoutValue_IsRejected()
    {
        const string json = """{ "FormatVersion": 1, "FpsCap": { "Mode": "Fijo", "FixedFps": null } }""";

        Assert.Contains("no tiene un valor mayor que 0",
            Assert.Throws<Cs2ProfileImportException>(() => Cs2ProfileSerializer.Deserialize(json)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateOption_IsRejected()
    {
        const string json = """
        { "formatVersion": 1, "options": [ { "id": "fps_max", "value": "60" }, { "id": "FPS_MAX", "value": "120" } ] }
        """;

        Assert.Contains("aparece más de una vez",
            Assert.Throws<Cs2ProfileImportException>(() => Cs2ProfileSerializer.Deserialize(json)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeRefresh_IsRejected_BecauseNullIsTheWayToSayUnknown()
    {
        const string json = """{ "FormatVersion": 1, "MeasuredRefreshHz": -1 }""";

        Assert.Contains("mayor que 0", Assert.Throws<Cs2ProfileImportException>(() => Cs2ProfileSerializer.Deserialize(json)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_AreApplied_WhenFieldsAreMissing_Additive()
    {
        // Un perfil mínimo solo con la versión: todo lo demás toma su valor por defecto (campos aditivos).
        var imported = Cs2ProfileSerializer.Deserialize("""{ "formatVersion": 1 }""");

        Assert.Equal(Cs2ProfileObjective.Equilibrado, imported.Objective);
        Assert.Equal(FpsCapMode.SinTope, imported.FpsCap.Mode);
        Assert.False(imported.IncludeUnverified);
        Assert.Null(imported.VSync);
        Assert.Null(imported.MeasuredRefreshHz);
        Assert.Empty(imported.Options);
    }
}
