using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class EngineTests
{
    private static string Sid() => TestProfiles.Pn64().InteractiveUserSid!;

    [Fact]
    public async Task Apply_Then_Revert_LeavesSystemIdentical_AndJournalsBothOperations()
    {
        using var harness = new EngineTestHarness();
        harness.Registry.Set(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value", "DWord", 3);
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        var apply = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True(apply.Success, string.Join(" | ", apply.Errors.Concat(apply.Warnings)));
        Assert.NotNull(apply.SnapshotId);
        Assert.Equal(1, harness.Restore.CreateCalls);
        Assert.Equal(1, harness.Registry.GetInt(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value"));
        var entries = await harness.Journal.ReadAllAsync();
        Assert.Contains(entries, e => e.EventType == JournalEventType.SnapshotCreated);
        Assert.Contains(entries, e => e.EventType == JournalEventType.TweakApplied && e.TweakId == "tweak.one");
        var revert = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.True(revert.Success, string.Join(" | ", revert.Errors.Concat(revert.Warnings)));
        Assert.Equal(3, harness.Registry.GetInt(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value"));
        entries = await harness.Journal.ReadAllAsync();
        Assert.Contains(entries, e => e.EventType == JournalEventType.RevertCompleted && e.TweakId == "tweak.one");
    }

    [Fact]
    public async Task Revert_DeletesValueAndAncestorsCreatedOnlyByFragForge()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.create", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeNew\Branch", "Flag", "String", "on")))));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.create"], harness.Options())).Success);
        Assert.Equal("on", harness.Registry.GetString(@"HKU\" + Sid() + @"\Software\FragForgeNew\Branch", "Flag"));
        Assert.True((await engine.RevertAsync(new RevertRequest { Last = true, Options = new RevertOptions { TargetUserSid = Sid() } })).Success, string.Join(" | ", (await engine.RevertAsync(new RevertRequest { Last = true, Options = new RevertOptions { TargetUserSid = Sid(), Force = true } })).Errors));
        Assert.Null(harness.Registry.GetString(@"HKU\" + Sid() + @"\Software\FragForgeNew\Branch", "Flag"));
        Assert.False(harness.Registry.Keys.ContainsKey(@"HKU\" + Sid() + @"\Software\FragForgeNew"));
    }

    [Fact]
    public async Task VerifyFailure_TriggersAutomaticRollback_AndReportsNotApplied()
    {
        using var harness = new EngineTestHarness(new Dictionary<string, ScriptedActionOptions>(StringComparer.OrdinalIgnoreCase) { ["fake.a"] = new ScriptedActionOptions { FailVerify = true } });
        harness.WithCatalog(Tweak.Create("tweak.fake", Spec.Action("Fake", new { id = "fake.a" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.fake"], harness.Options());
        Assert.False(report.Success);
        var result = Assert.Single(report.Results);
        Assert.True(result.RolledBack);
        Assert.False(result.RollbackFailed);
        Assert.Equal(TweakState.NotApplied, result.State);
        Assert.Equal(["verify:fake.a", "apply:fake.a", "verify:fake.a", "revert:fake.a"], harness.Factory.Trace);
    }

    [Fact]
    public async Task RollbackFailure_IsCritical_AndStopsFurtherTweaks()
    {
        using var harness = new EngineTestHarness(new Dictionary<string, ScriptedActionOptions>(StringComparer.OrdinalIgnoreCase) { ["fake.a"] = new ScriptedActionOptions { FailVerify = true, FailRevert = true } });
        harness.WithCatalog(Tweak.Create("tweak.a", Spec.Action("Fake", new { id = "fake.a" })), Tweak.Create("tweak.b", Spec.Action("Fake", new { id = "fake.b" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.a", "tweak.b"], harness.Options());
        Assert.True(report.HasCriticalFailure);
        Assert.DoesNotContain("apply:fake.b", harness.Factory.Trace);
        Assert.Contains("revert:fake.a", harness.Factory.Trace);
    }

    /// <summary>
    /// Capa 5.3: si un tweak reinicia el enlace, el aviso aparece ANTES de aplicar. Es lo que la UI y el CLI
    /// muestran al usuario, y tiene que estar aunque el tweak se aplique bien.
    /// </summary>
    [Fact]
    public async Task NetworkLinkRestart_ProducesAWarning_BeforeApplying()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.net", Spec.Action("Fake", new { id = "net" })) with { RestartsNetworkLink = true });
        var engine = harness.CreateEngine();

        var report = await engine.ApplyAsync(["tweak.net"], harness.Options());

        Assert.True(report.Success, string.Join("; ", report.Errors.Concat(report.Warnings)));
        var warning = Assert.Single(report.Warnings, w => w.Contains("reinicia el adaptador", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("tweak.net", warning, StringComparison.Ordinal);
        Assert.Contains("cortará", warning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Un tweak que NO reinicia el enlace no genera ese aviso: no se avisa de lo que no pasa.</summary>
    [Fact]
    public async Task WithoutNetworkLinkRestart_ThereIsNoSuchWarning()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.plain", Spec.Action("Fake", new { id = "plain" })));
        var engine = harness.CreateEngine();

        var report = await engine.ApplyAsync(["tweak.plain"], harness.Options());

        Assert.DoesNotContain(report.Warnings, w => w.Contains("reinicia el adaptador", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dependencies_AreOrderedBeforeDependents()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(
            Tweak.Create("tweak.child", Spec.Action("Fake", new { id = "child" }), dependsOn: ["tweak.parent"]),
            Tweak.Create("tweak.parent", Spec.Action("Fake", new { id = "parent" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.child"], harness.Options());
        Assert.True(report.Success, string.Join("; ", report.Errors.Concat(report.Warnings)));
        Assert.Equal(["verify:parent", "verify:child", "apply:parent", "verify:parent", "apply:child", "verify:child"], harness.Factory.Trace);
    }

    [Fact]
    public async Task Conflicts_AreRejectedBeforeTouchingTheSystem()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(
            Tweak.Create("tweak.a", Spec.Action("Fake", new { id = "a" }), conflictsWith: ["tweak.b"]),
            Tweak.Create("tweak.b", Spec.Action("Fake", new { id = "b" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.a", "tweak.b"], harness.Options());
        Assert.False(report.Success);
        Assert.Contains(report.Errors, e => e.Contains("Conflicto", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.Factory.Trace);
    }

    [Fact]
    public async Task AppliedExternally_IsNotOverwrittenWithoutForce()
    {
        using var harness = new EngineTestHarness();
        harness.Registry.Set(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value", "DWord", 1);
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        var scan = await engine.ScanAsync();
        Assert.Equal(TweakState.AppliedExternally, Assert.Single(scan.Tweaks).State);
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.Equal(TweakState.AppliedExternally, Assert.Single(report.Results).State);
        Assert.Equal(0, harness.Restore.CreateCalls);
        Assert.Empty(harness.Registry.Writes);
    }

    [Fact]
    public async Task IdempotentSecondApply_DoesNotCreateAnotherSnapshot()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.one"], harness.Options())).Success);
        var second = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True(second.Success);
        Assert.Null(second.SnapshotId);
        Assert.Equal(TweakState.Applied, Assert.Single(second.Results).State);
        Assert.Single(await harness.Snapshots.ListAsync());
    }

    [Fact]
    public async Task DryRun_DescribesChangesAndNeverWritesSnapshotsOrRestorePoints()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options(dryRun: true));
        Assert.True(report.Success, string.Join("; ", report.Errors));
        Assert.True(report.DryRun);
        Assert.Contains("no existía", Assert.Single(Assert.Single(report.Results).PlannedChanges).Description, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Registry.Writes);
        Assert.Equal(0, harness.Restore.CreateCalls);
        Assert.Empty(await harness.Snapshots.ListAsync());
    }

    [Fact]
    public async Task UnmetConditions_YieldNotApplicable_AndDependentsAreSkipped()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(
            Tweak.Create("tweak.nvidia", Spec.Action("Fake", new { id = "nvidia" }), conditions: [Spec.Condition("gpuVendor", new { vendor = "Nvidia" })]),
            Tweak.Create("tweak.dependent", Spec.Action("Fake", new { id = "dependent" }), conditions: [Spec.Condition("gpuVendor", new { vendor = "Nvidia" })], dependsOn: ["tweak.nvidia"]));
        var engine = harness.CreateEngine();
        var scan = await engine.ScanAsync();
        Assert.All(scan.Tweaks, t => Assert.Equal(TweakState.NotApplicable, t.State));
        var apply = await engine.ApplyAsync(["tweak.dependent"], harness.Options());
        Assert.All(apply.Results, r => Assert.Equal(TweakState.NotApplicable, r.State));
        Assert.Empty(harness.Factory.Trace);
    }

    [Fact]
    public async Task UnknownConditions_AreNotTreatedAsMet_AndAreReportedAsUnknown_NotAsNotApplicable()
    {
        // Hotfix PARTE A: una condición DESCONOCIDA no es una condición incumplida. Antes se
        // reportaba NotApplicable, que afirma "esto no aplica en tu equipo" sobre algo que no se
        // pudo medir. R-L: Unknown antes que adivinar.
        using var harness = new EngineTestHarness();
        harness.WithProfile(TestProfiles.NonWindows());
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" }), conditions: [Spec.Condition("windows")]));
        var engine = harness.CreateEngine();

        var scanned = Assert.Single((await engine.ScanAsync()).Tweaks);
        Assert.Equal(TweakState.Unknown, scanned.State);
        Assert.NotEqual(TweakState.NotApplicable, scanned.State);

        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        var applied = Assert.Single(report.Results);
        Assert.Equal(TweakState.Unknown, applied.State);
        Assert.NotEqual(TweakState.NotApplicable, applied.State);

        // Lo que NO cambia: la acción jamás se ejecuta.
        Assert.Empty(harness.Factory.Trace);
    }

    [Fact]
    public async Task UnmetConditions_StayNotApplicable_BecauseAKnownNoIsNotAnUnknownOne()
    {
        // Contraste: gpuVendor=Nvidia sobre una iGPU Intel es un "no" CONOCIDO, no un "no sé".
        using var harness = new EngineTestHarness();
        harness.WithProfile(TestProfiles.Pn64());
        harness.WithCatalog(Tweak.Create("tweak.nvidia", Spec.Action("Fake", new { id = "nvidia" }), conditions: [Spec.Condition("gpuVendor", new { vendor = "Nvidia" })]));
        var engine = harness.CreateEngine();

        var scanned = Assert.Single((await engine.ScanAsync()).Tweaks);

        Assert.Equal(TweakState.NotApplicable, scanned.State);
        Assert.True(scanned.Reason.Length > 0);
    }

    [Fact]
    public async Task UnknownCondition_InvalidatesAnExperimentArm_InsteadOfPretendingTheBaselineIsFine()
    {
        // El endurecimiento es deliberado: ExperimentRunner.EnsureArmStateAsync NO acepta Unknown al
        // preparar el brazo base (solo NotApplied/NotApplicable/Inspected), así que un estado no
        // determinado invalida la medición en lugar de producir números que parecen evidencia.
        using var harness = new EngineTestHarness();
        harness.WithProfile(TestProfiles.NonWindows());
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" }), conditions: [Spec.Condition("windows")]));
        var engine = harness.CreateEngine();

        var state = Assert.Single((await engine.ScanAsync()).Tweaks).State;

        // NotApplicable habría pasado el filtro del brazo base; Unknown no.
        Assert.Equal(TweakState.Unknown, state);
        Assert.False(state is TweakState.NotApplied or TweakState.NotApplicable or TweakState.Inspected);
    }

    [Fact]
    public async Task PendingReboot_BlocksRealApply_ButDryRunStillDescribes()
    {
        using var harness = new EngineTestHarness();
        harness.WithProfile(TestProfiles.PendingReboot());
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.one"], harness.Options(dryRun: true))).Success);
        var blocked = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.False(blocked.Success);
        Assert.Contains(blocked.Errors, e => e.Contains("reinicio pendiente", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("apply:one", harness.Factory.Trace);
    }

    [Fact]
    public async Task RestorePointFailure_AbortsBeforeApplyingAnything()
    {
        using var harness = new EngineTestHarness();
        harness.Restore.FailCreate = true;
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.False(report.Success, string.Join(" | ", report.Errors));
        Assert.DoesNotContain("apply:one", harness.Factory.Trace);
    }

    [Fact]
    public async Task AdvancedRisk_RequiresProfileAcknowledgementAndExactPhrase()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.advanced", Spec.Action("Fake", new { id = "advanced" }), risk: RiskLevel.Advanced));
        var engine = harness.CreateEngine();
        var denied = await engine.ApplyAsync(["tweak.advanced"], harness.Options(profile: TweakProfile.Balanced));
        Assert.False(denied.Success);
        Assert.Contains(denied.Errors, e => e.Contains("risk_confirmation_required", StringComparison.Ordinal) || e.Contains("perfil", StringComparison.OrdinalIgnoreCase));
        var accepted = await engine.ApplyAsync(["tweak.advanced"], harness.Options() with { RiskAcknowledgements = new HashSet<string>(["tweak.advanced"], StringComparer.OrdinalIgnoreCase), AdvancedRiskPhrase = "ACEPTO EL RIESGO" });
        Assert.True(accepted.Success, string.Join("; ", accepted.Errors));
    }

    [Fact]
    public async Task FailingExperimentHook_BlocksBeforeApply_WhenFatal()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine([new ThrowingExperimentHook()]);
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.False(report.Success, string.Join(" | ", report.Errors));
        Assert.DoesNotContain("apply:one", harness.Factory.Trace);
    }

    [Fact]
    public async Task Revert_RefusesAlreadyRevertedSnapshot_WithoutForce()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        var apply = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True((await engine.RevertAsync(new RevertRequest { SnapshotId = apply.SnapshotId, Options = new RevertOptions { TargetUserSid = Sid() } })).Success);
        var ex = await engine.RevertAsync(new RevertRequest { SnapshotId = apply.SnapshotId, Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.False(ex.Success);
        Assert.Contains(ex.Errors, e => e.Contains("ya fue revertido", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Revert_DetectsExternalDrift_AndForceOverrides()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.one"], harness.Options())).Success);
        harness.Registry.Set(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value", "DWord", 99);
        var drift = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.False(drift.Success);
        Assert.Contains(drift.Errors, e => e.Contains("cambió desde la aplicación", StringComparison.OrdinalIgnoreCase));
        var forced = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = Sid(), Force = true } });
        Assert.True(forced.Success, string.Join(" | ", forced.Errors));
        Assert.Null(harness.Registry.GetInt(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value"));
    }

    [Fact]
    public async Task Revert_RejectsAmbiguousSelection()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine();
        var ex = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Last = true, Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.False(ex.Success);
        Assert.Contains(ex.Errors, e => e.Contains("exactamente uno", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Journal_IsTamperEvident()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True((await harness.Journal.VerifyIntegrityAsync()).IsValid);
        var lines = await File.ReadAllLinesAsync(harness.Paths.Journal);
        lines[0] = lines[0].Replace("\"OperationStarted\"", "\"OperationFailed\"", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(harness.Paths.Journal, lines);
        var integrity = await harness.Journal.VerifyIntegrityAsync();
        Assert.False(integrity.IsValid);
        var blocked = await engine.RevertAsync(new RevertRequest { Last = true, Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.False(blocked.Success);
        Assert.Contains(blocked.Errors, e => e.Contains("journal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InspectOnlyTweak_IsReadOnly_AndNeverCreatesRestorePoint()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.inspect", Spec.Action("Fake", new { id = "inspect" }), tags: ["diagnostic"]));
        var engine = harness.CreateEngine();
        var scan = await engine.ScanAsync();
        Assert.False(harness.Catalog.Definitions[0].ReadOnly);
        Assert.NotNull(scan.Tweaks);
    }

    [Fact]
    public async Task Cancellation_BeforeApply_LeavesNoPartialState()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ApplyAsync(["tweak.one"], harness.Options(), cts.Token));
        Assert.Empty(harness.Registry.Writes);
        Assert.Equal(0, harness.Restore.CreateCalls);
    }
}
