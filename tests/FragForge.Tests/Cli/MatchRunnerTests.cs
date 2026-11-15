using System.Threading.Channels;
using FragForge.Actions.Measurement;
using FragForge.Cli;
using FragForge.Core.Analysis.Live;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class MatchRunnerTests
{
    private const string Header = "Application,SwapChainAddress,TimeInMs,MsBetweenPresents\r\n";
    private const string Row = "cs2.exe,0x0,0,10\r\n";
    private sealed class Prompt : IConsolePrompt
    {
        public List<string> Lines { get; } = [];
        public List<string> Warnings { get; } = [];
        public bool? Answer { get; set; } = true;
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
        public bool Started = true;
        public int Disposals;
        public IReadOnlyList<HotkeyRegistration> Registrations { get; set; } = [];
        public bool TryStart(out string? error) { error = Started ? null : "F10 ocupada"; return Started; }
        public void Send(HotkeyAction action) => Pressed?.Invoke(action);
        public void Dispose() => Disposals++;
    }
    private sealed class Reader : TextReader
    {
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
        public void Send(string s) => _chunks.Writer.TryWrite(s);
        public void End() => _chunks.Writer.TryComplete();
        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            if (!await _chunks.Reader.WaitToReadAsync()) return 0;
            var s = await _chunks.Reader.ReadAsync();
            s.CopyTo(0, buffer, index, s.Length);
            return s.Length;
        }
    }
    private sealed class Process : ILivePresentMonProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Reader Reader { get; } = new();
        public int Kills;
        public bool Disposed;
        public TextReader StandardOutput => Reader;
        public Task<string> ReadStandardErrorAsync() => Task.FromResult("boom");
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);
        public void End(int code) { Reader.End(); _exit.TrySetResult(code); }
        public void Kill() { Interlocked.Increment(ref Kills); End(1); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Launcher(Process process) : ILivePresentMonLauncher
    {
        public int Calls;
        public ILivePresentMonProcess Start(string exePath, IReadOnlyList<string> arguments) { Calls++; return process; }
    }
    private static readonly Cs2WindowObservation Window = new(Cs2WindowStatus.Observed, [7], 1024, 768, 1920, 1080, 1920, 1080, true, []);
    private static readonly DateTimeOffset Time = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static MatchRunner Runner(Prompt prompt, Hotkeys hotkeys, Launcher launcher,
        Func<TimeSpan, CancellationToken, Task> delay, Func<bool>? elevated = null,
        Func<Task<OrphanPresentMonSearch>>? orphans = null, Func<Task<Cs2Inspection>>? inspection = null,
        Func<Cs2WindowObservation>? window = null, MatchLogWriter? log = null, Action<LivePresentMonSession>? capture = null, Func<long>? nowMs = null) =>
        new(prompt, new Keys(), hotkeys,
            options => { var session = new LivePresentMonSession(launcher, () => true, () => new LiveToolResolution("approved", null), options, nowMs ?? (() => 0)); capture?.Invoke(session); return session; },
            window ?? (() => Window), inspection ?? (() => Task.FromResult(new Cs2Inspection())),
            orphans ?? (() => Task.FromResult(new OrphanPresentMonSearch([], []))), () => [],
            elevated ?? (() => true), log, () => Time, delay);

    private static Task WaitUntil(Func<bool> condition) => Poll(condition).WaitAsync(TimeSpan.FromSeconds(15));

    private static async Task Poll(Func<bool> condition)
    {
        while (!condition()) await Task.Yield();
    }

    [Fact]
    public async Task NotElevated_DoesNotLaunch()
    {
        var process = new Process(); var launcher = new Launcher(process); var hotkeys = new Hotkeys(); var prompt = new Prompt();
        var result = await Runner(prompt, hotkeys, launcher, (_, _) => Task.CompletedTask, elevated: () => false).RunAsync(new MatchOptions(null, false));
        // 0 lanzamientos; una disposición de la fuente aunque no haya iniciado.
        Assert.Equal(ExitCodes.Failure, result);
        Assert.Equal(0, launcher.Calls);
        Assert.Equal(1, hotkeys.Disposals);
        Assert.Contains(prompt.Warnings, x => x.Contains("administrador"));
    }

    [Fact]
    public async Task OrphanRefused_DoesNotLaunch()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt { Answer = false }; var hotkeys = new Hotkeys();
        var result = await Runner(prompt, hotkeys, launcher, (_, _) => Task.CompletedTask,
            orphans: () => Task.FromResult(new OrphanPresentMonSearch([new OrphanPresentMon(4, "ruta", "cmd", "FragForgeLive-a")], []))).RunAsync(new MatchOptions(null, false));
        // Un candidato y confirmación no: cero lanzamientos.
        Assert.Equal(ExitCodes.Cancelled, result);
        Assert.Equal(0, launcher.Calls);
        Assert.Contains(prompt.Warnings, x => x.Contains("PID 4"));
    }

    [Fact]
    public async Task NoFrames_ShowThenFinish_FailsWithSummary()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        var ticks = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) => { if (ticks++ == 0) hotkeys.Send(HotkeyAction.ShowNow); else if (ticks == 4) await Task.Run(() => hotkeys.Send(HotkeyAction.Finish)); });
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // Ninguna fila recibida => fallo, un Kill y resumen final.
        Assert.Equal(ExitCodes.Failure, result);
        Assert.Contains(prompt.Lines, x => x.Contains("Todavía no llegaron frames"));
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL"));
        Assert.Contains(prompt.Warnings, x => x.Contains("no llegaron frames"));
        Assert.Equal(4, ticks);
        Assert.Equal(1, process.Kills);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task DurationCompleted_Formats100FpsAndDoesNotSave()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        var sent = false;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (!sent)
            {
                sent = true;
                process.Reader.Send(Header + Row + "cs2.exe,0x0,10,10\r\ncs2.exe,0x0,20,10\r\ncs2.exe,0x0,30,10\r\ncs2.exe,0x0,1000,10\r\n");
                await WaitUntil(() => Volatile.Read(ref process.Kills) == 1);
            }
        });
        var result = await runner.RunAsync(new MatchOptions(1, false));
        // Cuatro frames de 10 ms; el quinto en 1000 ms completa: 1000/10 = 100 FPS.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL") && x.Contains("FPS promedio: 100.0 FPS"));
        Assert.Contains(prompt.Lines, x => x.Contains("No se guardó ningún log"));
        Assert.Equal(1, process.Kills);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task LogSaved_FinalAndPartialAreProduced()
    {
        using var temp = new TempDirectory();
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        var stage = 0;
        LivePresentMonSession? session = null;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (stage++ == 0) { process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); }
            else if (stage == 2) { hotkeys.Send(HotkeyAction.ShowNow); hotkeys.Send(HotkeyAction.Finish); }
        }, log: new MatchLogWriter(temp.Path, () => Time), capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, true));
        // Una fila de 10 ms => 100 FPS; el guardado crea un único resumen final.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL"));
        Assert.Contains(prompt.Lines, x => x.Contains("VISTA PARCIAL"));
        Assert.Single(Directory.GetFiles(temp.Path, "match-*.txt"));
    }
    [Fact]
    public async Task SegmentClosed_ProcessesCrossThreadEvents()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null;
        var tick = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            switch (tick++)
            {
                case 0:
                    process.Reader.Send(Header + Row);
                    await WaitUntil(() => session!.State == LiveSessionState.Running);
                    await Task.Run(() => hotkeys.Send(HotkeyAction.ToggleSegment));
                    break;
                case 1:
                    process.Reader.Send("cs2.exe,0x0,10,20\r\n");
                    await WaitUntil(() => session!.Ingest.Counters.FramesParsed == 2);
                    hotkeys.Send(HotkeyAction.ToggleSegment);
                    break;
                case 2: hotkeys.Send(HotkeyAction.ShowNow); hotkeys.Send(HotkeyAction.Finish); break;
            }
        }, capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // Segmento [inicio, fin): un frame de 20 ms => 50 FPS; dos frames en total.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Lines, x => x.Contains("Segmento iniciado"));
        Assert.Contains(prompt.Lines, x => x.Contains("Segmento cerrado"));
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL") && x.Contains("Segmento:") && x.Contains("FPS promedio: 50.0 FPS"));
        Assert.Contains(prompt.Lines, x => x.Contains("VISTA PARCIAL"));
        Assert.Equal(1, process.Kills);
    }

    [Fact]
    public async Task OpenSegmentAndPartialHotkeys_AreReported()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt();
        var hotkeys = new Hotkeys { Started = false, Registrations = [new(HotkeyAction.ToggleSegment, "F9")] };
        LivePresentMonSession? session = null; var tick = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0)
            {
                process.Reader.Send(Header + Row);
                await WaitUntil(() => session!.State == LiveSessionState.Running);
                hotkeys.Send(HotkeyAction.ToggleSegment);
                hotkeys.Send(HotkeyAction.ShowNow);
                hotkeys.Send(HotkeyAction.Finish);
            }
        }, capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // Una fila válida y segmento abierto: no se presenta un segmento incompleto.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Warnings, x => x.Contains("F10 ocupada"));
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL") && x.Contains("segmento sin cerrar: no se muestra"));
        Assert.Equal(1, hotkeys.Disposals);
    }

    [Fact]
    public async Task DetectorFailureAndEndConfigChange_Continue()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; var tick = 0; var calls = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0) { process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); hotkeys.Send(HotkeyAction.Finish); }
        }, orphans: () => Task.FromException<OrphanPresentMonSearch>(new InvalidOperationException("WMI")),
        inspection: () => Task.FromResult(new Cs2Inspection { LaunchOptions = ++calls == 1 ? "a" : "b" }), capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // WMI falla sin impedir la medición; a -> b produce una diferencia.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Equal(1, launcher.Calls);
        Assert.Contains(prompt.Warnings, x => x.Contains("WMI"));
        Assert.Contains(prompt.Lines, x => x.Contains("LA CONFIGURACIÓN CAMBIÓ DURANTE LA MEDICIÓN"));
    }

    [Fact]
    public async Task ConfirmOrphanAndInitialInspectionFailure_Continue()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt { Answer = true }; var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; var tick = 0; var calls = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0) { process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); hotkeys.Send(HotkeyAction.Finish); }
        }, orphans: () => Task.FromResult(new OrphanPresentMonSearch([new OrphanPresentMon(4, "ruta", "cmd", "FragForgeLive-a")], [])),
        inspection: () => ++calls == 1 ? Task.FromException<Cs2Inspection>(new InvalidOperationException("sin config")) : Task.FromResult(new Cs2Inspection()),
        capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // La confirmación sí permite un lanzamiento; sin hoja inicial el resumen no la muestra.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Equal(1, launcher.Calls);
        Assert.Contains(prompt.Warnings, x => x.Contains("sin config"));
        Assert.DoesNotContain(prompt.Lines, x => x.Contains("Hoja de configuración:"));
    }

    [Fact]
    public async Task ProcessFails_ReportsStderr()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; var tick = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0) { process.End(1); await WaitUntil(() => session!.State == LiveSessionState.Failed); }
        }, capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // Código 1 y cero frames: fallo con cola stderr, sin matar un proceso ya terminado.
        Assert.Equal(ExitCodes.Failure, result);
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL") && x.Contains("boom"));
        Assert.Equal(0, process.Kills);
    }

    [Fact]
    public async Task Cancellation_ProducesFinalSummary()
    {
        using var cancel = new CancellationTokenSource();
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; var tick = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0) { process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); cancel.Cancel(); }
        }, capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, false), cancel.Token);
        // Un frame, luego cancelación como solicitud de terminar: éxito y un Kill.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL"));
        Assert.Equal(1, process.Kills);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task WindowUnavailableAtEnd_UsesPreviousObservation()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; var tick = 0; var windows = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0) { process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); }
            else hotkeys.Send(HotkeyAction.Finish);
        }, window: () => ++windows == 1 ? Window : throw new InvalidOperationException("no disponible"), capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // Primera observación 1024x768; al final falla y se conserva la primera con nota.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL") && x.Contains("Área cliente: 1024x768") && x.Contains("ventana observada durante la medición"));
    }

    [Fact]
    public async Task LoopFailure_DisposesSessionAndHotkeys()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        var runner = Runner(prompt, hotkeys, launcher, (_, _) => Task.FromException(new InvalidOperationException("bucle")));
        // Una excepción del delay propaga, pero ambas dependencias se limpian una vez.
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new MatchOptions(null, false)));
        Assert.Equal(1, process.Kills);
        Assert.Equal(1, hotkeys.Disposals);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task FixedDuration_SilenceStallsAfterGraceWithSuccess()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; long now = 0; var tick = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0) { process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); Interlocked.Exchange(ref now, 31000); }
        }, capture: value => session = value, nowMs: () => Interlocked.Read(ref now));
        var result = await runner.RunAsync(new MatchOptions(60, false));
        // 31 s sin frames > gracia de 30 s; un frame de 10 ms = 100 FPS.
        Assert.Equal(LiveSessionState.Stalled, session!.State);
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL") && x.Contains("FPS promedio: 100.0 FPS"));
        Assert.Equal(1, process.Kills);
    }

    [Fact]
    public async Task FullMatch_LongSilenceWarnsOncePerIntervalWithoutStalling()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; long now = 0; var tick = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            switch (tick++)
            {
                case 0: process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); Interlocked.Exchange(ref now, 11000); break;
                case 1: Interlocked.Exchange(ref now, 40000); break;
                case 2: process.Reader.Send("cs2.exe,0x0,10,10\r\n"); await WaitUntil(() => session!.SecondsSinceLastFrame() == 0 && session.Ingest.Counters.FramesParsed == 2); break;
                case 3: Interlocked.Exchange(ref now, 51000); break;
                case 4: Interlocked.Exchange(ref now, 90000); break;
                case 5: Assert.Equal(LiveSessionState.Running, session!.State); hotkeys.Send(HotkeyAction.Finish); break;
            }
        }, capture: value => session = value, nowMs: () => Interlocked.Read(ref now));
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // 11 y 51 s: 11 s desde cada frame; 40 y 90 s no repiten el aviso.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Equal(2, prompt.Lines.Count(x => x == "Sin frames hace 11 s (¿cs2 en pausa, minimizado o cerrado?)"));
        Assert.Equal(1, process.Kills);
    }

    [Fact]
    public async Task LogFailure_DoesNotChangeExit()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "file"); File.WriteAllText(file, "ocupado");
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; var tick = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0) { process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); hotkeys.Send(HotkeyAction.Finish); }
        }, log: new MatchLogWriter(Path.Combine(file, "logs"), () => Time), capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, true));
        // Un frame de 10 ms = 100 FPS; la ruta bajo un archivo no se puede crear.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Warnings, x => x.Contains("No se pudo guardar el log"));
        Assert.Contains(prompt.Lines, x => x.Contains("FPS promedio: 100.0 FPS"));
    }

    [Fact]
    public async Task WindowNotRunningAtEnd_UsesPreviousObservation()
    {
        var process = new Process(); var launcher = new Launcher(process); var prompt = new Prompt(); var hotkeys = new Hotkeys();
        LivePresentMonSession? session = null; var tick = 0; var windows = 0;
        var runner = Runner(prompt, hotkeys, launcher, async (_, _) =>
        {
            if (tick++ == 0) { process.Reader.Send(Header + Row); await WaitUntil(() => session!.State == LiveSessionState.Running); }
            else hotkeys.Send(HotkeyAction.Finish);
        }, window: () => ++windows == 1 ? Window : new Cs2WindowObservation(Cs2WindowStatus.NotRunning, [], null, null, null, null, null, null, null, []), capture: value => session = value);
        var result = await runner.RunAsync(new MatchOptions(null, false));
        // Una observación de 1024x768 seguida de NotRunning conserva la primera.
        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(prompt.Lines, x => x.Contains("RESUMEN FINAL") && x.Contains("Área cliente: 1024x768") && x.Contains("ventana observada durante la medición"));
    }

}
