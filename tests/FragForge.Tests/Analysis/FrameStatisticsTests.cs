using FragForge.Core.Analysis;
using Xunit;

namespace FragForge.Tests.Analysis;

/// <summary>
/// Todos los valores esperados están calculados A MANO en el comentario de cada caso. Nada aquí mide tiempos
/// reales ni depende de la máquina.
/// </summary>
public sealed class FrameStatisticsTests
{
    private const double Tolerance = 1e-9;

    /// <summary>1000 frames: 990 de 10 ms y 10 de 50 ms. El ejemplo de referencia de la especificación.</summary>
    private static double[] ReferenceSet()
    {
        var values = new double[1000];
        for (var i = 0; i < 990; i++) values[i] = 10.0;
        for (var i = 990; i < 1000; i++) values[i] = 50.0;
        return values;
    }

    [Fact]
    public void ReferenceSet_ComputesTheHandCalculatedValues()
    {
        var stats = FrameStatisticsCalculator.Compute(ReferenceSet());

        // Media = (990*10 + 10*50) / 1000 = (9900 + 500) / 1000 = 10400/1000 = 10.4 ms.
        Assert.Equal(10.4, stats.MeanMs!.Value, Tolerance);

        // P99 tipo 7: h = (N-1)*p = 999 * 0.99 = 989.01 -> entre índice 989 (10 ms) y 990 (50 ms), peso 0.01:
        // 10 + 0.01 * (50 - 10) = 10 + 0.4 = 10.4 ms.
        Assert.Equal(10.4, stats.P99FrameTimeMs!.Value, Tolerance);

        // P99.9 tipo 7: h = 999 * 0.999 = 998.001 -> entre índice 998 y 999. La serie ordenada tiene 990
        // valores de 10 ms en los índices 0..989 y 50 ms en 990..999, así que AMBOS índices son 10 ms:
        // 10 + 0.001 * (10 - 10) = 10 ms. (El 99.9 % no llega a tocar la cola: para eso está el peor 1 %.)
        Assert.Equal(10.0, stats.P999FrameTimeMs!.Value, Tolerance);

        // Peor 1 %: ceil(0.01 * 1000) = 10 frames más lentos = los diez de 50 ms -> media 50 ms -> 1000/50 = 20 FPS.
        Assert.Equal(20.0, stats.Low1PercentAverageFps!.Value, Tolerance);

        // El peor 0.1 % NO se afirma aquí: con 1000 frames queda por debajo de su umbral y es NULL.
        // Lo cubre el test siguiente, que exige motivo en vez de un número inventado.

        // Mediana: h = 999 * 0.5 = 499.5 -> entre índice 499 y 500, ambos 10 ms -> 10.
        Assert.Equal(10.0, stats.MedianMs!.Value, Tolerance);
        Assert.Equal(10.0, stats.MinMs!.Value, Tolerance);
        Assert.Equal(50.0, stats.MaxMs!.Value, Tolerance);
        Assert.Equal(1000, stats.FrameCount);
    }

    [Fact]
    public void Low01Percent_IsNullBelowItsThreshold_WithATextualReason()
    {
        var stats = FrameStatisticsCalculator.Compute(ReferenceSet());

        // 1000 frames < MinFramesForLow01Percent (10000): la métrica es NULL con motivo, no 0 ni un número.
        Assert.Null(stats.Low01PercentAverageFps);
        Assert.True(stats.UnavailableReasons.ContainsKey(nameof(FrameStatistics.Low01PercentAverageFps)));
        var reason = stats.UnavailableReasons[nameof(FrameStatistics.Low01PercentAverageFps)];
        Assert.Contains("insuficiente", reason, StringComparison.Ordinal);
        Assert.Contains("1000", reason, StringComparison.Ordinal);
        Assert.Contains("10000", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UniformSet_P99_Median_AndLow1_AllEqualTheConstant()
    {
        var stats = FrameStatisticsCalculator.Compute(Enumerable.Repeat(8.0, 1200).ToArray());

        // Con todos los valores iguales a 8: cualquier percentil y cualquier media dan 8, y 1000/8 = 125 FPS.
        Assert.Equal(8.0, stats.P99FrameTimeMs!.Value, Tolerance);
        Assert.Equal(8.0, stats.MedianMs!.Value, Tolerance);
        Assert.Equal(0.0, stats.StdDevMs!.Value, Tolerance);
        Assert.Equal(125.0, stats.P99Fps!.Value, Tolerance);
        Assert.Equal(125.0, stats.Low1PercentAverageFps!.Value, Tolerance);
        Assert.Equal(1200, stats.FrameCount);
        // Duración = 1200 * 8 = 9600 ms.
        Assert.Equal(9600.0, stats.TotalDurationMs, Tolerance);
    }

    [Fact]
    public void SingleEnormousFrame_MovesTheTailButNotTheMedian()
    {
        // 999 frames de 10 ms y uno de 1000 ms.
        var values = Enumerable.Repeat(10.0, 999).Append(1000.0).ToArray();
        var stats = FrameStatisticsCalculator.Compute(values);

        // P99 tipo 7: h = 999 * 0.99 = 989.01 -> índices 989 (10) y 990 (10): ambos 10 -> 10.
        Assert.Equal(10.0, stats.P99FrameTimeMs!.Value, Tolerance);
        // P99.9: h = 999 * 0.999 = 998.001 -> índices 998 (10) y 999 (1000), peso 0.001:
        // 10 + 0.001 * 990 = 10.99 ms.
        Assert.Equal(10.99, stats.P999FrameTimeMs!.Value, Tolerance);
        // Mediana: índice 499.5 -> 10.
        Assert.Equal(10.0, stats.MedianMs!.Value, Tolerance);
        // Máximo: 1000.
        Assert.Equal(1000.0, stats.MaxMs!.Value, Tolerance);
        // Peor 1 %: ceil(0.01*1000) = 10 frames más lentos -> 9 de 10 ms + el de 1000 ms:
        // media = (9*10 + 1000)/10 = (90 + 1000)/10 = 109 ms -> 1000/109 = 9.174311926605505 FPS.
        Assert.Equal(1000.0 / 109.0, stats.Low1PercentAverageFps!.Value, Tolerance);
    }

    [Fact]
    public void EmptyInput_IsAllNull_NotZero()
    {
        var stats = FrameStatisticsCalculator.Compute([], naFrames: 7);

        Assert.Equal(0, stats.FrameCount);
        Assert.Equal(7, stats.NaFrames);
        Assert.Null(stats.MeanMs);
        Assert.Null(stats.P99FrameTimeMs);
        Assert.Null(stats.Low1PercentAverageFps);
        Assert.Null(stats.MinMs);
        Assert.Null(stats.MaxMs);
    }

    [Fact]
    public void BelowMinFramesForLow1_IsNullWithReason_WhileP99StillWorks()
    {
        // 999 frames: uno menos que MinFramesForLow1Percent.
        var stats = FrameStatisticsCalculator.Compute(Enumerable.Repeat(16.0, 999).ToArray());

        Assert.Null(stats.Low1PercentAverageFps);
        Assert.NotNull(stats.P99FrameTimeMs);
        Assert.Contains("999", stats.UnavailableReasons[nameof(FrameStatistics.Low1PercentAverageFps)], StringComparison.Ordinal);
        Assert.Contains("1000", stats.UnavailableReasons[nameof(FrameStatistics.Low1PercentAverageFps)], StringComparison.Ordinal);
    }

    [Fact]
    public void Percentile_IsType7_OnAKnownVector()
    {
        var sorted = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        // Cálculo a mano con h = (N-1)*p, N = 10:
        // p=50 -> h=4.5 -> 5.5; p=25 -> h=2.25 -> 3 + 0.25 = 3.25; p=90 -> h=8.1 -> 9 + 0.1 = 9.1;
        // p=99 -> h=8.91 -> 9 + 0.91 = 9.91.
        Assert.Equal(5.5, FrameStatisticsCalculator.Percentile(sorted, 50), Tolerance);
        Assert.Equal(3.25, FrameStatisticsCalculator.Percentile(sorted, 25), Tolerance);
        Assert.Equal(9.1, FrameStatisticsCalculator.Percentile(sorted, 90), Tolerance);
        Assert.Equal(9.91, FrameStatisticsCalculator.Percentile(sorted, 99), Tolerance);
        Assert.Equal(1.0, FrameStatisticsCalculator.Percentile(sorted, 0), Tolerance);
        Assert.Equal(10.0, FrameStatisticsCalculator.Percentile(sorted, 100), Tolerance);
    }

    [Fact]
    public void Method_IsDeclaredExplicitly()
    {
        // El método forma parte del resultado: el mismo dato da 10.4 (tipo 7) o 10 (más cercano) en el P99.
        Assert.Equal(PercentileMethod.LinearType7, FrameStatisticsCalculator.Method);
    }

    [Fact]
    public void NaFrames_AreCountedButNeverEnterTheMath()
    {
        var stats = FrameStatisticsCalculator.Compute([10.0, 10.0, 10.0], naFrames: 4);

        // Con solo los 3 válidos: media 10, N = 3. Los 4 NA se informan pero no valen 0 (bajarían la media a 30/7).
        Assert.Equal(10.0, stats.MeanMs!.Value, Tolerance);
        Assert.Equal(3, stats.FrameCount);
        Assert.Equal(4, stats.NaFrames);
        Assert.Equal(30.0, stats.TotalDurationMs, Tolerance);
    }
}
