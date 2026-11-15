using FragForge.Cli;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class MemoryCloseCommandTests
{
    private static readonly DateTimeOffset Start = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private sealed class ConsoleFake : ICloseConsole
    {
        public bool IsInputRedirected { get; set; }
        public Queue<string?> Answers { get; } = new();
        public List<string> Lines { get; } = [];
        public Task<string?> ReadLineAsync() => Task.FromResult(Answers.Count == 0 ? null : Answers.Dequeue());
        public Task WriteLineAsync(string text) { Lines.Add(text); return Task.CompletedTask; }
    }
    private sealed class LogFake : IMemoryCloseLog
    {
        public bool Reserved { get; private set; }
        public bool Fail { get; set; }
        public List<string> Text { get; } = [];
        public Task ReserveAsync(string text, CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException();
            Reserved = true; Text.Add(text); return Task.CompletedTask;
        }
        public Task AppendAsync(string text, CancellationToken cancellationToken) { if (Fail) throw new IOException(); Text.Add(text); return Task.CompletedTask; }
    }
    private sealed class IdentityFake : ICloseIdentityReader
    {
        public int Calls { get; private set; }
        public bool ChangeAfterConfirmation { get; set; }
        public Task<IReadOnlyList<CloseIdentity>?> ReadAsync(CancellationToken cancellationToken)
        {
            Calls++;
            IReadOnlyList<CloseIdentity> values = [new("fragforge", 999, Start), new("alpha", 42, ChangeAfterConfirmation && Calls >= 3 ? Start.AddSeconds(1) : Start)];
            return Task.FromResult<IReadOnlyList<CloseIdentity>?>(values);
        }
    }
    private sealed class ProcessFake : ICloseProcess
    {
        public string? Name => "alpha";
        public long? Pid => 42;
        public DateTimeOffset? StartTime => Start;
        public int Closes { get; private set; }
        public int Kills { get; private set; }
        public int Disposes { get; private set; }
        public bool CloseMainWindow() { Closes++; return false; }
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(false);
        public void Kill() => Kills++;
        public void Dispose() => Disposes++;
    }
    private sealed class OpenerFake : ICloseProcessOpener
    {
        public int Opens { get; private set; }
        public Func<bool>? IsReserved { get; set; }
        public bool ReservedWhenOpened { get; private set; }
        public ProcessFake Process { get; } = new();
        public ICloseProcess Open(long pid) { Opens++; ReservedWhenOpened = IsReserved?.Invoke() == true; return Process; }
    }
    private sealed class Fixture
    {
        public ConsoleFake Console { get; } = new();
        public LogFake Log { get; } = new();
        public IdentityFake Identity { get; } = new();
        public OpenerFake Opener { get; } = new();
        public StringWriter Error { get; } = new();
        public void Answers(params string[] answers) { foreach (var answer in answers) Console.Answers.Enqueue(answer); }
        public Task<int> Run(params string[] args)
        {
            Task<MemoryCloseResult> RunLogic(string[] arguments, Func<string, CancellationToken, Task> reserve, CancellationToken ct)
            {
                var group = new ProcessMemoryGroup("alpha", 1, 100, 90, "no protegido", "test");
                var logic = new MemoryCloseLogic(_ => Task.FromResult(new MemorySnapshot(null, [group])), Identity, Opener,
                    _ => Task.FromResult<double?>(1000), (_, _) => Task.CompletedTask,
                    (_, _, _) => Task.FromResult(true), Console, () => Start, 999, reserve);
                return logic.RunAsync(arguments, ct);
            }
            Opener.IsReserved = () => Log.Reserved;
            return new MemoryCloseCommand(RunLogic, Log, Error).RunAsync(args);
        }
    }

    [Fact]
    public async Task ConfirmedFlowReservesLogBeforeOpeningAndAppendsResult()
    {
        var f = new Fixture(); f.Answers("1", "s", "s");
        Assert.Equal(0, await f.Run());
        Assert.Equal(1, f.Opener.Opens); Assert.Equal(1, f.Opener.Process.Closes); Assert.Equal(1, f.Opener.Process.Kills); Assert.Equal(1, f.Opener.Process.Disposes);
        Assert.True(f.Opener.ReservedWhenOpened);
        Assert.Collection(f.Log.Text,
            before => Assert.Contains("Identidad seleccionada: alpha PID 42", before),
            after => Assert.Contains("alpha PID 42: cierre forzado solicitado.", after));
    }

    [Fact]
    public async Task EmptyEntryReturnsSuccessWithoutOpening()
    {
        var f = new Fixture(); f.Answers(""); Assert.Equal(0, await f.Run());
        Assert.Equal(0, f.Opener.Opens); Assert.Single(f.Log.Text);
    }

    [Fact]
    public async Task InvalidSelectionReturnsUsageWithoutOpening()
    {
        var f = new Fixture(); f.Answers("2"); Assert.Equal(2, await f.Run());
        Assert.Equal(0, f.Opener.Opens); Assert.Contains("Selección inválida; no se cerró nada.", f.Log.Text[0]);
    }

    [Fact]
    public async Task RedirectedInputReturnsUsageWithoutOpening()
    {
        var f = new Fixture(); f.Console.IsInputRedirected = true;
        Assert.Equal(2, await f.Run()); Assert.Equal(0, f.Opener.Opens);
        Assert.Contains("Entrada redirigida: se requiere una consola interactiva.", f.Console.Lines);
    }

    [Fact]
    public async Task DecliningForceNeverKills()
    {
        var f = new Fixture(); f.Answers("1", "s", "n"); Assert.Equal(0, await f.Run());
        Assert.Equal(1, f.Opener.Process.Closes); Assert.Equal(0, f.Opener.Process.Kills); Assert.Equal(1, f.Opener.Process.Disposes);
    }

    [Fact]
    public async Task ChangedIdentityIsRecordedWithoutOpening()
    {
        var f = new Fixture(); f.Identity.ChangeAfterConfirmation = true; f.Answers("1", "s");
        Assert.Equal(0, await f.Run()); Assert.Equal(0, f.Opener.Opens);
        Assert.Contains("alpha PID 42: identidad cambiada o desconocida; omitido.", f.Log.Text[1]);
    }

    [Fact]
    public async Task LogReservationFailurePreventsActionsAndReturnsFailure()
    {
        var f = new Fixture(); f.Log.Fail = true; f.Answers("1", "s");
        Assert.Equal(1, await f.Run()); Assert.Equal(0, f.Opener.Opens);
        Assert.Contains("Aviso: no se pudo guardar el registro de cierre.", f.Error.ToString());
    }

    [Fact]
    public async Task UnexpectedExceptionStillWritesLog()
    {
        var log = new LogFake(); var error = new StringWriter();
        var command = new MemoryCloseCommand((_, _, _) => throw new InvalidOperationException(), log, error);
        Assert.Equal(1, await command.RunAsync([]));
        Assert.Single(log.Text); Assert.Equal("Error inesperado; el estado puede ser desconocido.\n", log.Text[0]);
    }

    [Fact]
    public void BuildServerStartInfoUsesExactArguments()
    {
        var info = BuildServerShutdown.CreateStartInfo(["build-server", "shutdown"]);
        Assert.Equal("dotnet", info.FileName); Assert.False(info.UseShellExecute);
        Assert.Equal(new[] { "build-server", "shutdown" }, info.ArgumentList.ToArray());
    }

    [Fact]
    public void WmiDatePreservesOffsetAndMicroseconds()
    {
        // 03:04:05.123456 at UTC-03 is 06:04:05.123456 UTC; one microsecond is 10 ticks.
        var expected = new DateTimeOffset(2025, 1, 2, 6, 4, 5, TimeSpan.Zero).AddTicks(1234560);
        Assert.Equal(expected, WmiCloseIdentityReader.ParseCreationDate("20250102030405.123456-180"));
        Assert.Null(WmiCloseIdentityReader.ParseCreationDate("20250102030405.******+000"));
    }

    private sealed class WmiFake : IWmiQuery
    {
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(@"root\cimv2", scope); Assert.Equal("Win32_Process", className);
            Assert.Equal(new[] { "Name", "ProcessId", "CreationDate" }, properties); Assert.Null(where);
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = [new Dictionary<string, object?> { ["Name"] = "alpha.exe", ["ProcessId"] = 42U, ["CreationDate"] = "20250102030405.000000+000" }];
            return Task.FromResult(rows);
        }
    }

    [Fact]
    public async Task WmiReaderUsesOnlyIdentityFields()
    {
        var reader = new WmiCloseIdentityReader(new WmiFake());
        var rows = await reader.ReadAsync(CancellationToken.None);
        var row = Assert.Single(rows!);
        Assert.Equal("alpha.exe", row.Name); Assert.Equal(42L, row.Pid); Assert.Equal(Start, row.StartTime);
    }
}
