using FragForge.Actions.Measurement;
using FragForge.Core.Abstractions;
using Xunit;

namespace FragForge.Tests.Measurement.Live;

public sealed class OrphanPresentMonDetectorTests
{
    private sealed class FakeWmi(params IReadOnlyDictionary<string, object?>[] rows) : IWmiQuery
    {
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className,
            IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal("Win32_Process", className);
            Assert.Equal(new[] { "Name", "ProcessId", "ExecutablePath", "CommandLine" }, properties);
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(rows);
        }
    }

    private static IReadOnlyDictionary<string, object?> Row(string name, int pid, string? path, string? command) =>
        new Dictionary<string, object?> { ["Name"] = name, ["ProcessId"] = pid, ["ExecutablePath"] = path, ["CommandLine"] = command };

    [Fact]
    public async Task Find_RequiresNamePathAndSession_NotesUnknown()
    {
        var approved = Path.GetFullPath("PresentMon.exe");
        var other = Path.GetFullPath("other/PresentMon.exe");
        var cmd = "PresentMon.exe --session_name FragForgeLive-12345678";
        var detector = new OrphanPresentMonDetector(new FakeWmi(
            Row("PresentMon.exe", 10, approved, cmd),
            Row("other.exe", 11, approved, cmd),
            Row("PresentMon.exe", 12, other, cmd),
            Row("PresentMon.exe", 13, approved, "PresentMon.exe --session_name Otra"),
            Row("PresentMon.exe", 14, null, cmd),
            Row("PresentMon.exe", 15, approved, null)), approved);
        var result = await detector.Find();
        // De seis filas solo la primera cumple los tres criterios; dos tienen datos desconocidos.
        var match = Assert.Single(result.Processes);
        Assert.Equal(10, match.ProcessId);
        Assert.Equal(approved, match.ExecutablePath);
        Assert.Equal("FragForgeLive-12345678", match.SessionName);
        Assert.Single(result.Notes);
        Assert.Contains("no se pudieron identificar", result.Notes[0]);
    }

    [Theory]
    [InlineData("--session_name FragForgeLive-12345678", "FragForgeLive-12345678")]
    [InlineData("--session_name \"FragForgeLive-abcdef12\"", "FragForgeLive-abcdef12")]
    [InlineData("--session_name Otro", null)]
    [InlineData("--other FragForgeLive-12345678", null)]
    public void ExtractSessionName_OnlyRecognizesFragForge(string command, string? expected)
    {
        // El valor esperado corresponde al argumento explícito; otro prefijo o flag no coincide.
        Assert.Equal(expected, OrphanPresentMonDetector.ExtractSessionName(command));
    }

    [Fact]
    public void Advice_ContainsKnownValuesWithoutClaimingEtwCleanup()
    {
        var text = OrphanPresentMonAdvice.Describe(new OrphanPresentMon(42, "ruta.exe", "comando", "FragForgeLive-12345678"));
        Assert.Equal("PresentMon PID 42; ruta: ruta.exe; sesión: FragForgeLive-12345678. Cerralo a mano desde el Administrador de tareas. Se observó una vez en una PC que cerrar PresentMon no cerró la sesión ETW; match ofrece cerrar sesiones FragForgeLive- al iniciar.", text);
    }
}
