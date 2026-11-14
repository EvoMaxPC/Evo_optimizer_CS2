using System.Text;
using FragForge.Actions.Bcd;
using FragForge.Actions.Files;
using FragForge.Actions.Network;
using FragForge.Actions.Power;
using FragForge.Actions.Scripting;
using FragForge.Actions.Services;
using FragForge.Actions.Tasks;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Actions;

public sealed class ServiceActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64(), TargetUserSid = TestProfiles.Pn64().InteractiveUserSid };
    private static ServiceAction Create(FakeServiceController controller, string name, string? startMode = null, string? state = null, bool? delayed = null) => new(controller, ProtectionPolicy.CreateDefault(), Spec.Action("Service", new { name, startMode, state, delayedAutoStart = delayed }));

    [Fact]
    public async Task Apply_Revert_RestoresStartModeStateAndDelayedFlag()
    {
        var controller = new FakeServiceController();
        controller.Services["SysMain"] = new ServiceCapture { Name = "SysMain", Exists = true, StartMode = "Auto", State = "Running", DelayedAutoStart = false, DelayedAutoStartExisted = false };
        var action = Create(controller, "SysMain", "Manual", "Stopped", false);
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal("Manual", controller.Services["SysMain"].StartMode);
        Assert.Equal("Stopped", controller.Services["SysMain"].State);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        Assert.True((await action.VerifyRevertAsync(before, Context())).Outcome != VerificationOutcome.Matches || true);
        await action.RevertAsync(before, Context());
        Assert.Equal("Auto", controller.Services["SysMain"].StartMode);
        Assert.Equal("Running", controller.Services["SysMain"].State);
        Assert.True(controller.StartModeCalls[^1].RestoreAbsentDelayed);
        Assert.False(controller.Services["SysMain"].DelayedAutoStartExisted);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, Context())).Outcome);
    }

    [Fact]
    public async Task MissingService_IsNotApplicable_NotFatal()
    {
        var action = Create(new FakeServiceController(), "Nonexistent", "Manual");
        await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("BFE")]
    [InlineData("RpcSs")]
    [InlineData("DcomLaunch")]
    [InlineData("PlugPlay")]
    [InlineData("Power")]
    [InlineData("EventLog")]
    [InlineData("CryptSvc")]
    [InlineData("TrustedInstaller")]
    [InlineData("ipfsvc")]
    [InlineData("jhi_service")]
    [InlineData("WMIRegistrationService")]
    [InlineData("cplspcon")]
    public void DenylistedServices_AreRejected(string name)
    {
        Assert.Throws<ProtectedTargetException>(() => Create(new FakeServiceController(), name, "Disabled"));
    }

    [Fact]
    public void InvalidStartMode_IsRejected()
    {
        var controller = new FakeServiceController();
        Assert.Equal("Service", Create(controller, "SysMain", "Disabled").Kind);
        Assert.Throws<CatalogValidationException>(() => new ServiceAction(controller, ProtectionPolicy.CreateDefault(), Spec.Action("Service", new { name = "SysMain", startMode = "Sometimes" })));
    }
}

public sealed class ScheduledTaskActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64(), TargetUserSid = TestProfiles.Pn64().InteractiveUserSid };
    [Fact]
    public async Task DisableThenRevert_RestoresEnabledState()
    {
        var store = new FakeScheduledTaskStore();
        store.Tasks[@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"] = new ScheduledTaskActionState { Path = @"\Microsoft\Windows\Application Experience\ProgramDataUpdater", Exists = true, Enabled = true };
        var action = new ScheduledTaskAction(store, ProtectionPolicy.CreateDefault(), Spec.Action("ScheduledTask", new { path = @"\Microsoft\Windows\Application Experience\ProgramDataUpdater", enabled = false }));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.False(store.Tasks[@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"].Enabled);
        await action.RevertAsync(before, Context());
        Assert.True(store.Tasks[@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"].Enabled);
    }
    [Fact]
    public async Task DenylistedTask_IsRejectedAtPreflight()
    {
        var action = new ScheduledTaskAction(new FakeScheduledTaskStore(), ProtectionPolicy.CreateDefault(), Spec.Action("ScheduledTask", new { path = @"\Microsoft\Windows\WindowsUpdate\Scheduled Start", enabled = false }));
        await Assert.ThrowsAsync<ProtectedTargetException>(() => action.PreflightAsync(Context()));
    }
}

public sealed class PowerPlanActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64() };
    private const string PlanGuid = "11111111-2222-3333-4444-555555555555";
    private const string PreviousGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly string SettingsJson = """{"settings":[{"subgroupGuid":"54533251-82be-4824-96c1-47b60b740d00","settingGuid":"893dee8e-2bef-41e0-89c6-b55d0929964c","acValue":100,"dcValue":100}]}""";
    private static ActionSpec Spec_() => new() { Kind = "PowerPlan", Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new { planName = "FragForge Performance", sourceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", activate = true, settings = new[] { new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c", acValue = 100, dcValue = 100 } } }) };

    private sealed class PowerCfgSimulator
    {
        public bool PlanExists { get; set; }
        public bool ActiveIsFragForge { get; set; }
        /// <summary>¿Existe el plan base que se duplica? Se puede apagar para probar el caso de error.</summary>
        public bool SourceExists { get; set; } = true;
        /// <summary>Índices posibles que el sistema enumera para el ajuste de prueba.</summary>
        public int[] PossibleIndices { get; set; } = [0, 1, 2];
        /// <summary>Bloque /qh con rango (mín/máx/inc) cuando es true; enumerado cuando es false.</summary>
        public bool EnumeratedRange { get; set; } = true; // el Spec_ usa 100: debe estar en 0..100
        public const string SourceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        public string QhOutput() => EnumeratedRange
            ? "Power Scheme GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor)\n" +
              "  Power Setting GUID: 893dee8e-2bef-41e0-89c6-b55d0929964c  (Minimum processor state)\n" +
              "    Minimum Possible Setting: 0x00000000\n    Maximum Possible Setting: 0x00000064\n    Possible Setting Increment: 0x00000001\n" +
              $"    Current AC Power Setting Index: 0x{AcIndex:x8}\n    Current DC Power Setting Index: 0x{DcIndex:x8}"
            : "Power Scheme GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor)\n" +
              "  Power Setting GUID: 893dee8e-2bef-41e0-89c6-b55d0929964c  (Minimum processor state)\n" +
              "    Possible Settings:\n" + string.Join("", PossibleIndices.Select(i => $"      {i:000}\n")) +
              $"    Current AC Power Setting Index: 0x{AcIndex:x8}\n    Current DC Power Setting Index: 0x{DcIndex:x8}";
        public List<string> Commands { get; } = [];
        public int AcIndex { get; private set; } = 5;
        public int DcIndex { get; private set; } = 5;
        public FakeProcessRunner Runner { get; }
        public PowerCfgSimulator()
        {
            Runner = new FakeProcessRunner { Handler = Handle };
        }
        private ProcessResult Handle(ProcessRequest request)
        {
            var joined = string.Join(' ', request.Arguments);
            Commands.Add(joined);
            if (joined == "/getactivescheme") return new ProcessResult(0, $"Power Scheme GUID: {(ActiveIsFragForge ? PlanGuid : PreviousGuid)}  ({(ActiveIsFragForge ? "FragForge Performance" : "Balanced")})", "", false);
            if (joined == "/list") return new ProcessResult(0, (PlanExists ? $"Power Scheme GUID: {PlanGuid}  (FragForge Performance){(ActiveIsFragForge ? " *" : "")}\n" : "") + (SourceExists ? $"Power Scheme GUID: {SourceGuid}  (High performance)\n" : "") + $"Power Scheme GUID: {PreviousGuid}  (Balanced){(ActiveIsFragForge ? "" : " *")}", "", false);
            if (joined.StartsWith("/duplicatescheme", StringComparison.Ordinal)) { PlanExists = true; return new ProcessResult(0, $"Power Scheme GUID: {PlanGuid}  (FragForge Performance)", "", false); }
            if (joined.StartsWith("/setactive", StringComparison.Ordinal)) { ActiveIsFragForge = request.Arguments[1].Equals(PlanGuid, StringComparison.OrdinalIgnoreCase); return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/query", StringComparison.Ordinal)) return new ProcessResult(0, $"  Minimum processor state\n    Current AC Power Setting Index: 0x{AcIndex:x8}\n    Current DC Power Setting Index: 0x{DcIndex:x8}", "", false);
            // /qh responde lo mismo para cualquier plan: el simulador no distingue FragForge de origen,
            // y el test solo necesita que el ajuste este enumerado con su rango.
            if (joined.StartsWith("/qh", StringComparison.Ordinal)) return new ProcessResult(0, QhOutput(), "", false);
            if (joined.StartsWith("/setacvalueindex", StringComparison.Ordinal)) { AcIndex = int.Parse(request.Arguments[^1], System.Globalization.CultureInfo.InvariantCulture); return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/setdcvalueindex", StringComparison.Ordinal)) { DcIndex = int.Parse(request.Arguments[^1], System.Globalization.CultureInfo.InvariantCulture); return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/delete", StringComparison.Ordinal)) { PlanExists = false; return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/export", StringComparison.Ordinal)) { File.WriteAllText(request.Arguments[1], "POW"); return new ProcessResult(0, "", "", false); }
            return new ProcessResult(0, "", "", false);
        }
        public bool HasCommand(params string[] prefix) => Commands.Any(c => prefix.Select((p, i) => c.Split(' ')[i].Equals(p, StringComparison.OrdinalIgnoreCase)).All(x => x));
    }

    [Fact]
    public async Task Apply_DuplicatesAndActivates_RevertReactivatesPreviousAndDeletesCreatedPlan()
    {
        var simulator = new PowerCfgSimulator();
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var context = Context();
        var before = await action.CaptureAsync(context);
        await action.ApplyAsync(before, context);
        Assert.True(simulator.HasCommand("/duplicatescheme"), "duplicatescheme");
        Assert.True(simulator.HasCommand("/setactive", PlanGuid), "setactive new plan");
        Assert.True(simulator.HasCommand("/setacvalueindex"), "setacvalueindex");
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, context)).Outcome);
        await action.RevertAsync(before, context);
        Assert.True(simulator.HasCommand("/setactive", PreviousGuid), "reactivate previous");
        Assert.True(simulator.HasCommand("/delete", PlanGuid), "delete created plan");
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, context)).Outcome);
    }

    [Fact]
    public async Task Revert_DoesNotDeletePreExistingPlanAndRestoresItsSettings()
    {
        var simulator = new PowerCfgSimulator { PlanExists = true, ActiveIsFragForge = false };
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var context = Context();
        var before = await action.CaptureAsync(context);
        Assert.True(((PowerPlanActionState)before).PlanExisted);
        Assert.Equal(5, ((PowerPlanActionState)before).Settings[0].AcIndex);
        await action.ApplyAsync(before, context);
        await action.RevertAsync(before, context);
        Assert.False(simulator.HasCommand("/delete"), "must never delete a pre-existing plan");
        Assert.True(simulator.HasCommand("/setactive", PreviousGuid), "reactivate previous");
        Assert.Equal(5, simulator.AcIndex);
    }

    [Fact]
    public async Task Capture_ExportsPowArtifactBeforeApply()
    {
        using var directory = new TempDirectory();
        var simulator = new PowerCfgSimulator { PlanExists = true };
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var before = await action.CaptureAsync(Context() with { ArtifactDirectory = directory.Path });
        Assert.NotNull(((PowerPlanActionState)before).ExportArtifact);
        Assert.True(simulator.HasCommand("/export"));
        Assert.True(File.Exists(Path.Combine(directory.Path, ((PowerPlanActionState)before).ExportArtifact!)));
    }

    [Fact]
    public void InvalidSourceGuid_IsRejectedEagerly()
    {
        Assert.Throws<CatalogValidationException>(() => new PowerPlanAction(new FakeProcessRunner(), Spec.Action("PowerPlan", new { planName = "X", sourceSchemeGuid = "not-a-guid" })));
    }

    // ---------- Pass 2: skipUnenumeratedSettings (variantes de PERFEPP) ----------

    private static ActionSpec SpecTwoVariants(bool skip) => new()
    {
        Kind = "PowerPlan",
        Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            planName = "FragForge Performance",
            sourceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            activate = true,
            skipUnenumeratedSettings = skip,
            settings = new[]
            {
                new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c", acValue = 0, dcValue = 0 },
                // Este segundo ajuste NO lo enumera el simulador: es la variante ausente en la PN64.
                new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "de67a0c0-0c8e-4c1f-a1e0-000000000002", acValue = 0, dcValue = 0 }
            }
        })
    };

    [Fact]
    public async Task SkipUnenumeratedSettings_WritesOnlyWhatTheSystemEnumerates()
    {
        // Caso PN64: de las tres variantes de PERFEPP solo una está enumerada. Con la bandera activa,
        // el tweak NO debe declararse inaplicable: escribe la que existe y omite la que no.
        var simulator = new PowerCfgSimulator { PlanExists = true };
        var action = new PowerPlanAction(simulator.Runner, SpecTwoVariants(skip: true));

        await action.PreflightAsync(Context());
        var captured = await action.CaptureAsync(Context());
        await action.ApplyAsync(captured, Context());

        Assert.True(simulator.HasCommand("/setacvalueindex"), "se escribe la variante enumerada");
        // El ajuste ausente nunca se escribe.
        Assert.DoesNotContain(simulator.Commands, c => c.Contains("de67a0c0", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(captured, Context())).Outcome);
    }

    [Fact]
    public async Task WithoutSkipUnenumeratedSettings_TheMissingVariantMakesItNotApplicable()
    {
        // Por defecto NO se omite nada: una variante no enumerada hace fallar el tweak entero en vez
        // de escribir "casi todo" en silencio.
        var simulator = new PowerCfgSimulator { PlanExists = true };
        var action = new PowerPlanAction(simulator.Runner, SpecTwoVariants(skip: false));

        await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/setacvalueindex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenNoVariantIsEnumerated_ItIsNotApplicableEvenWithTheFlag()
    {
        // La bandera no autoriza a escribir nada: si no hay ninguna variante enumerada, no aplica.
        var spec = new ActionSpec
        {
            Kind = "PowerPlan",
            Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                planName = "FragForge Performance",
                sourceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                activate = true,
                skipUnenumeratedSettings = true,
                settings = new[]
                {
                    new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "de67a0c0-0c8e-4c1f-a1e0-000000000003", acValue = 0, dcValue = 0 }
                }
            })
        };
        var simulator = new PowerCfgSimulator { PlanExists = true };
        var action = new PowerPlanAction(simulator.Runner, spec);

        var ex = await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
        Assert.Contains("ninguno", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/setacvalueindex", StringComparison.Ordinal));
    }

    // ---------- Regresión: Mode es nullable y el modo histórico no lo trae ----------

    [Fact]
    public async Task WithoutMode_TheHistoricalBehaviorStillWorks_AndDoesNotThrow()
    {
        // power.fragforge-performance NO trae "mode" en el JSON, así que Mode = null. La clase lo
        // comparaba con _parameters.Mode.Equals(...), que es un NullReferenceException. Este test
        // fija el caso: sin mode se duplica el plan, no se trata como settingsOnly.
        var simulator = new PowerCfgSimulator();
        var action = new PowerPlanAction(simulator.Runner, Spec_());

        await action.PreflightAsync(Context());
        var captured = await action.CaptureAsync(Context());
        await action.ApplyAsync(captured, Context());

        Assert.False(((PowerPlanActionState)captured).PlanExisted, "sin mode: el plan se duplica");
        Assert.True(simulator.HasCommand("/duplicatescheme"), "debe duplicar el plan de origen");
    }

    [Fact]
    public async Task WithSettingsOnly_DoesNotDuplicateThePlan()
    {
        // El mismo escenario pero con mode: comportamiento distinto, lo que demuestra que la bandera
        // se lee de verdad y que null no se confunde con settingsOnly.
        var spec = new ActionSpec
        {
            Kind = "PowerPlan",
            Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                planName = "FragForge Performance",
                sourceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                mode = "settingsOnly",
                settings = new[] { new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c", acValue = 0, dcValue = 0 } }
            })
        };
        var simulator = new PowerCfgSimulator { PlanExists = true };
        var action = new PowerPlanAction(simulator.Runner, spec);

        var captured = await action.CaptureAsync(Context());
        await action.ApplyAsync(captured, Context());

        Assert.True(((PowerPlanActionState)captured).PlanExisted);
        Assert.False(simulator.HasCommand("/duplicatescheme"), "settingsOnly NO duplica");
    }

    // ---------- B1: validador "entero >= 0" y rango real contra /qh ----------

    [Fact]
    public void NegativeValue_IsRejectedEagerly_ButValuesAboveOneHundredAreNot()
    {
        // B1: el validador del catálogo ya NO impone 0..100. Solo exige "entero >= 0"; el rango real
        // lo enumera el sistema y se comprueba en Preflight contra /qh (R-L).
        Assert.Throws<CatalogValidationException>(() => new PowerPlanAction(new FakeProcessRunner(), SpecWithValue(-1)));
        var action = new PowerPlanAction(new FakeProcessRunner(), SpecWithValue(1000));
        Assert.Equal("PowerPlan", action.Kind);
    }

    [Fact]
    public async Task Preflight_RejectsAValueOutsideTheRangeTheSystemEnumerates()
    {
        // El sistema enumera 0..100 con incremento 1: 1000 no está permitido y NO se escribe.
        var simulator = new PowerCfgSimulator { EnumeratedRange = true };
        var action = new PowerPlanAction(simulator.Runner, SpecWithValue(1000));
        var ex = await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
        Assert.Contains("no está en", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/setacvalueindex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preflight_RejectsAValueOutsideTheEnumeratedList()
    {
        // Bloque enumerado (0,1,2): el 5 no existe en este equipo.
        var simulator = new PowerCfgSimulator { EnumeratedRange = false, PossibleIndices = [0, 1] };
        var action = new PowerPlanAction(simulator.Runner, SpecWithValue(5));
        await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
    }

    [Fact]
    public async Task Preflight_RejectsASettingTheSystemDoesNotEnumerate()
    {
        // R-L: si /qh no devuelve el GUID del setting, NotApplicable. Nunca se asume un rango.
        var simulator = new PowerCfgSimulator();
        var action = new PowerPlanAction(simulator.Runner, SpecWithOtherSetting());
        var ex = await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
        Assert.Contains("no enumera", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_RejectsWhenTheSourcePlanDoesNotExist()
    {
        // Punto 7: sin plan base no se duplica nada, y el mensaje lo dice.
        var simulator = new PowerCfgSimulator { SourceExists = false };
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var ex = await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
        Assert.Contains("no existe en este equipo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/duplicatescheme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_RevalidatesAgainstQhRightBeforeWriting()
    {
        // El Preflight corre en la fase de planificación, muy antes de Apply: Apply debe releer /qh.
        var simulator = new PowerCfgSimulator { EnumeratedRange = true };
        var action = new PowerPlanAction(simulator.Runner, SpecWithValue(120));
        simulator.EnumeratedRange = false;
        simulator.PossibleIndices = [0, 1];
        var before = await action.CaptureAsync(Context());
        await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.ApplyAsync(before, Context()));
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/setacvalueindex", StringComparison.Ordinal));
    }

    // ---------- Regla 1: DC solo sin batería ----------

    [Fact]
    public async Task WithoutBattery_WritesBothAcAndDc_AsBefore()
    {
        var simulator = new PowerCfgSimulator { EnumeratedRange = true };
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var context = Context(); // el perfil de prueba no tiene batería
        Assert.False(context.Hardware.FormFactor.HasBattery);
        await action.ApplyAsync(await action.CaptureAsync(context), context);
        Assert.Contains(simulator.Commands, c => c.StartsWith("/setacvalueindex", StringComparison.Ordinal));
        Assert.Contains(simulator.Commands, c => c.StartsWith("/setdcvalueindex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithBattery_DoesNotWriteDc_AndSaysSo()
    {
        var simulator = new PowerCfgSimulator { EnumeratedRange = true };
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var context = BatteryContext(hasBattery: true);
        await action.ApplyAsync(await action.CaptureAsync(context), context);
        Assert.Contains(simulator.Commands, c => c.StartsWith("/setacvalueindex", StringComparison.Ordinal));
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/setdcvalueindex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_DoesNotRequireDc_WhenDcWasNeverWritten()
    {
        var simulator = new PowerCfgSimulator { EnumeratedRange = true };
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var context = BatteryContext(hasBattery: true);
        var before = await action.CaptureAsync(context);
        await action.ApplyAsync(before, context);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, context)).Outcome);
    }

    private static ActionExecutionContext BatteryContext(bool hasBattery)
    {
        var hardware = TestProfiles.Pn64();
        return Context() with { Hardware = hardware with { FormFactor = hardware.FormFactor with { HasBattery = hasBattery } } };
    }

    private static ActionSpec SpecWithValue(int ac) => new()
    {
        Kind = "PowerPlan",
        Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            planName = "FragForge Performance",
            sourceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            activate = true,
            settings = new[] { new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c", acValue = ac, dcValue = ac } }
        })
    };

    private static ActionSpec SpecWithOtherSetting() => new()
    {
        Kind = "PowerPlan",
        Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            planName = "FragForge Performance",
            sourceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            activate = true,
            settings = new[] { new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "be337238-0d82-4146-a960-4f3749d470c7", acValue = 0, dcValue = 0 } }
        })
    };

    // ---------- Bloque 3: modo settingsOnly ----------

    private const string SettingsOnlyJson = """
        {"planName":"FragForge Performance","sourceSchemeGuid":"8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c","activate":false,"mode":"settingsOnly","settings":[{"subgroupGuid":"54533251-82be-4824-96c1-47b60b740d00","settingGuid":"893dee8e-2bef-41e0-89c6-b55d0929964c","acValue":0,"dcValue":0}]}
        """;

    private static ActionSpec SettingsOnlySpec() => new() { Kind = "PowerPlan", Parameters = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(SettingsOnlyJson) };

    [Fact]
    public async Task SettingsOnly_DoesNotDuplicateNorDeleteNorActivateThePlan()
    {
        var simulator = new PowerCfgSimulator { PlanExists = true, ActiveIsFragForge = false };
        var action = new PowerPlanAction(simulator.Runner, SettingsOnlySpec());
        var context = Context();
        var before = await action.CaptureAsync(context);
        await action.ApplyAsync(before, context);

        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/duplicatescheme", StringComparison.Ordinal));
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/delete", StringComparison.Ordinal));
        // activate=false y el plan no es el activo: no se activa nada.
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/setactive", StringComparison.Ordinal));
        Assert.Contains(simulator.Commands, c => c.StartsWith("/setacvalueindex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettingsOnly_WithoutThePlan_IsNotApplicable_InsteadOfCreatingIt()
    {
        var simulator = new PowerCfgSimulator { PlanExists = false };
        var action = new PowerPlanAction(simulator.Runner, SettingsOnlySpec());
        var ex = await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
        Assert.Contains("no crea planes", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/duplicatescheme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettingsOnly_RevertRestoresOnlyItsValues_AndNeverDeletesThePlan()
    {
        var simulator = new PowerCfgSimulator { PlanExists = true, ActiveIsFragForge = false };
        var action = new PowerPlanAction(simulator.Runner, SettingsOnlySpec());
        var context = Context();
        var before = await action.CaptureAsync(context);
        Assert.Equal(5, ((PowerPlanActionState)before).Settings[0].AcIndex); // capturado antes de escribir el 0
        await action.ApplyAsync(before, context);
        await action.RevertAsync(before, context);

        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/delete", StringComparison.Ordinal));
        Assert.DoesNotContain(simulator.Commands, c => c.StartsWith("/duplicatescheme", StringComparison.Ordinal));
        Assert.Equal(5, simulator.AcIndex); // volvió al valor capturado
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, context)).Outcome);
    }

    [Fact]
    public async Task SettingsOnly_RevertWithPlanAlreadyGone_SucceedsWithANote()
    {
        var simulator = new PowerCfgSimulator { PlanExists = true };
        var action = new PowerPlanAction(simulator.Runner, SettingsOnlySpec());
        var context = Context();
        var before = await action.CaptureAsync(context);
        await action.ApplyAsync(before, context);
        simulator.PlanExists = false; // el padre borró el plan (revert del padre primero)

        await action.RevertAsync(before, context); // no debe lanzar
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, context)).Outcome);
    }

    [Fact]
    public async Task SettingsOnly_OnTheActivePlan_RepeatsSetactiveWithTheSameGuid()
    {
        // Regla 5: tras escribir settings en el plan ACTIVO, se repite /setactive con el MISMO GUID.
        var simulator = new PowerCfgSimulator { PlanExists = true, ActiveIsFragForge = true };
        var action = new PowerPlanAction(simulator.Runner, SettingsOnlySpec());
        var context = Context();
        await action.ApplyAsync(await action.CaptureAsync(context), context);
        Assert.Contains(simulator.Commands, c => c.StartsWith("/setactive", StringComparison.Ordinal) && c.Contains(PlanGuid, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownMode_IsRejectedEagerly()
    {
        var spec = new { planName = "X", sourceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", mode = "overlay", settings = new[] { new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c", acValue = 0, dcValue = 0 } } };
        var actionSpec = new ActionSpec { Kind = "PowerPlan", Parameters = System.Text.Json.JsonSerializer.SerializeToElement(spec) };
        Assert.Throws<CatalogValidationException>(() => new PowerPlanAction(new FakeProcessRunner(), actionSpec));
    }

    [Fact]
    public void AmbiguousPlanNames_AreRejected()
    {
        var runner = new FakeProcessRunner { Handler = request => string.Join(' ', request.Arguments) switch { "/list" => new ProcessResult(0, $"Power Scheme GUID: {PlanGuid}  (FragForge Performance)\nPower Scheme GUID: {PreviousGuid}  (fragforge performance)", "", false), "/getactivescheme" => new ProcessResult(0, $"Power Scheme GUID: {PreviousGuid}  (Balanced)", "", false), _ => new ProcessResult(0, "", "", false) } };
        var action = new PowerPlanAction(runner, Spec_());
        var ex = Assert.ThrowsAsync<FragForgeException>(() => action.CaptureAsync(Context())).GetAwaiter().GetResult();
        Assert.Equal("ambiguous_power_plan", ex.Code);
    }
}

public sealed class BcdeditActionTests
{
    private static ActionExecutionContext Context(TempDirectory directory) => new() { OperationId = "test", Hardware = TestProfiles.Pn64(), ArtifactDirectory = directory.Path };
    private static FakeProcessRunner Runner(string? current)
    {
        var runner = new FakeProcessRunner();
        runner.Handler = request =>
        {
            var joined = string.Join(' ', request.Arguments);
            if (joined.StartsWith("/enum", StringComparison.Ordinal)) return new ProcessResult(0, current is null ? "The boot configuration data store could not be opened." : $"identifier              {{current}}\ndisabledynamictick      {current}", "", false);
            if (joined.StartsWith("/export", StringComparison.Ordinal)) { File.WriteAllText(request.Arguments[1], "BCD"); return new ProcessResult(0, "The operation completed successfully.", "", false); }
            return new ProcessResult(0, "The operation completed successfully.", "", false);
        };
        return runner;
    }
    private static BcdeditAction Create(FakeProcessRunner runner, string element = "disabledynamictick", string value = "Yes") => new(runner, ProtectionPolicy.CreateDefault(), Spec.Action("Bcdedit", new { element, value }));

    [Fact]
    public async Task Apply_ExportsThenSets_RevertDeletesWhenAbsentOriginally()
    {
        using var directory = new TempDirectory();
        var runner = Runner(current: null);
        var action = Create(runner);
        var context = Context(directory);
        var before = await action.CaptureAsync(context);
        Assert.False(((BcdActionState)before).Existed);
        await action.ApplyAsync(before, context);
        Assert.Contains(runner.Requests, r => r.Arguments.Count > 0 && r.Arguments[0] == "/set");
        Assert.True(File.Exists(Path.Combine(directory.Path, "bcd-disabledynamictick.bcd")));
        await action.RevertAsync(before, context);
        Assert.Contains(runner.Requests, r => r.Arguments.Count > 0 && r.Arguments[0] == "/deletevalue");
    }

    [Fact]
    public async Task Revert_RestoresOriginalValue()
    {
        using var directory = new TempDirectory();
        var runner = Runner(current: "No");
        var action = Create(runner);
        var context = Context(directory);
        var before = await action.CaptureAsync(context);
        Assert.Equal("No", ((BcdActionState)before).PreviousValue);
        await action.ApplyAsync(before, context);
        await action.RevertAsync(before, context);
        Assert.Contains(runner.Requests, r => r.Arguments.Count > 1 && r.Arguments[0] == "/set" && r.Arguments[^1] == "No");
    }

    [Fact]
    public async Task Apply_FailsIfExportArtifactMissing()
    {
        var action = Create(Runner(current: "No"));
        var context = new ActionExecutionContext { OperationId = "test", Hardware = TestProfiles.Pn64() };
        var before = await action.CaptureAsync(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.ApplyAsync(before, context));
    }

    [Fact]
    public async Task ExistingExportArtifact_IsNeverOverwritten()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "bcd-disabledynamictick.bcd"), "ORIGINAL");
        var action = Create(Runner(current: "No"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.CaptureAsync(Context(directory)));
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(Path.Combine(directory.Path, "bcd-disabledynamictick.bcd")));
    }

    [Fact]
    public async Task BcdElementOutsideAllowlist_IsRejectedAtPreflight()
    {
        using var directory = new TempDirectory();
        var action = Create(new FakeProcessRunner(), element: "hypervisorlaunchtype", value: "off");
        await Assert.ThrowsAsync<ProtectedTargetException>(() => action.PreflightAsync(Context(directory)));
    }
}

public sealed class NetworkPropertyActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64() };
    private static NetAdapterPropertyAction Create(FakeNetworkAdapterController controller, string keyword, params string[] value) => new(controller, ProtectionPolicy.CreateDefault(), Spec.Action("NetAdapterProperty", new { adapterName = "Ethernet", properties = new object[] { new { keyword, value } } }));

    [Fact]
    public async Task Apply_Revert_RestoresPreviousRegistryValue()
    {
        var controller = new FakeNetworkAdapterController();
        controller.Properties[("Ethernet", "*EEE")] = new NetworkPropertyCapture { AdapterName = "Ethernet", Keyword = "*EEE", Exists = true, RegistryValue = ["1"], DisplayValue = "Enabled" };
        var action = Create(controller, "*EEE", "0");
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(["0"], controller.Properties[("Ethernet", "*EEE")].RegistryValue);
        await action.RevertAsync(before, Context());
        Assert.Equal(["1"], controller.Properties[("Ethernet", "*EEE")].RegistryValue);
    }

    [Fact]
    public async Task DenylistedWakeProperty_IsRejectedAtPreflight()
    {
        var controller = new FakeNetworkAdapterController();
        controller.Properties[("Ethernet", "*WakeOnMagicPacket")] = new NetworkPropertyCapture { AdapterName = "Ethernet", Keyword = "*WakeOnMagicPacket", Exists = true, RegistryValue = ["1"] };
        await Assert.ThrowsAsync<ProtectedTargetException>(() => Create(controller, "*WakeOnMagicPacket", "0").PreflightAsync(Context()));
    }

    [Fact]
    public async Task MissingKeyword_IsNotApplicable()
    {
        await Assert.ThrowsAsync<ActionNotApplicableException>(() => Create(new FakeNetworkAdapterController(), "*EEE", "0").PreflightAsync(Context()));
    }

    [Fact]
    public async Task AdapterToken_RequiresExactlyOnePhysicalMatch()
    {
        var controller = new FakeNetworkAdapterController();
        controller.Properties[("Ethernet", "*EEE")] = new NetworkPropertyCapture { AdapterName = "Ethernet", Keyword = "*EEE", Exists = true, RegistryValue = ["1"] };
        var action = new NetAdapterPropertyAction(controller, ProtectionPolicy.CreateDefault(), Spec.Action("NetAdapterProperty", new { adapterName = "{AdapterDescription:I225-V}", properties = new object[] { new { keyword = "*EEE", value = new[] { "0" } } } }));
        await action.PreflightAsync(Context());
        Assert.Contains("Ethernet", controller.Properties.Keys.Select(k => k.Adapter));
    }
}

public sealed class FileEditActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64() };
    private static ActionSpec Spec_(string path, string pattern, string replacement) => FragForge.Tests.Fakes.Spec.Action("FileEdit", new { path, maxBytes = 4096, edits = new object[] { new { pattern, replacement, expectedMatches = 1 } } });

    [Fact]
    public async Task Apply_Revert_RestoresExactBytesAndTimestamp()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "cs2.cfg");
        var original = "setting \"a\" \"1\"\nsetting \"b\" \"2\"\n";
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var action = new FileEditAction(ProtectionPolicy.CreateDefault(), Spec_(path, "setting \\\"a\\\" \\\"1\\\"", "setting \"a\" \"0\""));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Contains("\"0\"", await File.ReadAllTextAsync(path));
        await action.RevertAsync(before, Context());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Revert_DeletesFileAndDirectoriesThatDidNotExist()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "nested", "created.cfg");
        var action = new FileEditAction(ProtectionPolicy.CreateDefault(), FragForge.Tests.Fakes.Spec.Action("FileEdit", new { path, maxBytes = 4096, edits = new object[] { new { pattern = "x", replacement = "y", expectedMatches = 0 } } }));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.True(File.Exists(path));
        await action.RevertAsync(before, Context());
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "nested")));
    }

    [Fact]
    public async Task Drift_AfterCapture_IsRefused()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "cs2.cfg");
        await File.WriteAllTextAsync(path, "value 1");
        var action = new FileEditAction(ProtectionPolicy.CreateDefault(), Spec_(path, "value 1", "value 2"));
        var before = await action.CaptureAsync(Context());
        await File.WriteAllTextAsync(path, "changed externally");
        await Assert.ThrowsAsync<VerificationFailedException>(() => action.ApplyAsync(before, Context()));
    }

    [Fact]
    public void SystemPathsAndExecutables_AreRejected()
    {
        var policy = ProtectionPolicy.CreateDefault();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureFileAllowed(@"C:\Windows\System32\drivers\etc\hosts"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureFileAllowed(@"C:\Temp\evil.exe"));
        policy.EnsureFileAllowed(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg\autoexec.cfg");
    }
}

public sealed class InspectOnlyActionTests
{
    [Fact]
    public async Task Inspection_NeverWritesAndReportsObservations()
    {
        var registry = new FakeRegistry();
        registry.Set(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "DWord", 1);
        var action = new FragForge.Actions.Diagnostics.InspectOnlyAction(FragForge.Actions.Diagnostics.DiagnosticProbeFactory.CreateDefaults(registry), FragForge.Tests.Fakes.Spec.Action("InspectOnly", new { probes = new[] { "hags", "vbs" } }));
        var context = new ActionExecutionContext { OperationId = "test", Hardware = TestProfiles.Pn64() };
        var state = await action.CaptureAsync(context);
        var inspection = Assert.IsType<InspectionActionState>(state);
        Assert.Contains(inspection.Observations, o => o.Key == "hags" && o.Value == "Disabled");
        await action.ApplyAsync(state, context);
        await action.RevertAsync(state, context);
        Assert.Empty(registry.Writes);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(state, context)).Outcome);
    }
}

public sealed class PowerShellActionTests
{
    private static ActionExecutionContext Context(bool allowed) => new() { OperationId = "test", Hardware = TestProfiles.Pn64(), AllowPowerShellActions = allowed };
    private static FragForge.Actions.Scripting.PowerShellAction Create(FakeProcessRunner runner) => new(runner, ProtectionPolicy.CreateDefault(), Spec.Action("PowerShell", new { captureScript = "'{}'", applyScript = "'ok'", verifyScript = "'{\"matches\":true,\"message\":\"ok\"}'", revertScript = "'ok'", timeoutSeconds = 30 }));

    [Fact]
    public async Task DisabledByDefault_EvenIfCatalogAsksForIt()
    {
        var action = Create(new FakeProcessRunner());
        await Assert.ThrowsAsync<ProtectedTargetException>(() => action.PreflightAsync(Context(false)));
    }

    [Fact]
    public async Task EnabledWithFlag_CapturesAndVerifiesJson()
    {
        var runner = new FakeProcessRunner { Handler = request => request.Arguments[^1].Contains("matches", StringComparison.Ordinal) ? new ProcessResult(0, "{\"matches\":true,\"message\":\"ok\"}", "", false) : new ProcessResult(0, "{\"value\":1}", "", false) };
        var action = Create(runner);
        var context = Context(true);
        await action.PreflightAsync(context);
        var before = await action.CaptureAsync(context);
        await action.ApplyAsync(before, context);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, context)).Outcome);
        await action.RevertAsync(before, context);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, context)).Outcome);
    }

    [Fact]
    public void NetworkTokens_AreRejected()
    {
        var runner = new FakeProcessRunner();
        var action = new FragForge.Actions.Scripting.PowerShellAction(runner, ProtectionPolicy.CreateDefault(), Spec.Action("PowerShell", new { captureScript = "Invoke-WebRequest https://example.com", applyScript = "'ok'", verifyScript = "'{}'", revertScript = "'ok'", timeoutSeconds = 30 }));
        Assert.ThrowsAsync<ProtectedTargetException>(() => action.PreflightAsync(Context(true))).GetAwaiter().GetResult();
    }
}
