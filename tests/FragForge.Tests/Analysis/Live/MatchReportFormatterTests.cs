using FragForge.Core.Analysis;
using FragForge.Core.Analysis.Live;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

public sealed class MatchReportFormatterTests
{
    private static readonly DateTimeOffset Time = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Partial_FormatsMetricsAndCounters()
    {
        var stats = new FrameStatistics { MeanMs = 20, MinMs = 10, MaxMs = 50, P99FrameTimeMs = 40, Low1PercentAverageFps = 25,
            UnavailableReasons = new Dictionary<string, string> { [nameof(FrameStatistics.Low01PercentAverageFps)] = "insuficiente: 1000 frames, se necesitan 10000." } };
        var view = new LiveViewSnapshot("total", null, 1000, 20, stats, new HitchReport { HitchCount = 2, PauseCount = 1, LongestFrameMs = 50 }, ["nota propia"]);
        var report = MatchReportFormatter.Format(new MatchReportInput { Kind = MatchReportKind.Partial, GeneratedAtLocal = Time, Mode = "partida", SessionStateText = "corriendo", Total = view,
            Counters = new LiveIngestCounters(true, true, true, 1000, 2, 3, 4, 5, 6), SecondsSinceLastFrame = 2.5 });
        // 1000/20=50 FPS, 1000/50=20, 1000/10=100; 1000 filas válidas y 3 NA.
        Assert.Contains("VISTA PARCIAL: la medición sigue", report);
        Assert.Contains("2026-01-02 03:04:05 +00:00", report);
        Assert.Contains("FPS promedio: 50.0 FPS", report);
        Assert.Contains("FPS mínimo: 20.0 FPS", report);
        Assert.Contains("FPS máximo: 100.0 FPS (el máximo puede estar inflado por un solo frame corto)", report);
        Assert.Contains("1% bajo: 25.0 FPS", report);
        Assert.Contains("P99 frametime: 40.00 ms", report);
        Assert.Contains("insuficiente: 1000 frames, se necesitan 10000.", report);
        Assert.Contains("Tirones: 2", report);
        Assert.Contains("Pausas: 1", report);
        Assert.Contains("NA: 3", report);
        Assert.Contains("Sin frames hace 2.5 s", report);
        Assert.Contains("nota propia", report);
    }

    [Theory]
    [InlineData(true, "sí")]
    [InlineData(false, "no")]
    [InlineData(null, "no se sabe")]
    public void Final_NullMetricsAndWindowCoverage(bool? covers, string expected)
    {
        var view = new LiveViewSnapshot("total", null, 0, null, new FrameStatistics(), null, []);
        var report = MatchReportFormatter.Format(new MatchReportInput { Kind = MatchReportKind.Final, GeneratedAtLocal = Time, Total = view,
            SecondsSinceLastFrame = 0, Environment = new MatchEnvironment { WindowCoversMonitor = covers } });
        // Los valores nulos no son cero; el tiempo 0 no activa el aviso.
        Assert.Contains("RESUMEN FINAL", report);
        Assert.Contains("FPS promedio: no se sabe", report);
        Assert.Contains("tirones: no disponible", report);
        Assert.Contains("La ventana cubre el monitor: " + expected, report);
        Assert.DoesNotContain("0.0 FPS", report);
        Assert.DoesNotContain("Sin frames hace", report);
        Assert.DoesNotContain("pantalla completa", report);
    }

    [Fact]
    public void ConfigChanges_OnlyShownWhenDifferent()
    {
        var sheet = new MatchConfigSheet { LaunchOptions = "a" };
        var same = MatchReportFormatter.Format(new MatchReportInput { Sheet = sheet, SheetAtEnd = sheet, GeneratedAtLocal = Time });
        var different = MatchReportFormatter.Format(new MatchReportInput { Sheet = sheet, SheetAtEnd = sheet with { LaunchOptions = "b" }, GeneratedAtLocal = Time });
        // a -> a no cambia; a -> b produce una diferencia.
        Assert.DoesNotContain("LA CONFIGURACIÓN CAMBIÓ", same);
        Assert.Contains("LA CONFIGURACIÓN CAMBIÓ", different);
        Assert.Contains("LaunchOptions: cambió de a a b", different);
        Assert.Contains("Resolución configurada: no se sabe", different);
    }
    [Fact]
    public void InsufficientLow1_UsesExactReason()
    {
        var stats = FrameStatisticsCalculator.Compute(Enumerable.Repeat(10.0, 900).ToArray());
        var view = new LiveViewSnapshot("total", null, 900, null, stats, null, []);
        var report = MatchReportFormatter.Format(new MatchReportInput { Total = view, GeneratedAtLocal = Time });
        // 900 < 1000: el calculador da null y el motivo exacto; no es 0 FPS.
        Assert.Contains("1% bajo: insuficiente: 900 frames, se necesitan 1000.", report);
        Assert.DoesNotContain("1% bajo: 0.0 FPS", report);
    }

    [Fact]
    public void Environment_LabelsResolutionComparisonWithoutChangingValue()
    {
        var report = MatchReportFormatter.Format(new MatchReportInput { Environment = new MatchEnvironment { ResolutionComparisonText = "no se sabe" }, GeneratedAtLocal = Time });
        // Un valor desconocido se presenta como tal, no como cero.
        Assert.Contains("\nComparación de resolución: no se sabe\n", report);
    }

    [Fact]
    public void Environment_LabelsEachDisplayWithoutChangingValues()
    {
        var report = MatchReportFormatter.Format(new MatchReportInput { Environment = new MatchEnvironment { DisplayModes = ["DISPLAY1 1024x768@180Hz", "DISPLAY2 640x480@60Hz"] }, GeneratedAtLocal = Time });
        // Dos pantallas activas producen dos líneas con sus valores originales.
        Assert.Contains("\nPantallas activas: DISPLAY1 1024x768@180Hz\n", report);
        Assert.Contains("\nPantallas activas: DISPLAY2 640x480@60Hz\n", report);
    }

    [Theory]
    [InlineData(MatchReportKind.Partial)]
    [InlineData(MatchReportKind.Final)]
    public void TotalNote_AppearsExactlyOnceAndOnlyUnderTotal(MatchReportKind kind)
    {
        var view = new LiveViewSnapshot("total", null, 1, 0, new FrameStatistics(), null, []);
        var report = MatchReportFormatter.Format(new MatchReportInput { Kind = kind, Total = view, Last30 = view, Segment = view, GeneratedAtLocal = Time });
        const string note = "El total incluye los frames acumulados de la serie mostrada desde el inicio de la medición (puede incluir menús y carga de mapa); para jugar mirá el segmento o los últimos 30 s.";
        var lines = report.Split('\n');
        // Hay tres vistas y la nota aparece una vez, justo después de la cabecera Total.
        Assert.Equal(1, lines.Count(line => line == note));
        Assert.Equal("Total:", lines[Array.IndexOf(lines, note) - 1]);
        Assert.DoesNotContain(note, lines.Skip(Array.IndexOf(lines, "Últimos 30 s:")));
        Assert.DoesNotContain(note, lines.Skip(Array.IndexOf(lines, "Segmento:")));
    }

    [Theory]
    [InlineData(1, "hay 1 serie más; esta vista muestra solo la serie con más frames acumulados")]
    [InlineData(2, "hay 2 series más; esta vista muestra solo la serie con más frames acumulados")]
    public void SeriesNote_ExplainsUncombinedSeriesInEachView(int extraSeries, string expected)
    {
        var accumulator = new LiveFrameAccumulator();
        accumulator.Add(new LiveFrame("cs2.exe", "principal", 10, 0));
        accumulator.Add(new LiveFrame("cs2.exe", "principal", 10, 10));
        for (var i = 0; i < extraSeries; i++) accumulator.Add(new LiveFrame("cs2.exe", i.ToString(System.Globalization.CultureInfo.InvariantCulture), 20, 0));
        accumulator.MarkStart();
        accumulator.Add(new LiveFrame("cs2.exe", "principal", 10, 20));
        accumulator.MarkEnd();
        var report = MatchReportFormatter.Format(new MatchReportInput { Total = accumulator.GetTotal(), Last30 = accumulator.GetLastWindow(), Segment = accumulator.GetSegment(), GeneratedAtLocal = Time });
        // La serie principal tiene 3 frames; hay 1 o 2 series adicionales de 1 frame cada una.
        Assert.Equal(3, report.Split('\n').Count(line => line == expected));
        Assert.DoesNotContain("hay 1 series más\n", report);
        Assert.DoesNotContain("hay 2 series más\n", report);
    }

}
