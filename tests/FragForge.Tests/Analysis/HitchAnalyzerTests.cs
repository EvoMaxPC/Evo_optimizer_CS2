using FragForge.Core.Analysis;
using Xunit;

namespace FragForge.Tests.Analysis;

public sealed class HitchAnalyzerTests
{
    [Fact]
    public void Hitch_IsThreeTimesTheMedian()
    {
        // 18 frames de 10 ms y 2 de 40 ms. Mediana = 10; umbral = 3 * 10 = 30 ms; los dos de 40 son tirones.
        var values = Enumerable.Repeat(10.0, 18).Concat(Enumerable.Repeat(40.0, 2)).ToArray();

        var (report, analyzed) = HitchAnalyzer.Analyze(values);

        Assert.Equal(3.0, HitchAnalyzer.HitchFactor);
        Assert.Equal(10.0, report.MedianMs, 10);
        Assert.Equal(30.0, report.ThresholdMs, 10);
        Assert.Equal(2, report.HitchCount);
        // 2 / 20 * 100 = 10 %.
        Assert.Equal(10.0, report.HitchPercent, 10);
        Assert.Equal(40.0, report.LongestFrameMs!.Value, 10);
        // Duración = 18*10 + 2*40 = 180 + 80 = 260 ms. Tirones/min = 2 / (260/60000) = 2 * 60000/260 = 461.538...
        Assert.Equal(2 * 60000.0 / 260.0, report.HitchesPerMinute!.Value, 9);
        Assert.Equal(20, analyzed.Count);
    }

    [Fact]
    public void Pauses_AreReportedSeparately_AndIncludedByDefault()
    {
        // 10 frames de 10 ms y uno de 5000 ms: el de 5000 es pausa (>1000) y también tirón (>30).
        var values = Enumerable.Repeat(10.0, 10).Append(5000.0).ToArray();

        var (report, _) = HitchAnalyzer.Analyze(values);

        Assert.Equal(5000.0, HitchAnalyzer.PauseThresholdMs, 10);
        Assert.Equal(1, report.PauseCount);
        Assert.False(report.PausesExcluded);
        // Mediana de 11 valores ordenados = índice 5 (base 0) = 10 ms. Umbral = 30 ms. Solo el de 5000 lo supera.
        Assert.Equal(1, report.HitchCount);
        Assert.Contains(report.Notes, n => n.Contains("INCLUIDAS", StringComparison.Ordinal));
    }

    [Fact]
    public void ExcludePauses_DropsThemButSaysSo()
    {
        var values = Enumerable.Repeat(10.0, 10).Append(5000.0).ToArray();

        var (report, analyzed) = HitchAnalyzer.Analyze(values, excludePauses: true);

        // Cálculo a mano: 11 frames, 1 pausa -> quedan 10 frames de 10 ms, mediana 10, umbral 30, 0 tirones.
        Assert.Equal(10, analyzed.Count);
        Assert.Equal(1, report.PauseCount);
        Assert.True(report.PausesExcluded);
        Assert.Equal(0, report.HitchCount);
        Assert.Equal(10.0, report.MedianMs, 10);
        Assert.Contains(report.Notes, n => n.Contains("excluyeron", StringComparison.Ordinal));
    }

    [Fact]
    public void Warmup_DropsWholeFramesAndReportsHowMany()
    {
        // 10 frames de 100 ms = 1 s exacto de calentamiento, luego 5 frames de 20 ms.
        var values = Enumerable.Repeat(100.0, 10).Concat(Enumerable.Repeat(20.0, 5)).ToArray();

        var (report, analyzed) = HitchAnalyzer.Analyze(values, warmupSeconds: 1.0);

        // Cálculo a mano: los 10 primeros suman 1000 ms >= 1000 ms -> se descartan 10 frames; quedan 5 de 20 ms.
        Assert.Equal(10, report.WarmupFramesDropped);
        Assert.Equal(5, analyzed.Count);
        Assert.Equal(20.0, report.MedianMs, 10);
        // Umbral = 3 * 20 = 60; ningún frame de 20 lo supera -> 0 tirones.
        Assert.Equal(0, report.HitchCount);
        Assert.Contains(report.Notes, n => n.Contains("Calentamiento", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultWarmup_IsZero_AndChangesNothing()
    {
        var values = Enumerable.Repeat(10.0, 20).ToArray();

        var (report, analyzed) = HitchAnalyzer.Analyze(values);

        Assert.Equal(0.0, HitchAnalyzer.DefaultWarmupSeconds, 12);
        Assert.Equal(0, report.WarmupFramesDropped);
        Assert.Equal(20, analyzed.Count);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void EmptySeries_DoesNotThrow()
    {
        var (report, analyzed) = HitchAnalyzer.Analyze([], warmupSeconds: 1.0);

        Assert.Empty(analyzed);
        Assert.Equal(0, report.AnalyzedFrames);
        Assert.Null(report.HitchesPerMinute);
        Assert.Null(report.LongestFrameMs);
    }
}
