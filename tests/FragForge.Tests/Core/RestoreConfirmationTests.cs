using FragForge.Cli;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class RestoreConfirmationTests
{
    private const string ConfirmedMessage = "Sin punto de restauración del sistema (confirmado por el usuario). La única vuelta atrás es el revert de FragForge desde su journal y los respaldos que hagas vos.";
    private const string Suggestion = "Si querés aplicar igual, repetí el comando con --acepto-sin-punto-de-restauracion";

    private static EngineTestHarness Harness()
    {
        var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.simple", Spec.Action("Fake", new { id = "simple" })),
            Tweak.Create("tweak.recovery", Spec.Action("Bcdedit", new { id = "recovery" })));
        return harness;
    }

    [Theory]
    [InlineData(FeatureState.Disabled)]
    [InlineData(FeatureState.Unknown)]
    public async Task UnconfirmedUnavailable_BlocksWithSuggestionAndExitThree(FeatureState state)
    {
        using var harness = Harness();
        harness.Restore.Protection = state;
        var report = await harness.CreateEngine().ApplyAsync(["tweak.simple"], harness.Options());
        // Una acción seleccionada, protección deshabilitada o desconocida: 0 cambios y salida 3.
        Assert.False(report.Success);
        Assert.True(report.PreflightBlocked);
        Assert.Equal(3, Program.ReportExit(report));
        Assert.Contains("La protección del sistema está desactivada o no pudo confirmarse.", Assert.Single(report.Errors));
        Assert.Contains(Suggestion, Assert.Single(report.Errors));
        Assert.Equal(0, harness.Restore.CreateCalls);
        Assert.DoesNotContain("apply:simple", harness.Factory.Trace);
    }

    [Fact]
    public async Task ConfirmedUnavailable_AppliesWithSnapshotAndLiteralWarning()
    {
        using var harness = Harness();
        harness.Restore.Protection = FeatureState.Disabled;
        var report = await harness.CreateEngine().ApplyAsync(["tweak.simple"], harness.Options() with { AcceptWithoutSystemRestore = true });
        // 1 acción aplicable, 1 snapshot propio, 0 puntos de restauración del sistema.
        Assert.True(report.Success, string.Join(" | ", report.Errors));
        Assert.False(report.PreflightBlocked);
        Assert.NotNull(report.SnapshotId);
        Assert.Null(report.RestorePointSequence);
        Assert.Equal(0, harness.Restore.CreateCalls);
        Assert.Contains("apply:simple", harness.Factory.Trace);
        Assert.Contains(ConfirmedMessage, report.Warnings);
        Assert.Equal(0, Program.ReportExit(report));
    }

    [Fact]
    public async Task ConfirmedEnabled_CreatesRestorePointWithoutConfirmationWarning()
    {
        using var harness = Harness();
        harness.Restore.Protection = FeatureState.Enabled;
        var report = await harness.CreateEngine().ApplyAsync(["tweak.simple"], harness.Options() with { AcceptWithoutSystemRestore = true });
        // La protección está habilitada: 1 llamada falsa crea el punto de secuencia 42.
        Assert.True(report.Success, string.Join(" | ", report.Errors));
        Assert.Equal(1, harness.Restore.CreateCalls);
        Assert.Equal(42, report.RestorePointSequence);
        Assert.DoesNotContain(ConfirmedMessage, report.Warnings);
    }

    [Fact]
    public async Task RecoveryTweakSelected_RemainsBlockedDespiteConfirmation()
    {
        using var harness = Harness();
        harness.Restore.Protection = FeatureState.Disabled;
        var report = await harness.CreateEngine().ApplyAsync(["tweak.recovery"], harness.Options() with { AcceptWithoutSystemRestore = true });
        // 1 tweak Bcdedit seleccionado: R-J bloquea antes de crear snapshot o aplicar.
        Assert.False(report.Success);
        Assert.True(report.PreflightBlocked);
        Assert.Equal(3, Program.ReportExit(report));
        Assert.Contains("tweak.recovery", Assert.Single(report.Errors));
        Assert.Contains("R-J", Assert.Single(report.Errors));
        Assert.Equal(0, harness.Restore.CreateCalls);
        Assert.DoesNotContain("apply:recovery", harness.Factory.Trace);
    }

    [Fact]
    public async Task RecoveryTweakNotSelected_DoesNotBlock()
    {
        using var harness = Harness();
        harness.Restore.Protection = FeatureState.Disabled;
        var report = await harness.CreateEngine().ApplyAsync(["tweak.simple"], harness.Options() with { AcceptWithoutSystemRestore = true });
        // 1 tweak simple seleccionado y 1 Bcdedit no seleccionado: C3 no bloquea.
        Assert.True(report.Success, string.Join(" | ", report.Errors));
        Assert.Contains("apply:simple", harness.Factory.Trace);
        Assert.DoesNotContain("apply:recovery", harness.Factory.Trace);
        Assert.Contains(ConfirmedMessage, report.Warnings);
    }

    [Fact]
    public async Task Confirmation_IsNotReusedForNextInvocation()
    {
        using var harness = Harness();
        harness.Restore.Protection = FeatureState.Disabled;
        var engine = harness.CreateEngine();
        var first = await engine.ApplyAsync(["tweak.simple"], harness.Options() with { AcceptWithoutSystemRestore = true });
        var second = await engine.ApplyAsync(["tweak.simple"], harness.Options());
        // Dos llamadas: la primera confirma; la segunda no, aunque use el mismo motor.
        Assert.True(first.Success, string.Join(" | ", first.Errors));
        Assert.Contains(ConfirmedMessage, first.Warnings);
        Assert.False(second.Success);
        Assert.True(second.PreflightBlocked);
        Assert.Equal(3, Program.ReportExit(second));
        Assert.Contains(Suggestion, Assert.Single(second.Errors));
        Assert.Equal(0, harness.Restore.CreateCalls);
    }

    [Fact]
    public async Task Confirmation_DoesNotBypassAdministratorCheck()
    {
        using var harness = Harness();
        harness.WithProfile(TestProfiles.NoAdmin());
        harness.Restore.Protection = FeatureState.Disabled;
        var report = await harness.CreateEngine().ApplyAsync(["tweak.simple"], harness.Options() with { AcceptWithoutSystemRestore = true });
        // 0 privilegios de administrador: el chequeo admin sigue bloqueando.
        Assert.False(report.Success);
        Assert.True(report.PreflightBlocked);
        Assert.Contains("Se requieren privilegios de administrador.", Assert.Single(report.Errors));
        Assert.DoesNotContain("apply:simple", harness.Factory.Trace);
    }

    [Fact]
    public void Flag_IsExplicitAndHasNoValue()
    {
        var absent = CommandLineParser.Parse(["apply", "tweak.simple"]);
        var present = CommandLineParser.Parse(["apply", "tweak.simple", "--acepto-sin-punto-de-restauracion"]);
        // Sin flag no hay consentimiento; con flag presente sí, sin consumir un valor.
        Assert.False(absent.Has("acepto-sin-punto-de-restauracion"));
        Assert.True(present.Has("acepto-sin-punto-de-restauracion"));
        Assert.Equal(new[] { "tweak.simple" }, present.Positionals);
        Assert.Throws<ArgumentException>(() => CommandLineParser.Parse(["apply", "tweak.simple", "--acepto-sin-punto-de-restauracion", "true"]));
        Assert.Throws<ArgumentException>(() => CommandLineParser.Parse(["apply", "tweak.simple", "--acepto-sin-punto-de-restauracion=false"]));
    }
}
