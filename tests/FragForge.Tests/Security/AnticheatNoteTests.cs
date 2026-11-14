using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using Xunit;

namespace FragForge.Tests.Security;

/// <summary>
/// Capa 10.2. El aviso anticheat es ADITIVO y opcional; cuando falta, la UI muestra el contrato común en vez de
/// una celda vacía. Y el contrato nunca puede afirmar compatibilidad con un anticheat concreto.
/// </summary>
public sealed class AnticheatNoteTests
{
    [Fact]
    public void MissingNote_FallsBackToTheCommonContract()
    {
        var text = AnticheatNote.Resolve(null, "es-MX");

        Assert.Contains("No modifica el proceso ni los archivos del juego", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingNote_RespectsTheCulture()
    {
        Assert.Contains("Does not modify the game process", AnticheatNote.Resolve(null, "en-US"), StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogNote_WinsOverTheDefault()
    {
        var note = new LocalizedText { EsMx = "aviso propio es", EnUs = "own en note" };

        Assert.Equal("aviso propio es", AnticheatNote.Resolve(note, "es-MX"));
        Assert.Equal("own en note", AnticheatNote.Resolve(note, "en-US"));
    }

    /// <summary>El campo es ADITIVO: un JSON sin <c>anticheatNote</c> sigue deserializando.</summary>
    [Fact]
    public void TheFieldIsOptional()
    {
        var json = MinimalJson();

        var definition = JsonSerializer.Deserialize<TweakDefinition>(json, JsonDefaults.Options);

        Assert.NotNull(definition);
        Assert.Null(definition!.AnticheatNote);
    }

    /// <summary>Y si está, se deserializa con sus dos idiomas.</summary>
    [Fact]
    public void TheFieldRoundTripsWhenPresent()
    {
        var json = MinimalJson().TrimEnd('}') + """
            , "anticheatNote": { "es-MX": "no toca el proceso", "en-US": "does not touch the process" } }
            """;

        var definition = JsonSerializer.Deserialize<TweakDefinition>(json, JsonDefaults.Options);

        Assert.NotNull(definition?.AnticheatNote);
        Assert.Equal("no toca el proceso", definition!.AnticheatNote!.EsMx);
    }

    /// <summary>
    /// Ningún tweak del catálogo real puede AFIRMAR compatibilidad con un anticheat por su nombre. El texto
    /// debe remitir al proveedor. Es la parte del contrato que se puede comprobar sin fuentes externas.
    /// </summary>
    [Fact]
    public async Task NoRealTweakClaimsAnticheatCompatibility()
    {
        var catalog = await new JsonTweakCatalogLoader(NewValidator()).LoadEmbeddedAsync();

        var offences = catalog.Definitions
            .Where(d => d.AnticheatNote is not null)
            .Where(d => ContainsClaim(d.AnticheatNote!.EsMx) || ContainsClaim(d.AnticheatNote!.EnUs))
            .Select(d => d.Id)
            .ToArray();

        Assert.Empty(offences);

        static bool ContainsClaim(string text) =>
            text.Contains("seguro con", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("compatible con", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("safe with", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("allowed by", StringComparison.OrdinalIgnoreCase);
    }

    private static string MinimalJson() => """
        {
          "id": "test.anticheat", "category": "Cs2",
          "title": { "es-MX": "t", "en-US": "t" },
          "description": { "es-MX": "d", "en-US": "d" },
          "evidence": "Low", "risk": "Safe", "requiresAdmin": true,
          "actions": [ { "kind": "InspectOnly", "parameters": { "probe": "vbs" } } ],
          "expectedImpact": {}, "references": [ "https://example.invalid/x" ]
        }
        """;

    /// <summary>
    /// Mismo arnés que <c>CatalogTests.CreateLoader</c>: árbol de acciones real sobre dobles, para que la
    /// validación del catálogo embebido sea la misma que en producción.
    /// </summary>
    private static TweakCatalogValidator NewValidator()
    {
        var schema = new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var policy = FragForge.Core.Persistence.ProtectionPolicy.CreateDefault();
        var factory = new FragForge.Actions.DependencyInjection.TweakActionFactory(
            registry, policy, new FakeServiceController(), new FakeScheduledTaskStore(),
            new FakeNetworkAdapterController(), new FakeProcessRunner(),
            FragForge.Actions.Diagnostics.DiagnosticProbeFactory.CreateDefaults(registry));
        return new TweakCatalogValidator(schema, new FragForge.Core.Conditions.ConditionFactory(), factory);
    }
}
