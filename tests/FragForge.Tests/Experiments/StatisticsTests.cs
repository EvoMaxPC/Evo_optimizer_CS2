using FragForge.Core.Experiments;
using Xunit;

namespace FragForge.Tests.Experiments;

public sealed class StatisticsTests
{
    [Fact]
    public void Percentile_MatchesLinearInterpolation_OnKnownVector()
    {
        // Vector dorado 1..10 con interpolación lineal (Excel PERCENTILE.INC / numpy linear):
        // P50 = 5.5, P25 = 3.25, P90 = 9.1, P99 = 9.91
        var values = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();
        Assert.Equal(5.5, Statistics.Percentile(values, 50), 10);
        Assert.Equal(3.25, Statistics.Percentile(values, 25), 10);
        Assert.Equal(9.1, Statistics.Percentile(values, 90), 10);
        Assert.Equal(9.91, Statistics.Percentile(values, 99), 10);
    }

    [Fact]
    public void Percentile_HandlesSingleAndEmptyInput()
    {
        Assert.Equal(42.0, Statistics.Percentile([42.0], 99));
        Assert.True(double.IsNaN(Statistics.Percentile([], 50)));
        Assert.True(double.IsNaN(Statistics.Mean([])));
        Assert.True(double.IsNaN(Statistics.StandardDeviation([1.0])));
    }

    [Fact]
    public void LowFps_UsesWorstOnePercentByDefinition()
    {
        // 99 frames de 10 ms y 1 frame de 100 ms: peor 1 % (ceil(100*0.01)=1) = 100 ms => 10 FPS.
        var frameTimes = Enumerable.Repeat(10.0, 99).Append(100.0).ToArray();
        Assert.Equal(10.0, Statistics.LowFps(frameTimes, 0.01), 10);
        Assert.Equal(10.0, Statistics.LowFps(frameTimes, 0.001), 10);
    }

    [Fact]
    public void LowFps_OnUniformFramesEqualsFps()
    {
        var frameTimes = Enumerable.Repeat(5.0, 200).ToArray();
        Assert.Equal(200.0, Statistics.LowFps(frameTimes, 0.01), 10);
        Assert.Equal(200.0, Statistics.LowFps(frameTimes, 0.001), 10);
    }

    [Fact]
    public void LowFps_EmptyIsNaN_NotEmptyZero() => Assert.True(double.IsNaN(Statistics.LowFps([], 0.01)));

    [Fact]
    public void CliffsDelta_ExtremesAndSymmetry()
    {
        Assert.Equal(1.0, Statistics.CliffsDelta([10, 11, 12], [1, 2, 3]), 10);
        Assert.Equal(-1.0, Statistics.CliffsDelta([1, 2, 3], [10, 11, 12]), 10);
        Assert.Equal(0.0, Statistics.CliffsDelta([1, 2, 3], [1, 2, 3]), 10);
        Assert.True(double.IsNaN(Statistics.CliffsDelta([], [1])));
    }

    [Fact]
    public void Bootstrap_IsDeterministicForTheSameSeed_AndDifferentForAnother()
    {
        var a = new[] { 10.0, 11, 12, 10.5, 11.5 };
        var b = new[] { 8.0, 8.5, 9, 8.2, 8.8 };
        var first = Statistics.BootstrapMedianDifference(a, b, iterations: 500, seed: 1234);
        var second = Statistics.BootstrapMedianDifference(a, b, iterations: 500, seed: 1234);
        var third = Statistics.BootstrapMedianDifference(a, b, iterations: 500, seed: 99);
        Assert.Equal(first.LowerBound, second.LowerBound, 12);
        Assert.Equal(first.UpperBound, second.UpperBound, 12);
        Assert.NotEqual(first.LowerBound, third.LowerBound);
        Assert.True(first.ExcludesZero, $"La diferencia debería excluir cero: [{first.LowerBound}, {first.UpperBound}]");
    }

    [Fact]
    public void Bootstrap_OverlappingSamplesIncludeZero()
    {
        var a = new[] { 10.0, 10.1, 9.9, 10.05, 10.02 };
        var b = new[] { 10.0, 9.95, 10.02, 10.01, 9.98 };
        var result = Statistics.BootstrapMedianDifference(a, b, iterations: 800, seed: 7);
        Assert.True(result.IsValid);
        Assert.False(result.ExcludesZero, $"[{result.LowerBound}, {result.UpperBound}] debería contener 0.");
    }

    [Fact]
    public void ExactPermutation_ThreeVersusThree_IsExactWithTwentyPartitions()
    {
        var a = new[] { 100.0, 101, 102 };
        var b = new[] { 1.0, 2, 3 };
        var result = Statistics.PermutationTest(a, b);
        Assert.True(result.IsExact);
        Assert.Equal(20, result.Permutations);
        Assert.Equal(2.0 / 20.0, result.PValue, 10);
    }

    [Fact]
    public void ExactPermutation_IdenticalSamples_GivesPValueOne()
    {
        var result = Statistics.PermutationTest([5.0, 5, 5], [5.0, 5, 5]);
        Assert.True(result.IsExact);
        Assert.Equal(1.0, result.PValue, 10);
    }

    [Fact]
    public void MonteCarloPermutation_IsDeterministicForTheSameSeed()
    {
        var a = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var b = new double[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        var first = Statistics.PermutationTest(a, b, seed: 55);
        var second = Statistics.PermutationTest(a, b, seed: 55);
        Assert.False(first.IsExact);
        Assert.Equal(first.PValue, second.PValue, 12);
    }
}
