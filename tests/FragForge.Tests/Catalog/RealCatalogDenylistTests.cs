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
        DiagnosticProbeFactory.CreateDefaults(registry),
        adapterResolver: new FragForge.Actions.Network.UnsupportedNetworkAdapterResolver());

    /// <summary>
    /// Un proveedor de acciones que revienta NO puede tumbar la app: la excepción se convierte en fallo de
    /// validación con el id del tweak (Capa 10.4). Antes salía de ValidatePolicy y llegaba al llamador.
    /// </summary>
    private sealed class ExplodingActionFactory : ITweakActionFactory
    {
        public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Registry" };
        public ITweakAction Create(ActionSpec specification) => throw new InvalidOperationException("el proveedor reventó");
    }

    /// <summary>Un factory que lanza OperationCanceledException: la cancelación no es un fallo de validación.</summary>
    private sealed class CancellingActionFactory : ITweakActionFactory
    {
        public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Registry" };
        public ITweakAction Create(ActionSpec specification) => throw new OperationCanceledException("cancelado por el usuario");
    }

    /// <summary>
    /// Un tweak de forma válida pero con un destino que NO está en la denylist: así el único error posible es
    /// el del factory que revienta. Sin esto, un fallo de denylist ensuciaría el assert.
    /// </summary>
    private const string HarmlessTweak = """
        {"id":"probe.harmless","category":"Debloat","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"Low","risk":"Safe","requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Registry","parameters":{"values":[{"key":"HKCU\\Software\\FragForgeTests","name":"V","kind":"DWord","value":1,"mode":"Set"}]}}],"references":["r"]}
        """;

    [Fact]
    public void AFactoryThatThrowsAnyException_BecomesAValidationErrorWithTheTweakId()
    {
        var definition = JsonSerializer.Deserialize<TweakDefinition>(HarmlessTweak, FragForge.Core.Logging.JsonDefaults.Options)!;
        var validator = new TweakCatalogValidator(new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()),
            new ConditionFactory(), new ExplodingActionFactory());

        // No lanza: devuelve el error.
        var errors = validator.ValidateCatalog([definition]);

        Assert.Contains(errors, e => e.Contains("probe.harmless", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("el proveedor reventó", StringComparison.Ordinal));
        // Y el mensaje aparece UNA sola vez: las dos funciones no lo duplican.
        Assert.Single(errors, e => e.Contains("el proveedor reventó", StringComparison.Ordinal));
    }

    [Fact]
    public void AFactoryThatIsCancelled_PropagatesTheCancellation_InsteadOfCallingItAnInvalidTweak()
    {
        var definition = JsonSerializer.Deserialize<TweakDefinition>(HarmlessTweak, FragForge.Core.Logging.JsonDefaults.Options)!;
        var validator = new TweakCatalogValidator(new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()),
            new ConditionFactory(), new CancellingActionFactory());

        Assert.Throws<OperationCanceledException>(() => validator.ValidateCatalog([definition]));
    }

    // ---- D2: la FIRMA PUBLICA de ValidatePolicy reporta TODO lo que atrapa ----
    //
    // Las dos rutas combinadas (ValidateCatalog y LoadDocumentsResilient) pasan reportForms: false para no
    // duplicar, porque ValidateSingle ya anoto la forma. Pero quien llame SOLO a la publica no puede
    // perder el fallo: eso es lo que fijan estos dos tests.

    [Fact]
    public void PublicValidatePolicy_ReportsByItselfEvenWithoutValidateSingle()
    {
        var definition = JsonSerializer.Deserialize<TweakDefinition>(HarmlessTweak, FragForge.Core.Logging.JsonDefaults.Options)!;
        var validator = new TweakCatalogValidator(new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()),
            new ConditionFactory(), new ExplodingActionFactory());

        // SOLO la publica: sin ValidateSingle. El fallo no puede perderse.
        var errors = validator.ValidatePolicy(definition);

        var error = Assert.Single(errors);
        Assert.Contains("probe.harmless", error, StringComparison.Ordinal);   // con el id del tweak
        Assert.Contains("el proveedor reventó", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicValidatePolicy_PropagatesCancellation()
    {
        var definition = JsonSerializer.Deserialize<TweakDefinition>(HarmlessTweak, FragForge.Core.Logging.JsonDefaults.Options)!;
        var validator = new TweakCatalogValidator(new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()),
            new ConditionFactory(), new CancellingActionFactory());

        Assert.Throws<OperationCanceledException>(() => validator.ValidatePolicy(definition));
        Assert.Throws<OperationCanceledException>(() => validator.ValidateCatalog([definition]));
    }

    /// <summary>
    /// La ruta combinada reporta EXACTAMENTE 1. Antes de D2 esto ya pasaba porque la publica descartaba la
    /// forma; ahora pasa porque se le dice explicitamente (reportForms: false). Si alguien quita esa bandera,
    /// este test da 2 y falla: es el guardia contra el mensaje duplicado.
    /// </summary>
    [Fact]
    public void TheCombinedRoute_ReportsTheFormErrorExactlyOnce()
    {
        var definition = JsonSerializer.Deserialize<TweakDefinition>(HarmlessTweak, FragForge.Core.Logging.JsonDefaults.Options)!;
        var validator = new TweakCatalogValidator(new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()),
            new ConditionFactory(), new ExplodingActionFactory());

        var catalog = validator.ValidateCatalog([definition]);
        Assert.Single(catalog, e => e.Contains("el proveedor reventó", StringComparison.Ordinal));

        // Y la ruta RESILIENTE (TweakCatalog.cs) tambien: un solo rechazo por la entrada.
        var loader = new JsonTweakCatalogLoader(validator);
        var loaded = loader.LoadDocumentsResilient([("boom.json", HarmlessTweak)]);
        Assert.Contains(loaded.LoadReport.Rejected, r => r.Reason.Contains("el proveedor reventó", StringComparison.Ordinal));
        Assert.Single(loaded.LoadReport.Rejected, r => r.Reason.Contains("el proveedor reventó", StringComparison.Ordinal));
    }

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

        Assert.Contains(withoutTiers.ValidateCatalog([definition]), e => e.Contains("RpcSs", StringComparison.Ordinal)); // el denylist real rechaza RpcSs aun sin tiers
        Assert.Contains(withTiers.ValidateCatalog([definition]), e => e.Contains("RpcSs", StringComparison.Ordinal));

        // La regla de tiers es otra capa: un servicio fuera del denylist y desconocido para los tiers solo lo frena el validador con tiers.
        var unlisted = JsonSerializer.Deserialize<TweakDefinition>(T0.Replace("probe.t0", "probe.unlisted").Replace("RpcSs", "FragForgeProbeUnlistedSvc"), FragForge.Core.Logging.JsonDefaults.Options)!;
        Assert.Empty(withoutTiers.ValidateCatalog([unlisted]));
        Assert.NotEmpty(withTiers.ValidateCatalog([unlisted]));
    }
}
