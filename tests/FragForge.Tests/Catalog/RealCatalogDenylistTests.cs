using System.Text.Json;
using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Diagnostics;
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Core.Tiers;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using Xunit;

namespace FragForge.Tests.Catalog;

/// <summary>
/// Contrato de seguridad del catálogo: TODO el catálogo real embebido debe cargar contra la
/// <c>denylist.json</c> real del repositorio, con el <see cref="ITweakActionFactory"/> real.
///
/// Se construye el mismo validador que producción (<c>CompositionRoot</c>): schema embebido,
/// fábrica de condiciones real, fábrica de acciones real y <see cref="ServiceTierCatalog.LoadEmbedded"/>.
/// Si un tweak fuera rechazado por la política (ProtectedTargetException / PolicyRejected), la carga
/// completa fallaría; este test lo atribuye al id concreto y los lista TODOS, no solo el primero.
/// </summary>
public sealed class RealCatalogDenylistTests
{
    /// <summary>La fábrica real con la denylist real, sobre dobles de plataforma.</summary>
    private static ITweakActionFactory RealFactory(FakeRegistry registry) => new TweakActionFactory(
        registry,
        ProtectionPolicy.CreateDefault(),
        new FakeServiceController(),
        new FakeScheduledTaskStore(),
        new FakeNetworkAdapterController(),
        new FakeProcessRunner(),
        DiagnosticProbeFactory.CreateDefaults(registry));

    /// <summary>Reproduce el validador de CompositionRoot, incluidos los tiers.</summary>
    private static JsonTweakCatalogLoader ProductionLikeLoader(ITweakActionFactory factory) => new(new TweakCatalogValidator(
        new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()),
        new ConditionFactory(),
        factory,
        ServiceTierCatalog.LoadEmbedded()));

    [Fact]
    public async Task RealCatalog_PassesTheRealDenylist()
    {
        var registry = new FakeRegistry();
        var factory = RealFactory(registry);
        var loader = ProductionLikeLoader(factory);

        TweakCatalog? catalog = null;
        IReadOnlyList<string> errors = [];
        try { catalog = await loader.LoadEmbeddedAsync(); }
        catch (CatalogValidationException ex) { errors = ex.Errors; }

        // Se listan TODOS los rechazos, no solo el primero: el mensaje del assert es el reporte.
        Assert.True(errors.Count == 0, $"El catálogo real no pasa la denylist real ({errors.Count} errores):\n  - " + string.Join("\n  - ", errors));
        Assert.NotNull(catalog);
        Assert.True(catalog!.Definitions.Count >= 10, $"El catálogo real quedó en {catalog.Definitions.Count} tweaks.");

        // Refuerzo por tweak: atribuye el fallo a un id concreto si alguna acción fuera rechazada.
        var rejected = new List<string>();
        foreach (var definition in catalog.Definitions)
        foreach (var action in definition.Actions)
        {
            try { _ = factory.Create(action); }
            catch (Exception filtered) when (filtered is ProtectedTargetException or CatalogValidationException)
            {
                rejected.Add($"{definition.Id} [{action.Kind}]: {filtered.Message}");
            }
        }
        Assert.True(rejected.Count == 0, "Tweaks rechazados por la política real:\n  - " + string.Join("\n  - ", rejected));
    }

    [Fact]
    public async Task RealCatalog_LoadsOneDefinitionPerEmbeddedResource()
    {
        // Guardia contra un catálogo que "pasa" porque no cargó nada.
        var catalog = await ProductionLikeLoader(RealFactory(new FakeRegistry())).LoadEmbeddedAsync();
        var resources = typeof(JsonTweakCatalogLoader).Assembly.GetManifestResourceNames()
            .Count(n => n.StartsWith("FragForge.Tweaks.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));
        Assert.True(resources > 0);
        Assert.Equal(resources, catalog.Definitions.Count);
    }

    [Fact]
    public void ValidatorWithTiers_RejectsT0Service_AndWithoutTiersDoesNot()
    {
        // Si producción dejara de pasar los tiers, el modo sin tiers lo delataría: aquí se fija que
        // la diferencia existe y que el validador de producción (con tiers) sí rechaza un T0.
        const string T0 = """
        {"id":"probe.t0","category":"Debloat","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"Low","risk":"Safe","requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Service","parameters":{"name":"RpcSs","startMode":"Manual"}}],"references":["r"]}
        """;
        var definition = JsonSerializer.Deserialize<TweakDefinition>(T0, FragForge.Core.Logging.JsonDefaults.Options)!;
        var factory = RealFactory(new FakeRegistry());

        var withTiers = new TweakCatalogValidator(new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()), new ConditionFactory(), factory, ServiceTierCatalog.LoadEmbedded());
        var withoutTiers = new TweakCatalogValidator(new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()), new ConditionFactory(), factory, null);

        Assert.Empty(withoutTiers.ValidateCatalog([definition]));
        Assert.Contains(withTiers.ValidateCatalog([definition]), e => e.Contains("RpcSs", StringComparison.Ordinal));
    }
}
