using FragForge.UI.Core;
using Xunit;

namespace FragForge.Tests.Ui;

public sealed class MeasurementViewModelTests
{
    private sealed class FakeSession : IMeasurementSession
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public int Finishes;
        public int Shows;
        public int Segments;
        public int? Duration;
        public bool Saved;
        public bool WarnOnFinish;
        public bool AutoComplete = true;
        public void Complete() => _done.TrySetResult();
        public async Task RunAsync(int? durationSeconds, bool saveLog, Action<string, bool> output, CancellationToken finishRequested)
        {
            Calls++; Duration = durationSeconds; Saved = saveLog;
            using var registration = finishRequested.Register(() => { Finishes++; if (WarnOnFinish) output("falló el cierre ETW", true); if (AutoComplete) _done.TrySetResult(); });
            await _done.Task;
        }
        public void ShowNow() => Shows++;
        public void ToggleSegment() => Segments++;
    }

    [Fact]
    public void DefaultsAndLabels_AreExact()
    {
        var vm = new MeasurementViewModel(() => new FakeSession());
        // Sin interacción: guardado desactivado y estado inicial Inactiva.
        Assert.False(vm.SaveLog);
        Assert.Equal(MeasurementState.Inactiva, vm.State);
        Assert.Equal("Guardar log al terminar", vm.SaveLogText);
        Assert.Equal("La app debe estar abierta como administrador; las teclas F9, F10 y F11 no llegan al juego mientras dura la medición.", vm.NoticeText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFlag_IsForwarded(bool save)
    {
        var fake = new FakeSession(); var vm = new MeasurementViewModel(() => fake) { SaveLog = save };
        var running = vm.StartAsync();
        // La solicitud de guardado coincide exactamente con la casilla; solo una corrida.
        Assert.Equal(save, fake.Saved);
        Assert.Equal(1, fake.Calls);
        await vm.FinishAsync(); await running;
        Assert.Equal(1, fake.Finishes);
        Assert.Equal(MeasurementState.Inactiva, vm.State);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("30", 30)]
    public async Task ValidDuration_Starts(string duration, int? expected)
    {
        var fake = new FakeSession(); var vm = new MeasurementViewModel(() => fake) { Duration = duration };
        var running = vm.StartAsync();
        // Cadena vacía = partida completa; 30 = 30 segundos.
        Assert.Equal(expected, fake.Duration);
        Assert.Equal(1, fake.Calls);
        await vm.FinishAsync(); await running;
    }

    [Theory]
    [InlineData("0")]
    [InlineData("86401")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1e3")]
    public async Task InvalidDuration_DoesNotStart(string duration)
    {
        var calls = 0; var vm = new MeasurementViewModel(() => { calls++; return new FakeSession(); }) { Duration = duration };
        await vm.StartAsync();
        // 0 y -5 < 1; 86401 > 86400; abc y 1e3 no son enteros decimales.
        Assert.Equal(0, calls);
        Assert.Equal(MeasurementState.Inactiva, vm.State);
        Assert.Contains("La duración debe ser un número entero entre 1 y 86400", vm.Output);
    }

    [Fact]
    public async Task StateAndActions_SingleRunAndSingleFinish()
    {
        var fake = new FakeSession(); var vm = new MeasurementViewModel(() => fake);
        var running = vm.StartAsync();
        // Dos inicios => una corrida; ver y marcar una vez; dos finalizaciones => una cancelación.
        Assert.Equal(MeasurementState.Midiendo, vm.State);
        await vm.StartAsync();
        vm.ShowNow(); vm.ToggleSegment();
        var finish = vm.FinishAsync();
        var close = vm.FinishAsync();
        await Task.WhenAll(finish, close, running);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(1, fake.Shows);
        Assert.Equal(1, fake.Segments);
        Assert.Equal(1, fake.Finishes);
        Assert.Equal(MeasurementState.Inactiva, vm.State);
    }

    [Fact]
    public async Task EtwWarning_IsVisibleAndReturnsToInactive()
    {
        var fake = new FakeSession { WarnOnFinish = true }; var vm = new MeasurementViewModel(() => fake);
        var running = vm.StartAsync();
        await vm.FinishAsync(); await running;
        // Un fallo informado por la sesión queda visible; no cambia el estado final Inactiva.
        Assert.Contains("Aviso: falló el cierre ETW", vm.Output);
        Assert.Equal(MeasurementState.Inactiva, vm.State);
        Assert.Equal(1, fake.Finishes);
    }
    [Fact]
    public async Task Finishing_RemainsTerminatingUntilSessionCompletes()
    {
        var fake = new FakeSession { AutoComplete = false }; var vm = new MeasurementViewModel(() => fake);
        var running = vm.StartAsync();
        var finish = vm.FinishAsync();
        // Una cancelación solicitada; estado Terminando hasta que la sesión complete su limpieza.
        Assert.Equal(MeasurementState.Terminando, vm.State);
        Assert.Equal(1, fake.Finishes);
        Assert.False(finish.IsCompleted);
        fake.Complete();
        await Task.WhenAll(running, finish);
        Assert.Equal(MeasurementState.Inactiva, vm.State);
    }

}
