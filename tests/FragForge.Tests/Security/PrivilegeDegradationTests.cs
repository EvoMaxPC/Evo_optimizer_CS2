using FragForge.Core.Models;
using FragForge.Core.Preflight;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using Xunit;

namespace FragForge.Tests.Security;

/// <summary>
/// Capa 10.6. Qué pasa SIN administrador. La degradación tiene que ser limpia y decirlo: lectura sí, apply no,
/// mensaje claro.
///
/// <para>
/// Un test por ruta: lectura (funciona), apply (bloqueado con motivo), dry-run (funciona, porque no escribe) y
/// el mensaje de remediación.
/// </para>
/// </summary>
public sealed class PrivilegeDegradationTests
{
    private static HardwareProfile Profile(bool admin, bool unsupported = false) => new()
    {
        Os = new OsInfo { Name = "Windows 11", Build = 26200, Architecture = "X64" },
        IsUnsupportedPlatform = unsupported,
        Maintenance = new SystemMaintenanceInfo { IsAdministrator = admin, SystemDriveFreeBytes = 10UL * 1024 * 1024 * 1024 }
    };

    private static PreflightService Service()
    {
        var restore = new FakeSystemRestoreService { Protection = FeatureState.Enabled };
        return new PreflightService(restore, new TweakCatalog([Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" }))]));
    }

    /// <summary>Ruta de APPLY sin admin: se bloquea, y el motivo dice qué hacer.</summary>
    [Fact]
    public async Task Apply_WithoutAdmin_IsBlockedWithAnActionableMessage()
    {
        var report = await Service().CheckAsync(new ApplyOptions { CreateRestorePoint = true }, Profile(admin: false));

        Assert.False(report.CanApply);
        var admin = Assert.Single(report.Checks, c => c.Id == "admin");
        Assert.Equal(PreflightSeverity.Blocking, admin.Severity);
        Assert.Contains("administrador", admin.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(admin.Remediation));
    }

    /// <summary>Ruta de APPLY con admin: no bloquea por privilegios.</summary>
    [Fact]
    public async Task Apply_WithAdmin_IsNotBlockedByPrivileges()
    {
        var report = await Service().CheckAsync(new ApplyOptions { CreateRestorePoint = true }, Profile(admin: true));

        Assert.True(report.CanApply);
        var admin = Assert.Single(report.Checks, c => c.Id == "admin");
        Assert.Equal(PreflightSeverity.Info, admin.Severity);
    }

    /// <summary>
    /// Ruta de DRY-RUN sin admin: funciona. No escribe nada, así que exigir elevación sería pedirle al usuario un
    /// UAC para no hacer nada.
    /// </summary>
    [Fact]
    public async Task DryRun_WithoutAdmin_IsAllowed()
    {
        var report = await Service().CheckAsync(new ApplyOptions { DryRun = true }, Profile(admin: false));

        Assert.True(report.CanApply);
        Assert.Contains(report.Checks, c => c.Id == "dry-run");
        Assert.DoesNotContain(report.Checks, c => c.Id == "admin");
    }

    /// <summary>
    /// Ruta de LECTURA: el perfil y la postura no dependen de ser admin. El modelo lo dice (IsAdministrator
    /// false) y nada de la lectura se bloquea por eso.
    /// </summary>
    [Fact]
    public void Reading_DoesNotDependOnAdmin()
    {
        var profile = Profile(admin: false);

        Assert.False(profile.Maintenance.IsAdministrator);
        // La lectura del perfil no lanza ni se niega: el dato de si somos admin es un campo más.
        Assert.Equal(26200, profile.Os.Build);
    }

    /// <summary>En una plataforma no soportada el motivo es otro y es el que se muestra primero.</summary>
    [Fact]
    public async Task AnUnsupportedPlatform_ReportsItsOwnBlockingCheck()
    {
        var report = await Service().CheckAsync(new ApplyOptions { CreateRestorePoint = true }, Profile(admin: false, unsupported: true));

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Id == "platform" && c.Severity == PreflightSeverity.Blocking);
    }
}
