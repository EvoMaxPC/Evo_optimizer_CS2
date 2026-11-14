using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Diagnostics;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using Xunit;

namespace FragForge.Tests.Security;

/// <summary>
/// Capa 10.4. Un catálogo EXTERNO del usuario no puede tumbar la app.
///
/// <para>
/// Antes, <c>LoadDocuments</c> acumulaba todos los errores y lanzaba: un solo JSON inválido en
/// <c>%ProgramData%\FragForge\catalog</c> dejaba al usuario sin aplicación. Ahora, con
/// <c>LoadDocumentsResilient</c>, la entrada mala se rechaza con motivo visible y el resto carga.
/// </para>
///
/// <para>
/// Los cinco casos del enunciado: Debugger, servicio protegido, prefijo Memory Management, JSON inválido e
/// id duplicado de uno embebido.
/// </para>
/// </summary>
public sealed class ExternalCatalogResilienceTests
{
    private static JsonTweakCatalogLoader Loader()
    {
        var schema = new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var policy = FragForge.Core.Persistence.ProtectionPolicy.CreateDefault();
        var factory = new TweakActionFactory(
            registry, policy, new FakeServiceController(), new FakeScheduledTaskStore(),
            new FakeNetworkAdapterController(), new FakeProcessRunner(),
            DiagnosticProbeFactory.CreateDefaults(registry));
        return new JsonTweakCatalogLoader(new TweakCatalogValidator(schema, new ConditionFactory(), factory));
    }

    /// <summary>Un tweak externo con la forma mínima válida, apuntando a un destino de registro.</summary>
    /// <summary>
    /// Forma mínima válida de un tweak de registro, copiada de <c>latency.game-mode-on.json</c>: los parámetros
    /// son <c>values[]</c> con <c>key</c>, <c>name</c>, <c>kind</c>, <c>value</c> y <c>mode</c>.
    /// </summary>
    private static string RegistryTweak(string id, string key, string name, string kind = "String", object value = null!) => $$"""
        {
          "id": "{{id}}",
          "category": "Cs2",
          "title": { "es-MX": "t", "en-US": "t" },
          "description": { "es-MX": "d", "en-US": "d" },
          "evidence": "Low", "risk": "Safe", "requiresAdmin": true,
          "actions": [ { "kind": "Registry", "parameters": { "values": [ { "key": "{{key.Replace("\\", "\\\\")}}", "name": "{{name}}", "kind": "{{kind}}", "value": {{System.Text.Json.JsonSerializer.Serialize(value ?? "x")}}, "mode": "Set" } ] } } ],
          "expectedImpact": {},
          "references": [ "https://example.invalid/x" ]
        }
        """;

    private static string ServiceTweak(string id, string service) => $$"""
        {
          "id": "{{id}}",
          "category": "Cs2",
          "title": { "es-MX": "t", "en-US": "t" },
          "description": { "es-MX": "d", "en-US": "d" },
          "evidence": "Low", "risk": "Advanced", "requiresAdmin": true,
          "actions": [ { "kind": "Service", "parameters": { "name": "{{service}}", "startMode": "Disabled" } } ],
          "expectedImpact": {},
          "references": [ "https://example.invalid/x" ]
        }
        """;

    /// <summary>
    /// El caso completo del enunciado: un catálogo externo malicioso con TODOS los ataques conocidos, más una
    /// entrada buena. La app carga la buena y rechaza el resto con motivo.
    /// </summary>
    [Fact]
    public void AMaliciousExternalCatalog_IsRejectedEntryByEntry_AndTheGoodOneLoads()
    {
        var loader = Loader();
        var documents = new List<(string Name, string Json)>
        {
            ("good.json", RegistryTweak("ext.good", @"HKLM\Software\FragForgeTests", "Value")),
            // 1) Debugger: protege el arranque de procesos (denylist).
            ("debugger.json", RegistryTweak("ext.debugger", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cs2.exe", "Debugger")),
            // 2) Servicio protegido.
            ("service.json", ServiceTweak("ext.service", "ipfsvc")),
            // 3) Prefijo protegido: todo Memory Management.
            ("memmgmt.json", RegistryTweak("ext.memmgmt", @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown", "DWord", 1)),
            // 4) JSON inválido.
            ("broken.json", "{ esto no es json"),
            // 5) ID duplicado de uno EMBEBIDO.
            ("duplicate.json", RegistryTweak("cs2.cpu-priority-high", @"HKLM\Software\FragForgeTests", "Other"))
        };

        var catalog = loader.LoadDocumentsResilient(documents);

        // El bueno sigue ahí.
        Assert.Contains(catalog.Definitions, d => d.Id == "ext.good");
        // Los cinco malos no, y todos tienen motivo.
        var rejectedIds = catalog.LoadReport.Rejected.Select(r => r.Id).Where(i => i is not null).ToArray();
        Assert.Contains("ext.debugger", rejectedIds);
        Assert.Contains("ext.service", rejectedIds);
        Assert.Contains("ext.memmgmt", rejectedIds);
        Assert.Contains("cs2.cpu-priority-high", rejectedIds);
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Name.Contains("broken.json", StringComparison.Ordinal) && r.Id is null);

        // Todos los rechazos traen un motivo NO vacío: se ve, no se oculta.
        Assert.All(catalog.LoadReport.Rejected, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }

    /// <summary>El motivo del rechazo por denylist es legible y nombra el destino.</summary>
    [Fact]
    public void ThePolicyRejection_ExplainsWhy()
    {
        var loader = Loader();

        var catalog = loader.LoadDocumentsResilient([
            ("debugger.json", RegistryTweak("ext.debugger", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cs2.exe", "Debugger"))
        ]);

        var rejection = Assert.Single(catalog.LoadReport.Rejected);
        Assert.Contains("Debugger", rejection.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protegido", rejection.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(catalog.Definitions);
    }

    /// <summary>Un JSON inválido no aporta id: se identifica por archivo.</summary>
    [Fact]
    public void ABrokenJson_IsRejectedByNameWithoutAnId()
    {
        var loader = Loader();

        var catalog = loader.LoadDocumentsResilient([("basura.json", "{ no cierra")]);

        var rejection = Assert.Single(catalog.LoadReport.Rejected);
        Assert.Equal("basura.json", rejection.Name);
        Assert.Null(rejection.Id);
    }

    /// <summary>
    /// El duplicado NO sobrescribe al embebido: la primera definición gana. Si el externo pudiera pisar un id
    /// embebido, podría reemplazar un tweak auditado por otro cualquiera.
    /// </summary>
    [Fact]
    public void ADuplicateOfAnEmbeddedId_NeverOverwritesIt()
    {
        var loader = Loader();
        var embedded = loader.LoadEmbeddedAsync().GetAwaiter().GetResult();
        var original = embedded.Definitions.First(d => d.Id == "cs2.cpu-priority-high");

        var catalog = loader.LoadDocumentsResilient([
            ("evil.json", RegistryTweak("cs2.cpu-priority-high", @"HKLM\Software\FragForgeTests", "Cheat"))
        ]);

        Assert.DoesNotContain(catalog.Definitions, d => d.Id == "cs2.cpu-priority-high");
        Assert.Equal(original.Actions[0].Kind, "IfeoPerfOptions");
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Reason.Contains("duplicado", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Un catálogo externo vacío es válido: solo significa que el usuario no añadió nada.</summary>
    [Fact]
    public void AnEmptyExternalCatalog_IsNotAnError()
    {
        var catalog = Loader().LoadDocumentsResilient([]);

        Assert.Empty(catalog.Definitions);
        Assert.False(catalog.LoadReport.HasRejections);
    }

    /// <summary>
    /// Una entrada que depende de otra RECHAZADA también se cae, y se dice: no puede quedar una dependencia
    /// colgando.
    /// </summary>
    [Fact]
    public void AnEntryThatDependsOnARejectedOne_IsAlsoRejected()
    {
        var loader = Loader();
        var dependent = RegistryTweak("ext.dependent", @"HKLM\Software\FragForgeTests", "A");
        dependent = dependent.TrimEnd('}', ',', ' ', '\n', '\r') + $$"""
            , "dependsOn": [ "ext.rejected" ] }
            """;

        var catalog = loader.LoadDocumentsResilient([
            ("dep.json", dependent),
            ("bad.json", RegistryTweak("ext.rejected", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cs2.exe", "Debugger"))
        ]);

        Assert.Empty(catalog.Definitions);
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Id == "ext.dependent" && r.Reason.Contains("depends", StringComparison.OrdinalIgnoreCase) || r.Id == "ext.dependent" && r.Reason.Contains("depende", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// El catálogo EMBEBIDO sigue siendo estricto: si algo del binario no valida, lanza. No se degrada un bug de
    /// la entrega a "un tweak menos".
    /// </summary>
    [Fact]
    public void TheStrictLoader_StillThrows()
    {
        var loader = Loader();

        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("b.json", "{ roto")]));
        Assert.Contains(ex.Errors, e => e.Contains("b.json", StringComparison.Ordinal));
    }

    /// <summary>
    /// El catálogo embebido real carga sin rechazos: si esta prueba falla, se ha colado un tweak inválido en el
    /// binario y es un fallo de la entrega, no del usuario.
    /// </summary>
    [Fact]
    public async Task TheRealEmbeddedCatalog_LoadsWithNoRejections()
    {
        var catalog = await Loader().LoadEmbeddedAsync();

        Assert.False(catalog.LoadReport.HasRejections);
        Assert.Empty(catalog.Definitions.Where(d => d.Id.Length == 0));
    }
}
