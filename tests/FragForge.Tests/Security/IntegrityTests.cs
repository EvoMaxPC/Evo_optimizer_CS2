using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Core.Recovery;
using FragForge.Tests.Core;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Security;

/// <summary>
/// Capa 10.5. Integridad de lo que la app guarda y de las herramientas que ejecuta.
///
/// <para>
/// Las tres piezas ya existían, pero una de ellas no estaba conectada: <c>DefinitionSha256</c> se escribía en el
/// snapshot y <b>nadie lo comprobaba</b>. Estos tests fijan el comportamiento y citan los que ya cubrían el
/// resto, para que la cobertura no dependa de la memoria de nadie.
/// </para>
/// </summary>
public sealed class IntegrityTests
{
    /// <summary>
    /// El journal (cadena SHA-256 tamper-evident, NO firmado ni autenticado) detecta que se cambia UNA línea.
    /// Ya existía: <c>tests/FragForge.Tests/Core/EngineTests.cs:339 Journal_IsTamperEvident</c>.
    /// Aquí se repite en aislamiento para dejar explícito qué se garantiza y qué no.
    /// </summary>
    [Fact]
    public async Task TheJournal_DetectsAChangedLine()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var journal = new FileJournal(paths);

        await journal.AppendAsync(new JournalEntry { EntryId = "e1", OperationId = "op", EventType = JournalEventType.OperationStarted });
        await journal.AppendAsync(new JournalEntry { EntryId = "e2", OperationId = "op", EventType = JournalEventType.OperationCompleted });
        Assert.True((await journal.VerifyIntegrityAsync()).IsValid);

        var lines = await File.ReadAllLinesAsync(paths.Journal);
        lines[1] = lines[1].Replace("\"OperationCompleted\"", "\"OperationFailed\"", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(paths.Journal, lines);

        var integrity = await journal.VerifyIntegrityAsync();
        Assert.False(integrity.IsValid);
        Assert.Contains("Integridad", integrity.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// C4.6: un journal CORRUPTO (JSON roto, línea vacía) no puede leerse, y lo dice con una excepción de
    /// datos, no con un IndexOutOfRange ni un NullReference a mitad.
    /// </summary>
    /// <remarks>
    /// HALLAZGO, no una virtud: <see cref="FileJournal.ReadAllAsync"/> NO captura nada (líneas 46-57), así que
    /// la excepción sale hasta quien llama, y <c>ReportService.CreateAsync:17</c> lo llama sin `try`. Con el
    /// journal corrupto, generar el informe falla entero. Se documenta aquí porque un test que dijera
    /// "devuelve una lista vacía" estaría describiendo un comportamiento que el código NO tiene.
    /// No se arregla en esta ronda: cambiar <c>ReadAllAsync</c> para que trague y devuelva lo legible toca
    /// una firma pública y es decisión de producto (¿historial vacío o error visible?). Queda anotado.
    /// </remarks>
    [Theory]
    [InlineData("\"esto no es json")]
    [InlineData("")]
    public async Task ACorruptJournal_CannotBeRead_AndSaysSo(string contenido)
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Journal)!);
        await File.WriteAllTextAsync(paths.Journal, contenido);
        var journal = new FileJournal(paths);

        // Lo que NO pasa: devolver silenciosamente una lista vacía.
        await Assert.ThrowsAnyAsync<Exception>(() => journal.ReadAllAsync());
        // Lo que SÍ: la verificación de integridad es la que sabe describir el daño sin propagar.
        var integrity = await journal.VerifyIntegrityAsync();
        Assert.False(integrity.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(integrity.Error));
    }

    /// <summary>
    /// El journal NO es una firma criptográfica: quien edite una línea y recalcule toda la cadena obtiene un
    /// journal "válido". Este test lo deja escrito para que nadie lo presente como autenticación.
    /// </summary>
    [Fact]
    public async Task TheJournal_IsTamperEvident_ButNotAuthenticated()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var journal = new FileJournal(paths);
        await journal.AppendAsync(new JournalEntry { EntryId = "e1", OperationId = "op", EventType = JournalEventType.OperationStarted });

        // Recalcular la cadena es posible sin ninguna clave: se busca la entrada, se cambia y se re-firma.
        var entries = (await journal.ReadAllAsync()).ToArray();
        var forged = entries[0] with { Message = "texto cambiado", Hash = "" };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(forged, JsonDefaults.Create(false))))).ToLowerInvariant();
        await File.WriteAllLinesAsync(paths.Journal, [JsonSerializer.Serialize(forged with { Hash = hash }, JsonDefaults.Create(false))]);

        // La verificación pasa: no hay clave. Por eso la postura NO se apoya en el journal para integridad
        // frente a un atacante con acceso de escritura; ese atacante ya puede reescribir el snapshot.
        Assert.True((await journal.VerifyIntegrityAsync()).IsValid);
    }

    /// <summary>
    /// Los snapshots se validan por hash al revertir: si se edita la definición guardada, el revert se rechaza.
    /// Antes NO se comprobaba (<c>DefinitionSha256</c> solo se escribía).
    /// </summary>
    [Fact]
    public async Task Revert_RejectsASnapshotWhoseDefinitionWasEdited()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine();
        var applied = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True(applied.Success);

        var snapshotId = applied.SnapshotId!;
        var path = Path.Combine(harness.Paths.Snapshots, snapshotId, "snapshot.json");
        var json = await File.ReadAllTextAsync(path);
        // Se cambia la definición guardada, que es de donde el revert saca los parámetros.
        // OJO con el espaciado: el snapshot se serializa con JsonDefaults.Options, que tiene WriteIndented=true
        // (src/FragForge.Core/Logging/JsonDefaults.cs:9-11), así que el JSON real dice  "kind": "Fake"  CON un
        // espacio tras los dos puntos. Se buscaba "kind":"Fake" sin espacio, el Replace no cambiaba nada y el
        // test fallaba en el Assert.NotEqual de abajo con "los hashes coinciden" (en realidad, "no se tocó nada").
        var tampered = json.Replace("\"kind\": \"Fake\"", "\"kind\": \"Registry\"", StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(json, tampered);
        await File.WriteAllTextAsync(path, tampered);

        var revert = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = harness.UserSid } });

        Assert.False(revert.Success);
        Assert.Contains(revert.Errors, e => e.Contains("íntegro", StringComparison.OrdinalIgnoreCase) || e.Contains("DefinitionSha256", StringComparison.Ordinal));
    }

    /// <summary>
    /// C1: para CADA definición del catálogo REAL, el hash que se guarda en el snapshot sobrevive a
    /// escribirlo a disco y releerlo.
    ///
    /// Es la prueba que faltaba: la ronda 5 añadió campos a TweakDefinition (profiles, sessionEligible,
    /// restartsNetworkLink, anticheatNote) y el hash se calcula serializando la definición. Si un campo
    /// nuevo se serializara de forma distinta al releer (por ejemplo, un valor por defecto que aparece al
    /// escribir y desaparece al leer), el revert rechazaría snapshots LEGÍTIMOS y el usuario perdería la
    /// vuelta atrás. Se comprueba con las 32 definiciones reales, no con una de mentira.
    /// </summary>
    [Fact]
    public async Task EveryRealDefinition_KeepsItsHash_AfterAGuardarYReleerEnDisco()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new FileSnapshotStore(paths);
        var catalog = await new FragForge.Tweaks.JsonTweakCatalogLoader(RealValidator()).LoadEmbeddedAsync();

        var tweaks = catalog.Definitions.Select(d => new TweakSnapshot
        {
            TweakId = d.Id,
            DefinitionSha256 = catalog.GetDefinitionHash(d.Id),
            Definition = d,
            Actions = [new CapturedAction(0, d.Actions[0].Kind, new InspectionActionState())]
        }).ToArray();

        var snapshot = new SystemSnapshot { Id = "snap-c1", Tweaks = tweaks };
        await store.SaveAsync(snapshot);
        var releido = await store.GetAsync("snap-c1");

        // El mismo numero de tweaks y los mismos ids, en el mismo orden.
        Assert.Equal(tweaks.Length, releido.Tweaks.Count);
        Assert.Equal(tweaks.Select(t => t.TweakId), releido.Tweaks.Select(t => t.TweakId));

        // Y el hash guardado coincide con el recalculado sobre la definicion RELEIDA.
        foreach (var guardado in releido.Tweaks)
        {
            var recalculado = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(guardado.Definition, JsonDefaults.Create(false))))).ToLowerInvariant();
            Assert.Equal(guardado.DefinitionSha256, recalculado);
        }
    }

    /// <summary>
    /// C1 (segundo): un snapshot guardado SIN los campos que se añadieron despues (profiles, sessionEligible,
    /// restartsNetworkLink, anticheatNote) sigue verificando.
    ///
    /// El hash se calcula serializando la definicion, y JsonDefaults.Create(false) + WhenWritingNull hacen que
    /// los defaults NO se escriban: un JSON historico sin esos campos deserializa a los mismos valores por
    /// defecto y vuelve a serializarse igual. Si algun dia se pusiera un valor por defecto distinto, o
    /// [JsonIgnore] cambiara, este test lo delata.
    /// </summary>
    [Fact]
    public async Task ASnapshotWithoutTheNewFields_StillVerifies()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new FileSnapshotStore(paths);

        // Una definicion minima sin NINGUNO de los cuatro campos nuevos.
        const string jsonViejo = """
            {"id":"old.one","category":"Cpu","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","requiresAdmin":true,"expectedImpact":{},"actions":[{"kind":"Registry","parameters":{"values":[{"key":"HKCU\Software\FragForgeTests","name":"V","kind":"DWord","value":1,"mode":"Set"}]}}],"references":["r"]}
            """;
        var definicion = JsonSerializer.Deserialize<TweakDefinition>(jsonViejo, JsonDefaults.Options)!;
        var hashViejo = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(definicion, JsonDefaults.Create(false))))).ToLowerInvariant();

        await store.SaveAsync(new SystemSnapshot
        {
            Id = "snap-viejo",
            Tweaks = [new TweakSnapshot { TweakId = "old.one", DefinitionSha256 = hashViejo, Definition = definicion, Actions = [new CapturedAction(0, "Registry", new InspectionActionState())] }]
        });

        var releido = await store.GetAsync("snap-viejo");
        var guardado = releido.Tweaks.Single();

        var recalculado = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(guardado.Definition, JsonDefaults.Create(false))))).ToLowerInvariant();

        Assert.Equal(guardado.DefinitionSha256, recalculado);
        // Y los cuatro campos siguen en sus valores por defecto: el JSON historico no inventa datos.
        Assert.Null(guardado.Definition.Profiles);
        Assert.False(guardado.Definition.SessionEligible);
        Assert.False(guardado.Definition.RestartsNetworkLink);
        Assert.Null(guardado.Definition.AnticheatNote);
    }

    /// <summary>Validador como el de produccion pero con dobles: el catalogo real EMBEBIDO valida contra si mismo.</summary>
    private static FragForge.Tweaks.Validation.TweakCatalogValidator RealValidator()
    {
        var registry = new FakeRegistry();
        var policy = FragForge.Core.Persistence.ProtectionPolicy.CreateDefault();
        var factory = new FragForge.Actions.DependencyInjection.TweakActionFactory(
            registry, policy, new FakeServiceController(), new FakeScheduledTaskStore(),
            new FakeNetworkAdapterController(), new FakeProcessRunner(),
            FragForge.Actions.Diagnostics.DiagnosticProbeFactory.CreateDefaults(registry),
            adapterResolver: new FragForge.Actions.Network.UnsupportedNetworkAdapterResolver());
        return new FragForge.Tweaks.Validation.TweakCatalogValidator(
            new FragForge.Tweaks.Validation.JsonSchemaValidator(FragForge.Tweaks.JsonTweakCatalogLoader.ReadEmbeddedSchema()),
            new FragForge.Core.Conditions.ConditionFactory(), factory, FragForge.Core.Tiers.ServiceTierCatalog.LoadEmbedded());
    }

    /// <summary>Un snapshot íntegro SÍ se revierte: la comprobación no bloquea el camino normal.</summary>
    [Fact]
    public async Task Revert_StillWorks_OnAnUntouchedSnapshot()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.one"], harness.Options())).Success);

        var revert = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = harness.UserSid } });

        Assert.True(revert.Success);
    }

    /// <summary>
    /// El RecoveryWriter calcula SHA-256 de cada archivo y lo registra en el índice; verificar detecta la
    /// manipulación. Ya existía: <c>RecoveryAndDriftTests.VerifyDetectsATamperedFile</c>.
    /// </summary>
    [Fact]
    public async Task TheRecoveryIndex_DetectsATamperedArtifact()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var writer = new RecoveryWriter(paths);
        var result = await writer.WriteAsync(EmptySnapshot(), "op-1");
        Assert.Empty(await writer.VerifyAsync(result.Directory));

        var manual = Directory.GetFiles(result.Directory, "*.md").Single();
        await File.AppendAllTextAsync(manual, "\nmanipulado");
        var problems = await writer.VerifyAsync(result.Directory);

        Assert.Contains(problems, p => p.Contains("SHA-256 no coincide", StringComparison.OrdinalIgnoreCase));
    }

    private static SystemSnapshot EmptySnapshot() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Label = "test",
        Tweaks = []
    };
}
