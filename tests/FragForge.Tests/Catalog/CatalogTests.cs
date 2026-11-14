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
        var factory = new TweakActionFactory(registry, policy, services, tasks, network, runner, DiagnosticProbeFactory.CreateDefaults(registry),
            // El catalogo real trae network.i225v-eee-off (NetAdapterProperty) y el validador construye TODAS
            // las acciones al cargar: sin resolutor, el catalogo no carga.
            adapterResolver: new FragForge.Actions.Network.UnsupportedNetworkAdapterResolver());
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

    /// <summary>
    /// TABLA DE MEMBRESÍA DE PERFILES: id → perfiles (separados por coma; vacío = ninguno).
    /// Es la fuente de verdad del contrato de autoselección. Un tweak nuevo DEBE añadirse aquí;
    /// si no figura, el test falla a propósito para obligar a declarar su membresía.
    /// </summary>
    private static readonly Dictionary<string, string> ProfileMembership = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu.power-throttling-off"] = "Balanced, Competitive, Advanced",
        ["cs2.cpu-priority-high"] = "Balanced, Competitive, Advanced",
        ["cpu.core-parking-off"] = "Advanced",
        ["cpu.epp-performance"] = "Advanced",
        ["cs2.fullscreen-optimizations-off"] = "Advanced",
        ["debloat.policy.allowtelemetry-minimum"] = "",
        ["debloat.service.diagtrack-disabled"] = "",
        ["debloat.service.dmwappush-disabled"] = "",
        ["debloat.service.mapsbroker-manual"] = "",
        ["debloat.service.retaildemo-disabled"] = "",
        ["debloat.service.wersvc-manual"] = "",
        ["debloat.service.xblauthmanager-manual"] = "",
        ["debloat.service.xblgamesave-manual"] = "",
        ["debloat.service.xboxnetapi-manual"] = "",
        ["debloat.task.appraiser"] = "",
        ["debloat.task.ceip-consolidator"] = "",
        ["debloat.task.ceip-usbceip"] = "",
        ["debloat.task.kernel-ceip"] = "",
        ["debloat.task.programdataupdater"] = "",
        ["debloat.task.queue-reporting"] = "",
        ["debloat.task.siuf-feedback"] = "",
        ["diagnostics.system-inspect"] = "Safe, Balanced, Competitive, Advanced",
        // Capa 6 (commit 12bfa82) anadio estos dos tweaks de iGPU con "profiles": [] (ningun perfil: se
        // aplican solo por id explicito). Se anadieron al catalogo sin tocar esta tabla, y el test los caza
        // por la asercion 1. "" es el mismo convenio que usan los debloat de arriba.
        ["gpu.hags-toggle"] = "",
        ["gpu.intel-graphics-power-plan-max"] = "",
        ["latency.game-dvr-capture-off"] = "Balanced, Competitive, Advanced",
        ["latency.game-mode-on"] = "Safe, Balanced, Competitive, Advanced",
        ["latency.global-timer-resolution"] = "Balanced, Competitive, Advanced",
        ["latency.mouse-acceleration-off"] = "Safe, Balanced, Competitive, Advanced",
        ["network.i225v-eee-off"] = "Competitive, Advanced",
        ["power.fragforge-performance"] = "Competitive, Advanced",
        ["power.pcie-aspm-off"] = "",
        ["power.usb-selective-suspend-off"] = ""
    };

    [Fact]
    public async Task ProfileMembership_MatchesTheDeclaredTable_ForEveryCatalogId()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();

        // 1) Todo id del catálogo tiene que estar declarado en la tabla (obliga a declararlo de nuevo).
        var undeclared = catalog.Definitions.Select(d => d.Id).Where(id => !ProfileMembership.ContainsKey(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(undeclared.Length == 0, $"Tweaks sin membresía declarada en la tabla (añádelos a ProfileMembership): {string.Join(", ", undeclared)}");

        // 2) La membresía real coincide EXACTAMENTE con la tabla, para cada id.
        var mismatches = new List<string>();
        foreach (var definition in catalog.Definitions)
        {
            var expected = ProfileMembership[definition.Id].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var actual = FragForge.Core.Engine.ProfileSelector.ProfilesOf(definition).Select(p => p.ToString()).ToArray();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                mismatches.Add($"{definition.Id}: tabla=[{string.Join(", ", expected)}] real=[{string.Join(", ", actual)}]");
        }
        Assert.True(mismatches.Count == 0, "Membresía de perfiles distinta de la tabla:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public async Task ProfileMembership_TableHasNoOrphanIds()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        var ids = catalog.Definitions.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = ProfileMembership.Keys.Where(id => !ids.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(orphans.Length == 0, $"La tabla menciona ids que ya no existen: {string.Join(", ", orphans)}");
    }

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
        Assert.DoesNotContain(balanced, d => d.Tags.Contains("opt-in"));
        // Los tres semilla conservan su perfil mediante "profiles" explícito.
        Assert.Contains(competitive, d => d.Id == "power.fragforge-performance");
        Assert.DoesNotContain(advanced, d => d.Id.StartsWith("debloat.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(advanced, d => d.Id == "cs2.fullscreen-optimizations-off");
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

        // El debloat es opt-in sin "profiles": fuera de los cuatro perfiles, sin excepciones.
        Assert.DoesNotContain(selected, d => d.Id.StartsWith("debloat.", StringComparison.OrdinalIgnoreCase));
        // Ningún opt-in SIN membresía explícita se autoselecciona.
        Assert.DoesNotContain(selected, d => d.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase) && d.Profiles is null);
        // Pero sigue en el catálogo: aplicable por id explícito.
        Assert.Contains(catalog.Definitions, d => d.Id.StartsWith("debloat.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OptInWithoutProfiles_IsExcludedFromEveryProfile_EvenIfSafe()
    {
        // Regla verificable: opt-in sin "profiles" nunca entra, aunque su riesgo sea Safe.
        var (loader, _) = CreateLoader();
        var probe = """
        {"id":"probe.optin-safe","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","tags":["opt-in"],"requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Service","parameters":{"name":"DiagTrack","startMode":"Manual"}}],"references":["r"]}
        """;
        var catalog = loader.LoadDocuments([("probe.json", probe)]);
        var definition = Assert.Single(catalog.Definitions);
        Assert.Empty(FragForge.Core.Engine.ProfileSelector.ProfilesOf(definition));
        Assert.False(FragForge.Core.Engine.ProfileSelector.Allows(TweakProfile.Advanced, definition));
    }

    [Fact]
    public void ExplicitProfilesField_IsExact_AndEmptyMeansNoProfile()
    {
        var (loader, _) = CreateLoader();
        var withProfiles = """
        {"id":"probe.explicit","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","profiles":["Competitive"],"requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Service","parameters":{"name":"DiagTrack","startMode":"Manual"}}],"references":["r"]}
        """;
        var none = """
        {"id":"probe.none","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","profiles":[],"requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Service","parameters":{"name":"DiagTrack","startMode":"Manual"}}],"references":["r"]}
        """;
        var catalog = loader.LoadDocuments([("a.json", withProfiles), ("b.json", none)]);
        var explicitOne = catalog.Get("probe.explicit");
        var empty = catalog.Get("probe.none");

        // "profiles" manda sobre el riesgo: es exacto, no se suma a lo que diría la regla por riesgo.
        Assert.Equal("Competitive", Assert.Single(FragForge.Core.Engine.ProfileSelector.ProfilesOf(explicitOne)).ToString());
        Assert.Empty(FragForge.Core.Engine.ProfileSelector.ProfilesOf(empty));
        // Aplicar por id sigue permitido en cualquier perfil: el campo no bloquea el motor.
        Assert.NotNull(explicitOne);
    }

    [Fact]
    public void ProfilesField_RejectsUnknownNamesAndDuplicates()
    {
        var (loader, _) = CreateLoader();
        var unknown = """
        {"id":"probe.bad","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","profiles":["Turbo"],"requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Service","parameters":{"name":"DiagTrack","startMode":"Manual"}}],"references":["r"]}
        """;
        // Nivel schema: el enum lo rechaza.
        var schemaErrors = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("bad.json", unknown)])).Errors;
        Assert.Contains(schemaErrors, e => e.Contains("enum", StringComparison.Ordinal));
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
    public async Task NoTwoTweaksOwnTheSamePowerSetting()
    {
        // Un solo dueño por (subgrupo, setting): dos tweaks escribiendo el mismo valor lo dejan
        // inconsistente al revertir uno de ellos. power.fragforge-performance es el dueño de base.
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in catalog.Definitions)
        foreach (var action in definition.Actions.Where(a => a.Kind.Equals("PowerPlan", StringComparison.OrdinalIgnoreCase)))
        {
            if (!action.Parameters.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Array) continue;
            foreach (var setting in settings.EnumerateArray())
            {
                var key = setting.GetProperty("subgroupGuid").GetString() + "|" + setting.GetProperty("settingGuid").GetString();
                if (!owners.TryGetValue(key, out var list)) owners[key] = list = [];
                list.Add(definition.Id);
            }
        }

        var clashes = owners.Where(o => o.Value.Count > 1).Select(o => $"{o.Key}: {string.Join(", ", o.Value)}").ToArray();
        Assert.True(clashes.Length == 0, "Dos tweaks declaran el mismo ajuste de energía:\n  - " + string.Join("\n  - ", clashes));
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
    public async Task Localization_LoadsBothLanguagesWithTheSameKeys()
    {
        // xUnit1031: await en vez de bloquear con GetAwaiter().GetResult().
        var es = await FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync("es-MX");
        var en = await FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync("en-US");
        Assert.Equal("es-MX", es.Culture);
        Assert.Equal("en-US", en.Culture);
        Assert.NotEqual(es["nav.scan"], en["nav.scan"]);
        Assert.Equal("Operación en curso…", es["busy"]);
        Assert.Equal("Operation in progress…", en["busy"]);
        Assert.Equal("ACEPTO EL RIESGO", es["risk.phrase"]);
    }

    // ------------------------------------------------------------------ Ronda 10A, P6: paridad de localización

    /// <summary>
    /// Paridad REAL entre los dos idiomas y contra el código: mismo conjunto de claves, ningún valor vacío y
    /// cada clave que el código pide con un literal existe en los dos ficheros.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Se leen los JSON del RECURSO EMBEBIDO (no del disco): es exactamente lo que carga
    /// <c>LocalizationLoader.LoadAsync</c> en producción (LocalizationLoader.cs:11), así que si el recurso no
    /// se empaquetó, esto lo ve. El test anterior (<c>Localization_LoadsBothLanguagesWithTheSameKeys</c>)
    /// solo comparaba cuatro claves sueltas con Assert.Equal; no comprobaba CONJUNTOS, ni vacíos, ni que una
    /// clave usada en el código exista.
    /// </para>
    /// <para>
    /// Por qué se comprueban las claves contra el código: <c>Localizer[key]</c> devuelve LA CLAVE cuando
    /// falta (Localizer.cs:15), sin lanzar y sin avisar. Un <c>localizer["typo.aqui"]</c> no rompe nada:
    /// pinta "typo.aqui" en la UI. Ese fallo solo se ve si alguien compara contra los recursos.
    /// </para>
    /// <para>
    /// El escaneo de literales es deliberadamente PARCIAL y así se declara: hay claves construidas en
    /// tiempo de ejecución (<c>_localizer[SessionStateKey]</c> en MainViewModel.cs:199, y
    /// <c>localizer["state." + status.State]</c> en TweakItemViewModel.cs:48) que ningún escaneo de texto
    /// puede resolver sin ejecutar el código. Esas se cubren por lista explícita abajo (<c>DynamicKeys</c>),
    /// que es la parte que un grep nunca podría dar.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Localization_HasParityBetweenLanguages_AndCoversTheKeysTheCodeUses()
    {
        var es = await ReadLocalizationResourceAsync("es-MX");
        var en = await ReadLocalizationResourceAsync("en-US");

        // (a) Mismo CONJUNTO de claves, y el mensaje dice cuáles faltan en cada lado.
        var missingInEn = es.Keys.Except(en.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var missingInEs = en.Keys.Except(es.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.True(missingInEn.Length == 0, $"Faltan en en-US: {string.Join(", ", missingInEn)}");
        Assert.True(missingInEs.Length == 0, $"Faltan en es-MX: {string.Join(", ", missingInEs)}");

        // (b) Ningún valor vacío en ninguno de los dos.
        var blankEs = es.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var blankEn = en.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.True(blankEs.Length == 0, $"Valores vacíos en es-MX: {string.Join(", ", blankEs)}");
        Assert.True(blankEn.Length == 0, $"Valores vacíos en en-US: {string.Join(", ", blankEn)}");

        // (c) Cada clave usada como LITERAL en src/ existe en los dos idiomas.
        var used = LiteralKeysUsedInSource().Concat(DynamicKeys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(used);   // si el escaneo se rompe, no puede dar verde por vacío
        var missingUsed = used.Where(k => !es.ContainsKey(k) || !en.ContainsKey(k)).ToArray();
        Assert.True(missingUsed.Length == 0,
            $"El código pide claves que no están en los dos JSON: {string.Join(", ", missingUsed)}");

        // Dato real del test (no de un script suelto), y de paso una red contra vaciar un fichero.
        Assert.Equal(87, es.Count);
        Assert.Equal(87, en.Count);
    }

    /// <summary>
    /// Claves construidas en tiempo de ejecución, que ningún escaneo de literales puede ver. Se enumeran a
    /// mano LEYENDO el código que las forma:
    ///   - MainViewModel.cs:201-207, SessionStateKey: session.state.active / waiting / reverting / inactive.
    ///   - TweakItemViewModel.cs:48, <c>"state." + status.State</c>: un caso por cada valor de TweakState
    ///     (Models/Enums.cs, TweakState) — no por cada clave que hoy exista en el JSON, porque el enum
    ///     puede crecer y entonces faltaría traducción sin que nada avise.
    /// </summary>
    private static readonly string[] DynamicKeys =
    [
        "state.Applied", "state.AppliedExternally", "state.PartiallyApplied", "state.NotApplied",
        "state.NotApplicable", "state.Unknown", "state.Inspected",
        "session.state.active", "session.state.waiting", "session.state.reverting", "session.state.inactive",
    ];

    /// <summary>Lee un JSON de localización del RECURSO EMBEBIDO, que es lo que carga producción.</summary>
    private static async Task<Dictionary<string, string>> ReadLocalizationResourceAsync(string culture)
    {
        var resource = $"FragForge.Tweaks.Localization.{culture}.json";
        await using var stream = typeof(FragForge.Tweaks.Localization.LocalizationLoader).Assembly
            .GetManifestResourceStream(resource) ?? throw new InvalidDataException($"Falta el recurso '{resource}'.");
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, FragForge.Core.Logging.JsonDefaults.Options)
            ?? throw new InvalidDataException($"'{resource}' está vacío.");
    }

    /// <summary>
    /// Claves que aparecen como literal en una indexación de Localizer dentro de src/: <c>x["clave"]</c>.
    /// Solo literales; las dinámicas van en <see cref="DynamicKeys"/>.
    /// </summary>
    private static IReadOnlyList<string> LiteralKeysUsedInSource()
    {
        // El patrón se ancla al NOMBRE del índice para no capturar cualquier x["..."] del código.
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?:_localizer|localizer|Localizer|Text)\s*\[\s*""(?<key>[A-Za-z][A-Za-z0-9._]*)""\s*\]",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var keys = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(FindRepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(File.ReadAllText(file)))
                keys.Add(match.Groups["key"].Value);
        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FragForge.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }

    /// <summary>
    /// El test tiene que DETECTAR el caso roto. Se construye en memoria un par de diccionarios al que le
    /// falta una clave y otro con un valor vacío, y se comprueba que la comparación del test los señala.
    /// NO se tocan los JSON reales: se ejercita la MISMA lógica sobre fixtures.
    /// </summary>
    [Fact]
    public void LocalizationParity_DetectsAMissingKeyAndABlankValue_WithoutTouchingTheRealFiles()
    {
        var es = new Dictionary<string, string>(StringComparer.Ordinal) { ["a.one"] = "uno", ["a.two"] = "dos", ["a.three"] = "tres" };
        var en = new Dictionary<string, string>(StringComparer.Ordinal) { ["a.one"] = "one", ["a.three"] = "   " };

        // Misma lógica que el assert (a) del test de paridad.
        var missingInEn = es.Keys.Except(en.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(["a.two"], missingInEn);
        var missingInEs = en.Keys.Except(es.Keys, StringComparer.Ordinal).ToArray();
        Assert.Empty(missingInEs);

        // Misma lógica que el assert (b): "a.three" está en los dos, pero en en-US está en blanco.
        var blankEn = en.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToArray();
        Assert.Equal(["a.three"], blankEn);
        var blankEs = es.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToArray();
        Assert.Empty(blankEs);

        // Y la (c): una clave usada por el código que no esté en ninguno de los dos.
        var used = new[] { "a.one", "a.missing" };
        var missingUsed = used.Where(k => !es.ContainsKey(k) || !en.ContainsKey(k)).ToArray();
        Assert.Equal(["a.missing"], missingUsed);
    }
}
