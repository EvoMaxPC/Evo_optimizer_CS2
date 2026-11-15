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

public sealed class RestorePointAckTests
{
    private sealed class Dialogs : IConfirmationService, IFileDialogService
    {
        public bool Confirm(string message, string title) => true;
        public bool ConfirmRisk(string first, string second, string title) => true;
        public string? SaveReport() => null;
        public string? OpenCs2() => null;
    }

    private sealed class Engine : ITweakEngine
    {
        public List<ApplyOptions> Calls { get; } = [];
        public BatchReport Next { get; set; } = Blocked("restore-unavailable");
        public TaskCompletionSource<BatchReport>? Pending { get; set; }
        public Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
        {
            Calls.Add(options);
            return Pending?.Task ?? Task.FromResult(Next);
        }
        public Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
    }

    private static BatchReport Blocked(params string[] ids) => new()
    {
        OperationId = "bloqueada", Success = false, PreflightBlocked = true, BlockingPreflightCheckIds = ids,
        Errors = ["bloqueo de prueba"]
    };

    private static BatchReport Success() => new() { OperationId = "correcta", Success = true };

    private static async Task<(MainViewModel Vm, Engine Engine)> CreateAsync()
    {
        var engine = new Engine();
        var catalog = new TweakCatalog([
            Tweak.Create("tweak.a", Spec.Action("Fake", new { id = "a" })),
            Tweak.Create("tweak.b", Spec.Action("Fake", new { id = "b" }))]);
        var dialogs = new Dialogs();
        var vm = new MainViewModel(engine, catalog, await LocalizationLoader.LoadAsync("es-MX"), new StubReports(), new UiLogSink(), dialogs, dialogs,
            new StubPreflight(), new FakeHardwareProfiler(), new StubContext());
        vm.Tweaks[0].IsSelected = true;
        return (vm, engine);
    }

    [Fact]
    public async Task RestoreBlock_ShowsPanelButDoesNotConfirm()
    {
        var (vm, engine) = await CreateAsync();
        using var cleanup = vm;
        await vm.ApplyCommand.ExecuteAsync(null);
        // Una llamada bloqueada sin consentimiento; la casilla comienza desmarcada.
        Assert.True(vm.NeedsRestorePointAck);
        Assert.False(vm.RestorePointAckChecked);
        Assert.Single(engine.Calls);
        Assert.False(engine.Calls[0].AcceptWithoutSystemRestore);
    }

    [Fact]
    public async Task Unchecked_CannotRunEvenIfInvokedDirectly()
    {
        var (vm, engine) = await CreateAsync();
        using var cleanup = vm;
        await vm.ApplyCommand.ExecuteAsync(null);
        // Sin marcar: CanExecute es falso y una ejecución directa no agrega llamadas.
        Assert.False(vm.ApplyWithoutRestoreCommand.CanExecute(null));
        await vm.ApplyWithoutRestoreCommand.ExecuteAsync(null);
        Assert.Single(engine.Calls);
        Assert.False(engine.Calls[0].AcceptWithoutSystemRestore);
    }

    [Fact]
    public async Task Checked_RetriesExactlyOnceWithExplicitConfirmation()
    {
        var (vm, engine) = await CreateAsync();
        using var cleanup = vm;
        await vm.ApplyCommand.ExecuteAsync(null);
        vm.RestorePointAckChecked = true;
        engine.Next = Success();
        Assert.True(vm.ApplyWithoutRestoreCommand.CanExecute(null));
        await vm.ApplyWithoutRestoreCommand.ExecuteAsync(null);
        // Dos llamadas totales: 1 inicial y exactamente 1 con confirmación.
        Assert.Equal(2, engine.Calls.Count);
        Assert.False(engine.Calls[0].AcceptWithoutSystemRestore);
        Assert.True(engine.Calls[1].AcceptWithoutSystemRestore);
        Assert.False(vm.RestorePointAckChecked);
        Assert.False(vm.NeedsRestorePointAck);
        Assert.False(vm.ApplyWithoutRestoreCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("apply")]
    [InlineData("selection")]
    public async Task NewActionOrSelection_ResetsConfirmation(string action)
    {
        var (vm, engine) = await CreateAsync();
        using var cleanup = vm;
        await vm.ApplyCommand.ExecuteAsync(null);
        vm.RestorePointAckChecked = true;
        engine.Next = Success();
        if (action == "preview") await vm.PreviewCommand.ExecuteAsync(null);
        else if (action == "apply") await vm.ApplyCommand.ExecuteAsync(null);
        else vm.Tweaks[1].IsSelected = true;
        // En cada uno de los tres casos el consentimiento anterior deja de valer.
        Assert.False(vm.RestorePointAckChecked);
        Assert.False(vm.NeedsRestorePointAck);
        Assert.False(vm.ApplyWithoutRestoreCommand.CanExecute(null));
        Assert.DoesNotContain(engine.Calls, c => c.AcceptWithoutSystemRestore);
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("admin")]
    public async Task OtherBlock_DoesNotShowPanel(string id)
    {
        var (vm, engine) = await CreateAsync();
        using var cleanup = vm;
        engine.Next = Blocked(id);
        await vm.ApplyCommand.ExecuteAsync(null);
        // Un bloqueo distinto de restauración no ofrece confirmar ese riesgo.
        Assert.False(vm.NeedsRestorePointAck);
        Assert.False(vm.RestorePointAckChecked);
        Assert.Single(engine.Calls);
        Assert.Equal("bloqueo de prueba", vm.StatusMessage);
    }

    [Fact]
    public async Task MultipleBlocks_DoNotOfferRetryWhileAnotherBlockRemains()
    {
        var (vm, engine) = await CreateAsync();
        using var cleanup = vm;
        engine.Next = Blocked("restore-unavailable", "recovery");
        await vm.ApplyCommand.ExecuteAsync(null);
        // Dos bloqueos, uno ajeno a restauración: no se ofrece una salida que no resolvería R-J.
        Assert.False(vm.NeedsRestorePointAck);
        Assert.False(vm.RestorePointAckChecked);
        Assert.Equal("bloqueo de prueba", vm.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulApply_DoesNotShowPanel()
    {
        var (vm, engine) = await CreateAsync();
        using var cleanup = vm;
        engine.Next = Success();
        await vm.ApplyCommand.ExecuteAsync(null);
        // Una llamada exitosa sin bloqueo: no hay solicitud de confirmación.
        Assert.False(vm.NeedsRestorePointAck);
        Assert.False(vm.RestorePointAckChecked);
        Assert.False(engine.Calls[0].AcceptWithoutSystemRestore);
    }

    [Fact]
    public async Task SelectionChangedDuringApply_DoesNotShowStalePanel()
    {
        var (vm, engine) = await CreateAsync();
        using var cleanup = vm;
        var pending = new TaskCompletionSource<BatchReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Pending = pending;
        var operation = vm.ApplyCommand.ExecuteAsync(null);
        vm.Tweaks[1].IsSelected = true;
        pending.SetResult(Blocked("restore-unavailable"));
        await operation;
        // La respuesta corresponde a la selección anterior, no a la nueva.
        Assert.False(vm.NeedsRestorePointAck);
        Assert.False(vm.RestorePointAckChecked);
        Assert.Single(engine.Calls);
    }
}
