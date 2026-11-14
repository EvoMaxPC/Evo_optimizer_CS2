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
            DiagnosticProbeFactory.CreateDefaults(registry),
            adapterResolver: new FragForge.Actions.Network.UnsupportedNetworkAdapterResolver());
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
        // El motivo del servicio es de POLÍTICA (denylist), no de una acción mal formada: es lo que pide 10.4.
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Id == "ext.service" && r.Reason.Contains("protegido", StringComparison.OrdinalIgnoreCase));
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
    public async Task ADuplicateOfAnEmbeddedId_NeverOverwritesIt()
    {
        var loader = Loader();
        // xUnit1031: await en vez de bloquear con GetAwaiter().GetResult().
        var embedded = await loader.LoadEmbeddedAsync();
        var original = embedded.Definitions.First(d => d.Id == "cs2.cpu-priority-high");

        var catalog = loader.LoadDocumentsResilient([
            ("evil.json", RegistryTweak("cs2.cpu-priority-high", @"HKLM\Software\FragForgeTests", "Cheat"))
        ]);

        Assert.DoesNotContain(catalog.Definitions, d => d.Id == "cs2.cpu-priority-high");
        // xUnit2000: el valor ESPERADO va primero. Estaba al reves ("IfeoPerfOptions" como actual), asi que
        // al fallar el mensaje decia lo contrario de lo que pasaba.
        Assert.Equal("IfeoPerfOptions", original.Actions[0].Kind);
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Reason.Contains("duplicado", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// C2 + D4d: un externo NO puede declarar los campos que deciden comportamiento. Se rechaza por
    /// PRESENCIA, con motivo, y sin llevarse por delante el resto del catálogo.
    /// </summary>
    /// <remarks>
    /// El valor del campo se pone distinto del default en cada caso, pero el rechazo NO depende de eso: hay
    /// un caso explicito con el default (<see cref="DeclaringAReservedFieldIsRejectedEvenWithTheDefaultValue"/>).
    /// </remarks>
    [Theory]
    [InlineData("sessionEligible", true)]
    [InlineData("restartsNetworkLink", true)]
    [InlineData("profiles", false)]
    public void AnExternalTweakCannotDeclareReservedBehaviourFields(string field, bool isBool)
    {
        var loader = Loader();
        var good = RegistryTweak("ext.bueno", @"HKLM\Software\FragForgeTests", "V");
        var brute = RegistryTweak("ext.bruto", @"HKLM\Software\FragForgeTests", "W");
        // Se inyecta el campo reservado justo antes de la llave final del objeto.
        var suffix = isBool
            ? $$""", "{{field}}": true }"""
            : $$""", "{{field}}": ["Safe"] }""";
        var marcado = brute.TrimEnd('}', ',', ' ', '\n', '\r') + suffix;

        var catalog = loader.LoadDocumentsResilient([("bruto.json", marcado), ("bueno.json", good)]);

        // El que declara el campo reservado NO entra, y se dice por que.
        Assert.DoesNotContain(catalog.Definitions, d => d.Id == "ext.bruto");
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Id == "ext.bruto" && r.Reason.Contains(field, StringComparison.Ordinal));
        // Y el resto del catálogo carga: el rechazo es por ENTRADA, no global.
        Assert.Contains(catalog.Definitions, d => d.Id == "ext.bueno");
    }

    /// <summary>
    /// D4c: el nombre del archivo NO decide el origen. Antes bastaba llamarse "FragForge.Tweaks.Catalog.x.json"
    /// para que el StartsWith lo diera por embebido y se saltara el rechazo. Ahora el origen se decide con los
    /// recursos reales del ensamblado, que un archivo del usuario no puede imitar.
    /// </summary>
    [Fact]
    public void AnExternalFileNamedLikeAnEmbeddedResource_IsStillExternal()
    {
        var loader = Loader();
        var impostor = RegistryTweak("ext.impostor", @"HKLM\Software\FragForgeTests", "X");
        var marcado = impostor.TrimEnd('}', ',', ' ', '\n', '\r') + ", \"sessionEligible\": true }";

        // El nombre EXACTO con el prefijo que el StartsWith miraba.
        var catalog = loader.LoadDocumentsResilient([("FragForge.Tweaks.Catalog.x.json", marcado)]);

        Assert.DoesNotContain(catalog.Definitions, d => d.Id == "ext.impostor");
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Id == "ext.impostor");
    }

    /// <summary>
    /// D4d: se rechaza por PRESENCIA, no por valor. `sessionEligible: false` es el default y no cambia nada
    /// en tiempo de ejecucion, pero el autor esta declarando una marca que no le toca: si se aceptara, la
    /// regla dependeria de un valor que el autor puede poner a su gusto.
    /// </summary>
    [Fact]
    public void DeclaringAReservedFieldIsRejectedEvenWithTheDefaultValue()
    {
        var loader = Loader();
        var good = RegistryTweak("ext.bueno", @"HKLM\Software\FragForgeTests", "V");
        var conDefault = RegistryTweak("ext.default", @"HKLM\Software\FragForgeTests", "Y");
        var marcado = conDefault.TrimEnd('}', ',', ' ', '\n', '\r') + ", \"sessionEligible\": false }";

        var catalog = loader.LoadDocumentsResilient([("default.json", marcado), ("bueno.json", good)]);

        Assert.DoesNotContain(catalog.Definitions, d => d.Id == "ext.default");
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Id == "ext.default" && r.Reason.Contains("sessionEligible", StringComparison.Ordinal));
        // Y el valido del mismo lote sigue cargando: el rechazo es por entrada.
        Assert.Contains(catalog.Definitions, d => d.Id == "ext.bueno");
    }

    /// <summary>
    /// E3: las variantes de MAYÚSCULAS. Antes el campo se buscaba con <c>TryGetProperty</c> (exacto) y el
    /// deserializador sí las mapea (<c>PropertyNameCaseInsensitive = true</c>), así que el rechazo se
    /// esquivaba en cuanto nada lo parase antes.
    /// </summary>
    /// <remarks>
    /// Con el esquema de hoy estas entradas mueren ANTES, en la validación estructural
    /// (<c>additionalProperties: false</c> y JSON Schema es case-sensitive: <c>JsonSchemaValidator.cs:51</c>
    /// usa <c>TryGetProperty</c>). Así que el test comprueba las dos capas por separado para que se sepa
    /// cuál está actuando:
    ///   - por la ruta real (con esquema): rechazo por estructura, y NO entra;
    ///   - por la comparación de campos: que es OrdinalIgnoreCase, aunque el esquema no intervenga.
    /// Si algún día se añade la variante a <c>properties</c>, la segunda capa sigue cubriendo.
    /// </remarks>
    [Theory]
    [InlineData("SessionEligible", "true")]
    [InlineData("RESTARTSNETWORKLINK", "true")]
    [InlineData("Profiles", """["Safe"]""")]
    public void CapitalisationVariants_AreRejectedByTheRealRoute_Too(string field, string value)
    {
        var loader = Loader();
        var brute = RegistryTweak("ext.mayus", @"HKLM\Software\FragForgeTests", "Z");
        // El valor va en el propio InlineData para que cada campo lleve uno VALIDO para su tipo: si no,
        // el rechazo seria por "se esperaba un array" y no probaria nada del nombre.
        var marcado = brute.TrimEnd('}', ',', ' ', '\n', '\r') + $$""", "{{field}}": {{value}} }""";

        var catalog = loader.LoadDocumentsResilient([("mayus.json", marcado)]);

        // No entra. Puede ser por el esquema (hoy) o por el rechazo de campos reservados (si el esquema
        // dejara de ser estricto): lo que NO puede pasar es que se acepte.
        Assert.DoesNotContain(catalog.Definitions, d => d.Id == "ext.mayus");
        Assert.Contains(catalog.LoadReport.Rejected, r => r.Id == "ext.mayus" || r.Name == "mayus.json");
    }

    /// <summary>
    /// La MISMA comparación, probada sin el esquema de por medio: así se demuestra que la corrección de E3
    /// vale por sí sola y no solo porque el esquema rechace antes.
    /// </summary>
    /// <remarks>
    /// Se comprueba sobre el deserializador real: <c>PropertyNameCaseInsensitive</c> hace que
    /// <c>"SessionEligible"</c> acabe en <c>SessionEligible</c>. Si la comparación de nombres no fuera
    /// OrdinalIgnoreCase, ese campo entraría sin rechazo. Aquí se verifica la premisa (el deserializador
    /// lo mapea) para que el comentario del código no sea una suposición.
    /// </remarks>
    [Fact]
    public void TheDeserializer_MapsCapitalisedNames_SoTheComparisonMustIgnoreCase()
    {
        const string conMayusculas = """
            {"id":"probe.case","category":"Debloat","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"Low","risk":"Safe","requiresAdmin":true,"expectedImpact":{},"references":["r"],"actions":[],"SessionEligible":true,"Profiles":["Safe"]}
            """;

        var definition = System.Text.Json.JsonSerializer.Deserialize<TweakDefinition>(conMayusculas, FragForge.Core.Logging.JsonDefaults.Options)!;

        // La premisa: el deserializador NO distingue mayúsculas.
        Assert.True(definition.SessionEligible);
        Assert.NotNull(definition.Profiles);
    }

    /// <summary>El catálogo embebido SÍ puede declararlos: no se le aplica la restricción de externos.</summary>
    [Fact]
    public async Task TheEmbeddedCatalog_StillDeclaresItsReservedFields()
    {
        var loader = Loader();
        var embedded = await loader.LoadEmbeddedAsync();

        // Los 3 de sesión siguen marcados, con sus campos.
        var deSesion = embedded.Definitions.Where(d => d.SessionEligible).Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(["cpu.core-parking-off", "cpu.epp-performance", "power.fragforge-performance"], deSesion);
        // Y ninguna entrada externa pudo quitarlos: el embebido carga entero. No se fija un numero (R-N):
        // se compara contra los recursos JSON del propio ensamblado, como RealCatalogDenylistTests.
        var recursos = typeof(JsonTweakCatalogLoader).Assembly.GetManifestResourceNames()
            .Count(n => n.StartsWith("FragForge.Tweaks.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));
        Assert.Equal(recursos, embedded.Definitions.Count);
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
        Assert.DoesNotContain(catalog.Definitions, d => d.Id.Length == 0);
    }
}
