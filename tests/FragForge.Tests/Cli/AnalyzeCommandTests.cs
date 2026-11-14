using FragForge.Cli;
using FragForge.Core.Analysis;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cli;

/// <summary>
/// Pruebas del comando <c>analyze</c>. Herméticas: leen CSV de un directorio temporal y escriben en
/// <see cref="StringWriter"/>. No lanzan PresentMon, no tocan servicios ni el registro, no piden admin.
/// </summary>
public sealed class AnalyzeCommandTests
{
    private const string Header260 =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    /// <summary>CSV real de 28 columnas: <paramref name="count"/> frames de <paramref name="ms"/> ms.</summary>
    private static string Csv(string app, string chain, int count, double ms)
    {
        var cells = Header260.Split(',');
        string Row(string value)
        {
            var values = Enumerable.Repeat("NA", cells.Length).ToArray();
            values[Array.IndexOf(cells, "Application")] = app;
            values[Array.IndexOf(cells, "SwapChainAddress")] = chain;
            values[Array.IndexOf(cells, "MsBetweenPresents")] = value;
            return string.Join(',', values);
        }
        var lines = new List<string> { Header260 };
        for (var i = 0; i < count; i++) lines.Add(Row(ms.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        return string.Join('\n', lines) + "\n";
    }

    private static ParsedArgs Args(params string[] argv) => CommandLineParser.Parse(argv);

    private static (int Code, string Out, string Err) Run(ParsedArgs parsed, params string[] argv)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = AnalyzeCommands.Analyze(parsed, argv, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void SingleCsv_ReportsEveryMetricWithItsFullNameAndUnit()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "captura.csv");
        // 20 frames de 10 ms: media 10, mediana 10, P99 10, ceros tirones.
        File.WriteAllText(path, Csv("cs2.exe", "0xABC", 20, 10.0));

        var argv = new[] { "analyze", path };
        var (code, output, error) = Run(Args(argv), argv);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("P99FrameTime", output, StringComparison.Ordinal);
        Assert.Contains("ms", output, StringComparison.Ordinal);
        Assert.Contains("Low1PercentAverage", output, StringComparison.Ordinal);
        Assert.Contains("FPS", output, StringComparison.Ordinal);
        Assert.Contains("Tirones", output, StringComparison.Ordinal);
        Assert.Contains("tirones por minuto", output, StringComparison.Ordinal);
        // Con 20 frames el peor 1 % es NULL y se dice POR QUÉ, no se pinta un 0.
        Assert.Contains("no disponible", output, StringComparison.Ordinal);
        Assert.Contains("se necesitan 1000", output, StringComparison.Ordinal);
        // Y el aviso del IC, que no decide el veredicto.
        Assert.Contains("no decide el veredicto", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NaAndMalformedRows_AreCountedInTheOutput()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "captura.csv");
        var cells = Header260.Split(',');
        var na = Enumerable.Repeat("NA", cells.Length).ToArray();
        na[Array.IndexOf(cells, "Application")] = "cs2.exe";
        na[Array.IndexOf(cells, "SwapChainAddress")] = "0xABC";
        // La celda de MsBetweenPresents queda en "NA".
        File.WriteAllText(path, Header260 + "\n" + string.Join(',', na) + "\ncs2.exe,0xABC\n" + Csv("cs2.exe", "0xABC", 3, 10.0).Split('\n')[1] + "\n");

        var argv = new[] { "analyze", path };
        var (code, output, _) = Run(Args(argv), argv);

        // Cálculo a mano: 1 fila NA + 1 truncada + 3 válidas -> NaFrames 1, MalformedLines 1, 3 frames.
        Assert.Equal(0, code);
        Assert.Contains("Filas NA en la columna principal: 1", output, StringComparison.Ordinal);
        Assert.Contains("líneas mal formadas: 1", output, StringComparison.Ordinal);
        Assert.Contains("Frames analizados: 3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFile_IsFailureWithMessage_NoException()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".csv");
        var argv = new[] { "analyze", missing };
        var (code, _, error) = Run(Args(argv), argv);

        Assert.Equal(1, code);
        Assert.Contains("No existe el archivo", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CsvWithoutThePrimaryColumn_IsFailureWithTheReason()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "otro.csv");
        File.WriteAllText(path, "Application,SwapChainAddress,MsBetweenDisplayChange\ncs2.exe,0x1,16.7\n");

        var argv = new[] { "analyze", path };
        var (code, _, error) = Run(Args(argv), argv);

        Assert.Equal(1, code);
        Assert.Contains("MsBetweenPresents", error, StringComparison.Ordinal);
        Assert.Contains("No se ha modificado nada", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_EmitsEnumsByName_AndTheFullStatistics()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "captura.csv");
        File.WriteAllText(path, Csv("cs2.exe", "0xABC", 20, 10.0));

        var argv = new[] { "analyze", path, "--json" };
        var (code, output, _) = Run(Args(argv), argv);

        Assert.Equal(0, code);
        using var document = System.Text.Json.JsonDocument.Parse(output);
        var stats = document.RootElement.GetProperty("run").GetProperty("series")[0].GetProperty("stats");
        // 20 frames de 10 ms: media 10, P99 10 (cálculo a mano: todos los valores son 10).
        Assert.Equal(10, stats.GetProperty("meanMs").GetDouble(), 9);
        Assert.Equal(10, stats.GetProperty("p99FrameTimeMs").GetDouble(), 9);
        // Los nulos se omiten (DefaultIgnoreCondition.WhenWritingNull): Low1Percent no aparece.
        Assert.False(stats.TryGetProperty("low1PercentAverageFps", out _));
    }

    [Fact]
    public void Versus_WithEnumsByName_InJson()
    {
        using var temp = new TempDirectory();
        var aPaths = new List<string>();
        var bPaths = new List<string>();
        // A: 4 corridas de 12.5 ms -> 80 FPS; B: 4 de 10.0 ms -> 100 FPS. 1200 frames (> 1000) => el peor 1 %
        // existe. Con series constantes el "conjunto" de cada condición es un único valor repetido, así que
        // 80 < 100 en las tres métricas y el veredicto debe ser Separado a favor de B.
        for (var i = 0; i < 4; i++)
        {
            var pa = Path.Combine(temp.Path, $"a{i}.csv");
            File.WriteAllText(pa, Csv("cs2.exe", "0xA", 1200, 12.5));
            aPaths.Add(pa);
            var pb = Path.Combine(temp.Path, $"b{i}.csv");
            File.WriteAllText(pb, Csv("cs2.exe", "0xB", 1200, 10.0));
            bPaths.Add(pb);
        }

        var argv = new[] { "analyze" }.Concat(aPaths).Append("--versus").Concat(bPaths).Append("--json").ToArray();
        var (code, output, error) = Run(Args(argv), argv);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        using var document = System.Text.Json.JsonDocument.Parse(output);
        var comparison = document.RootElement.GetProperty("comparison");
        // El enum viaja por NOMBRE de miembro, nunca como número (R-N).
        Assert.Equal("Mejora", comparison.GetProperty("overall").GetString());
        Assert.Equal("Separado", comparison.GetProperty("low1Percent").GetProperty("verdict").GetString());
        Assert.Equal("B", comparison.GetProperty("low1Percent").GetProperty("betterCondition").GetString());
    }

    [Fact]
    public void Versus_RejectsCsvWithSeveralSeries_InsteadOfPickingOne()
    {
        using var temp = new TempDirectory();
        // Dos swapchains en el mismo CSV: elegir una en silencio invalidaría el veredicto.
        var mixed = Path.Combine(temp.Path, "mixed.csv");
        var rowsOne = Csv("cs2.exe", "0x1", 50, 10.0);
        var rowsTwo = Csv("cs2.exe", "0x2", 50, 20.0);
        // Un solo encabezado: se le quitan las filas de la segunda llamada y se pegan tras las primeras.
        var mixedCsv = rowsOne + string.Join('\n', rowsTwo.Split('\n').Skip(1).Where(l => l.Length > 0)) + "\n";
        File.WriteAllText(mixed, mixedCsv);
        var a = Path.Combine(temp.Path, "a.csv");
        File.WriteAllText(a, Csv("cs2.exe", "0xA", 50, 10.0));

        var argv = new[] { "analyze", a, "--versus", mixed };
        var (code, _, error) = Run(Args(argv), argv);

        Assert.Equal(1, code);
        Assert.Contains("mezclan varias", error, StringComparison.Ordinal);
    }

    [Fact]
    public void WarmupSeconds_DropsTheFramesAndSaysHowMany()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "captura.csv");
        // 10 frames de 100 ms (= 1 s) y luego 5 de 20 ms.
        var cells = Header260.Split(',');
        var lines = new List<string> { Header260 };
        string Row(double ms)
        {
            var values = Enumerable.Repeat("NA", cells.Length).ToArray();
            values[Array.IndexOf(cells, "Application")] = "cs2.exe";
            values[Array.IndexOf(cells, "SwapChainAddress")] = "0xA";
            values[Array.IndexOf(cells, "MsBetweenPresents")] = ms.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(',', values);
        }
        for (var i = 0; i < 10; i++) lines.Add(Row(100.0));
        for (var i = 0; i < 5; i++) lines.Add(Row(20.0));
        File.WriteAllText(path, string.Join('\n', lines) + "\n");

        var argv = new[] { "analyze", path, "--warmup-seconds", "1" };
        var (code, output, _) = Run(Args(argv), argv);

        // Cálculo a mano: 10 frames de 100 ms cubren 1 s -> quedan 5 frames de 20 ms.
        Assert.Equal(0, code);
        Assert.Contains("Frames analizados: 5", output, StringComparison.Ordinal);
        Assert.Contains("Calentamiento descartado: 10 frames", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludePauses_IsReported()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "captura.csv");
        var cells = Header260.Split(',');
        var lines = new List<string> { Header260 };
        string Row(double ms)
        {
            var values = Enumerable.Repeat("NA", cells.Length).ToArray();
            values[Array.IndexOf(cells, "Application")] = "cs2.exe";
            values[Array.IndexOf(cells, "SwapChainAddress")] = "0xA";
            values[Array.IndexOf(cells, "MsBetweenPresents")] = ms.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(',', values);
        }
        for (var i = 0; i < 10; i++) lines.Add(Row(10.0));
        lines.Add(Row(2000.0));
        File.WriteAllText(path, string.Join('\n', lines) + "\n");

        var argv = new[] { "analyze", path, "--exclude-pauses" };
        var (code, output, _) = Run(Args(argv), argv);

        // Cálculo a mano: 11 frames, uno > 1000 ms -> pausa 1; queda 10 frames de 10 ms.
        Assert.Equal(0, code);
        Assert.Contains("Pausas (> 1000 ms): 1 · excluidas", output, StringComparison.Ordinal);
        Assert.Contains("Frames analizados: 10", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeWarmup_IsAUsageError()
    {
        var argv = new[] { "analyze", "x.csv", "--warmup-seconds", "-1" };
        Assert.Throws<ArgumentException>(() => AnalyzeCommands.Analyze(Args(argv), argv, new StringWriter(), new StringWriter()));
    }
}
