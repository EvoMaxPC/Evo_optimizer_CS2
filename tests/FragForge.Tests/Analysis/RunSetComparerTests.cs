using FragForge.Core.Analysis;
using Xunit;

namespace FragForge.Tests.Analysis;

/// <summary>
/// Los valores de cada corrida se escriben a mano; no se calculan con el motor para que el test no pueda
/// pasar por construcción.
/// </summary>
public sealed class RunSetComparerTests
{
    private static RunMetricSet Run(string label, double? low1Fps, double? p99Ms, double? hitchesPerMinute, double seconds = 60)
        => new(label, low1Fps, p99Ms, hitchesPerMinute, seconds);

    /// <summary>
    /// Cuatro corridas por condición SIN solape en las tres métricas, con B mejor en todas: FPS más altos,
    /// P99 más bajo y menos tirones. Es el caso que debería producir <see cref="OverallVerdict.Mejora"/>.
    /// </summary>
    /// <summary>
    /// Construye las métricas de una corrida recorriendo el camino REAL del motor: frames crudos en ms ->
    /// <see cref="HitchAnalyzer.Analyze"/> -> <see cref="FrameStatisticsCalculator.Compute"/> -> RunMetricSet.
    /// Es el mismo orden que usa <c>AnalyzeCommands.BuildReport</c>. Nada se pone a mano: si una corrida tiene
    /// 500 frames, el null de Low1PercentAverageFps lo produce el motor, no el test.
    /// </summary>
    private static RunMetricSet RunFromFrames(string label, int frameCount, double frameTimeMs)
    {
        var frames = Enumerable.Repeat(frameTimeMs, frameCount).ToArray();
        var (hitch, analyzed) = HitchAnalyzer.Analyze(frames);
        var stats = FrameStatisticsCalculator.Compute(analyzed);
        return new RunMetricSet(label, stats.Low1PercentAverageFps, stats.P99FrameTimeMs, hitch.HitchesPerMinute, stats.TotalDurationMs / 1000.0);
    }

    private static (RunMetricSet[] A, RunMetricSet[] B) Disjoint()
    {
        var a = new[]
        {
            Run("a1", 80, 16, 4), Run("a2", 85, 15, 4.5), Run("a3", 82, 17, 5), Run("a4", 88, 15.5, 3.8)
        };
        var b = new[]
        {
            Run("b1", 100, 12, 1), Run("b2", 105, 11.5, 1.2), Run("b3", 102, 12.5, 0.9), Run("b4", 110, 11, 1.1)
        };
        return (a, b);
    }

    [Fact]
    public void FourAgainstFour_Disjoint_IsSeparado_AndFavoursB()
    {
        var (a, b) = Disjoint();

        var result = RunSetComparer.Compare(a, b);

        // Low1PercentAverage: A va de 80 a 88 FPS; B de 100 a 110. Todos los de B > todos los de A -> B mejor.
        Assert.Equal(MetricVerdict.Separado, result.Low1Percent.Verdict);
        Assert.Equal("B", result.Low1Percent.BetterCondition);
        Assert.Equal(80.0, result.Low1Percent.A.Min!.Value, 10);
        Assert.Equal(88.0, result.Low1Percent.A.Max!.Value, 10);
        Assert.Equal(100.0, result.Low1Percent.B.Min!.Value, 10);
        Assert.Equal(110.0, result.Low1Percent.B.Max!.Value, 10);
        // Mediana de A: valores ordenados 80,82,85,88 -> h = 3*0.5 = 1.5 -> (82 + 85)/2 = 83.5.
        Assert.Equal(83.5, result.Low1Percent.A.Median!.Value, 10);
        Assert.Equal(OverallVerdict.Mejora, result.Overall);
    }

    [Fact]
    public void OverlappingRanges_AreSolapado()
    {
        var a = new[] { Run("a1", 100, 12, 1), Run("a2", 104, 11.5, 1.2), Run("a3", 108, 12.5, 0.9), Run("a4", 112, 11, 1.1) };
        var b = new[] { Run("b1", 101, 16, 4), Run("b2", 105, 15, 4.5), Run("b3", 109, 17, 5), Run("b4", 113, 15.5, 3.8) };

        var result = RunSetComparer.Compare(a, b);

        // A: 100..112, B: 101..113 -> se cruzan y además el máximo de A (112) queda dentro de B.
        Assert.Equal(MetricVerdict.Solapado, result.Low1Percent.Verdict);
        Assert.Null(result.Low1Percent.BetterCondition);
        Assert.Equal(OverallVerdict.SinDiferenciaDistinguible, result.Overall);
    }

    [Fact]
    public void ThreeAgainstThree_IsInsuficiente_WithReason()
    {
        var a = new[] { Run("a1", 100, 12, 1), Run("a2", 105, 11.5, 1.2), Run("a3", 102, 12.5, 0.9) };
        var b = new[] { Run("b1", 80, 16, 4), Run("b2", 85, 15, 4.5), Run("b3", 82, 17, 5) };

        var result = RunSetComparer.Compare(a, b);

        Assert.Equal(4, RunSetComparer.MinRunsPerCondition);
        Assert.Equal(MetricVerdict.Insuficiente, result.Low1Percent.Verdict);
        Assert.Contains("al menos 4", result.Low1Percent.Reason, StringComparison.Ordinal);
        // Aunque los números estén perfectamente separados, con 3 no se afirma nada.
        Assert.Equal(OverallVerdict.SinDiferenciaDistinguible, result.Overall);
    }

    [Fact]
    public void OneRunWithoutTheMetric_PoisonaElVeredicto()
    {
        var a = new[] { Run("a1", 100, 12, 1), Run("a2", 105, 11.5, 1.2), Run("a3", 102, 12.5, 0.9), Run("a4", 110, 11, 1.1) };
        // b4 no llega a 1000 frames -> Low1PercentAverageFps es null.
        var b = new[] { Run("b1", 80, 16, 4), Run("b2", 85, 15, 4.5), Run("b3", 82, 17, 5), Run("b4", null, 15.5, 3.8) };

        var result = RunSetComparer.Compare(a, b);

        Assert.Equal(MetricVerdict.Insuficiente, result.Low1Percent.Verdict);
        Assert.Equal(1, result.Low1Percent.B.MissingRuns);
        Assert.Contains("sin dato", result.Low1Percent.Reason, StringComparison.Ordinal);
        // P99 y tirones sí tienen las 4 corridas: esos veredictos se calculan igual.
        Assert.Equal(MetricVerdict.Separado, result.P99FrameTime.Verdict);
    }

    [Fact]
    public void Low1ImprovesButP99WorsensSeparately_IsNotMejora()
    {
        var a = new[] { Run("a1", 100, 10.0, 1), Run("a2", 105, 10.2, 1), Run("a3", 102, 10.4, 1), Run("a4", 110, 10.1, 1) };
        // B mejora el peor 1 %: su MÍNIMO (111) supera el MÁXIMO de A (110), así que hay separación estricta.
        // Y empeora el P99: su mínimo (20) supera el máximo de A (10.4) -> separado, pero en contra.
        var b = new[] { Run("b1", 111, 20.0, 1), Run("b2", 115, 21.0, 1), Run("b3", 112, 22.0, 1), Run("b4", 120, 20.5, 1) };

        var result = RunSetComparer.Compare(a, b);

        Assert.Equal(MetricVerdict.Separado, result.Low1Percent.Verdict);
        Assert.Equal("B", result.Low1Percent.BetterCondition);
        Assert.Equal(MetricVerdict.Separado, result.P99FrameTime.Verdict);
        Assert.Equal("A", result.P99FrameTime.BetterCondition); // en ms, más bajo es mejor -> A
        // La métrica principal mejora, pero el P99 se separa en contra: NO se declara mejora.
        Assert.Equal(OverallVerdict.SinDiferenciaDistinguible, result.Overall);
        Assert.Contains("P99FrameTime se separa en contra de B", result.OverallReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Low1WorsensSeparately_IsEmpeora()
    {
        var a = new[] { Run("a1", 120, 10.0, 1), Run("a2", 125, 10.2, 1), Run("a3", 122, 10.4, 1), Run("a4", 130, 10.1, 1) };
        var b = new[] { Run("b1", 100, 10.5, 1), Run("b2", 105, 10.6, 1), Run("b3", 102, 10.7, 1), Run("b4", 108, 10.8, 1) };

        var result = RunSetComparer.Compare(a, b);

        Assert.Equal("A", result.Low1Percent.BetterCondition);
        Assert.Equal(OverallVerdict.Empeora, result.Overall);
    }

    [Fact]
    public void HitchesWorseningVetoesAMejora()
    {
        var a = new[] { Run("a1", 100, 12, 1), Run("a2", 105, 11.5, 1.1), Run("a3", 102, 12.5, 0.9), Run("a4", 110, 11, 1.2) };
        // Mejor Low1 y mejor P99, pero MUCHOS más tirones: 8..10 frente a 0.9..1.2.
        var b = new[] { Run("b1", 150, 8, 8), Run("b2", 155, 8.2, 9), Run("b3", 152, 8.1, 10), Run("b4", 160, 8.3, 8.5) };

        var result = RunSetComparer.Compare(a, b);

        Assert.Equal(MetricVerdict.Separado, result.Low1Percent.Verdict);
        Assert.Equal("B", result.P99FrameTime.BetterCondition);
        Assert.Equal("A", result.HitchesPerMinute.BetterCondition); // menos tirones = mejor
        Assert.Equal(OverallVerdict.SinDiferenciaDistinguible, result.Overall);
        Assert.Contains("tirones por minuto se separan en contra de B", result.OverallReason, StringComparison.Ordinal);
    }

    [Fact]
    public void InterchangeabilityWarning_IsAlwaysPresent()
    {
        var (a, b) = Disjoint();
        var result = RunSetComparer.Compare(a, b);

        Assert.Contains(RunSetComparer.InterchangeabilityWarning, result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("28 W", StringComparison.Ordinal));
    }

    [Fact]
    public void UnequalRunCounts_AndDurations_AreWarned()
    {
        var a = new[] { Run("a1", 100, 12, 1, 60), Run("a2", 105, 11.5, 1.2, 60), Run("a3", 102, 12.5, 0.9, 60), Run("a4", 110, 11, 1.1, 60) };
        var b = new[] { Run("b1", 80, 16, 4, 180), Run("b2", 85, 15, 4.5, 180), Run("b3", 82, 17, 5, 180), Run("b4", 88, 15.5, 3.8, 180), Run("b5", 89, 16.5, 3.9, 180) };

        var result = RunSetComparer.Compare(a, b, aDurationSeconds: 60, bDurationSeconds: 180);

        Assert.Contains(result.Warnings, w => w.Contains("A tiene 4 corridas y B tiene 5", StringComparison.Ordinal));
        // 180 / 60 = 3 > DurationMismatchFactor (1.5).
        Assert.Contains(result.Warnings, w => w.Contains("Duración muy distinta", StringComparison.Ordinal));
        Assert.Equal(1.5, RunSetComparer.DurationMismatchFactor, 10);
    }

    [Fact]
    public void LowerIsBetterForMilliseconds_AndHigherForFps()
    {
        var (a, b) = Disjoint();
        var result = RunSetComparer.Compare(a, b);

        Assert.Equal(BetterDirection.Higher, result.Low1Percent.Direction);
        Assert.Equal(BetterDirection.Lower, result.P99FrameTime.Direction);
        Assert.Equal(BetterDirection.Lower, result.HitchesPerMinute.Direction);
        Assert.Equal("FPS", result.Low1Percent.Unit);
        Assert.Equal("ms", result.P99FrameTime.Unit);
    }

    [Fact]
    public void RunWith500Frames_MakesLow1Insuficiente_AndTheGlobalVerdictIsNotMejora()
    {
        // 4 corridas por condición. A: 1200 frames de 12.5 ms. B: tres de 1200 frames de 10.0 ms y UNA de
        // solo 500 frames. Los frames son crudos: el camino es HitchAnalyzer + FrameStatisticsCalculator.
        var a = new[]
        {
            RunFromFrames("a1", 1200, 12.5), RunFromFrames("a2", 1200, 12.5),
            RunFromFrames("a3", 1200, 12.5), RunFromFrames("a4", 1200, 12.5)
        };
        var b = new[]
        {
            RunFromFrames("b1", 1200, 10.0), RunFromFrames("b2", 1200, 10.0),
            RunFromFrames("b3", 1200, 10.0), RunFromFrames("b4", 500, 10.0)
        };

        var result = RunSetComparer.Compare(a, b);

        // Cálculo a mano: con N copias de un mismo frametime x, cualquier percentil y cualquier media valen x.
        //   P99FrameTime: A = 12.5 ms, B = 10.0 ms -> en ms, más bajo es mejor -> se separa a favor de B.
        //   Low1PercentAverage: A = 1000/12.5 = 80 FPS, B = 1000/10.0 = 100 FPS... salvo la corrida de 500.
        //
        // 500 frames < MinFramesForLow1Percent (1000), así que el peor 1 % (ceil(0.01*500) = 5 frames) NO se
        // calcula: Low1PercentAverageFps es null CON MOTIVO, no 0 ni un valor estimado (R-L). El comparador no
        // inventa nada: al faltar el dato de una corrida de B, la métrica entera pasa a Insuficiente.
        Assert.Null(b[3].Low1PercentAverageFps);

        Assert.Equal(MetricVerdict.Insuficiente, result.Low1Percent.Verdict);
        Assert.Equal(1, result.Low1Percent.B.MissingRuns);
        Assert.Equal(0, result.Low1Percent.A.MissingRuns);
        Assert.Contains("sin dato", result.Low1Percent.Reason, StringComparison.Ordinal);
        Assert.Contains("1000", result.Low1Percent.Reason, StringComparison.Ordinal);

        // Las OTRAS corridas sí están separadas: 10.0 < 12.5 para las cuatro de A, así que el P99 se separa a
        // favor de B. Esto demuestra que el global no decae por falta de separación, sino por el hueco.
        Assert.Equal(MetricVerdict.Separado, result.P99FrameTime.Verdict);
        Assert.Equal("B", result.P99FrameTime.BetterCondition);

        // Y aun así NO se declara mejora: la métrica principal no se puede comparar.
        Assert.NotEqual(OverallVerdict.Mejora, result.Overall);
        Assert.Equal(OverallVerdict.SinDiferenciaDistinguible, result.Overall);
        Assert.Contains("No hay corridas suficientes", result.OverallReason, StringComparison.Ordinal);
    }
}
