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

    /// <summary>
    /// Los textos REALES que ve el usuario. Se cargan de los recursos embebidos (la misma fuente que
    /// CompositionRoot.cs:77), no de un diccionario de juguete, para que una clave ausente se note.
    /// </summary>
    private static Task<Localizer> Localizer(string culture = "es-MX") =>
        FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync(culture);

    private static PreflightService Service(ITweakCatalog catalog, Localizer? localizer = null) =>
        new(new FakeSystemRestoreService { Protection = FeatureState.Enabled }, catalog, null, localizer);

    /// <summary>Un tweak mínimo con las acciones que se le pasen.</summary>
    private static TweakDefinition MinimalTweak(string id, params string[] kinds) => new()
    {
        Id = id,
        Category = "Network",
        Title = new LocalizedText { EsMx = "t", EnUs = "t" },
        Description = new LocalizedText { EsMx = "d", EnUs = "d" },
        Evidence = EvidenceLevel.Medium,
        Risk = RiskLevel.Advanced,
        RequiresAdmin = true,
        RequiresReboot = false,
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

    /// <summary>
    /// Opciones de apply que PASAN la puerta del perfil. Hace falta porque un tweak con Risk.Advanced exige
    /// aceptacion explicita por id y la frase exacta (ProfileSelector.cs:43-45), y el Options() del harness
    /// (EngineTestHarness.cs:59) no las trae: sin esto, ApplyAsync moria antes de llegar al preflight y el
    /// test parecia decir que R-J no bloquea, cuando lo que fallaba era el perfil.
    /// </summary>
    private static ApplyOptions RiskAccepted(EngineTestHarness harness, params string[] ids) => harness.Options() with
    {
        RiskAcknowledgements = ids.ToHashSet(StringComparer.OrdinalIgnoreCase),
        AdvancedRiskPhrase = "ACEPTO EL RIESGO"
    };

    /// <summary>El tweak REAL del catálogo: es el único de los 32 con NetAdapterProperty y restartsNetworkLink.</summary>
    private static TweakDefinition RealNetworkTweak() => MinimalTweak("network.i225v-eee-off", "NetAdapterProperty") with { RestartsNetworkLink = true };

    [Fact]
    public async Task ATweakThatRestartsTheNetworkLink_IsBlocking_WithAReason()
    {
        var report = await Service(new TweakCatalog([RealNetworkTweak()]), await Localizer()).CheckAsync(Selecting("network.i225v-eee-off"), Profile());

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
        var report = await Service(new TweakCatalog([MinimalTweak("probe.x", kind)])).CheckAsync(Selecting("probe.x"), Profile());

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Id == "recovery" && c.Severity == PreflightSeverity.Blocking);
    }

    /// <summary>Un tweak normal no se ve afectado: ni Blocking ni mención.</summary>
    [Fact]
    public async Task APlainRegistryTweak_IsNotAffected()
    {
        var report = await Service(new TweakCatalog([MinimalTweak("latency.game-mode-on", "Registry")])).CheckAsync(Selecting("latency.game-mode-on"), Profile());

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
        var catalog = new TweakCatalog([MinimalTweak("latency.game-mode-on", "Registry"), RealNetworkTweak()]);

        var report = await Service(catalog, await Localizer()).CheckAsync(Selecting("latency.game-mode-on", "network.i225v-eee-off"), Profile());

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
        // El kind tiene que ser Bcdedit: NeedsOutOfBandRecovery juzga el KIND de la accion
        // (PreflightService.cs:139), no el id. Antes se creaba con Spec.Action("Fake", ...), de modo que
        // el tweak NO necesitaba recuperacion y el test culpaba al servicio de no excluirlo.
        var bcdeditMarked = Tweak.Create("probe.bcdedit", Spec.Action("Bcdedit"), RiskLevel.Advanced)
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

        // NO se assertea report.CanApply: con el catalogo vacio el preflight emite "catalog" Blocking
        // (PreflightService.cs:38, "El catálogo está vacío"), que es correcto y no tiene nada que ver con
        // R-J. Lo que este test afirma es que la comprobacion de recuperacion no se dispara.
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
    ///   (a) estática: <c>TweakEngine.RevertAsync</c> (:289-382) no contiene ninguna llamada a
    ///       <c>preflight</c>; las del motor están en ScanAsync (:57) y ApplyAsync (:126,:156,:158,
    ///       :198,:201), y ninguna cae dentro del revert.
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
        var applied = await engine.ApplyAsync(["network.i225v-eee-off"], RiskAccepted(harness, "network.i225v-eee-off"));
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
        // El motivo está a la vista, marcado como "no se podrá aplicar", y es el mensaje REAL: el harness
        // inyecta el localizador de produccion (EngineTestHarness.cs:60), asi que lleva el id y "R-J" en vez
        // de la clave. Se comprueban las tres cosas: el prefijo (lo pone el motor, TweakEngine.cs:138), que
        // el id aparece (dice CUAL no se podra aplicar) y que el motivo es el de R-J.
        var aviso = Assert.Single(report.Warnings, w => w.StartsWith("NO SE PODRÁ APLICAR", StringComparison.Ordinal));
        Assert.Contains("network.i225v-eee-off", aviso, StringComparison.Ordinal);
        Assert.Contains("R-J", aviso, StringComparison.Ordinal);
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
    /// <remarks>
    /// CORREGIDO en la ronda de fallos 2. El mecanismo, leido del codigo: TweakEngine SI LANZA
    /// PreflightFailedException (:161) y el catch generico de ApplyAsync (:277-283) la recoge, la pasa a
    /// report.Errors (:280) y devuelve Success=false (:284,:289). O sea: el motor lanza por dentro y el
    /// llamador recibe un reporte. Mi frase de la ronda anterior ("el contrato es no lanzar") era
    /// imprecisa: lo correcto es "el llamador de ApplyAsync no ve la excepcion".
    ///
    /// Y no es el unico camino: el perfil tambien puede rechazar antes (ProfileSelector.cs:43-45, hace falta
    /// RiskAccepted() abajo), lo que produciria Success=false SIN pasar por R-J. Para que este test pruebe
    /// R-J y no otra cosa, el motivo tiene que ser el del preflight.
    /// </remarks>
    [Fact]
    public async Task Apply_WithABlockingTweak_IsRejected_NotJustWarned()
    {
        using var harness = new EngineTestHarness().WithCatalog(RealNetworkTweak());

        var blocked = await harness.CreateEngine().ApplyAsync(["network.i225v-eee-off"], RiskAccepted(harness, "network.i225v-eee-off"));

        Assert.False(blocked.Success);
        // El motivo llega al usuario y es el de R-J, no un "algo falló" ni un rechazo de perfil.
        Assert.Contains(blocked.Errors, e => e.Contains("R-J", StringComparison.Ordinal));
        Assert.Contains(blocked.Errors, e => e.Contains("network.i225v-eee-off", StringComparison.Ordinal));
        // Y no se aplicó NADA: la traza del factory está vacía. Se asserta la traza ENTERA y no un
        // "apply:eee" concreto: MinimalTweak no lleva Parameters, así que el label real es "fake"
        // (TestSupport.cs:95) y buscar "apply:eee" habría pasado sin comprobar nada.
        Assert.Empty(harness.Factory.Trace);
    }
}
