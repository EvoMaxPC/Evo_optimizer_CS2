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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FragForge.Tests.Inventory;

/// <summary>
/// La regla central de la Capa 2: una acción <c>Service</c> solo es válida si el servicio está en
/// <c>service-tiers.json</c> como T1 o T2. Se prueba con el validador real y el catálogo real.
/// </summary>
public sealed class TierValidatorIntegrationTests
{
    private static TweakCatalogValidator Validator(ServiceTierCatalog? tiers)
    {
        // Misma fabrica que RealCatalogDenylistTests.RealFactory: un DI minimo no alcanza (faltan IServiceController, etc.).
        var registry = new FakeRegistry();
        ITweakActionFactory factory = new TweakActionFactory(
            registry,
            ProtectionPolicy.CreateDefault(),
            new FakeServiceController(),
            new FakeScheduledTaskStore(),
            new FakeNetworkAdapterController(),
            new FakeProcessRunner(),
            DiagnosticProbeFactory.CreateDefaults(registry),
            adapterResolver: new FragForge.Actions.Network.UnsupportedNetworkAdapterResolver());
        return new TweakCatalogValidator(
            new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()),
            new ConditionFactory(),
            factory,
            tiers);
    }

    private static TweakDefinition ServiceTweak(string id, string service) => new()
    {
        Id = id,
        Category = "Debloat",
        Title = new LocalizedText { EsMx = id, EnUs = id },
        Description = new LocalizedText { EsMx = id, EnUs = id },
        Evidence = EvidenceLevel.Low,
        Risk = RiskLevel.Safe,
        RequiresAdmin = true,
        Actions = [new ActionSpec { Kind = "Service", Parameters = JsonDocument.Parse($$"""{"name":"{{service}}","startMode":"Disabled"}""").RootElement.Clone() }],
        ExpectedImpact = new ExpectedImpact(),
        References = ["https://learn.microsoft.com/"]
    };

    [Fact]
    public void ServiceInT2_PassesValidation()
    {
        var errors = Validator(ServiceTierCatalog.LoadEmbedded()).ValidateCatalog([ServiceTweak("test.diagtrack", "DiagTrack")]);
        Assert.Empty(errors);
    }

    [Fact]
    public void ServiceInT3_FailsValidation()
    {
        var errors = Validator(ServiceTierCatalog.LoadEmbedded()).ValidateCatalog([ServiceTweak("test.pcasvc", "PcaSvc")]);
        Assert.Contains(errors, e => e.Contains("PcaSvc", StringComparison.Ordinal) && e.Contains("T1/T2", StringComparison.Ordinal));
    }

    [Fact]
    public void CoreService_FailsValidation()
    {
        var errors = Validator(ServiceTierCatalog.LoadEmbedded()).ValidateCatalog([ServiceTweak("test.rpcss", "RpcSs")]);
        Assert.Contains(errors, e => e.Contains("RpcSs", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownService_FailsValidation_ByDefaulting()
    {
        // Un servicio que no está en la tabla es T0 por omisión: jamás aplicable.
        var errors = Validator(ServiceTierCatalog.LoadEmbedded()).ValidateCatalog([ServiceTweak("test.inventado", "ServicioQueNoExiste")]);
        Assert.Contains(errors, e => e.Contains("ServicioQueNoExiste", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutATierTable_TheRuleIsNotApplied_SoLayer0And1KeepWorking()
    {
        var errors = Validator(null).ValidateCatalog([ServiceTweak("test.diagtrack", "DiagTrack")]);
        Assert.Empty(errors);
    }

    [Fact]
    public void EveryEmbeddedCatalogTweak_PassesWithTheRealTiers()
    {
        // Los 16 tweaks de debloat y todo el catálogo previo deben seguir validando con la regla nueva.
        var assembly = typeof(JsonTweakCatalogLoader).Assembly;
        var documents = new List<(string Name, string Json)>();
        foreach (var resource in assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("FragForge.Tweaks.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            documents.Add((resource, reader.ReadToEnd()));
        }

        var definitions = documents.Select(d => (d.Name, Definition: JsonSerializer.Deserialize<TweakDefinition>(d.Json, FragForge.Core.Logging.JsonDefaults.Options)!)).ToArray();
        var errors = Validator(ServiceTierCatalog.LoadEmbedded()).ValidateCatalog(definitions.Select(d => d.Definition).ToArray());
        Assert.Empty(errors);
    }

    [Fact]
    public void EveryEmbeddedCatalogTweak_HasABilingualWhenNotToApply_WhenItMutatesSomething()
    {
        var assembly = typeof(JsonTweakCatalogLoader).Assembly;
        var problems = new List<string>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("FragForge.Tweaks.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var definition = JsonSerializer.Deserialize<TweakDefinition>(reader.ReadToEnd(), FragForge.Core.Logging.JsonDefaults.Options)!;
            if (definition.ReadOnly) continue;
            if (definition.WhenNotToApply is null || definition.WhenNotToApply.EsMx.Length == 0 || definition.WhenNotToApply.EnUs.Length == 0)
                problems.Add(definition.Id);
        }
        Assert.Empty(problems);
    }
}
