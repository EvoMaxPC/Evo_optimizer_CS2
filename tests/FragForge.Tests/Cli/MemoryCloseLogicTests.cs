using FragForge.Cli;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class MemoryCloseLogicTests
{
    private static readonly DateTimeOffset Started = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly CloseIdentity Self = new("fragforge", 999, Started);
    private static readonly CloseIdentity Alpha = new("alpha", 42, Started);
    private static ProcessMemoryGroup Group(string name, double mb, string classification = "no protegido") => new(name, 1, mb, mb, classification, "test");

    private sealed class Input : ICloseConsole
    {
        public bool IsInputRedirected { get; set; }
        public Queue<string?> Lines { get; } = new();
        public List<string> Output { get; } = [];
        public Task<string?> ReadLineAsync() => Task.FromResult(Lines.Count == 0 ? null : Lines.Dequeue());
        public Task WriteLineAsync(string text) { Output.Add(text); return Task.CompletedTask; }
    }
    private sealed class Reader : ICloseIdentityReader
    {
        public int Calls { get; private set; }
        public Func<int, IReadOnlyList<CloseIdentity>?> Get { get; set; } = _ => [Self, Alpha];
        public Task<IReadOnlyList<CloseIdentity>?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Get(++Calls));
    }
    private sealed class Handle : ICloseProcess
    {
        public string? ProcessName { get; set; } = "alpha";
        public long? Pid { get; set; } = 42;
        public DateTimeOffset? Created { get; set; } = Started;
        public bool ThrowOnName { get; set; }
        public string? Name => ThrowOnName ? throw new InvalidOperationException() : ProcessName;
        public DateTimeOffset? StartTime => Created;
        public bool HasWindow { get; set; } = true;
        public bool Exits { get; set; }
        public Action? OnWait { get; set; }
        public int Closes { get; private set; }
        public int Kills { get; private set; }
        public int Disposes { get; private set; }
        public TimeSpan? Timeout { get; private set; }
        public bool CloseMainWindow() { Closes++; return HasWindow; }
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) { Timeout = timeout; OnWait?.Invoke(); return Task.FromResult(Exits); }
        public void Kill() => Kills++;
        public void Dispose() => Disposes++;
    }
    private sealed class Opener : ICloseProcessOpener
    {
        public Handle Handle { get; } = new();
        public List<long> Opened { get; } = [];
        public ICloseProcess Open(long pid) { Opened.Add(pid); return Handle; }
    }
    private sealed class Setup
    {
        public Input Console { get; } = new();
        public Reader Reader { get; } = new();
        public Opener Opener { get; } = new();
        public List<ProcessMemoryGroup> Groups { get; } = [Group("alpha", 100)];
        public Queue<double?> Memory { get; } = new([1000, 1250]);
        public List<TimeSpan> Delays { get; } = [];
        public List<(string[] Arguments, TimeSpan Timeout)> ServerCalls { get; } = [];
        public Task<MemoryCloseResult> Run(params string[] args) => new MemoryCloseLogic(
            _ => Task.FromResult(new MemorySnapshot(null, Groups)), Reader, Opener,
            _ => Task.FromResult(Memory.Dequeue()),
            (duration, _) => { Delays.Add(duration); return Task.CompletedTask; },
            (arguments, timeout, _) => { ServerCalls.Add((arguments.ToArray(), timeout)); return Task.FromResult(true); },
            Console, () => Started, 999).RunAsync(args);
        public void Answers(params string[] values) { foreach (var value in values) Console.Lines.Enqueue(value); }
    }

    [Fact]
    public async Task ProtectedGroupCannotBeSelected()
    {
        var s = new Setup(); s.Groups.Add(Group("cs2", 900));
        s.Reader.Get = _ => [Self, Alpha, new("cs2", 50, Started)];
        s.Answers("2");
        var result = await s.Run();
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(s.Opener.Opened);
        Assert.DoesNotContain(s.Console.Output, line => line.Contains("cs2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvalidIndexDoesNothing()
    {
        var s = new Setup(); s.Answers("1,3");
        var result = await s.Run();
        Assert.Equal(2, result.ExitCode); Assert.Empty(s.Opener.Opened);
        Assert.Contains("Selección inválida; no se cerró nada.", s.Console.Output);
    }

    [Fact]
    public async Task ChangedIdentityAfterConfirmationIsSkippedAndRecorded()
    {
        var s = new Setup();
        s.Reader.Get = n => n < 3 ? [Self, Alpha] : [Self, Alpha with { StartTime = Started.AddSeconds(1) }];
        s.Answers("1", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Empty(s.Opener.Opened);
        Assert.Contains("alpha PID 42: identidad cambiada o desconocida; omitido.", result.Log);
    }

    [Fact]
    public async Task UnknownCreationIsSkipped()
    {
        var s = new Setup(); s.Reader.Get = _ => [Self, Alpha with { StartTime = null }]; s.Answers("1", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Empty(s.Opener.Opened);
        Assert.Contains("alpha: identidad desconocida, duplicada o protegida; omitido.", result.Log);
    }

    [Fact]
    public async Task ProtectedObtainedObjectIsDisposedWithoutActions()
    {
        var s = new Setup(); s.Opener.Handle.ProcessName = "steam"; s.Answers("1", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Equal(0, s.Opener.Handle.Closes); Assert.Equal(0, s.Opener.Handle.Kills);
        Assert.Equal(1, s.Opener.Handle.Disposes);
        Assert.Contains("alpha PID 42: identidad del objeto distinta o protegida; omitido.", result.Log);
    }

    [Fact]
    public async Task ChangedIdentityBeforeKillIsSkipped()
    {
        var s = new Setup(); s.Opener.Handle.OnWait = () => s.Opener.Handle.Created = Started.AddSeconds(1); s.Answers("1", "s", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Equal(1, s.Opener.Handle.Closes); Assert.Equal(0, s.Opener.Handle.Kills);
        Assert.Equal(1, s.Opener.Handle.Disposes); Assert.Contains("alpha PID 42: identidad cambiada; omitido.", result.Log);
    }

    [Fact]
    public async Task VerificationExceptionSkipsAndDisposes()
    {
        var s = new Setup(); s.Opener.Handle.ThrowOnName = true; s.Answers("1", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Equal(0, s.Opener.Handle.Closes); Assert.Equal(0, s.Opener.Handle.Kills);
        Assert.Equal(1, s.Opener.Handle.Disposes); Assert.Contains("alpha PID 42: no se pudo verificar o cerrar; omitido.", result.Log);
    }

    [Fact]
    public async Task ForceRequiresSecondConfirmation()
    {
        var s = new Setup(); s.Answers("1", "s", "");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Equal(1, s.Opener.Handle.Closes); Assert.Equal(0, s.Opener.Handle.Kills);
        Assert.Equal(TimeSpan.FromSeconds(10), s.Opener.Handle.Timeout); Assert.Equal(1, s.Opener.Handle.Disposes);
    }

    [Fact]
    public async Task ForceWithSecondConfirmationKillsOnce()
    {
        var s = new Setup(); s.Opener.Handle.HasWindow = false; s.Answers("1", "s", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Equal(1, s.Opener.Handle.Closes); Assert.Equal(1, s.Opener.Handle.Kills);
        Assert.Equal(1, s.Opener.Handle.Disposes); Assert.Contains("alpha PID 42: cierre forzado solicitado.", result.Log);
    }

    [Fact]
    public async Task BrowserWarningOnlyForSelectedBrowser()
    {
        var browser = new Setup(); browser.Groups.Clear(); browser.Groups.Add(Group("chrome", 100)); browser.Answers("1", "");
        var result = await browser.Run(); Assert.Equal(0, result.ExitCode);
        Assert.Contains("Se pueden perder pestañas o formularios sin guardar.", browser.Console.Output);
        var normal = new Setup(); normal.Answers("1", ""); await normal.Run();
        Assert.DoesNotContain("Se pueden perder pestañas o formularios sin guardar.", normal.Console.Output);
    }

    [Fact]
    public async Task AvailableMemoryDeltaIsCalculatedAndRecorded()
    {
        var s = new Setup(); s.Opener.Handle.Exits = true; s.Answers("1", "s");
        var result = await s.Run();
        // 1250 - 1000 = 250 MB.
        Assert.Contains("Disponible antes (MB): 1000.0", result.Log);
        Assert.Contains("Disponible después (MB): 1250.0", result.Log);
        Assert.Contains("Delta disponible (MB): 250.0", result.Log);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, s.Delays);
        Assert.Contains("La memoria libre puede volver a bajar. Esto no es una promesa de FPS.", result.Log);
    }

    [Fact]
    public async Task RedirectedInputIsUsageErrorWithoutSnapshot()
    {
        var s = new Setup(); s.Console.IsInputRedirected = true;
        var result = await s.Run();
        Assert.Equal(2, result.ExitCode); Assert.Equal(0, s.Reader.Calls); Assert.Empty(s.Opener.Opened);
    }

    [Fact]
    public async Task ServerOptionUsesExactArgumentsAndTimeout()
    {
        var s = new Setup(); s.Groups.Add(Group("VBCSCompiler", 50)); s.Answers("3", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Empty(s.Opener.Opened);
        var call = Assert.Single(s.ServerCalls);
        Assert.Equal(new[] { "build-server", "shutdown" }, call.Arguments); Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
    }
    [Theory]
    [InlineData("cs2")]
    [InlineData("steamwebhelper")]
    [InlineData("vgc")]
    [InlineData("vgk")]
    [InlineData("vgtray")]
    [InlineData("lghub_agent")]
    [InlineData("svchost")]
    public async Task ProtectedNamesAreNotOptionsEvenWithUntrustedClassification(string name)
    {
        var s = new Setup(); s.Groups.Add(Group(name, 900)); s.Reader.Get = _ => [Self, Alpha, new(name, 50, Started)];
        s.Answers("2"); var result = await s.Run();
        Assert.Equal(2, result.ExitCode); Assert.Empty(s.Opener.Opened);
        Assert.DoesNotContain(s.Console.Output, line => line.StartsWith("1: " + name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownIdentityAfterConfirmationIsSkipped()
    {
        var s = new Setup(); s.Reader.Get = n => n < 3 ? [Self, Alpha] : [Self, Alpha with { StartTime = null }]; s.Answers("1", "s");
        var result = await s.Run();
        Assert.Empty(s.Opener.Opened); Assert.Contains("alpha PID 42: identidad cambiada o desconocida; omitido.", result.Log);
    }

    [Fact]
    public async Task OwnProcessGroupIsNotOffered()
    {
        var s = new Setup(); s.Groups.Add(Group("other", 900)); s.Reader.Get = _ => [new("other", 999, Started), Alpha];
        s.Answers("2"); var result = await s.Run();
        Assert.Equal(2, result.ExitCode); Assert.Empty(s.Opener.Opened);
        Assert.DoesNotContain(s.Console.Output, line => line.StartsWith("1: other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingOwnNameFailsClosed()
    {
        var s = new Setup(); s.Reader.Get = _ => [Alpha]; s.Answers("1", "s");
        var result = await s.Run();
        Assert.Equal(1, result.ExitCode); Assert.Empty(s.Opener.Opened);
        Assert.Contains("Nombre del proceso propio desconocido; no se puede seleccionar procesos.", result.Log);
    }

    [Fact]
    public async Task SnapshotGroupsAreOrderedByEstimatedMemory()
    {
        var s = new Setup(); s.Groups.Add(Group("beta", 200)); s.Answers("");
        var result = await s.Run("--top", "1");
        Assert.Equal(0, result.ExitCode); Assert.Contains("1: beta / 1 instancias / 200.0 MB estimados", s.Console.Output);
        Assert.DoesNotContain(s.Console.Output, line => line.StartsWith("2: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IdentityReaderExceptionAfterConfirmationIsSkipped()
    {
        var s = new Setup(); s.Reader.Get = n => n == 3 ? throw new InvalidOperationException() : [Self, Alpha]; s.Answers("1", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Empty(s.Opener.Opened);
        Assert.Contains("alpha PID 42: no se pudo verificar o cerrar; omitido.", result.Log);
    }

    [Fact]
    public async Task IdentityReadFailureBeforeConfirmationOmitsGroup()
    {
        var s = new Setup(); s.Reader.Get = n => n == 2 ? throw new InvalidOperationException() : [Self, Alpha]; s.Answers("1", "s");
        var result = await s.Run();
        Assert.Equal(0, result.ExitCode); Assert.Empty(s.Opener.Opened);
        Assert.Contains("alpha: identidad desconocida; omitido.", result.Log);
    }

}
