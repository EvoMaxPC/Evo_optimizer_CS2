namespace FragForge.Core.Experiments;

/// <summary>
/// Serie de frames de una corrida. Los frametimes están en milisegundos y en orden de presentación.
/// <see cref="IsPartial"/> marca una captura cancelada: la corrida es inválida y nunca entra en el veredicto.
/// </summary>
public sealed record FrameSeries
{
    public IReadOnlyList<double> FrameTimesMs { get; init; } = [];
    public IReadOnlyList<double?> CpuBusyMs { get; init; } = [];
    public IReadOnlyList<double?> GpuBusyMs { get; init; } = [];
    public IReadOnlyList<string> PresentModes { get; init; } = [];
    public bool IsPartial { get; init; }
    public string? SourceColumns { get; init; }
    public int FrameCount => FrameTimesMs.Count;
    public bool IsEmpty => FrameTimesMs.Count == 0;
}

/// <summary>
/// Métricas derivadas de una corrida. Todas las definiciones son exactas y están implementadas en
/// <see cref="FrameMetricsCalculator"/>:
/// <list type="bullet">
/// <item><c>avgFps</c> — frames / tiempo transcurrido, calculado sobre el recorte de calentamiento.</item>
/// <item><c>p1Low</c> — 1000 / media de los frametimes del peor 1 % (los más altos).</item>
/// <item><c>p01Low</c> — idéntico con el peor 0.1 %.</item>
/// <item><c>frametimeP50/P99/P999</c> — percentiles de frametime en ms.</item>
/// <item><c>jitterMs</c> — P99 − P50: dispersión robusta, no depende de la media.</item>
/// <item><c>frameTimeCv</c> — desviación estándar / media de frametime (coeficiente de variación).</item>
/// <item><c>stutterPct</c> — % de frames con frametime &gt; 2× la mediana móvil de ±60 frames.</item>
/// <item><c>cpuBusyP95 / gpuBusyP95</c> — P95 del tiempo ocupado si el CSV trae esas columnas.</item>
/// </list>
/// Una métrica sin datos es <c>null</c> (Unknown), nunca 0.
/// </summary>
public sealed record RunMetrics
{
    public double? AvgFps { get; init; }
    public double? P1Low { get; init; }
    public double? P01Low { get; init; }
    public double? FrameTimeP50 { get; init; }
    public double? FrameTimeP99 { get; init; }
    public double? FrameTimeP999 { get; init; }
    public double? JitterMs { get; init; }
    public double? FrameTimeCv { get; init; }
    public double? StutterPct { get; init; }
    public double? CpuBusyP95 { get; init; }
    public double? GpuBusyP95 { get; init; }
    public string? PresentModePrimary { get; init; }
    public IReadOnlyDictionary<string, double> PresentModeDistribution { get; init; } = new Dictionary<string, double>();
    public int FramesUsed { get; init; }
    public double AnalyzedDurationSeconds { get; init; }
    public double WarmupSkippedSeconds { get; init; }
    public IReadOnlyList<string> MissingMetrics { get; init; } = [];
}

public static class FrameMetricsCalculator
{
    public const double DefaultWarmupSeconds = 10.0;
    private const int StutterWindowRadius = 60;

    /// <summary>
    /// Calcula las métricas de una corrida. <paramref name="warmupSeconds"/> descarta los primeros segundos
    /// (compilación de shaders, carga de mapa). El recorte efectivo se devuelve en <see cref="RunMetrics.WarmupSkippedSeconds"/>.
    /// </summary>
    public static RunMetrics Compute(FrameSeries series, double warmupSeconds = DefaultWarmupSeconds)
    {
        var missing = new List<string>();
        var warmup = Math.Max(0, warmupSeconds);

        var frameTimes = series.FrameTimesMs.Where(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
        if (frameTimes.Length == 0) return new RunMetrics { MissingMetrics = ["all"], WarmupSkippedSeconds = warmup };

        // Recorte de calentamiento: acumulamos tiempo hasta superar el umbral.
        var skipped = 0;
        var elapsed = 0.0;
        while (skipped < frameTimes.Length && elapsed < warmup) { elapsed += frameTimes[skipped]; skipped++; }
        var analyzed = frameTimes[skipped..];
        if (analyzed.Length == 0)
        {
            analyzed = frameTimes;
            skipped = 0;
            missing.Add("warmupExceedsCapture");
        }

        var durationMs = analyzed.Sum();
        var avgFps = durationMs > 0 ? analyzed.Length / (durationMs / 1000.0) : (double?)null;
        if (avgFps is null) missing.Add("avgFps");

        var p50 = Statistics.Percentile(analyzed, 50);
        var p99 = Statistics.Percentile(analyzed, 99);
        var p999 = Statistics.Percentile(analyzed, 99.9);
        var mean = Statistics.Mean(analyzed);
        var std = Statistics.StandardDeviation(analyzed);

        var cpu = AlignedP95(series.CpuBusyMs, series.FrameTimesMs, skipped);
        var gpu = AlignedP95(series.GpuBusyMs, series.FrameTimesMs, skipped);
        if (cpu is null) missing.Add("cpuBusyP95");
        if (gpu is null) missing.Add("gpuBusyP95");

        var (primary, distribution) = PresentModeDistribution(series.PresentModes, skipped);
        if (primary is null) missing.Add("presentMode");

        return new RunMetrics
        {
            AvgFps = avgFps,
            P1Low = Valid(Statistics.LowFps(analyzed, 0.01)),
            P01Low = Valid(Statistics.LowFps(analyzed, 0.001)),
            FrameTimeP50 = p50,
            FrameTimeP99 = p99,
            FrameTimeP999 = p999,
            JitterMs = Valid(p99 - p50),
            FrameTimeCv = mean > 0 && !double.IsNaN(std) ? std / mean : null,
            StutterPct = StutterPercent(analyzed),
            CpuBusyP95 = cpu,
            GpuBusyP95 = gpu,
            PresentModePrimary = primary,
            PresentModeDistribution = distribution,
            FramesUsed = analyzed.Length,
            AnalyzedDurationSeconds = durationMs / 1000.0,
            WarmupSkippedSeconds = warmup,
            MissingMetrics = missing
        };
    }

    /// <summary>
    /// % de frames cuyo frametime supera 2× la mediana móvil de una ventana de ±60 frames.
    /// La mediana móvil se calcula con fuerza bruta en ventanas pequeñas: 121 muestras, O(n·121),
    /// suficientemente barato y sin aproximaciones.
    /// </summary>
    public static double? StutterPercent(IReadOnlyList<double> frameTimes)
    {
        if (frameTimes.Count < 3) return null;
        var stutters = 0;
        var counted = 0;
        for (var i = 0; i < frameTimes.Count; i++)
        {
            var from = Math.Max(0, i - StutterWindowRadius);
            var to = Math.Min(frameTimes.Count - 1, i + StutterWindowRadius);
            var window = new double[to - from + 1];
            for (var j = from; j <= to; j++) window[j - from] = frameTimes[j];
            var median = Statistics.Median(window);
            if (median <= 0) continue;
            counted++;
            if (frameTimes[i] > 2 * median) stutters++;
        }
        return counted == 0 ? null : stutters * 100.0 / counted;
    }

    private static double? Valid(double value) => double.IsNaN(value) || double.IsInfinity(value) ? null : value;

    private static double? AlignedP95(IReadOnlyList<double?> values, IReadOnlyList<double> frameTimes, int skip)
    {
        if (values.Count == 0 || values.Count != frameTimes.Count) return null;
        var usable = new List<double>();
        for (var i = skip; i < values.Count; i++)
        {
            var value = values[i];
            if (value is > 0 && !double.IsNaN(value.Value)) usable.Add(value.Value);
        }
        if (usable.Count < 10) return null;
        return Valid(Statistics.Percentile(usable, 95));
    }

    private static (string? Primary, IReadOnlyDictionary<string, double> Distribution) PresentModeDistribution(IReadOnlyList<string> modes, int skip)
    {
        if (modes.Count == 0) return (null, new Dictionary<string, double>());
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = Math.Min(skip, modes.Count - 1); i < modes.Count; i++)
        {
            var mode = modes[i];
            if (string.IsNullOrWhiteSpace(mode)) continue;
            counts[mode] = counts.GetValueOrDefault(mode) + 1;
        }
        var total = counts.Values.Sum();
        if (total == 0) return (null, new Dictionary<string, double>());
        var distribution = counts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value * 100.0 / total, StringComparer.OrdinalIgnoreCase);
        return (distribution.Keys.First(), distribution);
    }
}
