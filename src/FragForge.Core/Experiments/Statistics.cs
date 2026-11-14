namespace FragForge.Core.Experiments;

/// <summary>
/// Estadística pura y determinista. Ningún método usa <see cref="Random"/> sin semilla explícita
/// ni depende de la cultura actual. Todos los cálculos trabajan sobre <c>double[]</c> ya ordenados
/// o los ordenan internamente sin mutar la entrada.
/// </summary>
public static class Statistics
{
    /// <summary>Percentil con interpolación lineal (equivalente al método "linear" de numpy/Excel PERCENTILE.INC).</summary>
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return double.NaN;
        if (percentile is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentile), "El percentil debe estar en [0,100].");
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1) return sorted[0];
        var rank = (sorted.Length - 1) * (percentile / 100.0);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        var weight = rank - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    public static double Median(IReadOnlyList<double> values) => Percentile(values, 50);

    /// <summary>Media aritmética. Devuelve <see cref="double.NaN"/> con entrada vacía (no cero).</summary>
    public static double Mean(IReadOnlyList<double> values) => values.Count == 0 ? double.NaN : values.Sum() / values.Count;

    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return double.NaN;
        var mean = Mean(values);
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }

    /// <summary>
    /// "1% low" en FPS: 1000 / media de los frametimes del peor 1 %.
    /// El peor 1 % son los frametimes MÁS ALTOS (frames más lentos).
    /// </summary>
    public static double LowFps(IReadOnlyList<double> frameTimesMs, double worstFraction)
    {
        if (frameTimesMs.Count == 0) return double.NaN;
        var sorted = frameTimesMs.OrderByDescending(v => v).ToArray();
        var count = Math.Max(1, (int)Math.Ceiling(sorted.Length * worstFraction));
        var average = sorted.Take(count).Sum() / count;
        return average <= 0 ? double.NaN : 1000.0 / average;
    }

    /// <summary>Delta de Cliff: (mayores - menores) / (n*m), en [-1, 1]. Positivo ⇒ <paramref name="a"/> tiende a ser mayor.</summary>
    public static double CliffsDelta(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count == 0 || b.Count == 0) return double.NaN;
        var greater = 0;
        var lesser = 0;
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                if (x > y) greater++;
                else if (x < y) lesser++;
            }
        }
        return (double)(greater - lesser) / (a.Count * b.Count);
    }

    /// <summary>
    /// Diferencia de medianas con IC95 por bootstrap percentil. Determinista para la misma semilla.
    /// Usa <see cref="Random"/> con semilla explícita y un solo flujo de números para que el resultado
    /// sea reproducible bit a bit en la misma versión del runtime.
    /// </summary>
    public static BootstrapResult BootstrapMedianDifference(IReadOnlyList<double> a, IReadOnlyList<double> b, int iterations = 2000, int seed = 20260101, double confidence = 0.95)
    {
        if (a.Count == 0 || b.Count == 0) return BootstrapResult.Empty;
        iterations = Math.Clamp(iterations, 200, 20000);
        var random = new Random(seed);
        var differences = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var sampleA = Resample(a, random);
            var sampleB = Resample(b, random);
            differences[i] = Median(sampleA) - Median(sampleB);
        }
        Array.Sort(differences);
        var alpha = (1 - confidence) / 2.0;
        var lower = PercentileOfSorted(differences, alpha);
        var upper = PercentileOfSorted(differences, 1 - alpha);
        var point = Median(a) - Median(b);
        return new BootstrapResult(point, lower, upper, iterations, seed, differences.Any(double.IsNaN));
    }

    private static double[] Resample(IReadOnlyList<double> source, Random random)
    {
        var result = new double[source.Count];
        for (var i = 0; i < source.Count; i++) result[i] = source[random.Next(source.Count)];
        return result;
    }

    private static double PercentileOfSorted(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return double.NaN;
        var rank = (sorted.Length - 1) * fraction;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        var weight = rank - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    /// <summary>
    /// Test exacto de permutación sobre la diferencia de medias.
    /// Con n ≤ 8 por brazo enumera TODAS las particiones (exacto). Con n mayor hace muestreo monte-carlo.
    /// </summary>
    public static PermutationResult PermutationTest(IReadOnlyList<double> a, IReadOnlyList<double> b, double alpha = 0.05, int monteCarloIterations = 20000, int seed = 20260101)
    {
        if (a.Count == 0 || b.Count == 0) return PermutationResult.Empty;
        var observed = Mean(a) - Mean(b);
        var pooled = a.Concat(b).ToArray();
        var n = a.Count;
        var total = pooled.Length;
        var exact = n <= 8 && b.Count <= 8;

        if (exact)
        {
            var count = 0L;
            var atLeastAsExtreme = 0L;
            var indices = new int[n];
            // Enumeración de combinaciones con orden lexicográfico para ser determinista.
            for (var i = 0; i < n; i++) indices[i] = i;
            while (true)
            {
                count++;
                var sumA = 0.0;
                var inA = new bool[total];
                foreach (var index in indices) { sumA += pooled[index]; inA[index] = true; }
                var meanA = sumA / n;
                var sumB = 0.0;
                foreach (var v in pooled.Where((_, i) => !inA[i])) sumB += v;
                var meanB = sumB / (total - n);
                if (Math.Abs(meanA - meanB) >= Math.Abs(observed) - 1e-12) atLeastAsExtreme++;
                var k = n - 1;
                while (k >= 0 && indices[k] == total - n + k) k--;
                if (k < 0) break;
                indices[k]++;
                for (var j = k + 1; j < n; j++) indices[j] = indices[j - 1] + 1;
            }
            return new PermutationResult(atLeastAsExtreme / (double)count, count, observed, true);
        }

        var random = new Random(seed);
        var iterations = Math.Clamp(monteCarloIterations, 1000, 200000);
        var extreme = 0;
        var assignment = new bool[total];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            for (var i = 0; i < total; i++) assignment[i] = i < n;
            for (var i = total - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (assignment[i], assignment[j]) = (assignment[j], assignment[i]);
            }
            var sumA = 0.0;
            var countA = 0;
            var sumB = 0.0;
            var countB = 0;
            for (var i = 0; i < total; i++)
            {
                if (assignment[i]) { sumA += pooled[i]; countA++; }
                else { sumB += pooled[i]; countB++; }
            }
            if (countA == 0 || countB == 0) continue;
            if (Math.Abs(sumA / countA - sumB / countB) >= Math.Abs(observed) - 1e-12) extreme++;
        }
        return new PermutationResult(extreme / (double)iterations, iterations, observed, false);
    }
}

public sealed record BootstrapResult(double PointEstimate, double LowerBound, double UpperBound, int Iterations, int Seed, bool HasNaN)
{
    public static BootstrapResult Empty => new(double.NaN, double.NaN, double.NaN, 0, 0, true);
    public bool IsValid => !HasNaN && !double.IsNaN(LowerBound) && !double.IsNaN(UpperBound);
    public bool ExcludesZero => IsValid && (LowerBound > 0 || UpperBound < 0);
    public double Width => IsValid ? UpperBound - LowerBound : double.NaN;
}

public sealed record PermutationResult(double PValue, long Permutations, double ObservedDifference, bool IsExact)
{
    public static PermutationResult Empty => new(double.NaN, 0, double.NaN, false);
    public bool IsSignificant(double alpha) => !double.IsNaN(PValue) && PValue < alpha;
}

/// <summary>
/// Piso de ruido estimado con una calibración A/A. Un efecto solo cuenta como real si lo supera.
/// </summary>
public sealed record NoiseFloor(double P1LowPct, double AvgFpsPct, double JitterPct, int RunsPerArm, bool IsEstimated)
{
    public static NoiseFloor Unknown => new(double.NaN, double.NaN, double.NaN, 0, false);
    /// <summary>Piso por defecto cuando no hay calibración A/A: conservador y documentado, nunca cero.</summary>
    public static NoiseFloor ConservativeDefault => new(5.0, 2.0, 5.0, 0, false);
}
