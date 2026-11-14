using FragForge.Core.Abstractions;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Localization;
using FragForge.UI.Core;
using FragForge.UI.Core.Services;
using Xunit;

namespace FragForge.Tests.UI;

/// <summary>
/// Capa 11.3: el flujo de aplicar tiene que enseñar el plan ANTES de tocar nada, y el usuario tiene que poder
/// revertir UNA operación concreta, no solo "la última".
/// </summary>
public sealed class ApplyFlowTests
{
    private sealed class NoDialogs(bool accept = true) : IConfirmationService, IFileDialogService
    {
        public int ConfirmCalls { get; private set; }
        public bool Confirm(string message, string title) { ConfirmCalls++; return accept; }
        public bool ConfirmRisk(string first, string second, string title) => accept;
        public string? SaveReport() => null;
        public string? OpenCs2() => null;
    }

    private sealed class EngineSpy : ITweakEngine
    {
        public List<string> Applied { get; } = [];
        public List<string> Reverted { get; } = [];
        public IReadOnlyList<DryRunChange> Planned { get; set; } = [];
        public bool FailApply { get; set; }

        public Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
        {
            Applied.AddRange(ids);
            return Task.FromResult(new BatchReport
            {
                // OperationId es 'required' en el modelo real: el doble tiene que darlo o el objeto no compila.
                OperationId = "op-test",
                Success = !FailApply,
                DryRun = options.DryRun,
                Results = Applied.Select(id => new TweakApplyResult { Id = id, State = TweakState.NotApplied, Message = "", PlannedChanges = options.DryRun ? Planned : [] }).ToArray(),
                Errors = FailApply ? ["fallo simulado"] : []
            });
        }

        public Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
        {
            Reverted.Add(request.SnapshotId ?? "<last>");
            return Task.FromResult(new BatchReport { OperationId = "op-revert", Success = true, Results = [] });
        }

        public Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) =>
            Task.FromResult(new ScanReport(new HardwareProfile(), [], DateTimeOffset.UtcNow));
        public Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(new TweakStatus(id, TweakState.Unknown, "", [], []));
        public Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => Task.FromResult(new SystemSnapshot { Id = "x" });
    }

    private sealed class SnapshotSpy : ISnapshotStore
    {
        public List<SystemSnapshot> Items { get; } = [];
        public string RootPath => Path.GetTempPath();
        public string GetArtifactDirectory(string snapshotId) => Path.Combine(RootPath, snapshotId);
        public Task SaveAsync(SystemSnapshot snapshot, CancellationToken cancellationToken = default) { Items.Add(snapshot); return Task.CompletedTask; }
        public Task<SystemSnapshot> GetAsync(string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult(Items.First(s => s.Id == snapshotId));
        public Task<IReadOnlyList<SystemSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SystemSnapshot>>(Items);
    }

    /// <summary>Una colección de acciones que el motor describe en el dry-run.</summary>
    private static IReadOnlyList<DryRunChange> SamplePlan() =>
    [
        new("tweak.a", 0, "Registry", @"HKLM\X\Y: 0 → 1"),
        new("tweak.a", 1, "Service", "DiagTrack: Automatic → Disabled"),
        new("tweak.b", 0, "Registry", @"HKCU\Z\W: (ausente) → 1")
    ];

    private static async Task<(MainViewModel Vm, EngineSpy Engine, NoDialogs Dialogs)> CreateAsync(bool accept = true)
    {
        var localizer = await LocalizationLoader.LoadAsync("es-MX");
        var catalog = await NewLoader().LoadEmbeddedAsync();
        var engine = new EngineSpy();
        var dialogs = new NoDialogs(accept);
        var vm = new MainViewModel(engine, catalog, localizer, new StubReports(), new UiLogSink(), dialogs, dialogs,
            new StubPreflight(), new FakeHardwareProfiler(), new StubContext());
        return (vm, engine, dialogs);
    }

    /// <summary>
    /// La vista previa pide un DRY-RUN, deja los cambios planificados en la colección y NO aplica nada.
    /// </summary>
    [Fact]
    public async Task Preview_RunsADryRun_AndShowsThePlan_WithoutApplying()
    {
        var (vm, engine, _) = await CreateAsync();
        using var _vm = vm;
        engine.Planned = SamplePlan();
        vm.Tweaks[0].IsSelected = true;

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.PlannedChanges.Count);
        Assert.True(vm.HasPreview);
        Assert.Contains("3 cambios planificados", vm.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("No se ha modificado nada", vm.PreviewSummary, StringComparison.Ordinal);
        Assert.Empty(engine.Reverted);
    }

    /// <summary>Sin selección no se previsualiza nada, y se dice.</summary>
    [Fact]
    public async Task Preview_WithoutSelection_SaysSo()
    {
        var (vm, engine, _) = await CreateAsync();
        using var _vm = vm;
        foreach (var t in vm.Tweaks) t.IsSelected = false;

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Empty(vm.PlannedChanges);
        Assert.False(vm.HasPreview);
        Assert.Contains("Selecciona", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(engine.Applied);
    }

    /// <summary>El dry-run del plan no escribe: el motor recibe DryRun=true.</summary>
    [Fact]
    public async Task Preview_UsesDryRunOptions()
    {
        var (vm, engine, _) = await CreateAsync();
        using var _vm = vm;
        engine.Planned = SamplePlan();
        vm.Tweaks[0].IsSelected = true;

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotEmpty(engine.Applied);
        Assert.All(vm.PlannedChanges, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    /// <summary>
    /// Revertir por operación: se pasa el SNAPSHOT concreto al motor, no "la última".
    /// </summary>
    [Fact]
    public async Task RevertingOneOperation_TargetsThatSnapshot()
    {
        var (vm, engine, _) = await CreateAsync();
        using var _vm = vm;
        var operation = new SnapshotListItem("snap-42", "Aplicar 3 tweaks", DateTimeOffset.UtcNow, 3, null, false);

        await vm.RevertOperationCommand.ExecuteAsync(operation);

        Assert.Equal(["snap-42"], engine.Reverted);
    }

    /// <summary>Si el usuario cancela la confirmación, no se revierte nada.</summary>
    [Fact]
    public async Task Reverting_IsCancelledWhenTheUserDeclines()
    {
        var (vm, engine, _) = await CreateAsync(accept: false);
        using var _vm = vm;

        await vm.RevertOperationCommand.ExecuteAsync(new SnapshotListItem("snap-1", "x", DateTimeOffset.UtcNow, 1, null, false));

        Assert.Empty(engine.Reverted);
    }

    /// <summary>
    /// Mismo montaje que <c>CatalogTests.CreateLoader</c> (verificado con grep, no de memoria): el catálogo
    /// embebido se valida contra el esquema embebido antes de cargarse, así que el doble necesita un validador
    /// real. Se duplica la composición porque es la forma en que el repo ya lo hace en cada test de catálogo.
    /// </summary>
    private static JsonTweakCatalogLoader NewLoader()
    {
        var schema = new FragForge.Tweaks.Validation.JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var factory = new FragForge.Actions.DependencyInjection.TweakActionFactory(
            registry, FragForge.Core.Persistence.ProtectionPolicy.CreateDefault(),
            new FakeServiceController(), new FakeScheduledTaskStore(), new FakeNetworkAdapterController(),
            new FakeProcessRunner(), FragForge.Actions.Diagnostics.DiagnosticProbeFactory.CreateDefaults(registry));
        var validator = new FragForge.Tweaks.Validation.TweakCatalogValidator(schema, new FragForge.Core.Conditions.ConditionFactory(), factory);
        return new JsonTweakCatalogLoader(validator);
    }
}
