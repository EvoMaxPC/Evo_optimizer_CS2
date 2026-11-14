using FragForge.Core.Analysis;
using Xunit;

namespace FragForge.Tests.Analysis;

public sealed class BlockBootstrapTests
{
    [Fact]
    public void SameSeed_ProducesIdenticalIntervals()
    {
        var values = Enumerable.Range(0, 2000).Select(i => 8.0 + (i % 17) * 0.5).ToArray();

        var first = BlockBootstrap.Compute(values, seed: 20260101);
        var second = BlockBootstrap.Compute(values, seed: 20260101);

        Assert.NotNull(first.P99FrameTimeMs);
        Assert.Equal(first.P99FrameTimeMs!.Low, second.P99FrameTimeMs!.Low, 12);
        Assert.Equal(first.P99FrameTimeMs.High, second.P99FrameTimeMs.High, 12);
        Assert.Equal(first.BlockLengthFrames, second.BlockLengthFrames);
    }

    [Fact]
    public void DifferentSeed_MayMoveTheInterval_ButNotTheDataSummary()
    {
        var values = Enumerable.Range(0, 2000).Select(i => 8.0 + (i % 17) * 0.5).ToArray();

        var first = BlockBootstrap.Compute(values, seed: 1);
        var second = BlockBootstrap.Compute(values, seed: 2);

        // Los tamaños de bloque dependen del frametime medio, no de la semilla: deben coincidir siempre.
        Assert.Equal(first.BlockLengthFrames, second.BlockLengthFrames);
        // Y ambos intervalos deben contener el valor puntual del P99 de la serie.
        var p99 = FrameStatisticsCalculator.Percentile(values.OrderBy(v => v).ToArray(), 99.0);
        Assert.True(first.P99FrameTimeMs!.Low <= p99 && p99 <= first.P99FrameTimeMs.High);
        Assert.True(second.P99FrameTimeMs!.Low <= p99 && p99 <= second.P99FrameTimeMs.High);
    }

    [Fact]
    public void ConstantSeries_HasZeroWidthInterval()
    {
        // Serie constante: cualquier remuestreo de cualquier bloque devuelve exactamente el mismo valor,
        // así que el IC no puede tener anchura. Cálculo a mano: todos los valores son 16.666... ms -> ancho 0.
        var values = Enumerable.Repeat(1000.0 / 60.0, 1200).ToArray();

        var result = BlockBootstrap.Compute(values, seed: 1234);

        Assert.NotNull(result.P99FrameTimeMs);
        Assert.Equal(0.0, result.P99FrameTimeMs!.Width, 9);
        Assert.Equal(1000.0 / 60.0, result.P99FrameTimeMs.Low, 9);
        Assert.Equal(1000.0 / 60.0, result.P99FrameTimeMs.High, 9);
    }

    [Fact]
    public void BlockLength_IsDerivedFromMeanFrameTime()
    {
        // Frametime medio 10 ms -> 100 frames por segundo -> con BlockSeconds = 1 s, bloque de 100 frames.
        var values = Enumerable.Repeat(10.0, 2000).ToArray();

        var result = BlockBootstrap.Compute(values, seed: 7);

        // BlockSeconds es la constante en SEGUNDOS (1 s), no la longitud del bloque en frames.
        Assert.Equal(1.0, BlockBootstrap.BlockSeconds, 12);
        // Media 10 ms -> 100 frames/s -> 1 s de bloque = 100 frames. 2000 frames / 100 = 20 bloques.
        Assert.Equal(100, result.BlockLengthFrames);
        Assert.Equal(20, 2000 / result.BlockLengthFrames);
        Assert.Equal(BlockBootstrap.BootstrapIterations, result.Iterations);
        Assert.Equal(1000, BlockBootstrap.BootstrapIterations);
    }

    [Fact]
    public void TooFewBlocks_IsNullWithReason()
    {
        // 150 frames a 10 ms: 15 ms de captura -> un solo bloque de 100 frames -> no hay 2 bloques distintos.
        var values = Enumerable.Repeat(10.0, 150).ToArray();

        var result = BlockBootstrap.Compute(values, seed: 7);

        Assert.Null(result.P99FrameTimeMs);
        Assert.NotNull(result.P99UnavailableReason);
        Assert.Contains("insuficiente", result.P99UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Low1Interval_IsNullWhenTheRunHasTooFewFrames()
    {
        // Con 500 frames el peor 1 % ya es NULL en las estadísticas; el IC no debe inventarlo.
        var values = Enumerable.Range(0, 500).Select(i => 10.0 + i % 3).ToArray();

        var result = BlockBootstrap.Compute(values, seed: 99);

        Assert.Null(result.Low1PercentAverageFps);
        Assert.NotNull(result.Low1PercentUnavailableReason);
        Assert.Contains("1000", result.Low1PercentUnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySeries_IsNullWithReason_NoException()
    {
        var result = BlockBootstrap.Compute([], seed: 1);

        Assert.Null(result.P99FrameTimeMs);
        Assert.Null(result.Low1PercentAverageFps);
        Assert.NotNull(result.P99UnavailableReason);
        Assert.NotNull(result.Low1PercentUnavailableReason);
    }
}
