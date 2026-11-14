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

        // El catálogo real tiene tweaks; el perfil Safe muestra un subconjunto pequeño. No se afirma el
        // tamaño exacto del catálogo (R-N): crece con cada capa y un número fijo solo añade ruido al diff.
        Assert.NotEmpty(catalog.Definitions);
        Assert.Equal(3, safe.Length);
        Assert.True(safe.Length < catalog.Definitions.Count);

        // El bug era que los 3 salían "No aplicable" por la arquitectura.
        foreach (var definition in safe)
        {
            var result = EvaluateAll(definition, profile);
            Assert.True(result.IsMet, $"{definition.Id}: {result.Reason}");
        }
    }

    [Fact]
    public async Task RealCatalog_WithTheLocalizedArchitectureText_ConditionsAreMet_NotBroken()
    {
        // Este test afirmaba lo CONTRARIO antes del hotfix: que con "64 bits" (texto localizado de
        // Win32_OperatingSystem.OSArchitecture en es-MX) ninguna condición se cumplía. Ese era el
        // bug, no el comportamiento deseado: el test estaba codificando el defecto.
        //
        // Comportamiento correcto: "64 bits" se interpreta como X64 (ArchitectureInterpreter.Parse,
        // rama de compatibilidad) y las condiciones se cumplen igual que con el valor de enum.
        var catalog = await LoadRealCatalogAsync();
        var fromLocalizedText = Pn64() with { Os = Pn64().Os with { Architecture = "64 bits" } };

        var safe = ProfileSelector.Select(catalog.Definitions, TweakProfile.Safe).ToArray();
        var evaluated = safe.Select(d => EvaluateAll(d, fromLocalizedText)).ToArray();

        Assert.All(evaluated, r => Assert.True(
            r.IsMet,
            $"el texto localizado '64 bits' debe equivaler a X64; condición no cumplida: {r.Reason}"));

        // Y no es que todo pase por la cara: el texto produce el MISMO resultado que el enum.
        var fromEnum = safe.Select(d => EvaluateAll(d, Pn64())).ToArray();
        Assert.Equal(
            fromEnum.Select(r => (r.IsMet, r.IsKnown)),
            evaluated.Select(r => (r.IsMet, r.IsKnown)));

        // Control negativo: con un texto que NO es una arquitectura, no se cumple nada.
        var garbage = Pn64() with { Os = Pn64().Os with { Architecture = "procesador raro" } };
        var withGarbage = safe.Select(d => EvaluateAll(d, garbage)).ToArray();
        Assert.All(withGarbage, r => Assert.False(
            r.IsMet && r.IsKnown,
            $"un texto no reconocido no puede cumplir una condición conocida: {r.Reason}"));
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
        // Relación, no tamaño exacto del catálogo (R-N).
        Assert.True(counts.Single(c => c.Profile == TweakProfile.Safe).Count < catalog.Definitions.Count);
        // Y los cuatro perfiles no son decrecientes: Safe ⊆ Balanced ⊆ Competitive ⊆ Advanced.
        var ordered = counts.Select(c => c.Count).ToArray();
        Assert.True(ordered.Zip(ordered.Skip(1)).All(pair => pair.Second >= pair.First));
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
