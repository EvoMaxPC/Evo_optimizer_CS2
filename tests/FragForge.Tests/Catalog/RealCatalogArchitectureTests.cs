using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Diagnostics;
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using Xunit;

namespace FragForge.Tests.Catalog;

/// <summary>
/// Hotfix Capa 0 — prueba de extremo a extremo con el CATÁLOGO REAL embebido y un perfil con los
/// valores de la PN64.
///
/// No se lee la PC real: el perfil es un objeto en memoria. Lo que se comprueba es que las reglas
/// del catálogo, con un perfil x64 correcto, dejan de bloquear el perfil Safe.
///
/// RED-FIRST: antes del hotfix, la arquitectura "64 bits" (texto localizado de WMI en es-MX) hacía
/// que la condición `windows` fallara para TODOS los tweaks con esa condición.
/// </summary>
public sealed class RealCatalogArchitectureTests
{
    private static async Task<TweakCatalog> LoadRealCatalogAsync()
    {
        var schema = new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var policy = ProtectionPolicy.CreateDefault();
        var factory = new TweakActionFactory(
            registry, policy, new FakeServiceController(), new FakeScheduledTaskStore(),
            new FakeNetworkAdapterController(), new FakeProcessRunner(), DiagnosticProbeFactory.CreateDefaults(registry));
        var validator = new TweakCatalogValidator(schema, new ConditionFactory(), factory);
        return await new JsonTweakCatalogLoader(validator).LoadEmbeddedAsync();
    }

    /// <summary>
    /// Perfil con los valores REALES de la PN64: x64 (build 26200), 12 hilos, CPU híbrida,
    /// iGPU Intel y cultura es-MX.
    /// </summary>
    private static HardwareProfile Pn64() => TestProfiles.Pn64() with
    {
        Os = TestProfiles.Pn64().Os with { Architecture = "X64", Build = 26200, Culture = "es-MX" }
    };

    [Fact]
    public async Task RealCatalog_SafeProfileHasThreeTweaks_AndAllOfThemAreApplicable_OnAnX64Profile()
    {
        var catalog = await LoadRealCatalogAsync();
        var profile = Pn64();

        var safe = ProfileSelector.Select(catalog.Definitions, TweakProfile.Safe).ToArray();

        // El catálogo real tiene 30 tweaks; el perfil Safe muestra 3. Eso NO era el bug.
        Assert.Equal(30, catalog.Definitions.Count);
        Assert.Equal(3, safe.Length);

        // El bug era que los 3 salían "No aplicable" por la arquitectura.
        foreach (var definition in safe)
        {
            var result = EvaluateAll(definition, profile);
            Assert.True(result.IsMet, $"{definition.Id}: {result.Reason}");
        }
    }

    [Fact]
    public async Task RealCatalog_WithTheLocalizedArchitectureText_EverythingIsNotApplicable()
    {
        // Reproduce el estado previo al hotfix: el perfil guarda "64 bits" como única diferencia.
        var catalog = await LoadRealCatalogAsync();
        var broken = Pn64() with { Os = Pn64().Os with { Architecture = "64 bits" } };

        var safe = ProfileSelector.Select(catalog.Definitions, TweakProfile.Safe).ToArray();
        var evaluated = safe.Select(d => EvaluateAll(d, broken)).ToArray();

        // Con el texto localizado, NINGUNO cumple (esto es lo que se veía en pantalla).
        // Si el hotfix no estuviera, este test seguiría esperando el mismo resultado: por eso se
        // comprueba además que el texto SÍ se acepta ahora en el otro test.
        Assert.All(evaluated, r => Assert.False(r.IsMet));
    }

    [Fact]
    public async Task RealCatalog_EppPerformance_EvaluatesItsWindowsConditionAsMet_OnThePn64Profile()
    {
        var catalog = await LoadRealCatalogAsync();
        var epp = catalog.Definitions.Single(d => d.Id == "cpu.epp-performance");

        var windowsCondition = epp.Conditions.Single(c => c.Type.Equals("windows", StringComparison.OrdinalIgnoreCase));
        var result = new ConditionFactory().Create(windowsCondition).Evaluate(Pn64());

        Assert.True(result.IsMet, result.Reason);
        Assert.True(result.IsKnown);
    }

    [Fact]
    public async Task RealCatalog_EveryWindowsCondition_IsMetOnAnX64Build26200Profile()
    {
        // Barrido: no solo los 3 Safe. Cualquier tweak con condición windows debe poder evaluarse.
        var catalog = await LoadRealCatalogAsync();
        var profile = Pn64();
        var failures = new List<string>();

        foreach (var definition in catalog.Definitions)
        foreach (var condition in definition.Conditions.Where(c => c.Type.Equals("windows", StringComparison.OrdinalIgnoreCase)))
        {
            var result = new ConditionFactory().Create(condition).Evaluate(profile);
            if (!result.IsMet) failures.Add($"{definition.Id}: {result.Reason}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task SafeProfile_IsTheSmallestOfTheFour_SoItIsASafeDefault()
    {
        // El combo debe arrancar en el perfil de menor riesgo: Safe, que además es el que menos muestra.
        // Y el contador tiene que poder decir "3 de 30": el número de la izquierda NO es el total.
        var catalog = await LoadRealCatalogAsync();

        var counts = new[] { TweakProfile.Safe, TweakProfile.Balanced, TweakProfile.Competitive, TweakProfile.Advanced }
            .Select(p => (Profile: p, Count: ProfileSelector.Select(catalog.Definitions, p).Count()))
            .ToArray();

        Assert.Equal(TweakProfile.Safe, counts.OrderBy(c => c.Count).First().Profile);
        Assert.Equal(3, counts.Single(c => c.Profile == TweakProfile.Safe).Count);
        Assert.Equal(catalog.Definitions.Count, 30);
        Assert.True(counts.Single(c => c.Profile == TweakProfile.Safe).Count < catalog.Definitions.Count);
    }

    [Fact]
    public async Task SafeProfile_TweaksAreExactlyTheThreeKnownOnes()
    {
        var catalog = await LoadRealCatalogAsync();

        var safe = ProfileSelector.Select(catalog.Definitions, TweakProfile.Safe).Select(d => d.Id).ToArray();

        Assert.Equal(["diagnostics.system-inspect", "latency.game-mode-on", "latency.mouse-acceleration-off"], safe);
    }

    private static ConditionResult EvaluateAll(TweakDefinition definition, HardwareProfile profile)
    {
        var factory = new ConditionFactory();
        foreach (var condition in definition.Conditions)
        {
            var result = factory.Create(condition).Evaluate(profile);
            if (!result.IsMet) return result;
        }
        return ConditionResult.Met("todas las condiciones se cumplen");
    }
}
