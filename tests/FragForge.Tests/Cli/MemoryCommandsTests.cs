using System.Globalization;
using FragForge.Actions.Platform;
using FragForge.Cli;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class MemoryCommandsTests
{
    private sealed class FakeWmi : IWmiQuery
    {
        public bool Fail { get; init; }
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Processes { get; init; } = [];
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Memory { get; init; } = [];
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(@"root\cimv2", scope);
            Assert.Null(where);
            if (Fail) throw new InvalidOperationException("detalle privado");
            if (className == "Win32_Process")
            {
                Assert.Equal(new[] { "Name", "ProcessId", "WorkingSetSize", "PrivatePageCount", "ExecutablePath" }, properties);
                return Task.FromResult(Processes);
            }
            Assert.Equal("Win32_PerfFormattedData_PerfOS_Memory", className);
            Assert.Equal(new[] { "AvailableMBytes", "CommittedBytes", "PercentCommittedBytesInUse" }, properties);
            return Task.FromResult(Memory);
        }
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    [Theory]
    [InlineData("CS2.EXE")]
    [InlineData("steam")]
    [InlineData("SteamWebHelper.exe")]
    [InlineData("steamservice")]
    [InlineData("gameoverlayui")]
    [InlineData("vgc")]
    [InlineData("vgk")]
    [InlineData("vgtray")]
    [InlineData("LGHUB_agent.exe")]
    [InlineData("System")]
    [InlineData("Idle")]
    [InlineData("Registry")]
    [InlineData("Memory Compression")]
    [InlineData("smss")]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("services")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    [InlineData("winlogon")]
    [InlineData("dwm")]
    [InlineData("explorer")]
    [InlineData("fontdrvhost")]
    [InlineData("audiodg")]
    [InlineData("sihost")]
    [InlineData("ctfmon")]
    [InlineData("taskhostw")]
    [InlineData("RuntimeBroker")]
    [InlineData("ShellExperienceHost")]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("SearchHost")]
    [InlineData("TextInputHost")]
    [InlineData("MsMpEng")]
    [InlineData("SecurityHealthService")]
    [InlineData("fragforge-app.EXE")]
    [InlineData("PRESENTMON64.exe")]
    [InlineData("")]
    public void ProtectedNames_AreRecognized(string name)
    {
        // Cada nombre corresponde a una entrada o familia protegida, independientemente del PID 42.
        Assert.Equal("protegido", ProtectedProcesses.Classify(name, 42).Classification);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void SmallPid_IsProtected(long pid)
    {
        // Todos los PID de la tabla son menores o iguales a 4.
        Assert.Equal("protegido", ProtectedProcesses.Classify("otro", pid).Classification);
    }

    [Fact]
    public void UnknownPid_IsConservativelyProtected_AndNormalNameIsNot()
    {
        // Un PID desconocido no permite descartar que sea <= 4; 5 sí.
        Assert.Equal("protegido", ProtectedProcesses.Classify("otro", null).Classification);
        Assert.Equal("no protegido", ProtectedProcesses.Classify("otro.exe", 5).Classification);
    }

    [Fact]
    public async Task Reader_GroupsOrdersConvertsAndPropagatesUnknown()
    {
        var wmi = new FakeWmi
        {
            Processes = [
                Row(("Name", "alpha.exe"), ("ProcessId", 10), ("WorkingSetSize", 104857600UL), ("PrivatePageCount", 52428800UL)),
                Row(("Name", "ALPHA.EXE"), ("ProcessId", 11), ("WorkingSetSize", 52428800UL), ("PrivatePageCount", 10485760UL)),
                Row(("Name", "cs2.exe"), ("ProcessId", 12), ("WorkingSetSize", 209715200UL), ("PrivatePageCount", null)),
                Row(("Name", "desconocido.exe"), ("ProcessId", 13), ("WorkingSetSize", null), ("PrivatePageCount", null)),
                Row(("Name", "DESCONOCIDO.exe"), ("ProcessId", 14), ("WorkingSetSize", 1048576UL), ("PrivatePageCount", 1048576UL))],
            Memory = [Row(("AvailableMBytes", 2048UL), ("CommittedBytes", 1073741824UL), ("PercentCommittedBytesInUse", 25UL))]
        };
        var result = await new ProcessMemoryReader(wmi).ReadAsync();
        // 209715200/1048576=200; (104857600+52428800)/1048576=150; (52428800+10485760)/1048576=60.
        Assert.Equal(3, result.Processes.Count);
        Assert.Equal("cs2", result.Processes[0].Name);
        Assert.Equal(200.0, result.Processes[0].WorkingSetMb);
        Assert.Equal("protegido", result.Processes[0].Classification);
        Assert.Null(result.Processes[0].PrivateMb);
        Assert.Equal("alpha", result.Processes[1].Name);
        Assert.Equal(2, result.Processes[1].Instances);
        Assert.Equal(150.0, result.Processes[1].WorkingSetMb);
        Assert.Equal(60.0, result.Processes[1].PrivateMb);
        Assert.Equal("no protegido", result.Processes[1].Classification);
        // Si una instancia carece de dato, no se presenta la suma parcial como total.
        Assert.Null(result.Processes[2].WorkingSetMb);
        Assert.Null(result.Processes[2].PrivateMb);
        Assert.Equal(2048.0, result.System!.AvailableMb);
        Assert.Equal(1024.0, result.System.CommittedMb); // 1073741824 / 1048576 = 1024.
        Assert.Equal(25.0, result.System.PercentCommittedBytesInUse);
    }

    [Fact]
    public void Group_WithOneProtectedPid_ProtectsWholeGroup()
    {
        var group = Assert.Single(ProcessMemoryGrouping.Group([
            new ProcessMemoryRow("otro.exe", 4, 1, 2, "protegido", "PID de sistema"),
            new ProcessMemoryRow("OTRO", 5, 3, 4, "no protegido", "no coincide") ]));
        // 1 + 3 = 4 MB; una instancia protegida protege el grupo completo.
        Assert.Equal(2, group.Instances);
        Assert.Equal(4.0, group.WorkingSetMb);
        Assert.Equal(6.0, group.PrivateMb);
        Assert.Equal("protegido", group.Classification);
        Assert.Equal("PID de sistema", group.Reason);
    }

    [Fact]
    public async Task MissingSystemFields_AreUnknownNotZero()
    {
        var result = await new ProcessMemoryReader(new FakeWmi { Memory = [Row()] }).ReadAsync();
        // La fila vacía tiene cero campos, no valores de medición cero.
        Assert.Null(result.System!.AvailableMb);
        Assert.Null(result.System.CommittedMb);
        Assert.Null(result.System.PercentCommittedBytesInUse);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("20", 20)]
    [InlineData("200", 200)]
    public void Top_AcceptsBounds(string text, int expected)
    {
        // Valores 1, 20 y 200 están dentro del intervalo inclusivo.
        Assert.True(MemoryCommands.TryParseTop(["--top", text], out var top));
        Assert.Equal(expected, top);
    }

    [Fact]
    public void Top_DefaultIsTwenty()
    {
        Assert.True(MemoryCommands.TryParseTop([], out var top));
        Assert.Equal(20, top); // Sin argumentos, el valor definido es 20.
    }

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData(" 20")]
    public async Task Top_InvalidIsUsageError(string value)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var command = new MemoryCommands(_ => throw new InvalidOperationException(), output, error);
        // Ningún valor en la tabla es un entero estricto entre 1 y 200.
        Assert.Equal(2, await command.RunAsync(["--top", value]));
        Assert.Equal(MemoryCommands.Usage + Environment.NewLine, error.ToString());
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("--top", "1", "--top", "20")]
    [InlineData("--otro", "1", "--top", "20")]
    public async Task DuplicateOrUnknown_IsUsageError(string a, string b, string c, string d)
    {
        var error = new StringWriter();
        var command = new MemoryCommands(_ => throw new InvalidOperationException(), new StringWriter(), error);
        // Cuatro argumentos no corresponden a la única opción permitida una sola vez.
        Assert.Equal(2, await command.RunAsync([a, b, c, d]));
        Assert.Equal(MemoryCommands.Usage + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task WmiFailure_IsSpanishAndDoesNotLeakException()
    {
        var error = new StringWriter();
        var command = new MemoryCommands(new ProcessMemoryReader(new FakeWmi { Fail = true }).ReadAsync, new StringWriter(), error);
        // Una consulta fallida produce exit 1, no un fallo sin capturar ni datos de la excepción.
        Assert.Equal(1, await command.RunAsync([]));
        Assert.Equal("No se pudo consultar la memoria mediante WMI; no se sabe el estado actual." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task Report_UsesInvariantNumbersTopAndExactFooter()
    {
        var output = new StringWriter();
        var snapshot = new MemorySnapshot(new SystemMemorySnapshot(1.5, null, 25), [
            new ProcessMemoryGroup("uno", 2, 100, null, "protegido", "nombre protegido"),
            new ProcessMemoryGroup("dos", 1, 50, null, "no protegido", "motivo")]);
        var command = new MemoryCommands(_ => Task.FromResult(snapshot), output, new StringWriter());
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-AR");
            // Top 1 muestra solo el primer grupo; 100 MB se representa 100.0 con punto invariante.
            Assert.Equal(0, await command.RunAsync(["--top", "1"]));
            Assert.Contains("Memoria disponible (MB): 1.5", output.ToString());
            Assert.Contains("Memoria comprometida (MB): no se sabe", output.ToString());
            Assert.Contains("uno / 2 / 100.0 / protegido", output.ToString());
            Assert.DoesNotContain("dos /", output.ToString());
            Assert.EndsWith("Esto es el working set actual, no una promesa de cuánta memoria se libera al cerrar. Esta versión no cierra nada." + Environment.NewLine, output.ToString());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
