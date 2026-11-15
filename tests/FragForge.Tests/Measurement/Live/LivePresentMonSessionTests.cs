using System.Threading.Channels;
using FragForge.Actions.Measurement;
using Xunit;

namespace FragForge.Tests.Measurement.Live;

public sealed class LivePresentMonSessionTests
{
    private const string Header = "Application,SwapChainAddress,TimeInMs,MsBetweenPresents\r\n";
    private const string Row = "cs2.exe,0x0,0,10\r\n";

    private sealed class FakeReader : TextReader
    {
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
        public void Send(string chunk) => _chunks.Writer.TryWrite(chunk);
        public void End() => _chunks.Writer.TryComplete();
        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            if (!await _chunks.Reader.WaitToReadAsync()) return 0;
            if (!_chunks.Reader.TryRead(out var chunk)) return 0;
            chunk.CopyTo(0, buffer, index, chunk.Length);
            return chunk.Length;
        }
    }

    private sealed class FakeProcess : ILivePresentMonProcess
    {
        public readonly FakeReader Reader = new();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Kills;
        public Exception? KillException;
        public string Error = "";
        public TextReader StandardOutput => Reader;
        public Task<string> ReadStandardErrorAsync() => Task.FromResult(Error);
        public Task<int> WaitForExitAsync(CancellationToken token) => _exit.Task.WaitAsync(token);
        public void End(int code) { Reader.End(); _exit.TrySetResult(code); }
        public void Kill()
        {
            Interlocked.Increment(ref Kills);
            if (KillException is not null) throw KillException;
            End(1);
        }
        public void Dispose() { }
    }

    private sealed class FakeLauncher(FakeProcess process) : ILivePresentMonLauncher
    {
        public int Calls;
        public bool Throw;
        public IReadOnlyList<string>? Arguments;
        public ILivePresentMonProcess Start(string exePath, IReadOnlyList<string> arguments)
        {
            Calls++;
            Arguments = arguments;
            if (Throw) throw new InvalidOperationException("launcher boom");
            return process;
        }
    }

    private static LivePresentMonSession Create(FakeLauncher launcher, Func<bool>? elevated = null,
        LiveToolResolver? resolver = null, LiveSessionOptions? options = null, Func<long>? clock = null) =>
        new(launcher, elevated ?? (() => true), resolver ?? (() => new LiveToolResolution("approved", null)),
            options ?? new LiveSessionOptions(), clock);

    private static async Task Until(Func<bool> predicate)
    {
        async Task Wait() { while (!predicate()) await Task.Yield(); }
        await Wait().WaitAsync(TimeSpan.FromSeconds(5)); // límite de seguridad, no criterio temporal de la prueba
    }

    [Fact]
    public async Task SessionName_GeneratedOrProvided_IsPassedToLauncher()
    {
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var generated = Create(l);
        Assert.Null(generated.EffectiveSessionName);
        await generated.StartAsync();
        var actual = generated.EffectiveSessionName;
        Assert.NotNull(actual);
        // 14 caracteres de prefijo + 8 hexadecimales = 22.
        Assert.Equal(22, actual.Length);
        Assert.StartsWith("FragForgeLive-", actual);
        Assert.Equal(actual, l.Arguments![4]); // --session_name en índice 3, valor en 4

        var p2 = new FakeProcess(); var l2 = new FakeLauncher(p2);
        await using var custom = Create(l2, options: new LiveSessionOptions { SessionName = "sesion-propia" });
        await custom.StartAsync();
        Assert.Equal("sesion-propia", custom.EffectiveSessionName);
        Assert.Equal("sesion-propia", l2.Arguments![4]);
    }

    [Fact]
    public async Task NotElevated_DoesNotLaunch()
    {
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var s = Create(l, elevated: () => false);
        await s.StartAsync();
        Assert.Equal(LiveSessionState.Failed, s.State);
        Assert.Contains("administrador", s.FailureMessage!);
        Assert.Equal(0, l.Calls); // rechazo anterior al lanzamiento
    }

    [Fact]
    public async Task RejectedTool_DoesNotLaunch()
    {
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var s = Create(l, resolver: () => new LiveToolResolution(null, "rechazada"));
        await s.StartAsync();
        Assert.Equal(LiveSessionState.Failed, s.State);
        Assert.Equal("rechazada", s.FailureMessage);
        Assert.Equal(0, l.Calls);
    }

    [Fact]
    public async Task LauncherThrows_Fails()
    {
        var l = new FakeLauncher(new FakeProcess()) { Throw = true };
        await using var s = Create(l);
        await s.StartAsync();
        Assert.Equal(LiveSessionState.Failed, s.State);
        Assert.Contains("launcher boom", s.FailureMessage!);
    }

    [Fact]
    public async Task WaitingThenRunning_AndNaturalExit()
    {
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var s = Create(l);
        await s.StartAsync();
        Assert.Equal(LiveSessionState.WaitingForFrames, s.State);
        p.Reader.Send(Header + Row + Row);
        await Until(() => s.State == LiveSessionState.Running);
        Assert.Equal(2L, s.Ingest.Accumulator.GetTotalQuick().FrameCount); // dos filas válidas
        p.End(0);
        await Until(() => s.State == LiveSessionState.ProcessEnded);
        await s.StopAsync();
        Assert.Equal(LiveSessionState.ProcessEnded, s.State); // terminal pegajoso
        Assert.Equal(0, p.Kills);
    }

    [Fact]
    public async Task ErrorExitWithoutFrames_IsFailedWithTail()
    {
        var p = new FakeProcess { Error = "boom" }; var l = new FakeLauncher(p);
        await using var s = Create(l);
        await s.StartAsync(); p.End(1);
        await Until(() => s.State == LiveSessionState.Failed);
        Assert.Equal(1, s.ExitCode);
        Assert.Contains("boom", s.StderrTail);
        Assert.Contains("1", s.FailureMessage!);
        await s.StopAsync();
        Assert.Equal(LiveSessionState.Failed, s.State);
    }

    [Fact]
    public async Task DurationCompleted_KillOnce_NonzeroExitDoesNotOverwrite()
    {
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var s = Create(l, options: new LiveSessionOptions { DurationSeconds = 1 });
        await s.StartAsync();
        // 0 ms aceptado, 1000 ms completa: 1 frame acumulado, 1 ignorado.
        p.Reader.Send(Header + Row + "cs2.exe,0x0,1000,10\r\n");
        await Until(() => s.State == LiveSessionState.Completed);
        await s.StopAsync();
        await Until(() => s.ExitCode.HasValue);
        Assert.Equal(1, p.Kills);
        Assert.Equal(1, s.ExitCode);
        Assert.Equal(LiveSessionState.Completed, s.State);
        Assert.Equal(1L, s.Ingest.Accumulator.GetTotalQuick().FrameCount);
        Assert.Equal(1L, s.Ingest.Accumulator.IgnoredAfterCompletion);
    }

    [Fact]
    public async Task Stall_UsesManualClock_AndStateIsSticky()
    {
        long now = 0;
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var s = Create(l, options: new LiveSessionOptions { StallGrace = LiveSessionOptions.DefaultStallGrace },
            clock: () => Interlocked.Read(ref now));
        await s.StartAsync(); p.Reader.Send(Header + Row);
        await Until(() => s.State == LiveSessionState.Running);
        var grace = (long)LiveSessionOptions.DefaultStallGrace.TotalMilliseconds;
        Interlocked.Exchange(ref now, grace - 1); s.CheckStall();
        Assert.Equal(LiveSessionState.Running, s.State);
        Interlocked.Exchange(ref now, grace + 1); s.CheckStall();
        await Until(() => s.ExitCode.HasValue);
        Assert.Equal(LiveSessionState.Stalled, s.State);
        Assert.Equal(1, p.Kills);
        await s.StopAsync();
        Assert.Equal(LiveSessionState.Stalled, s.State);
    }

    [Fact]
    public async Task NoStallGrace_DoesNotStopLongMatch()
    {
        long now = 1000;
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var s = Create(l, clock: () => Interlocked.Read(ref now));
        await s.StartAsync(); p.Reader.Send(Header + Row);
        await Until(() => s.State == LiveSessionState.Running);
        // Por defecto null: aunque pasen 10.000.000 - 1.000 = 9.999.000 ms,
        // no hay transición a Stalled y no se llama a Kill.
        Interlocked.Exchange(ref now, 10_000_000);
        s.CheckStall();
        Assert.Equal(LiveSessionState.Running, s.State);
        Assert.Equal(0, p.Kills);
    }

    [Fact]
    public async Task SecondsSinceLastFrame_ReportsUnknownThenElapsed()
    {
        long now = 1000;
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var s = Create(l, clock: () => Interlocked.Read(ref now));
        await s.StartAsync();
        // Sin frames todavía, no se conoce el tiempo transcurrido.
        Assert.Null(s.SecondsSinceLastFrame());
        p.Reader.Send(Header + Row);
        await Until(() => s.State == LiveSessionState.Running);
        Interlocked.Exchange(ref now, 3500);
        // (3500 - 1000) / 1000.0 = 2.5 segundos.
        Assert.Equal(2.5, s.SecondsSinceLastFrame()!.Value, 1e-9);
    }

    [Fact]
    public async Task Stop_KillDenied_ReportsErrorWithoutThrowing()
    {
        var p = new FakeProcess { KillException = new System.ComponentModel.Win32Exception("denegado") };
        var l = new FakeLauncher(p);
        await using var s = Create(l);
        await s.StartAsync();
        var stop = s.StopAsync();
        await Until(() => s.KillError is not null);
        // El proceso falso no termina al fallar Kill: cerramos su stream manualmente.
        p.End(0);
        await stop;
        // Una solicitud de parada produce un intento; la excepción no altera Stopped.
        Assert.Equal(LiveSessionState.Stopped, s.State);
        Assert.Contains("denegado", s.KillError!);
        Assert.Equal(1, p.Kills);
    }

    [Fact]
    public async Task Completed_KillDenied_ReportsErrorAndKeepsCompleted()
    {
        var p = new FakeProcess { KillException = new System.ComponentModel.Win32Exception("denegado") };
        var l = new FakeLauncher(p);
        await using var s = Create(l, options: new LiveSessionOptions { DurationSeconds = 1 });
        await s.StartAsync();
        // t=0 se acepta; t=1000 alcanza el límite de 1000 ms y completa.
        p.Reader.Send(Header + Row + "cs2.exe,0x0,1000,10\r\n");
        await Until(() => s.KillError is not null);
        Assert.Equal(LiveSessionState.Completed, s.State);
        Assert.Contains("denegado", s.KillError!);
        Assert.Equal(1, p.Kills);
        p.End(0);
        await s.StopAsync();
    }

    [Fact]
    public async Task Stall_KillDenied_ReportsErrorAndKeepsStalled()
    {
        long now = 0;
        var p = new FakeProcess { KillException = new System.ComponentModel.Win32Exception("denegado") };
        var l = new FakeLauncher(p);
        await using var s = Create(l, options: new LiveSessionOptions { StallGrace = TimeSpan.FromSeconds(30) },
            clock: () => Interlocked.Read(ref now));
        await s.StartAsync(); p.Reader.Send(Header + Row);
        await Until(() => s.State == LiveSessionState.Running);
        // 30001 - 0 = 30001 ms > 30000 ms: Stalled y un intento de Kill.
        Interlocked.Exchange(ref now, 30001);
        s.CheckStall();
        Assert.Equal(LiveSessionState.Stalled, s.State);
        Assert.Contains("denegado", s.KillError!);
        Assert.Equal(1, p.Kills);
        p.End(0);
        await s.StopAsync();
    }

    [Fact]
    public async Task Stop_AlreadyExitedException_IsSilent()
    {
        var p = new FakeProcess { KillException = new InvalidOperationException("ya terminó") };
        var l = new FakeLauncher(p);
        await using var s = Create(l);
        await s.StartAsync();
        var stop = s.StopAsync();
        await Until(() => Volatile.Read(ref p.Kills) == 1);
        p.End(0);
        await stop;
        // Un intento que indica proceso ya terminado no produce KillError.
        Assert.Equal(1, p.Kills);
        Assert.Equal(LiveSessionState.Stopped, s.State);
        Assert.Null(s.KillError);
    }

    [Fact]
    public async Task ConcurrentStop_DiscardsPartialAndKeepsStoppedAfterKillExit()
    {
        var p = new FakeProcess(); var l = new FakeLauncher(p);
        await using var s = Create(l);
        await s.StartAsync(); p.Reader.Send(Header + Row + "cs2.exe,0x0,100,16.");
        await Until(() => s.State == LiveSessionState.Running);
        await Task.WhenAll(s.StopAsync(), s.StopAsync());
        await Until(() => s.ExitCode.HasValue);
        Assert.Equal(1, p.Kills); // dos llamadas concurrentes, un solo Kill
        Assert.Equal(1, s.ExitCode); // Kill simulado devuelve código no cero
        Assert.Equal(LiveSessionState.Stopped, s.State);
        Assert.Equal(1L, s.Ingest.Counters.DiscardedPartialLines); // una fila parcial, descartada una vez
        Assert.Equal(1L, s.Ingest.Counters.FramesParsed); // solo la fila completa
    }
}
