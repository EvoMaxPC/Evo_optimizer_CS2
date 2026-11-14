using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Diagnostics;
using Xunit;

namespace FragForge.Tests.Catalog;

public sealed class CatalogTests
{
    private static (JsonTweakCatalogLoader Loader, JsonSchemaValidator Schema) CreateLoader()
    {
        var schema = new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var policy = FragForge.Core.Persistence.ProtectionPolicy.CreateDefault();
        var services = new FakeServiceController();
        var tasks = new FakeScheduledTaskStore();
        var network = new FakeNetworkAdapterController();
        var runner = new FakeProcessRunner();
        var factory = new TweakActionFactory(registry, policy, services, tasks, network, runner, DiagnosticProbeFactory.CreateDefaults(registry));
        var validator = new TweakCatalogValidator(schema, new ConditionFactory(), factory);
        return (new JsonTweakCatalogLoader(validator), schema);
    }

    private static readonly string[] SeedIds =
    [
        "cpu.power-throttling-off",
        "cs2.cpu-priority-high",
        "cs2.fullscreen-optimizations-off",
        "diagnostics.system-inspect",
        "latency.game-dvr-capture-off",
        "latency.game-mode-on",
        "latency.global-timer-resolution",
        "latency.mouse-acceleration-off",
        "network.i225v-eee-off",
        "power.fragforge-performance"
    ];

    [Fact]
    public async Task EmbeddedCatalog_LoadsAtLeastTheTenSeedTweaks_WithBothCultures()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        // El catálogo crece por capas: ya no hay un número exacto que cuidar. Lo que no puede
        // pasar es PERDER un tweak semilla, así que se exige el mínimo y la presencia de cada id.
        Assert.True(catalog.Definitions.Count >= 10, $"El catálogo quedó en {catalog.Definitions.Count} tweaks; deben ser al menos 10.");
        Assert.All(SeedIds, id => Assert.Contains(catalog.Definitions, d => d.Id == id));
        Assert.All(catalog.Definitions, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Title.EsMx));
            Assert.False(string.IsNullOrWhiteSpace(d.Title.EnUs));
            Assert.NotEmpty(d.References);
            Assert.NotEmpty(d.Actions);
        });
        Assert.Single(catalog.Definitions, d => d.ReadOnly);
    }

    [Fact]
    public async Task Catalog_IsSortedAndHashesAreStableAndDistinct()
    {
        var (loader, _) = CreateLoader();
        var first = await loader.LoadEmbeddedAsync();
        var second = await loader.LoadEmbeddedAsync();
        Assert.Equal(first.Definitions.Select(d => d.Id), first.Definitions.Select(d => d.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        var hashes = first.Definitions.Select(d => first.GetDefinitionHash(d.Id)).ToArray();
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
        Assert.Equal(hashes, second.Definitions.Select(d => second.GetDefinitionHash(d.Id)).ToArray());
    }

    [Fact]
    public async Task Catalog_UnknownTweak_Throws()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("does.not.exist"));
        Assert.False(catalog.TryGet("does.not.exist", out _));
    }

    [Fact]
    public void Schema_RejectsMissingRequiredFields_AndUnknownProperties()
    {
        var (_, schema) = CreateLoader();
        using var missing = JsonDocument.Parse("""{"id":"x.y","category":"Latency"}""");
        var errors = schema.Validate(missing.RootElement);
        Assert.Contains(errors, e => e.Contains("title", StringComparison.Ordinal));
        using var extra = JsonDocument.Parse("""{"id":"x.y","category":"Latency","title":{"es-MX":"a","en-US":"b"},"description":{"es-MX":"a","en-US":"b"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry"}],"references":["r"],"placebo":true}""");
        Assert.Contains(schema.Validate(extra.RootElement), e => e.Contains("placebo", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_RejectsBadCategoryAndMissingEnglish()
    {
        var (_, schema) = CreateLoader();
        using var document = JsonDocument.Parse("""{"id":"x.y","category":"Overclock","title":{"es-MX":"a"},"description":{"es-MX":"a","en-US":"b"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry"}],"references":["r"]}""");
        var errors = schema.Validate(document.RootElement);
        Assert.Contains(errors, e => e.Contains("enum", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("category", StringComparison.Ordinal) || e.Contains("en-US", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsDuplicateIdsAndUnknownKinds()
    {
        var (loader, _) = CreateLoader();
        var duplicate = """{"id":"dup.one","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry","parameters":{"values":[]}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("a.json", duplicate)]));
        Assert.Contains(ex.Errors, e => e.Contains("values", StringComparison.Ordinal) || e.Contains("Registry", StringComparison.Ordinal));
        var unknownKind = duplicate.Replace("\"dup.one\"", "\"dup.two\"").Replace("\"Registry\"", "\"Teleport\"");
        var ex2 = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("b.json", unknownKind)]));
        Assert.Contains(ex2.Errors, e => e.Contains("Teleport", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsSelfDependencyAndMissingReferences()
    {
        var (loader, _) = CreateLoader();
        var selfDependency = """{"id":"dup.three","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","dependsOn":["dup.three"],"expectedImpact":{},"actions":[{"kind":"IfeoPerfOptions","parameters":{"imageName":"cs2.exe","priorityClass":3}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("c.json", selfDependency)]));
        Assert.Contains(ex.Errors, e => e.Contains("consigo mismo", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RequiresAdvancedRiskForPowerShellAndBcdedit()
    {
        var (loader, _) = CreateLoader();
        var bcd = """{"id":"dup.four","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"Experimental","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Bcdedit","parameters":{"element":"disabledynamictick","value":"yes"}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("d.json", bcd)]));
        Assert.Contains(ex.Errors, e => e.Contains("Advanced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EverySeedTweak_HasHonestMetadata()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        foreach (var definition in catalog.Definitions.Where(d => !d.ReadOnly))
        {
            Assert.True(definition.RequiresAdmin);
            Assert.NotNull(definition.WhenNotToApply);
            Assert.False(string.IsNullOrWhiteSpace(definition.Notes?.EsMx));
        }
        Assert.DoesNotContain(catalog.Definitions, d => d.Actions.Any(a => a.Kind.Equals("Service", StringComparison.OrdinalIgnoreCase) && a.Parameters.ToString().Contains("WinDefend", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Semilla con opt-in ANTERIOR al hotfix: conserva su perfil, no es debloat.</summary>
    private static readonly string[] LegacyOptInSeeds =
    [
        "cs2.fullscreen-optimizations-off",
        "network.i225v-eee-off",
        "power.fragforge-performance"
    ];

    [Fact]
    public async Task ProfileSelection_MatchesDocumentedRiskGates()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        var safe = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Safe);
        var balanced = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Balanced);
        var competitive = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Competitive);
        var advanced = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Advanced);

        Assert.DoesNotContain(safe, d => d.Risk != RiskLevel.Safe);
        Assert.Contains(balanced, d => d.Id == "latency.game-dvr-capture-off");
        // Safe y Balanced ya excluían todo opt-in: sigue igual.
        Assert.DoesNotContain(balanced, d => d.Tags.Contains("opt-in"));
        // Los semilla que ya eran opt-in conservan su perfil (no se toca nada fuera del debloat).
        Assert.Contains(competitive, d => d.Id == "power.fragforge-performance");
        // Advanced ya no es "todo": el debloat opt-in queda fuera.
        Assert.DoesNotContain(advanced, d => d.Id.StartsWith("debloat.", StringComparison.OrdinalIgnoreCase));

        var debloat = catalog.Definitions.Count(d => d.Id.StartsWith("debloat.", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(catalog.Definitions.Count - debloat, advanced.Count);
        Assert.Contains(advanced, d => LegacyOptInSeeds.Contains(d.Id));
    }

    [Theory]
    [InlineData(TweakProfile.Safe)]
    [InlineData(TweakProfile.Balanced)]
    [InlineData(TweakProfile.Competitive)]
    [InlineData(TweakProfile.Advanced)]
    public async Task DebloatOptIn_IsNeverSelectedByAnyProfile(TweakProfile profile)
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        var selected = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, profile);

        // El debloat de la Capa 2 es opt-in de verdad: aparece SOLO si se pide por id.
        Assert.DoesNotContain(selected, d => d.Id.StartsWith("debloat.", StringComparison.OrdinalIgnoreCase));

        // Ningún tweak CON opt-in entra automáticamente salvo los semilla que ya lo eran.
        Assert.DoesNotContain(selected, d => d.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase)
            && !LegacyOptInSeeds.Contains(d.Id, StringComparer.OrdinalIgnoreCase));

        // Pero el tweak existe en el catálogo: sigue siendo aplicable por id explícito.
        Assert.Contains(catalog.Definitions, d => d.Id.StartsWith("debloat.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EveryDebloatTweak_IsSelectableOnlyByExplicitId()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        var debloat = catalog.Definitions.Where(d => d.Id.StartsWith("debloat.", StringComparison.OrdinalIgnoreCase)).ToArray();
        // El catálogo crece por capas: mínimo, nunca un número exacto (regla R-N).
        Assert.True(debloat.Length >= 16, $"El debloat quedó en {debloat.Length} tweaks; deben ser al menos 16.");
        foreach (var definition in debloat)
        {
            Assert.Contains("windows", definition.Conditions.Select(c => c.Type), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("atlas", definition.Conditions.Select(c => c.Type), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(1, definition.Tags.Count(t => string.Equals(t, "opt-in", StringComparison.OrdinalIgnoreCase)));
            // Permiso explícito por id: única vía de entrada, ni siquiera en Advanced.
            Assert.False(FragForge.Core.Engine.ProfileSelector.Allows(TweakProfile.Advanced, definition));
        }
    }

    [Fact]
    public void APolicyRejectedAction_FailsTheWholeCatalogLoad_InsteadOfIsolatingTheTweak()
    {
        // Punto 5 del hotfix: este es el comportamiento REAL de hoy, fijado para que nadie lo dé por
        // bueno sin querer. ValidateCatalog llama a ITweakActionFactory.Create y acumula su excepción:
        // un solo tweak con destino protegido tumba la carga ENTERA, no se aísla.
        // NO se arregla en este hotfix (se pidió reportar). El test fallará el día que se decida aislar.
        const string Protected = """
        {"id":"probe.protected","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Service","parameters":{"name":"WinDefend","startMode":"Manual"}}],"references":["r"]}
        """;
        const string Good = """
        {"id":"probe.good","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Service","parameters":{"name":"DiagTrack","startMode":"Manual"}}],"references":["r"]}
        """;

        var (loader, _) = CreateLoader();

        // El tweak sano, solo, carga perfectamente: el catálogo no está roto de origen.
        Assert.Equal("probe.good", Assert.Single(loader.LoadDocuments([("good.json", Good)]).Definitions).Id);

        // Con el tweak protegido presente no se pierde solo ese: se pierde TODO el catálogo.
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("good.json", Good), ("protected.json", Protected)]));
        Assert.Contains(ex.Errors, e => e.Contains("probe.protected", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("protegido", StringComparison.OrdinalIgnoreCase));

        // Y por tanto el tweak bueno NO llega a cargarse: esa es la consecuencia que se reporta.
        Assert.DoesNotContain(ex.Errors, e => e.Contains("probe.good", StringComparison.Ordinal));
    }

    [Fact]
    public void Localization_LoadsBothLanguagesWithTheSameKeys()
    {
        var es = FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync("es-MX").GetAwaiter().GetResult();
        var en = FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync("en-US").GetAwaiter().GetResult();
        Assert.Equal("es-MX", es.Culture);
        Assert.Equal("en-US", en.Culture);
        Assert.NotEqual(es["nav.scan"], en["nav.scan"]);
        Assert.Equal("Operación en curso…", es["busy"]);
        Assert.Equal("Operation in progress…", en["busy"]);
        Assert.Equal("ACEPTO EL RIESGO", es["risk.phrase"]);
    }
}
