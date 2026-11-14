using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
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
    /// El journal firmado (encadenado por hash) detecta que se cambia UNA línea.
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
        var tampered = json.Replace("\"kind\":\"Fake\"", "\"kind\":\"Registry\"", StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(json, tampered);
        await File.WriteAllTextAsync(path, tampered);

        var revert = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = harness.UserSid } });

        Assert.False(revert.Success);
        Assert.Contains(revert.Errors, e => e.Contains("íntegro", StringComparison.OrdinalIgnoreCase) || e.Contains("DefinitionSha256", StringComparison.Ordinal));
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
