using FragForge.Core.Abstractions;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Core.Preflight;
using FragForge.Tests.Core;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using Xunit;

namespace FragForge.Tests.Security;

/// <summary>
/// R-J: mientras el motor no escriba el script de recuperación fuera de banda, un tweak que pueda dejar el
/// equipo sin red o sin arranque no se aplica.
/// </summary>
/// <remarks>
/// <para>
/// El hallazgo que motiva esto: <c>TweakEngine</c> NO invoca <c>RecoveryWriter</c> (no aparece en el
/// fichero; solo se construye en <c>RecoveryWriter.cs:48</c>). Sin ese script, un cambio que corte el enlace
/// de red o el arranque no tiene vuelta atrás desde la app.
/// </para>
/// <para>
/// Que un <see cref="PreflightSeverity.Blocking"/> impide aplicar NO se supone: <c>PreflightReport.CanApply</c>
/// es falso si hay alguno (Results.cs:80) y <c>TweakEngine</c> lanza <c>PreflightFailedException</c> en
/// TweakEngine.cs:141 (apply) y :58 (snapshot). El último test de esta clase comprueba ese enlace con el
/// motor real.
/// </para>
/// </remarks>
public sealed class RecoveryCoverageTests
{
    private static HardwareProfile Profile() => new()
    {
        Os = new OsInfo { Name = "Windows 11", Build = 26100, Architecture = "X64" },
        Maintenance = new SystemMaintenanceInfo
        {
            IsAdministrator = true,
            SystemDriveFreeBytes = 8UL * 1024 * 1024 * 1024,
            PendingReboot = false,
            WindowsServicingActive = false
        }
    };

    private static PreflightService Service(ITweakCatalog catalog, Localizer? localizer = null) =>
        new(new FakeSystemRestoreService { Protection = FeatureState.Enabled }, catalog, null, localizer);

    /// <summary>Un tweak mínimo con las acciones que se le pasen.</summary>
    private static TweakDefinition Tweak(string id, params string[] kinds) => new()
    {
        Id = id,
        Category = "Network",
        Title = new LocalizedText { EsMx = "t", EnUs = "t" },
        Description = new LocalizedText { EsMx = "d", EnUs = "d" },
        Evidence = EvidenceLevel.Medium,
        Risk = RiskLevel.Advanced,
        RequiresAdmin = true,
        RequiresReboot = false,
        ReadOnly = false,
        ExpectedImpact = new ExpectedImpact(),
        References = ["https://example.invalid/r"],
        Actions = [.. kinds.Select(k => new ActionSpec { Kind = k })]
    };

    /// <summary>
    /// Opciones de apply con los ids SELECCIONADOS. Es lo que el motor rellena en TweakEngine.cs (planes
    /// resueltos) y lo único que puede hacer que R-J bloquee: sin ids no se bloquea nada (E1c).
    /// </summary>
    private static ApplyOptions Selecting(params string[] ids) =>
        new() { SelectedIds = ids.ToHashSet(StringComparer.OrdinalIgnoreCase) };

    /// <summary>El tweak REAL del catálogo: es el único de los 32 con NetAdapterProperty y restartsNetworkLink.</summary>
    private static TweakDefinition RealNetworkTweak() => Tweak("network.i225v-eee-off", "NetAdapterProperty") with { RestartsNetworkLink = true };

    [Fact]
    public async Task ATweakThatRestartsTheNetworkLink_IsBlocking_WithAReason()
    {
        var report = await Service(new TweakCatalog([RealNetworkTweak()])).CheckAsync(Selecting("network.i225v-eee-off"), Profile());

        Assert.False(report.CanApply);
        var check = Assert.Single(report.Checks, c => c.Id == "recovery");
        Assert.Equal(PreflightSeverity.Blocking, check.Severity);
        // El motivo: qué puede pasar y por qué no hay vuelta atrás.
        Assert.Contains("network.i225v-eee-off", check.Message, StringComparison.Ordinal);
        Assert.Contains("R-J", check.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(check.Remediation));
    }

    /// <summary>Probado por las tres vías por separado: no se exige que vengan juntas.</summary>
    [Theory]
    [InlineData("NetAdapterProperty")]
    [InlineData("Bcdedit")]
    [InlineData("DriverInstall")]
    public async Task EachOutOfBandKind_IsBlockingOnItsOwn(string kind)
    {
        var report = await Service(new TweakCatalog([Tweak("probe.x", kind)])).CheckAsync(Selecting("probe.x"), Profile());

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Id == "recovery" && c.Severity == PreflightSeverity.Blocking);
    }

    /// <summary>Un tweak normal no se ve afectado: ni Blocking ni mención.</summary>
    [Fact]
    public async Task APlainRegistryTweak_IsNotAffected()
    {
        var report = await Service(new TweakCatalog([Tweak("latency.game-mode-on", "Registry")])).CheckAsync(Selecting("latency.game-mode-on"), Profile());

        Assert.True(report.CanApply);
        Assert.DoesNotContain(report.Checks, c => c.Id == "recovery");
    }

    /// <summary>
    /// E1f.3: la MEZCLA. Se seleccionan un tweak inocuo y el que necesita recuperación: se bloquea, y el
    /// motivo nombra al culpable (no a los dos, ni al inocuo).
    /// </summary>
    [Fact]
    public async Task SelectingOneDangerousTweakAmongSafeOnes_BlocksAndNamesOnlyTheCulprit()
    {
        var catalog = new TweakCatalog([Tweak("latency.game-mode-on", "Registry"), RealNetworkTweak()]);

        var report = await Service(catalog).CheckAsync(Selecting("latency.game-mode-on", "network.i225v-eee-off"), Profile());

        Assert.False(report.CanApply);
        var check = Assert.Single(report.Checks, c => c.Id == "recovery");
        Assert.Contains("network.i225v-eee-off", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("latency.game-mode-on", check.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// E1f.4 y E1c: DRY-RUN no se bloquea. Y una llamada SIN ids (la vieja, o la del informe) tampoco: no
    /// se puede bloquear por algo que no consta que se vaya a aplicar.
    /// </summary>
    [Fact]
    public async Task WithoutSelectedIds_NothingIsBlocked_EvenWithADangerousTweakInTheCatalog()
    {
        var service = Service(new TweakCatalog([RealNetworkTweak()]));

        // Sin ids: es la llamada "vieja". Antes bloqueaba por catálogo; ese era el bug.
        var sinIds = await service.CheckAsync(new ApplyOptions(), Profile());
        Assert.True(sinIds.CanApply);
        Assert.DoesNotContain(sinIds.Checks, c => c.Id == "recovery");

        // Dry-run: ni siquiera se pasa de la primera comprobación.
        var dryRun = await service.CheckAsync(new ApplyOptions { DryRun = true, SelectedIds = new HashSet<string>(["network.i225v-eee-off"]) }, Profile());
        Assert.True(dryRun.CanApply);
    }

    /// <summary>
    /// E1f.1 y E1f.2 con el MOTOR: un apply real de un tweak inocuo pasa, y con el peligroso lanza.
    /// Es la prueba de que el cableado TweakEngine -> SelectedIds funciona, no solo el preflight suelto.
    /// </summary>
    [Fact]
    public async Task ApplyOfASafeTweak_IsNotBlocked_EvenWithTheNetworkTweakInTheCatalog()
    {
        // "Fake" con parametros: es lo que el FakeActionFactory del harness sabe ejecutar (TestSupport.cs:91-97).
        var safe = Tweak.Create("latency.mouse-acceleration-off", Spec.Action("Fake", new { id = "mouse" }), RiskLevel.Safe);
        var dangerous = Tweak.Create("network.i225v-eee-off", Spec.Action("Fake", new { id = "eee" }), RiskLevel.Advanced) with { RestartsNetworkLink = true };

        using var harness = new EngineTestHarness().WithCatalog([safe, dangerous]);
        // El tweak peligroso ESTÁ en el catálogo, y aun así no bloquea: solo cuenta lo seleccionado.
        Assert.Contains(harness.Catalog.Definitions, d => d.Id == "network.i225v-eee-off");

        var report = await harness.CreateEngine().ApplyAsync(["latency.mouse-acceleration-off"], harness.Options());

        Assert.True(report.Success, string.Join(" | ", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("R-J", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// E1d: una sesión de juego no puede incluir un tweak que necesita recuperación fuera de banda, aunque
    /// alguien marque sessionEligible por error. Se usa el MISMO predicado que el preflight.
    /// </summary>
    [Fact]
    public void GameSession_ExcludesEveryTweakThatNeedsOutOfBandRecovery()
    {
        var dangerousButMarked = Tweak.Create("network.i225v-eee-off", Spec.Action("Fake", new { id = "eee" }), RiskLevel.Advanced)
            with { RestartsNetworkLink = true, SessionEligible = true };
        var bcdeditMarked = Tweak.Create("probe.bcdedit", Spec.Action("Fake", new { id = "bcd" }), RiskLevel.Advanced)
            with { SessionEligible = true };
        var fine = Tweak.Create("cpu.epp-performance", Spec.Action("Fake", new { id = "epp" }), RiskLevel.Safe) with { SessionEligible = true };

        using var harness = new EngineTestHarness().WithCatalog([dangerousButMarked, bcdeditMarked, fine]);
        var service = new FragForge.Core.Sessions.GameSessionService(harness.CreateEngine(), harness.Catalog, harness.Paths);

        var ids = service.EligibleIds(TweakProfile.Advanced);

        Assert.Contains("cpu.epp-performance", ids);
        Assert.DoesNotContain("network.i225v-eee-off", ids);   // restartsNetworkLink
        Assert.DoesNotContain("probe.bcdedit", ids);           // el Kind, sin depender del flag
    }

    /// <summary>Un catálogo vacío tampoco: la comprobación es por definiciones, no por estar el servicio vivo.</summary>
    [Fact]
    public async Task AnEmptyCatalog_IsNotAffected()
    {
        var report = await Service(new TweakCatalog([])).CheckAsync(Selecting(), Profile());

        Assert.True(report.CanApply);
        Assert.DoesNotContain(report.Checks, c => c.Id == "recovery");
    }

    /// <summary>El mensaje está en los dos idiomas y sale en el que toca, sin texto suelto en el código.</summary>
    /// <summary>
    /// El texto sale del JSON de localización REAL, no de un diccionario de juguete: si la clave no estuviera
    /// en los recursos, <c>Localizer[key]</c> devuelve la clave y este test lo vería.
    /// </summary>
    [Theory]
    [InlineData("es-MX", "todavía no escribe")]
    [InlineData("en-US", "does not write")]
    public async Task TheReason_IsLocalized(string culture, string expectedFragment)
    {
        var localizer = await FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync(culture);
        var report = await Service(new TweakCatalog([RealNetworkTweak()]), localizer)
            .CheckAsync(Selecting("network.i225v-eee-off"), Profile());

        var check = Assert.Single(report.Checks, c => c.Id == "recovery");
        Assert.Contains(expectedFragment, check.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// F1: el REVERT no se bloquea nunca por R-J. Volver atrás es la salida de emergencia: si el preflight
    /// pudiera impedirla, el usuario quedaría atrapado con el cambio aplicado.
    /// </summary>
    /// <remarks>
    /// Se demuestra de dos formas complementarias:
    ///   (a) estática: <c>TweakEngine.RevertAsync</c> (línea 278) no contiene ninguna llamada a
    ///       <c>preflight</c>; las únicas están en ScanAsync (:57) y ApplyAsync (:145), y ninguna está
    ///       después de la línea 277.
    ///   (b) aquí: se aplica un tweak, se revierte, y un preflight ESPÍA confirma que el revert no consultó.
    /// </remarks>
    [Fact]
    public async Task Revert_DoesNotConsultThePreflight_SoRecoveryBlockingCannotTrapTheUser()
    {
        var danger = Tweak.Create("network.i225v-eee-off", Spec.Action("Fake", new { id = "eee" }), RiskLevel.Advanced)
            with { RestartsNetworkLink = true };

        using var harness = new EngineTestHarness().WithCatalog([danger]);
        var spy = new CountingPreflight();
        // Mismo ctor de 11 argumentos que el harness (TweakEngine.cs:14-25), con el espía en su sitio.
        // No se anade nada a EngineTestHarness ni al ctor del motor (R-A).
        var engine = new FragForge.Core.Engine.TweakEngine(
            harness.Catalog, harness.Profiler, harness.Factory, new FragForge.Core.Conditions.ConditionFactory(),
            harness.Snapshots, harness.Journal, harness.Restore, spy,
            new FragForge.Core.Persistence.FileExecutionGate(harness.Paths), [],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FragForge.Core.Engine.TweakEngine>.Instance);

        // Para tener algo que revertir hace falta un snapshot; se aplica con un preflight que NO bloquea
        // (el espía delega pero se le quita el catálogo de la comprobación: ver CountingPreflight).
        var applied = await engine.ApplyAsync(["network.i225v-eee-off"], harness.Options(dryRun: false));
        var callsAfterApply = spy.Calls;
        Assert.True(callsAfterApply > 0, "si el apply no consultó el preflight, el test no prueba nada del revert");

        // El revert: sin saber el snapshot se usa --last, que es lo que hace el CLI.
        var reverted = await engine.RevertAsync(new RevertRequest { Last = true });

        Assert.True(reverted.Success, string.Join(" | ", reverted.Errors));
        Assert.Equal(callsAfterApply, spy.Calls);   // el revert NO consultó el preflight
    }

    /// <summary>
    /// Preflight que cuenta consultas y delega. Para el apply deja pasar (sin Blocking) porque lo que se
    /// prueba es el revert; el bloqueo real se prueba en los otros tests de esta clase.
    /// </summary>
    private sealed class CountingPreflight : IPreflightService
    {
        public int Calls { get; private set; }
        public Task<PreflightReport> CheckAsync(ApplyOptions options, HardwareProfile profile, CancellationToken cancellationToken = default)
        {
            Calls++;
            // Sin checks Blocking: aquí solo interesa CONTAR si el revert consulta.
            return Task.FromResult(PreflightReport.Passed([new PreflightCheck("spy", PreflightSeverity.Info, "ok")]));
        }
    }

    /// <summary>
    /// F2: el DRY-RUN no lanza, pero TAMPOCO esconde el motivo. La vista previa tiene que decir "esto no se
    /// podrá aplicar y por qué" ANTES de que el usuario lo intente.
    /// </summary>
    /// <remarks>
    /// Antes de F2, el dry-run retornaba en TweakEngine.cs:125, doce líneas antes del preflight (:145), así
    /// que el chequeo "recovery" no se calculaba nunca y la vista previa salía limpia para un tweak que
    /// después fallaba al aplicar. Eso es peor que bloquear: el usuario se entera al final.
    /// </remarks>
    [Fact]
    public async Task DryRun_DoesNotThrow_ButShowsTheBlockingCheckAsAWarning()
    {
        var danger = Tweak.Create("network.i225v-eee-off", Spec.Action("Fake", new { id = "eee" }), RiskLevel.Advanced)
            with { RestartsNetworkLink = true };
        using var harness = new EngineTestHarness().WithCatalog([danger]);

        // NO lanza: un dry-run no cambia nada.
        var report = await harness.CreateEngine().ApplyAsync(["network.i225v-eee-off"], harness.Options(dryRun: true));

        Assert.True(report.DryRun);
        Assert.True(report.Success, string.Join(" | ", report.Errors));
        // Y el motivo está a la vista, marcado como "no se podrá aplicar".
        Assert.Contains(report.Warnings, w => w.Contains("NO SE PODRÁ APLICAR", StringComparison.Ordinal)
                                          && w.Contains("network.i225v-eee-off", StringComparison.Ordinal));
    }

    /// <summary>Un dry-run de algo inocuo no arrastra avisos de R-J.</summary>
    [Fact]
    public async Task DryRun_OfASafeTweak_HasNoRecoveryWarning()
    {
        var safe = Tweak.Create("latency.mouse-acceleration-off", Spec.Action("Fake", new { id = "m" }), RiskLevel.Safe);
        using var harness = new EngineTestHarness().WithCatalog([safe]);

        var report = await harness.CreateEngine().ApplyAsync(["latency.mouse-acceleration-off"], harness.Options(dryRun: true));

        Assert.DoesNotContain(report.Warnings, w => w.Contains("NO SE PODRÁ APLICAR", StringComparison.Ordinal));
    }

    /// <summary>
    /// El enlace que hace que "Blocking" signifique algo: el MOTOR rechaza aplicar. Sin este test, los de
    /// arriba solo prueban que se emite un aviso.
    /// </summary>
    [Fact]
    public async Task Apply_WithABlockingTweak_IsRejected_NotJustWarned()
    {
        using var harness = new EngineTestHarness().WithCatalog(RealNetworkTweak());

        var failure = await Assert.ThrowsAsync<PreflightFailedException>(() =>
            harness.CreateEngine().ApplyAsync(["network.i225v-eee-off"], harness.Options()));

        Assert.Contains("recovery", string.Join(" ", failure.Report.Checks.Select(c => c.Id)), StringComparison.Ordinal);
    }
}
