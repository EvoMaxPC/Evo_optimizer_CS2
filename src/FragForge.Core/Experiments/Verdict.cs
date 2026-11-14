namespace FragForge.Core.Experiments;

public enum ExperimentVerdictKind { Improvement, NoDifference, Regression, Inconclusive }
public enum VerdictConfidence { Indicative, Confirmed }

/// <summary>
/// Reglas del veredicto A/B. Se documentan aquí como tabla, y los tests las verifican una por una.
///
/// <code>
/// ------------------------------------------------------------------------------------------------------
/// Condición                                                        | Veredicto
/// ------------------------------------------------------------------------------------------------------
/// IC95 de p1Low excluye 0 (a favor de B) AND |efecto| > pisoP1   AND avgFps no empeora > pisoAvg
///                                                                  | Improvement  (Indicative si n&lt;5)
/// IC95 de p1Low excluye 0 (en contra) OR jitter empeora > pisoJit
///                                                                  | Regression   (Indicative si n&lt;5)
/// IC95 dentro de ±piso en p1Low AND avgFps dentro de ±pisoAvg      | NoDifference
/// cualquier otro caso, corridas inválidas o n insuficiente         | Inconclusive
/// ------------------------------------------------------------------------------------------------------
/// </code>
///
/// Reglas adicionales:
/// <list type="bullet">
/// <item>Una regresión de p1Low o de jitter por encima del piso PRIMA sobre una mejora de avgFps.</item>
/// <item>Con n &lt; 5 por brazo el veredicto máximo es <see cref="VerdictConfidence.Indicative"/>
/// (el p exacto mínimo con 3 vs 3 es 0.1, así que "Confirmado" es imposible con esa muestra).</item>
/// <item>Si <see cref="NoiseFloor.IsEstimated"/> es false se usa el piso por defecto conservador, y el veredicto
/// queda como máximo Indicativo: sin A/A no hay derecho a decir "Confirmado".</item>
/// </list>
/// </summary>
public static class VerdictCalculator
{
    public const int MinimumConfirmedRunsPerArm = 5;
    /// <summary>Corridas mínimas por brazo para poder declarar equivalencia (NoDifference) con honestidad.</summary>
    public const int MinimumRunsForEquivalence = 3;
    public const double Alpha = 0.05;

    /// <summary>IC95 del bootstrap expresado en % de la mediana del brazo base. Null si no es calculable.</summary>
    private static (double LowerPct, double UpperPct)? IntervalPercent(BootstrapResult bootstrap, double? baselineMedian)
    {
        if (!bootstrap.IsValid || baselineMedian is null || double.IsNaN(baselineMedian.Value) || baselineMedian.Value == 0) return null;
        var reference = Math.Abs(baselineMedian.Value);
        return (bootstrap.LowerBound / reference * 100.0, bootstrap.UpperBound / reference * 100.0);
    }

    public static ExperimentVerdict Evaluate(ExperimentArm baseline, ExperimentArm candidate, NoiseFloor floor)
    {
        var reasons = new List<string>();
        if (!baseline.IsValid || !candidate.IsValid)
        {
            reasons.Add("Una de las dos ramas tiene corridas inválidas.");
            return new ExperimentVerdict(ExperimentVerdictKind.Inconclusive, VerdictConfidence.Indicative, reasons, null, null, null, null, null);
        }
        if (baseline.Runs.Count == 0 || candidate.Runs.Count == 0)
        {
            reasons.Add("Faltan corridas en una rama.");
            return new ExperimentVerdict(ExperimentVerdictKind.Inconclusive, VerdictConfidence.Indicative, reasons, null, null, null, null, null);
        }

        var effectiveFloor = floor.IsEstimated ? floor : NoiseFloor.ConservativeDefault;
        if (!floor.IsEstimated) reasons.Add("Sin calibración A/A: se usa el piso de ruido conservador y no se permite 'Confirmado'.");

        var baselineP1 = baseline.Metric(r => r.P1Low);
        var candidateP1 = candidate.Metric(r => r.P1Low);
        var baselineFps = baseline.Metric(r => r.AvgFps);
        var candidateFps = candidate.Metric(r => r.AvgFps);
        var baselineJitter = baseline.Metric(r => r.JitterMs);
        var candidateJitter = candidate.Metric(r => r.JitterMs);

        var p1Delta = PercentChange(MedianOrNull(candidateP1), MedianOrNull(baselineP1));
        var fpsDelta = PercentChange(MedianOrNull(candidateFps), MedianOrNull(baselineFps));
        var jitterDelta = PercentChange(MedianOrNull(candidateJitter), MedianOrNull(baselineJitter));
        var p1Bootstrap = Statistics.BootstrapMedianDifference(candidateP1, baselineP1);
        var fpsBootstrap = Statistics.BootstrapMedianDifference(candidateFps, baselineFps);
        var p1Permutation = Statistics.PermutationTest(candidateP1, baselineP1);
        var cliffP1 = Statistics.CliffsDelta(candidateP1, baselineP1);

        var bothEnough = baseline.Runs.Count >= MinimumConfirmedRunsPerArm && candidate.Runs.Count >= MinimumConfirmedRunsPerArm;
        var confidence = bothEnough && floor.IsEstimated ? VerdictConfidence.Confirmed : VerdictConfidence.Indicative;
        var evidence = new EffectEstimate(p1Delta, fpsDelta, jitterDelta, p1Bootstrap, fpsBootstrap, p1Permutation, cliffP1, baseline.Runs.Count, candidate.Runs.Count);

        var p1AboveFloor = p1Delta is not null && Math.Abs(p1Delta.Value) > effectiveFloor.P1LowPct;
        var fpsAboveFloor = fpsDelta is not null && Math.Abs(fpsDelta.Value) > effectiveFloor.AvgFpsPct;
        var jitterAboveFloor = jitterDelta is not null && Math.Abs(jitterDelta.Value) > effectiveFloor.JitterPct;

        // 1) Regresión por jitter o por p1Low prima sobre cualquier mejora de avgFps.
        if (jitterDelta is not null && jitterAboveFloor && jitterDelta.Value > 0)
        {
            reasons.Add($"Jitter empeora {jitterDelta.Value:F1} % (piso {effectiveFloor.JitterPct:F1} %).");
            return new ExperimentVerdict(ExperimentVerdictKind.Regression, confidence, reasons, evidence, VerdictConfidence.Indicative, null, null, null);
        }

        var p1Improved = p1Bootstrap.ExcludesZero && p1Bootstrap.PointEstimate > 0 && p1AboveFloor;
        var p1Regressed = p1Bootstrap.ExcludesZero && p1Bootstrap.PointEstimate < 0 && p1AboveFloor;

        // 2) Mejora: IC excluye 0 a favor, supera piso y avgFps no empeora más que el piso.
        if (p1Improved)
        {
            if (fpsDelta is not null && fpsDelta.Value < -effectiveFloor.AvgFpsPct)
            {
                reasons.Add($"p1Low mejora {p1Delta!.Value:F1} % pero avgFps cae {fpsDelta.Value:F1} %: el intercambio no es una mejora limpia.");
                return new ExperimentVerdict(ExperimentVerdictKind.Inconclusive, VerdictConfidence.Indicative, reasons, evidence, VerdictConfidence.Indicative, null, null, null);
            }
            reasons.Add($"p1Low mejora {p1Delta!.Value:F1} % (piso {effectiveFloor.P1LowPct:F1} %), IC95 [{p1Bootstrap.LowerBound:F2}, {p1Bootstrap.UpperBound:F2}], p={p1Permutation.PValue:F4}.");
            if (p1Permutation.PValue >= Alpha) reasons.Add($"El test de permutación no es significativo a α={Alpha} con n={baseline.Runs.Count} vs {candidate.Runs.Count}.");
            return new ExperimentVerdict(ExperimentVerdictKind.Improvement, confidence, reasons, evidence, confidence, null, null, null);
        }

        if (p1Regressed)
        {
            reasons.Add($"p1Low empeora {p1Delta!.Value:F1} % (piso {effectiveFloor.P1LowPct:F1} %), IC95 [{p1Bootstrap.LowerBound:F2}, {p1Bootstrap.UpperBound:F2}], p={p1Permutation.PValue:F4}.");
            return new ExperimentVerdict(ExperimentVerdictKind.Regression, confidence, reasons, evidence, confidence, null, null, null);
        }

        // 3) Equivalencia práctica: TODO el IC95, expresado en % de la mediana base, cae dentro del piso.
        //    No se pide "que el IC contenga el cero": un efecto perfectamente medido de +0.2 % también es
        //    "sin diferencia", y exigir significación estadística para declarar equivalencia sería un error.
        var p1IntervalPct = IntervalPercent(p1Bootstrap, MedianOrNull(baselineP1));
        var fpsIntervalPct = IntervalPercent(fpsBootstrap, MedianOrNull(baselineFps));
        var p1WithinFloor = p1IntervalPct is { } p1Interval
            && p1Interval.LowerPct >= -effectiveFloor.P1LowPct && p1Interval.UpperPct <= effectiveFloor.P1LowPct;
        var fpsWithinFloor = fpsIntervalPct is { } fpsInterval
            && fpsInterval.LowerPct >= -effectiveFloor.AvgFpsPct && fpsInterval.UpperPct <= effectiveFloor.AvgFpsPct;
        var smallEnough = (p1Delta is null || Math.Abs(p1Delta.Value) <= effectiveFloor.P1LowPct)
            && (fpsDelta is null || Math.Abs(fpsDelta.Value) <= effectiveFloor.AvgFpsPct)
            && (jitterDelta is null || Math.Abs(jitterDelta.Value) <= effectiveFloor.JitterPct);
        var enoughToClaimEquivalence = baseline.Runs.Count >= MinimumRunsForEquivalence && candidate.Runs.Count >= MinimumRunsForEquivalence;
        if (smallEnough && enoughToClaimEquivalence && p1WithinFloor && (fpsDelta is null || fpsWithinFloor))
        {
            reasons.Add($"Todos los efectos están dentro del piso (±{effectiveFloor.P1LowPct:F1} % p1Low, ±{effectiveFloor.AvgFpsPct:F1} % avgFps, ±{effectiveFloor.JitterPct:F1} % jitter).");
            if (p1IntervalPct is { } interval) reasons.Add($"IC95 de p1Low en % de la base: [{interval.LowerPct:F2} %, {interval.UpperPct:F2} %].");
            return new ExperimentVerdict(ExperimentVerdictKind.NoDifference, confidence, reasons, evidence, confidence, null, null, null);
        }
        if (smallEnough && !enoughToClaimEquivalence) reasons.Add($"Con {baseline.Runs.Count} vs {candidate.Runs.Count} corridas no se puede afirmar equivalencia; hacen falta al menos {MinimumRunsForEquivalence} por brazo.");
        else if (smallEnough) reasons.Add($"El efecto es pequeño pero el IC95 no cabe entero dentro del piso de ±{effectiveFloor.P1LowPct:F1} %: no se declara equivalencia.");

        if (fpsAboveFloor && fpsDelta is not null && fpsDelta.Value > 0 && p1Delta is not null && p1Delta.Value < -effectiveFloor.P1LowPct)
            reasons.Add("avgFps mejora pero p1Low empeora por encima del piso: patrón típico de 'más FPS, peor fluidez'.");
        if (!p1Bootstrap.IsValid) reasons.Add("Bootstrap inválido: muestra demasiado pequeña o con NaN.");
        reasons.Add("No hay evidencia suficiente para un veredicto direccional.");
        return new ExperimentVerdict(ExperimentVerdictKind.Inconclusive, VerdictConfidence.Indicative, reasons, evidence, VerdictConfidence.Indicative, null, null, null);
    }

    private static double? MedianOrNull(double[] values) => values.Length == 0 ? null : Statistics.Median(values);

    /// <summary>Cambio porcentual de <paramref name="candidate"/> respecto a <paramref name="baseline"/>.</summary>
    public static double? PercentChange(double? candidate, double? baseline)
    {
        if (candidate is null || baseline is null) return null;
        if (double.IsNaN(candidate.Value) || double.IsNaN(baseline.Value)) return null;
        if (baseline.Value == 0) return null;
        return (candidate.Value - baseline.Value) / Math.Abs(baseline.Value) * 100.0;
    }
}

public sealed record EffectEstimate(
    double? P1LowChangePct,
    double? AvgFpsChangePct,
    double? JitterChangePct,
    BootstrapResult P1LowBootstrap,
    BootstrapResult AvgFpsBootstrap,
    PermutationResult P1LowPermutation,
    double CliffsDeltaP1Low,
    int BaselineRuns,
    int CandidateRuns);

public sealed record ExperimentVerdict(
    ExperimentVerdictKind Kind,
    VerdictConfidence Confidence,
    IReadOnlyList<string> Reasons,
    EffectEstimate? Effect,
    VerdictConfidence? MaxConfidence,
    string? PromotedEvidenceSuggestion,
    string? Notes,
    string? BlockingIssue)
{
    public bool IsUsable => Kind is not ExperimentVerdictKind.Inconclusive;
    public string DisplayKey => Kind switch
    {
        ExperimentVerdictKind.Improvement => "verdict.improvement",
        ExperimentVerdictKind.Regression => "verdict.regression",
        ExperimentVerdictKind.NoDifference => "verdict.noDifference",
        _ => "verdict.inconclusive"
    };
}
