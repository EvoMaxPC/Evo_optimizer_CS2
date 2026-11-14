using FragForge.Core.Experiments;
using Xunit;

namespace FragForge.Tests.Experiments;

public sealed class FrameMetricsTests
{
    private static FrameSeries Series(IEnumerable<double> frameTimes, string mode = "Composed: Flip") => new()
    {
        FrameTimesMs = frameTimes.ToArray(),
        PresentModes = Enumerable.Repeat(mode, frameTimes.Count()).ToArray()
    };

    [Fact]
    public void Uniform200Hz_ProducesExactMetrics()
    {
        var series = Series(Enumerable.Repeat(5.0, 2000));
        var metrics = FrameMetricsCalculator.Compute(series, warmupSeconds: 0);
        Assert.Equal(200.0, metrics.AvgFps!.Value, 6);
        Assert.Equal(200.0, metrics.P1Low!.Value, 6);
        Assert.Equal(5.0, metrics.FrameTimeP50!.Value, 6);
        Assert.Equal(5.0, metrics.FrameTimeP99!.Value, 6);
        Assert.Equal(0.0, metrics.JitterMs!.Value, 6);
        Assert.Equal(0.0, metrics.FrameTimeCv!.Value, 6);
        Assert.Equal(0.0, metrics.StutterPct!.Value, 6);
    }

    [Fact]
    public void WarmupDiscardsTheWorstFirstSecond()
    {
        // 1 s de 50 ms (malo) + 9 s de 5 ms (bueno). Con warmup=1 s el P50 debe ser 5.
        var frameTimes = Enumerable.Repeat(50.0, 20).Concat(Enumerable.Repeat(5.0, 1800));
        var metrics = FrameMetricsCalculator.Compute(Series(frameTimes), warmupSeconds: 1.0);
        Assert.Equal(5.0, metrics.FrameTimeP50!.Value, 6);
        Assert.Equal(1.0, metrics.WarmupSkippedSeconds, 6);
    }

    [Fact]
    public void EmptySeries_ReturnsAllUnknown_NeverZero()
    {
        var metrics = FrameMetricsCalculator.Compute(new FrameSeries(), warmupSeconds: 10);
        Assert.Null(metrics.AvgFps);
        Assert.Null(metrics.P1Low);
        Assert.Null(metrics.JitterMs);
        Assert.Contains("all", metrics.MissingMetrics);
    }

    [Fact]
    public void SingleFrame_DoesNotCrashAndReportsP50()
    {
        var metrics = FrameMetricsCalculator.Compute(Series([5.0]), warmupSeconds: 0);
        Assert.Equal(5.0, metrics.FrameTimeP50!.Value);
        Assert.Equal(1, metrics.FramesUsed);
        Assert.Null(metrics.StutterPct);
    }

    [Fact]
    public void MissingPresentModeColumn_IsUnknown()
    {
        var metrics = FrameMetricsCalculator.Compute(new FrameSeries { FrameTimesMs = Enumerable.Repeat(5.0, 500).ToArray() }, 0);
        Assert.Null(metrics.PresentModePrimary);
        Assert.Contains("presentMode", metrics.MissingMetrics);
    }

    [Fact]
    public void PresentModeDistribution_ReportsPercentages()
    {
        var modes = Enumerable.Repeat("Independent Flip", 750).Concat(Enumerable.Repeat("Composed: Flip", 250)).ToArray();
        var metrics = FrameMetricsCalculator.Compute(new FrameSeries { FrameTimesMs = Enumerable.Repeat(5.0, 1000).ToArray(), PresentModes = modes }, 0);
        Assert.Equal("Independent Flip", metrics.PresentModePrimary);
        Assert.Equal(75.0, metrics.PresentModeDistribution["Independent Flip"], 6);
        Assert.Equal(25.0, metrics.PresentModeDistribution["Composed: Flip"], 6);
    }

    [Fact]
    public void StutterPercent_CountsFramesAboveTwiceTheRollingMedian()
    {
        var frameTimes = Enumerable.Repeat(5.0, 500).ToList();
        for (var i = 100; i < 105; i++) frameTimes[i] = 20.0; // 4x la mediana
        var stutter = FrameMetricsCalculator.StutterPercent(frameTimes);
        Assert.NotNull(stutter);
        Assert.InRange(stutter!.Value, 0.8, 1.2); // 5 de 500
    }

    [Fact]
    public void CpuBusy_MisalignedLength_IsTreatedAsMissing()
    {
        var metrics = FrameMetricsCalculator.Compute(new FrameSeries { FrameTimesMs = Enumerable.Repeat(5.0, 500).ToArray(), CpuBusyMs = Enumerable.Repeat<double?>(3.0, 10).ToArray() }, 0);
        Assert.Null(metrics.CpuBusyP95);
        Assert.Contains("cpuBusyP95", metrics.MissingMetrics);
    }

    [Fact]
    public void CpuBusy_Aligned_ProducesP95()
    {
        var cpu = Enumerable.Range(0, 500).Select(i => (double?)(i % 10)).ToArray();
        var metrics = FrameMetricsCalculator.Compute(new FrameSeries { FrameTimesMs = Enumerable.Repeat(5.0, 500).ToArray(), CpuBusyMs = cpu }, 0);
        Assert.NotNull(metrics.CpuBusyP95);
        Assert.InRange(metrics.CpuBusyP95!.Value, 8.5, 9.5);
    }
}
