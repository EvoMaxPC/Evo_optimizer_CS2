using System.Globalization;

namespace FragForge.Core.Analysis;

/// <summary>
/// Estadística de cola de una captura de frametimes. Funciones PURAS: no hacen E/S, no usan
/// <see cref="Random"/> sin semilla, no dependen de la cultura y no mutan la entrada.
/// </summary>
/// <remarks>
/// <para>
/// Por qué nombres explícitos y no un "1 % low" a secas: "1 % low" se usa en la calle para dos cosas
/// distintas (el percentil 99 del frametime, o la media del peor 1 %), y dan números diferentes. Aquí se
/// separan en <see cref="P99FrameTimeMs"/> y <see cref="Low1PercentAverageFps"/>, cada una con su nombre.
/// </para>
/// <para>
/// Un valor NULL con motivo (no 0, no NaN silencioso) significa "no se puede calcular con estos datos".
/// Se prefiere decir por qué a inventar un número (R-L).
/// </para>
/// </remarks>
public sealed record FrameStatistics
{
    /// <summary>Frames válidos usados en el cálculo (ya excluidos NA y pausas si se pidió).</summary>
    public int FrameCount { get; init; }

    /// <summary>Celdas NA de la columna principal. Se cuentan aparte y NUNCA valen 0.</summary>
    public int NaFrames { get; init; }

    public double? MeanMs { get; init; }
    public double? MedianMs { get; init; }
    public double? StdDevMs { get; init; }
    public double? MinMs { get; init; }
    public double? MaxMs { get; init; }

    /// <summary>Percentil 99 del frametime, método <see cref="PercentileMethod.LinearType7"/>.</summary>
    public double? P99FrameTimeMs { get; init; }

    /// <summary>Percentil 99.9 del frametime, mismo método.</summary>
    public double? P999FrameTimeMs { get; init; }

    /// <summary>FPS equivalentes del percentil 99: 1000 / <see cref="P99FrameTimeMs"/>.</summary>
    public double? P99Fps { get; init; }

    /// <summary>FPS equivalentes del percentil 99.9.</summary>
    public double? P999Fps { get; init; }

    /// <summary>
    /// Media en FPS del peor 1 % de los frames: 1000 / media de los <c>ceil(0.01 * N)</c> frametimes MÁS
    /// ALTOS. Null si N &lt; <see cref="FrameStatisticsCalculator.MinFramesForLow1Percent"/>.
    /// </summary>
    public double? Low1PercentAverageFps { get; init; }

    /// <summary>Igual que el anterior con el peor 0.1 %. Null si N &lt; <see cref="FrameStatisticsCalculator.MinFramesForLow01Percent"/>.</summary>
    public double? Low01PercentAverageFps { get; init; }

    /// <summary>Motivo por el que una métrica es null. Clave = nombre de la métrica.</summary>
    public IReadOnlyDictionary<string, string> UnavailableReasons { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Duración total de los frames usados, en ms (suma). Base de la tasa de tirones por minuto.</summary>
    public double TotalDurationMs { get; init; }
}

/// <summary>Cálculo de <see cref="FrameStatistics"/>. Funciones puras.</summary>
public static class FrameStatisticsCalculator
{
    /// <summary>
    /// HEURÍSTICA NO VALIDADA. Mínimo de frames para que "el peor 1 %" no sea un puñado de frames: con
    /// menos, la media de los peores es más ruido que medida. No sale de ningún estudio; es un umbral de
    /// prudencia revisable.
    /// </summary>
    public const int MinFramesForLow1Percent = 1000;

    /// <summary>
    /// HEURÍSTICA NO VALIDADA. Igual que <see cref="MinFramesForLow1Percent"/> para el peor 0.1 %, con el
    /// mismo razonamiento pero más exigente porque el 0.1 % de N es aún menos frames.
    /// </summary>
    public const int MinFramesForLow01Percent = 10000;

    /// <summary>Método de percentil del motor. Se documenta en el DTO para que el número no sea ambiguo.</summary>
    public const PercentileMethod Method = PercentileMethod.LinearType7;

    /// <summary>
    /// Calcula todas las métricas. <paramref name="frameTimesMs"/> son los frames ya filtrados de NA.
    /// </summary>
    public static FrameStatistics Compute(IReadOnlyList<double> frameTimesMs, int naFrames = 0)
    {
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (frameTimesMs.Count == 0)
        {
            reasons["all"] = "no hay frames utilizables.";
            return new FrameStatistics { NaFrames = naFrames, UnavailableReasons = reasons };
        }

        // Se ordena UNA vez y se reutiliza. Ascendente sirve para las dos cosas: los percentiles interpolan
        // sobre índices crecientes y el peor X % son los ÚLTIMOS `count` elementos.
        var ascending = frameTimesMs.OrderBy(v => v).ToArray();
        var n = ascending.Length;

        var total = ascending.Sum();
        var mean = total / n;
        var p99 = Percentile(ascending, 99.0);
        var p999 = Percentile(ascending, 99.9);

        double? WorstFractionFps(double fraction, int minimum, out string? reason)
        {
            var count = (int)Math.Ceiling(fraction * n);
            if (n < minimum)
            {
                reason = string.Create(CultureInfo.InvariantCulture, $"insuficiente: {n} frames, se necesitan {minimum}.");
                return null;
            }
            reason = null;
            // Los `count` más lentos son los últimos de la lista ascendente (solo frames con dato).
            var average = ascending[(n - count)..].Sum() / count;
            return average > 0 ? 1000.0 / average : null;
        }

        var low1 = WorstFractionFps(0.01, MinFramesForLow1Percent, out var why1);
        if (why1 is not null) reasons[nameof(FrameStatistics.Low1PercentAverageFps)] = why1;

        var low01 = WorstFractionFps(0.001, MinFramesForLow01Percent, out var why01);
        if (why01 is not null) reasons[nameof(FrameStatistics.Low01PercentAverageFps)] = why01;

        return new FrameStatistics
        {
            FrameCount = n,
            NaFrames = naFrames,
            MeanMs = mean,
            MedianMs = Percentile(ascending, 50.0),
            StdDevMs = n < 2 ? null : Math.Sqrt(ascending.Sum(v => (v - mean) * (v - mean)) / (n - 1)),
            MinMs = ascending[0],
            MaxMs = ascending[^1],
            P99FrameTimeMs = p99,
            P999FrameTimeMs = p999,
            P99Fps = p99 > 0 ? 1000.0 / p99 : null,
            P999Fps = p999 > 0 ? 1000.0 / p999 : null,
            Low1PercentAverageFps = low1,
            Low01PercentAverageFps = low01,
            UnavailableReasons = reasons,
            TotalDurationMs = total
        };
    }

    /// <summary>
    /// Percentil <paramref name="percentile"/> (0-100) con <see cref="PercentileMethod.LinearType7"/>.
    /// <paramref name="sortedAscending"/> DEBE venir ordenado: no se reordena aquí (se ordena una sola vez
    /// por captura, y reordenar en cada llamada sería O(n log n) por métrica).
    /// </summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double percentile)
    {
        if (sortedAscending.Count == 0) return double.NaN;
        if (sortedAscending.Count == 1) return sortedAscending[0];
        // h = (N - 1) * p, con p en [0,1] e índices base 0.
        var rank = (sortedAscending.Count - 1) * (percentile / 100.0);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedAscending[lower];
        var weight = rank - lower;
        return sortedAscending[lower] * (1 - weight) + sortedAscending[upper] * weight;
    }
}
