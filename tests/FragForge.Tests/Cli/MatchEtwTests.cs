using System.Threading.Channels;
using FragForge.Actions.Measurement;
using FragForge.Cli;
using FragForge.Core.Analysis.Live;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class MatchEtwTests
{
    private const string Name = "FragForgeLive-035877d0";
    private sealed class Prompt : IConsolePrompt
    {
        public bool? Answer;
        public List<string> Warnings { get; } = [];
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void WriteWarning(string text) => Warnings.Add(text);
        public string? Ask(string question) => null;
        public bool? Confirm(string question) => Answer;
    }
    private sealed class Keys : IConsoleKeyReader
    {
        public bool IsAvailable => false;
        public ConsoleKey? TryReadKey() => null;
    }
    private sealed class Hotkeys : IHotkeySource
    {
        public event Action<HotkeyAction>? Pressed;
        public IReadOnlyList<HotkeyRegistration> Registrations => [];
        public bool TryStart(out string? error) { error = null; return true; }
        public void Send(HotkeyAction action) => Pressed?.Invoke(action);
        public void Dispose() { }
    }
    private sealed class Reader : TextReader
    {
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
        public void Send(string text) => _chunks.Writer.TryWrite(text);
        public void End() => _chunks.Writer.TryComplete();
        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            if (!await _chunks.Reader.WaitToReadAsync()) return 0;
            var text = await _chunks.Reader.ReadAsync();
            text.CopyTo(0, buffer, index, text.Length);
            return text.Length;
        }
    }
    private sealed class FakeProcess : ILivePresentMonProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Reader Output { get; } = new();
        public int Kills;
        public TextReader StandardOutput => Output;
        public Task<string> ReadStandardErrorAsync() => Task.FromResult("");
        public Task<int> WaitForExitAsync(CancellationToken token) => _exit.Task.WaitAsync(token);
        public void Kill() { Kills++; Output.End(); _exit.TrySetResult(1); }
        public void Dispose() { }
    }
    private sealed class Launcher(FakeProcess process, bool throws) : ILivePresentMonLauncher
    {
        public ILivePresentMonProcess Start(string exePath, IReadOnlyList<string> arguments)
        {
            if (throws) throw new InvalidOperationException("falló el lanzador");
            return process;
        }
    }
    private sealed class Etw : IEtwSessionControl
    {
        public int ListCalls;
        public List<string> Closed { get; } = [];
        public Func<int, IReadOnlyList<string>> Listing = _ => [];
        public bool CloseSucceeds = true;
        public Task<IReadOnlyList<string>> ListOwnSessionsAsync(CancellationToken ct) => Task.FromResult(Listing(++ListCalls));
        public Task<EtwCloseResult> CloseAsync(string name, CancellationToken ct)
        {
            Closed.Add(name);
            return Task.FromResult(new EtwCloseResult(CloseSucceeds, CloseSucceeds ? "cerrada" : "falló"));
        }
    }
    private static MatchRunner Create(Prompt prompt, Hotkeys hotkeys, FakeProcess process, Etw etw,
        Func<TimeSpan, CancellationToken, Task> delay, bool launchFails = false, bool rejectTool = false,
        Action<LivePresentMonSession>? capture = null)
    {
        LivePresentMonSession Factory(LiveSessionOptions options, ILivePresentMonLauncher launcher)
        {
            var session = new LivePresentMonSession(launcher, () => true,
                () => rejectTool ? new LiveToolResolution(null, "rechazada") : new LiveToolResolution("falso", null),
                options with { SessionName = Name });
            capture?.Invoke(session);
            return session;
        }
        var launcher = new Launcher(process, launchFails);
        return new MatchRunner(prompt, new Keys(), hotkeys, options => Factory(options, launcher),
            () => new Cs2WindowObservation(Cs2WindowStatus.NotRunning, [], null, null, null, null, null, null, null, []),
            () => Task.FromResult(new Cs2Inspection()), () => Task.FromResult(new OrphanPresentMonSearch([], [])),
            () => [], () => true, null, () => DateTimeOffset.UnixEpoch, delay, etw,
            (options, wrap) => Factory(options, wrap(launcher)));
    }
    private static async Task WaitForRunning(LivePresentMonSession session)
    {
        async Task Poll() { while (session.State != LiveSessionState.Running) await Task.Yield(); }
        await Poll().WaitAsync(TimeSpan.FromSeconds(15));
    }
    private static void SendFrame(FakeProcess process) => process.Output.Send("Application,SwapChainAddress,TimeInMs,MsBetweenPresents\r\ncs2.exe,0x0,0,10\r\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishOrCancellation_ClosesExactlyOnce(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var prompt = new Prompt(); var hotkeys = new Hotkeys(); var process = new FakeProcess(); var etw = new Etw();
        LivePresentMonSession? session = null; var ticks = 0;
        var runner = Create(prompt, hotkeys, process, etw, async (_, _) =>
        {
            if (ticks++ == 0) { SendFrame(process); await WaitForRunning(session!); if (cancel) cts.Cancel(); else hotkeys.Send(HotkeyAction.Finish); }
        }, capture: value => session = value);
        var exit = await runner.RunAsync(new MatchOptions(null, false), cts.Token);
        // Un frame de 10 ms y fin manual: éxito, 1 cierre ETW y 1 Kill del proceso propio.
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(new[] { "FragForgeLive-035877d0" }, etw.Closed);
        Assert.Equal(1, process.Kills);
    }

    [Fact]
    public async Task Exception_StopsThenClosesExactlyOnce()
    {
        var prompt = new Prompt(); var hotkeys = new Hotkeys(); var process = new FakeProcess(); var etw = new Etw();
        var runner = Create(prompt, hotkeys, process, etw, (_, _) => Task.FromException(new InvalidOperationException("bucle")));
        // Un lanzamiento correcto seguido de excepción: 1 Kill y 1 cierre, excepción propagada.
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new MatchOptions(null, false)));
        Assert.Equal(1, process.Kills);
        Assert.Equal(new[] { "FragForgeLive-035877d0" }, etw.Closed);
    }

    [Fact]
    public async Task CloseFailure_DoesNotChangeExit()
    {
        var prompt = new Prompt(); var hotkeys = new Hotkeys(); var process = new FakeProcess(); var etw = new Etw { CloseSucceeds = false };
        LivePresentMonSession? session = null;
        var runner = Create(prompt, hotkeys, process, etw, async (_, _) => { SendFrame(process); await WaitForRunning(session!); hotkeys.Send(HotkeyAction.Finish); }, capture: value => session = value);
        var exit = await runner.RunAsync(new MatchOptions(null, false));
        // Un frame permite exit 0 aun cuando el único cierre falla.
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(new[] { "FragForgeLive-035877d0" }, etw.Closed);
        Assert.Contains(prompt.Warnings, x => x.Contains("logman stop FragForgeLive-035877d0 -ets"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LauncherFailure_QueriesAssignedNameAndClosesOnlyIfListed(bool listed)
    {
        var prompt = new Prompt(); var hotkeys = new Hotkeys(); var process = new FakeProcess();
        var etw = new Etw { Listing = n => n == 1 ? [] : listed ? [Name] : [] };
        var runner = Create(prompt, hotkeys, process, etw, (_, _) => Task.CompletedTask, launchFails: true);
        var exit = await runner.RunAsync(new MatchOptions(null, false));
        // 1 consulta inicial + 1 tras fallo; cierre = 1 si está listada, si no = 0.
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(2, etw.ListCalls);
        Assert.Equal(listed ? new[] { "FragForgeLive-035877d0" } : Array.Empty<string>(), etw.Closed);
        Assert.DoesNotContain(prompt.Warnings, x => x.Contains("no se pudo comprobar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LauncherFailure_QueryFails_WarnsWithoutChangingExit()
    {
        var prompt = new Prompt(); var hotkeys = new Hotkeys(); var process = new FakeProcess();
        var etw = new Etw { Listing = n => n == 1 ? [] : throw new InvalidOperationException("consulta fallida") };
        var runner = Create(prompt, hotkeys, process, etw, (_, _) => Task.CompletedTask, launchFails: true);
        var exit = await runner.RunAsync(new MatchOptions(null, false));
        // Consulta inicial vacía, consulta final falla: 0 cierres, exit 1 del lanzamiento.
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(2, etw.ListCalls);
        Assert.Empty(etw.Closed);
        Assert.Contains(prompt.Warnings, x => x.Contains("logman query -ets") && x.Contains("PowerShell como administrador"));
    }

    [Fact]
    public async Task RejectedTool_NoAssignedName_NoCleanupQueryOrClose()
    {
        var prompt = new Prompt(); var hotkeys = new Hotkeys(); var process = new FakeProcess(); var etw = new Etw();
        var runner = Create(prompt, hotkeys, process, etw, (_, _) => Task.CompletedTask, rejectTool: true);
        var exit = await runner.RunAsync(new MatchOptions(null, false));
        // La única consulta es la inicial; sin nombre asignado no hay consulta final ni cierre.
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(1, etw.ListCalls);
        Assert.Empty(etw.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSessions_ConfirmationOnlyClosesValidNames(bool yes)
    {
        var prompt = new Prompt { Answer = yes }; var hotkeys = new Hotkeys(); var process = new FakeProcess();
        var etw = new Etw { Listing = _ => ["FragForgeLive-1234abcd", "PresentMon", "FragForgeLive-1234abcd"] };
        var runner = Create(prompt, hotkeys, process, etw, (_, _) => Task.CompletedTask, rejectTool: true);
        var exit = await runner.RunAsync(new MatchOptions(null, false));
        // Una sesión válida duplicada: 1 cierre con sí y 0 con no; el rechazo del tool es exit 1.
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(yes ? new[] { "FragForgeLive-1234abcd" } : Array.Empty<string>(), etw.Closed);
    }

    [Fact]
    public async Task OldSessions_ListFailure_WarnsAndContinues()
    {
        var prompt = new Prompt(); var hotkeys = new Hotkeys(); var process = new FakeProcess();
        var etw = new Etw { Listing = _ => throw new InvalidOperationException("sin lista") };
        var runner = Create(prompt, hotkeys, process, etw, (_, _) => Task.CompletedTask, rejectTool: true);
        var exit = await runner.RunAsync(new MatchOptions(null, false));
        // Falla una consulta inicial; se sigue hasta el rechazo de la herramienta y no se cierra nada.
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(1, etw.ListCalls);
        Assert.Empty(etw.Closed);
        Assert.Contains(prompt.Warnings, x => x.Contains("No se pudieron listar sesiones ETW anteriores") && x.Contains("sin lista"));
        Assert.Contains(prompt.Warnings, x => x.Contains("rechazada"));
    }
}
