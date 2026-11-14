using FragForge.Core.Cs2Profile;
using Xunit;

namespace FragForge.Tests.Cs2Profile;

/// <summary>Catálogo de opciones: hermético, se lee del recurso embebido y no toca Steam ni el disco del usuario.</summary>
public sealed class Cs2OptionCatalogTests
{
    private static Cs2OptionCatalog Catalog => Cs2OptionCatalog.LoadEmbedded();

    [Fact]
    public void Seed_IsExactlyTheEightRequested()
    {
        // Semilla cerrada: seis convars + dos opciones de lanzamiento. Ni una más (las extra son propuesta).
        var ids = Catalog.Options.Select(o => o.Id).ToArray();

        Assert.Equal(8, ids.Length);
        Assert.Equal(
            new[] { "fps_max", "fps_max_ui", "engine_no_focus_sleep", "r_low_latency", "mat_vsync", "engine_low_latency_sleep_after_client_tick", "-novid", "-nojoy" },
            ids);
        Assert.Equal(6, Catalog.Options.Count(o => o.Kind == Cs2OptionKind.Cfg));
        Assert.Equal(2, Catalog.Options.Count(o => o.Kind == Cs2OptionKind.LaunchOption));
    }

    [Fact]
    public void EveryOption_HasBothLanguagesComplete()
    {
        foreach (var option in Catalog.Options)
        {
            Assert.True(option.Description.IsComplete, $"{option.Id}: descripción incompleta");
            Assert.True(option.ExpectedEffect.IsComplete, $"{option.Id}: efecto incompleto");
            // El idioma se resuelve igual que en Localizer: en* -> inglés, el resto -> español.
            Assert.Equal(option.Description.EsMx, option.Description.Resolve("es-MX"));
            Assert.Equal(option.Description.EnUs, option.Description.Resolve("en-US"));
        }
    }

    [Fact]
    public void Ids_AreUnique()
    {
        var ids = Catalog.Options.Select(o => o.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void NoInternalConflicts_AndConflictsAreSymmetric()
    {
        var byId = Catalog.Options.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);

        // Ninguna de las 8 se contradice con otra: fps_max manda en partida y fps_max_ui en menús, y
        // r_low_latency convive con mat_vsync. Si se declarara un conflicto aquí, el catálogo mentiría.
        Assert.All(Catalog.Options, o => Assert.Empty(o.ConflictsWith));

        // Y la comprobación genérica de simetría sigue siendo cierta (vacía implica simétrica).
        foreach (var option in Catalog.Options)
        foreach (var other in option.ConflictsWith)
        {
            Assert.True(byId.ContainsKey(other), $"{option.Id} apunta a {other}, que no existe.");
            Assert.Contains(option.Id, byId[other].ConflictsWith, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoOptionClaimsVerified_WithoutACitableSource()
    {
        // Regla de la ronda: sin fuente citable NO es Verified. Todas son Observed con la fuente del usuario.
        Assert.All(Catalog.Options, o => Assert.NotEqual(Cs2Evidence.Verified, o.Evidence));
        Assert.All(Catalog.Options, o => Assert.Equal(Cs2Evidence.Observed, o.Evidence));
        Assert.All(Catalog.Options, o => Assert.Contains("solo lectura", o.Source, StringComparison.Ordinal));
        Assert.All(Catalog.Options, o => Assert.False(o.IsVerifiable));
    }

    [Fact]
    public void Text_MatchesTheIdForConvars_AndKeepsTheDashForLaunchOptions()
    {
        foreach (var option in Catalog.Options)
            Assert.Equal(option.Id, option.Text);

        Assert.Equal("-novid", Catalog.Find("-novid")!.Text);
        Assert.Equal(Cs2OptionValueType.Int, Catalog.Find("fps_max")!.ValueType);
        Assert.Equal(Cs2OptionValueType.Bool, Catalog.Find("mat_vsync")!.ValueType);
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndReturnsNullWhenMissing()
    {
        Assert.NotNull(Catalog.Find("FPS_MAX"));
        Assert.NotNull(Catalog.Find("Mat_Vsync"));
        Assert.Null(Catalog.Find("no-existe"));
    }

    [Fact]
    public void Parser_RejectsVerifiedWithoutSource()
    {
        const string json = """
        { "schemaVersion": 1, "options": [ {
            "id": "x", "kind": "Cfg", "text": "x", "valueType": "Bool",
            "description": { "esMx": "a", "enUs": "b" },
            "expectedEffect": { "esMx": "a", "enUs": "b" },
            "evidence": "Verified", "source": "", "conflictsWith": []
        } ] }
        """;

        var error = Assert.Throws<InvalidDataException>(() => Cs2OptionCatalog.Parse(json));
        Assert.Contains("sin fuente citable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_RejectsDuplicateIds_AndUnsupportedSchema()
    {
        const string body = """
        { "id": "x", "kind": "Cfg", "text": "x", "valueType": "Bool",
          "description": { "esMx": "a", "enUs": "b" },
          "expectedEffect": { "esMx": "a", "enUs": "b" },
          "evidence": "Observed", "source": "s", "conflictsWith": [] }
        """;
        Assert.Contains("duplicado", Assert.Throws<InvalidDataException>(
            () => Cs2OptionCatalog.Parse($$"""{ "schemaVersion": 1, "options": [ {{body}}, {{body}} ] }""")).Message, StringComparison.Ordinal);

        Assert.Contains("schemaVersion", Assert.Throws<InvalidDataException>(
            () => Cs2OptionCatalog.Parse($$"""{ "schemaVersion": 9, "options": [ {{body}} ] }""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_RejectsAsymmetricConflicts()
    {
        const string json = """
        { "schemaVersion": 1, "options": [
          { "id": "a", "kind": "Cfg", "text": "a", "valueType": "Bool",
            "description": { "esMx": "x", "enUs": "y" }, "expectedEffect": { "esMx": "x", "enUs": "y" },
            "evidence": "Observed", "source": "s", "conflictsWith": ["b"] },
          { "id": "b", "kind": "Cfg", "text": "b", "valueType": "Bool",
            "description": { "esMx": "x", "enUs": "y" }, "expectedEffect": { "esMx": "x", "enUs": "y" },
            "evidence": "Observed", "source": "s", "conflictsWith": [] }
        ] }
        """;

        Assert.Contains("pero no al revés", Assert.Throws<InvalidDataException>(() => Cs2OptionCatalog.Parse(json)).Message, StringComparison.Ordinal);
    }
}
