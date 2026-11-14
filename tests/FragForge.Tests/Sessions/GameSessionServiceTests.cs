using FragForge.Core.Models;
using FragForge.Core.Sessions;
using FragForge.Tests.Core;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Sessions;

/// <summary>
/// Capa 9.6 — modo sesión, con dobles y motor REAL.
///
/// El motor y el almacén de snapshots son los reales; las ACCIONES son falsas (FakeActionFactory), así que
/// el ciclo apply → revert es el de verdad pero no toca el sistema. El vigilante recibe un sondeo y un
/// retraso inyectados: no se esperan 2 segundos por paso ni se depende de que cs2.exe exista.
///
/// Casos exigidos en 9.6: ciclo completo, caída con marcador, revert parcial, doble Start y Stop sin sesión.
/// </summary>
public sealed class GameSessionServiceTests : IDisposable
{
    private readonly EngineTestHarness _harness = new(inMemoryJournal: true);

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// Tres tweaks: uno elegible, uno NO elegible y uno elegible pero con reinicio (que debe quedar fuera
    /// aunque el JSON lo marque: segunda barrera).
    /// </summary>
    private static TweakDefinition[] Definitions() =>
    [
        Tweak.Create("session.eligible", Spec.Action("Fake", new { id = "e1" })) with { SessionEligible = true },
        Tweak.Create("session.not-eligible", Spec.Action("Fake", new { id = "n1" })),
        Tweak.Create("session.needs-reboot", Spec.Action("Fake", new { id = "r1" }), requiresReboot: true) with { SessionEligible = true },
        // Capa 5.3: elegible y sin reinicio, pero REINICIA EL ENLACE. Debe quedar fuera igual.
        Tweak.Create("session.network", Spec.Action("Fake", new { id = "w1" })) with { SessionEligible = true, RestartsNetworkLink = true }
    ];

    private GameSessionService CreateService()
    {
        _harness.WithCatalog(Definitions()).WithProfile(TestProfiles.Pn64());
        return new GameSessionService(_harness.CreateEngine(), _harness.Catalog, _harness.Paths);
    }

    // ---------------------------------------------------------------- 9.1 elegibilidad

    [Fact]
    public void EligibleIds_TakesOnlySessionEligibleTweaksWithoutReboot()
    {
        var service = CreateService();

        var ids = service.EligibleIds(TweakProfile.Advanced);

        // "session.needs-reboot" esta marcado pero requiere reinicio: queda FUERA.
        // "session.network" esta marcado y no requiere reinicio, pero reinicia el enlace: tambien FUERA,
        // porque cortar la red en mitad de una partida no es admisible (5.3).
        Assert.Equal(["session.eligible"], ids);
    }

    [Fact]
    public async Task RealCatalog_HasExactlyTheThreeImmediateReversibleTweaksMarkedEligible()
    {
        var catalog = await LoadRealCatalogAsync();
        var eligible = catalog.Definitions.Where(d => d.SessionEligible).Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["cpu.core-parking-off", "cpu.epp-performance", "power.fragforge-performance"],
            eligible);

        // Y ninguno de ellos exige reinicio: es la condicion que hace posible revertir al cerrar el juego.
        Assert.All(catalog.Definitions.Where(d => d.SessionEligible), d => Assert.False(d.RequiresReboot, $"{d.Id} exige reinicio y no puede ser de sesion"));
    }

    // ---------------------------------------------------------------- 9.6 ciclo completo

    [Fact]
    public async Task Start_AppliesAndWritesTheMarker_ThenStopRevertsAndClearsIt()
    {
        var service = CreateService();
        Assert.Null(await service.ReadMarkerAsync());

        var start = await service.StartAsync(TweakProfile.Advanced);
        Assert.True(start.Started, string.Join(" | ", start.Messages));
        Assert.NotNull(start.Marker);
        Assert.True(File.Exists(service.MarkerPath), "el marcador tiene que existir tras Start");
        Assert.Equal(["session.eligible"], start.Marker!.TweakIds);
        Assert.False(string.IsNullOrWhiteSpace(start.Marker.SnapshotId), "sin snapshot no se puede revertir");

        var stop = await service.StopAsync();
        Assert.True(stop.Reverted, string.Join(" | ", stop.Errors));
        Assert.False(File.Exists(service.MarkerPath), "tras un revert correcto el marcador se borra");
    }

    [Fact]
    public async Task Watch_RevertsWhenTheGameAppearsAndThenCloses()
    {
        var service = CreateService();
        var start = await service.StartAsync(TweakProfile.Advanced);
        Assert.True(start.Started);

        // Guion: aun no corre, arranca, sigue corriendo, se cierra.
        var states = new Queue<bool?>([false, true, true, false]);
        var polls = 0;
        bool? Probe() { polls++; return states.Count > 0 ? states.Dequeue() : false; }
        Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

        var result = await service.WatchAsync(Probe, NoDelay);

        Assert.True(result.Reverted, string.Join(" | ", result.Errors));
        Assert.False(File.Exists(service.MarkerPath));
        Assert.True(polls >= 4, $"deberia haber sondeado al menos 4 veces, sono {polls}");
    }

    [Fact]
    public async Task Watch_DoesNotRevertWhileTheGameWasNeverRunning()
    {
        var service = CreateService();
        Assert.True((await service.StartAsync(TweakProfile.Advanced)).Started);

        // El juego nunca aparece: la sesion espera. Se cancela para salir del bucle.
        using var cts = new CancellationTokenSource();
        var polls = 0;
        bool? Probe() { polls++; if (polls >= 3) cts.Cancel(); return false; }
        Task NoDelay(TimeSpan _, CancellationToken token) => Task.CompletedTask;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WatchAsync(Probe, NoDelay, cts.Token));

        Assert.True(File.Exists(service.MarkerPath), "sin haber visto el juego NO se revierte: la sesion sigue preparada");
    }

    /// <summary>
    /// Si no se puede saber si el juego corre, no se revierte: hacerlo con el juego abierto seria una
    /// sorpresa en mitad de una partida.
    /// </summary>
    [Fact]
    public async Task Watch_WithUnknownProbe_DoesNotRevert()
    {
        var service = CreateService();
        Assert.True((await service.StartAsync(TweakProfile.Advanced)).Started);

        using var cts = new CancellationTokenSource();
        var polls = 0;
        bool? Probe() { polls++; if (polls >= 3) cts.Cancel(); return null; }
        Task NoDelay(TimeSpan _, CancellationToken token) => Task.CompletedTask;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WatchAsync(Probe, NoDelay, cts.Token));
        Assert.True(File.Exists(service.MarkerPath));
    }

    // ---------------------------------------------------------------- 9.3 recuperacion tras caida

    [Fact]
    public async Task Recover_AfterACrashWithTheGameClosed_RevertsAndCleansUp()
    {
        var service = CreateService();
        Assert.True((await service.StartAsync(TweakProfile.Advanced)).Started);
        // "Caida": no se llama a Stop, el marcador queda en disco. Un servicio nuevo lo encuentra.
        var recovered = new GameSessionService(_harness.CreateEngine(), _harness.Catalog, _harness.Paths);

        var marker = await recovered.ReadMarkerAsync();
        Assert.NotNull(marker);

        // Se simula el revert directo (CS2 no corre) para no depender del estado real de la maquina.
        var stop = await recovered.StopAsync();
        Assert.True(stop.Reverted, string.Join(" | ", stop.Errors));
        Assert.Null(await recovered.ReadMarkerAsync());
    }

    [Fact]
    public async Task Recover_IsIdempotent_WhenThereIsNoMarker()
    {
        var service = CreateService();

        var first = await service.RecoverAsync();
        var second = await service.RecoverAsync();

        Assert.True(first.AlreadyReverted);
        Assert.True(second.AlreadyReverted);
    }

    /// <summary>
    /// Revert ya hecho y borrado fallido: el marcador dice <c>reverted: true</c>. El siguiente arranque NO
    /// revierte otra vez, solo limpia.
    /// </summary>
    [Fact]
    public async Task Recover_WithAMarkerAlreadyReverted_OnlyCleansUp_WithoutRevertingAgain()
    {
        var service = CreateService();
        Assert.True((await service.StartAsync(TweakProfile.Advanced)).Started);

        // Se marca como revertido a mano, como quedaria si el borrado hubiera fallado.
        var marker = await service.ReadMarkerAsync();
        var reverted = marker! with { Reverted = true };
        Directory.CreateDirectory(_harness.Paths.Root);
        await File.WriteAllTextAsync(service.MarkerPath,
            System.Text.Json.JsonSerializer.Serialize(reverted, GameSessionPaths.SerializerOptions));

        var result = await service.RecoverAsync();

        Assert.True(result.AlreadyReverted);
        Assert.False(File.Exists(service.MarkerPath), "el marcador revertido se limpia");
    }

    // ---------------------------------------------------------------- 9.6 revert parcial

    [Fact]
    public async Task Stop_WhenTheRevertFails_KeepsTheMarkerAndReports()
    {
        var service = CreateService();
        Assert.True((await service.StartAsync(TweakProfile.Advanced)).Started);

        // El snapshot se corrompe: revertir contra el va a fallar y el marcador NO debe borrarse.
        var marker = await service.ReadMarkerAsync();
        // FileSnapshotStore: <snapshots>\<id>\snapshot.json (FileSnapshotStore.cs:17,24)
        var snapshotFile = Path.Combine(_harness.Paths.Snapshots, marker!.SnapshotId!, "snapshot.json");
        Assert.True(File.Exists(snapshotFile), $"snapshot esperado en {snapshotFile}");
        await File.WriteAllTextAsync(snapshotFile, "{ esto no es un snapshot valido }");

        var stop = await service.StopAsync();

        Assert.False(stop.Reverted, "un revert que falla no puede reportarse como exitoso");
        Assert.True(stop.Errors.Count > 0, "el fallo tiene que venir con un motivo");
        Assert.True(File.Exists(service.MarkerPath), "el marcador se conserva para poder reintentar");
    }

    [Fact]
    public async Task Stop_WithoutASnapshot_DoesNotInventAReversion()
    {
        var service = CreateService();
        Directory.CreateDirectory(_harness.Paths.Root);
        await File.WriteAllTextAsync(service.MarkerPath, System.Text.Json.JsonSerializer.Serialize(
            new GameSessionMarker { OperationId = "op-sin-snapshot", SnapshotId = null, TweakIds = ["session.eligible"] },
            GameSessionPaths.SerializerOptions));

        var stop = await service.StopAsync();

        Assert.False(stop.Reverted);
        var error = Assert.Single(stop.Errors);
        Assert.Contains("snapshot", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(service.MarkerPath));
    }

    // ---------------------------------------------------------------- 9.6 casos de borde

    [Fact]
    public async Task StartTwice_DoesNotStackASecondSession()
    {
        var service = CreateService();

        var first = await service.StartAsync(TweakProfile.Advanced);
        var second = await service.StartAsync(TweakProfile.Advanced);

        Assert.True(first.Started);
        Assert.False(second.Started, "un segundo Start no debe apilar otra sesion");
        Assert.True(second.Messages.Count > 0);
        Assert.Equal(first.Marker!.OperationId, second.Marker!.OperationId);
    }

    [Fact]
    public async Task Stop_WithoutASession_DoesNotFail()
    {
        var service = CreateService();

        var stop = await service.StopAsync();

        Assert.True(stop.AlreadyReverted);
        Assert.Empty(stop.Errors);
    }

    [Fact]
    public async Task Status_WithoutSession_IsInactive()
    {
        var service = CreateService();
        var status = await service.StatusAsync();
        Assert.Equal(GameSessionState.Inactive, status.State);
        Assert.Null(status.Marker);
    }

    [Fact]
    public async Task Start_WithAProfileWithoutEligibleTweaks_DoesNotWriteAMarker()
    {
        var service = CreateService();
        // Perfil Safe sobre un catalogo cuyos elegibles exigen Advanced... aqui se usa un catalogo vacio de
        // elegibles a proposito.
        _harness.WithCatalog([Tweak.Create("session.plain", Spec.Action("Fake", new { id = "p1" }))]);
        var empty = new GameSessionService(_harness.CreateEngine(), _harness.Catalog, _harness.Paths);

        var result = await empty.StartAsync(TweakProfile.Advanced);

        Assert.False(result.Started);
        Assert.False(File.Exists(empty.MarkerPath));
    }

    // ---------------------------------------------------------------- catalogo real

    private static async Task<FragForge.Tweaks.TweakCatalog> LoadRealCatalogAsync()
    {
        var schema = new FragForge.Tweaks.Validation.JsonSchemaValidator(FragForge.Tweaks.JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var factory = new FragForge.Actions.DependencyInjection.TweakActionFactory(
            registry, FragForge.Core.Persistence.ProtectionPolicy.CreateDefault(), new FakeServiceController(),
            new FakeScheduledTaskStore(), new FakeNetworkAdapterController(), new FakeProcessRunner(),
            FragForge.Actions.Diagnostics.DiagnosticProbeFactory.CreateDefaults(registry));
        var validator = new FragForge.Tweaks.Validation.TweakCatalogValidator(schema, new FragForge.Core.Conditions.ConditionFactory(), factory);
        return await new FragForge.Tweaks.JsonTweakCatalogLoader(validator).LoadEmbeddedAsync();
    }
}
