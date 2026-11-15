using FragForge.Cli;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class MemoryCloseSafetyTests
{
    private static readonly DateTimeOffset Start = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private sealed class Input : ICloseConsole
    {
        public bool IsInputRedirected => false;
        public Queue<string?> Answers { get; } = new();
        public List<string> Lines { get; } = [];
        public Task<string?> ReadLineAsync() => Task.FromResult(Answers.Count == 0 ? null : Answers.Dequeue());
        public Task WriteLineAsync(string text) { Lines.Add(text); return Task.CompletedTask; }
    }
    private sealed class Reader : ICloseIdentityReader
    {
        public int Calls { get; private set; }
        public Func<int, IReadOnlyList<CloseIdentity>?> Read { get; set; } = _ => [];
        public Task<IReadOnlyList<CloseIdentity>?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read(++Calls));
    }
    private sealed class Handle(string name, long id, List<string> events) : ICloseProcess
    {
        public string? Name => name;
        public long? Pid => id;
        public DateTimeOffset? StartTime => Start;
        public bool HasExited { get; set; }
        public int Closes { get; private set; }
        public int Kills { get; private set; }
        public int Disposes { get; private set; }
        public bool CloseMainWindow() { events.Add("CLOSE " + id); Closes++; return false; }
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(false);
        public void Kill() { events.Add("KILL " + id); Kills++; }
        public void Dispose() => Disposes++;
    }
    private sealed class Opener(Dictionary<long, Handle> handles) : ICloseProcessOpener
    {
        public List<long> Opened { get; } = [];
        public ICloseProcess Open(long pid) { Opened.Add(pid); return handles[pid]; }
    }
    private sealed class Fixture
    {
        public Input Input { get; } = new();
        public Reader Reader { get; } = new();
        public List<string> Events { get; } = [];
        public Dictionary<long, Handle> Handles { get; } = [];
        public List<ProcessMemoryRow> Rows { get; } = [];
        public List<string> Logged { get; } = [];
        public int ServerCalls { get; private set; }
        public string? FailPrefix { get; set; }
        public Opener Opener { get; }
        public Fixture() => Opener = new Opener(Handles);
        public void Add(string name, long pid, double mb) { Rows.Add(new(name, pid, mb, mb, "no protegido", "test")); Handles[pid] = new Handle(name, pid, Events); }
        public void Answers(params string[] answers) { foreach (var answer in answers) Input.Answers.Enqueue(answer); }
        public Task<MemoryCloseResult> Run() => new MemoryCloseLogic(
            _ => Task.FromResult(new MemorySnapshot(null, ProcessMemoryGrouping.Group(Rows)) { ProcessRows = Rows }), Reader, Opener,
            _ => Task.FromResult<double?>(1000), (_, _) => Task.CompletedTask,
            (_, _, _) => { ServerCalls++; return Task.FromResult(true); }, Input, () => Start, 999,
            (line, _) => { if (FailPrefix is not null && line.StartsWith(FailPrefix, StringComparison.Ordinal)) throw new IOException(); Logged.Add(line); Events.Add("LOG " + line); return Task.CompletedTask; }).RunAsync([]);
    }
    private static CloseIdentity Self(long parent = 0) => new("fragforge", 999, Start, parent);
    private static CloseIdentity Alpha() => new("alpha", 42, Start, 0);

    [Fact]
    public async Task AncestorsExcludedFromCountsAndRamButOtherInstancesRemain()
    {
        var f = new Fixture();
        f.Add("dotnet", 999, 100); f.Add("dotnet", 777, 200); f.Add("powershell", 666, 300);
        f.Add("dotnet", 42, 50); f.Add("powershell", 43, 15);
        f.Reader.Read = _ => [new("dotnet", 999, Start, 777), new("dotnet", 777, Start, 666), new("powershell", 666, null, 0), new("dotnet", 42, Start, 0), new("powershell", 43, Start, 0)];
        f.Answers("1", "s", "s"); var result = await f.Run();
        // dotnet: 100 + 200 excluidos, solo 50; powershell: 300 excluidos, solo 15.
        Assert.Equal(0, result.ExitCode); Assert.Equal(new long[] { 42 }, f.Opener.Opened);
        Assert.Equal(1, f.Handles[42].Closes); Assert.Equal(1, f.Handles[42].Kills);
        Assert.Contains("1: dotnet / 1 instancias / 50.0 MB estimados", f.Input.Lines);
        Assert.Contains("2: powershell / 1 instancias / 15.0 MB estimados", f.Input.Lines);
        Assert.Contains("PID propios y ancestros excluidos: 999 (dotnet), 777 (dotnet), 666 (powershell)", f.Input.Lines);
        Assert.Contains("Puede cerrar terminales, compilaciones o programas .NET que estés usando.", f.Input.Lines);
        Assert.Contains("Identidad seleccionada: dotnet PID 42 inicio 2025-01-02T03:04:05.0000000+00:00 / 50.0 MB estimados", f.Input.Lines);
        Assert.Empty(f.Handles.Where(x => (x.Key is 999 or 777 or 666) && (x.Value.Closes != 0 || x.Value.Kills != 0)));
    }

    [Fact]
    public void CyclesAndDepthLimitTerminateConservatively()
    {
        var cycle = new[] { Self(7), new CloseIdentity("parent", 7, Start, 999) };
        Assert.True(MemoryCloseLogic.TryAncestors(cycle, 999, out var cycled));
        Assert.Equal(new long[] { 999, 7 }, cycled.Keys);
        var longChain = Enumerable.Range(0, 40).Select(i => new CloseIdentity("p", 100 + i, Start, 101 + i)).ToArray();
        Assert.False(MemoryCloseLogic.TryAncestors(longChain, 100, out var limited));
        // PID 100 a 132 inclusive: 33 PID observados; la cadena continúa y se rechaza.
        Assert.Equal(Enumerable.Range(100, 33).Select(i => (long)i), limited.Keys);
        Assert.True(MemoryCloseLogic.TryAncestors([Self(7)], 999, out var vanished));
        Assert.Equal(new long[] { 999, 7 }, vanished.Keys); Assert.Equal("no se sabe", vanished[7]);
    }

    [Fact]
    public async Task MissingChainOffersNoActions()
    {
        var f = new Fixture(); f.Add("alpha", 42, 50); f.Reader.Read = _ => [Alpha()]; f.Answers("1", "s", "s");
        var result = await f.Run();
        Assert.Equal(1, result.ExitCode); Assert.Empty(f.Opener.Opened);
        Assert.DoesNotContain(f.Input.Lines, line => line.StartsWith("1: ", StringComparison.Ordinal));
        Assert.Contains("No se pudo resolver la cadena de ancestros; no se ofrecerán más cierres.", result.Log);
        Assert.Equal(0, f.Handles[42].Closes); Assert.Equal(0, f.Handles[42].Kills);
    }

    [Fact]
    public async Task WmiFailureOffersNoActions()
    {
        var f = new Fixture(); f.Add("alpha", 42, 50); f.Reader.Read = _ => throw new InvalidOperationException(); f.Answers("1", "s", "s");
        var result = await f.Run();
        Assert.Equal(1, result.ExitCode); Assert.Empty(f.Opener.Opened);
        Assert.Equal(0, f.Handles[42].Closes); Assert.Equal(0, f.Handles[42].Kills);
        Assert.Contains("No se pudo resolver la cadena de ancestros; no se ofrecerán más cierres.", result.Log);
    }

    [Theory]
    [InlineData(3, 0, 0)]
    [InlineData(4, 1, 0)]
    [InlineData(7, 1, 1)]
    public async Task NewlyObservedAncestorIsOmittedAtEachLayer(int fromCall, int opened, int closes)
    {
        var f = new Fixture(); f.Add("alpha", 42, 50);
        f.Reader.Read = n => [Self(n >= fromCall ? 42 : 0), Alpha()]; f.Answers("1", "s", "s");
        var result = await f.Run();
        Assert.Equal(0, result.ExitCode); Assert.Equal(opened == 0 ? Array.Empty<long>() : new long[] { 42 }, f.Opener.Opened);
        Assert.Equal(closes, f.Handles[42].Closes); Assert.Equal(0, f.Handles[42].Kills);
        Assert.Contains("omitido.", result.Log);
        if (opened == 1) Assert.Equal(1, f.Handles[42].Disposes);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("powershell")]
    public async Task TerminalWarningIsLiteral(string name)
    {
        var f = new Fixture(); f.Add(name, 42, 50); f.Reader.Read = _ => [Self(), new(name, 42, Start, 0)]; f.Answers("1", "n");
        Assert.Equal(0, (await f.Run()).ExitCode);
        Assert.Contains("Puede cerrar terminales, compilaciones o programas .NET que estés usando.", f.Input.Lines);
        Assert.Equal(0, f.ServerCalls);
    }

    [Fact]
    public async Task ForcePromptCountsOnlyLiveInstances()
    {
        var f = new Fixture(); f.Add("alpha", 42, 50); f.Add("alpha", 43, 30); f.Handles[43].HasExited = true;
        f.Reader.Read = _ => [Self(), Alpha(), new("alpha", 43, Start, 0)]; f.Answers("1", "s", "n");
        Assert.Equal(0, (await f.Run()).ExitCode);
        Assert.Contains("Pendiente: alpha PID 42", f.Input.Lines);
        Assert.DoesNotContain("Pendiente: alpha PID 43", f.Input.Lines);
        Assert.Contains("¿Forzar el cierre de 1 proceso(s) de alpha? [s/N]", f.Input.Lines);
        Assert.Equal(0, f.Handles[42].Kills); Assert.Equal(0, f.Handles[43].Kills);
    }

    [Theory]
    [InlineData("Intención cierre amable:", 0, 0)]
    [InlineData("Intención cierre forzado:", 1, 0)]
    public async Task FailedIntentLoggingPreventsThatAction(string prefix, int expectedCloses, int expectedKills)
    {
        var f = new Fixture(); f.Add("alpha", 42, 50); f.Reader.Read = _ => [Self(), Alpha()]; f.FailPrefix = prefix;
        f.Answers("1", "s", "s"); var result = await f.Run();
        Assert.Equal(1, result.ExitCode); Assert.Equal(expectedCloses, f.Handles[42].Closes); Assert.Equal(expectedKills, f.Handles[42].Kills);
        Assert.Contains("Aviso: no se pudo escribir el registro; no se realizó la siguiente acción.", f.Input.Lines);
    }

    [Fact]
    public async Task IntentIsRecordedBeforeEachActionAndAnswersAreLogged()
    {
        var f = new Fixture(); f.Add("alpha", 42, 50); f.Reader.Read = _ => [Self(), Alpha()]; f.Answers("1", "s", "s");
        Assert.Equal(0, (await f.Run()).ExitCode);
        Assert.True(f.Events.FindIndex(x => x.StartsWith("LOG Intención cierre amable:", StringComparison.Ordinal)) < f.Events.IndexOf("CLOSE 42"));
        Assert.True(f.Events.FindIndex(x => x.StartsWith("LOG Intención cierre forzado:", StringComparison.Ordinal)) < f.Events.IndexOf("KILL 42"));
        Assert.Contains("Selección ingresada: 1", f.Logged); Assert.Contains("Respuesta de confirmación: s", f.Logged); Assert.Contains("Respuesta de cierre forzado: s", f.Logged);
        Assert.Contains("Disponible antes (MB): 1000.0", f.Logged); Assert.Contains("Disponible después (MB): 1000.0", f.Logged);
    }
}
