using FragForge.Cli;
using FragForge.Core.Cs2Profile;
using Xunit;

namespace FragForge.Tests.Cli;

/// <summary>
/// Pruebas de la lógica del comando sin tocar la máquina: se ejercitan las funciones puras del CLI
/// (<c>ParseFpsCap</c>, <c>SteamRootFor</c>) y la consola simulada. El export real se prueba en
/// <c>Cs2ProfileExporterTests</c> con una instalación ficticia en carpeta temporal.
/// </summary>
public sealed class Cs2ProfileCommandTests
{
    /// <summary>Consola simulada: devuelve respuestas preparadas y captura lo escrito.</summary>
    private sealed class FakePrompt(params string?[] answers) : IConsolePrompt
    {
        private readonly Queue<string?> _answers = new(answers);
        public List<string> Output { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Questions { get; } = [];

        public void WriteLine(string text) => Output.Add(text);
        public void WriteWarning(string text) => Warnings.Add(text);

        public string? Ask(string question)
        {
            Questions.Add(question);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }

        public bool? Confirm(string question)
        {
            var answer = Ask(question);
            if (answer is null) return null;
            return answer.Trim().Equals("s", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Vocabulary_IsTheClosedListOfThreeVerbs()
    {
        Assert.Equal(new[] { "export", "import", "restore-autoexec" }, Cs2ProfileCommands.Verbs);
    }

    [Theory]
    [InlineData(null, FpsCapMode.SinTope)]
    [InlineData("", FpsCapMode.SinTope)]
    [InlineData("sin-tope", FpsCapMode.SinTope)]
    [InlineData("240", FpsCapMode.Fijo)]
    [InlineData("refresco", FpsCapMode.DerivadoDelRefresco)]
    [InlineData("refresco:0.5", FpsCapMode.DerivadoDelRefresco)]
    public void ParseFpsCap_RecognizesEveryForm(string? raw, FpsCapMode expectedMode)
    {
        Assert.Equal(expectedMode, Cs2ProfileCommands.ParseFpsCap(raw).Mode);
    }

    [Fact]
    public void ParseFpsCap_ReadsTheNumbers()
    {
        // "240" -> fijo 240; "refresco" -> factor 1.0; "refresco:0.5" -> factor 0.5.
        Assert.Equal(240, Cs2ProfileCommands.ParseFpsCap("240").FixedFps);
        Assert.Equal(1.0, Cs2ProfileCommands.ParseFpsCap("refresco").RefreshFactor!.Value, 10);
        Assert.Equal(0.5, Cs2ProfileCommands.ParseFpsCap("refresco:0.5").RefreshFactor!.Value, 10);
        // Y los campos que no tocan quedan nulos: "refresco" no arrastra un FixedFps.
        Assert.Null(Cs2ProfileCommands.ParseFpsCap("refresco").FixedFps);
        Assert.Null(Cs2ProfileCommands.ParseFpsCap("240").RefreshFactor);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("refresco:0")]
    [InlineData("refresco:-1")]
    public void ParseFpsCap_RejectsNonsense(string raw)
    {
        Assert.Throws<ArgumentException>(() => Cs2ProfileCommands.ParseFpsCap(raw));
    }

    [Fact]
    public void SteamRoot_IsFoundByWalkingUpToSteamApps()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-steam", "Steam");
        var exe = Path.Combine(root, "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin", "win64", "cs2.exe");

        Assert.Equal(root, Cs2ProfileCommands.SteamRootFor(exe));
        Assert.Null(Cs2ProfileCommands.SteamRootFor(null));
        // Sin steamapps en la cadena no se inventa una raíz.
        Assert.Null(Cs2ProfileCommands.SteamRootFor(Path.Combine(Path.GetTempPath(), "suelto", "cs2.exe")));
    }

    [Fact]
    public void FakePrompt_AnswersWithYesOrNo()
    {
        Assert.True(new FakePrompt("s").Confirm("?"));
        Assert.False(new FakePrompt("n").Confirm("?"));
        Assert.Null(new FakePrompt().Confirm("?"));
    }
}
