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

    /// <summary>El tweak REAL del catálogo: es el único de los 32 con NetAdapterProperty y restartsNetworkLink.</summary>
    private static TweakDefinition RealNetworkTweak() => Tweak("network.i225v-eee-off", "NetAdapterProperty") with { RestartsNetworkLink = true };

    [Fact]
    public async Task ATweakThatRestartsTheNetworkLink_IsBlocking_WithAReason()
    {
        var report = await Service(new TweakCatalog([RealNetworkTweak()])).CheckAsync(new ApplyOptions(), Profile());

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
        var report = await Service(new TweakCatalog([Tweak("probe.x", kind)])).CheckAsync(new ApplyOptions(), Profile());

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Id == "recovery" && c.Severity == PreflightSeverity.Blocking);
    }

    /// <summary>Un tweak normal no se ve afectado: ni Blocking ni mención.</summary>
    [Fact]
    public async Task APlainRegistryTweak_IsNotAffected()
    {
        var report = await Service(new TweakCatalog([Tweak("latency.game-mode-on", "Registry")])).CheckAsync(new ApplyOptions(), Profile());

        Assert.True(report.CanApply);
        Assert.DoesNotContain(report.Checks, c => c.Id == "recovery");
    }

    /// <summary>Un catálogo vacío tampoco: la comprobación es por definiciones, no por estar el servicio vivo.</summary>
    [Fact]
    public async Task AnEmptyCatalog_IsNotAffected()
    {
        var report = await Service(new TweakCatalog([])).CheckAsync(new ApplyOptions(), Profile());

        Assert.True(report.CanApply);
        Assert.DoesNotContain(report.Checks, c => c.Id == "recovery");
    }

    /// <summary>El mensaje está en los dos idiomas y sale en el que toca, sin texto suelto en el código.</summary>
    [Theory]
    [InlineData("es-MX", "todavía no escribe")]
    [InlineData("en-US", "does not write")]
    public async Task TheReason_IsLocalized(string culture, string expectedFragment)
    {
        var report = await Service(new TweakCatalog([RealNetworkTweak()]), new Localizer(culture, new Dictionary<string, string>()))
            .CheckAsync(new ApplyOptions(), Profile());

        var check = Assert.Single(report.Checks, c => c.Id == "recovery");
        Assert.Contains(expectedFragment, check.Message, StringComparison.Ordinal);
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
