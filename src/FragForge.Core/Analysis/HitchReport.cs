using System.Globalization;

namespace FragForge.Core.Analysis;

/// <summary>
/// Tirones ("hitches") de una corrida. Un tirón es un frame que dura bastante más que el típico; no es un
/// diagnóstico de la causa, solo un recuento de lo que pasó.
/// </summary>
public sealed record HitchReport
{
    /// <summary>Umbral efectivo usado, en ms (= <see cref="HitchAnalyzer.HitchFactor"/> * mediana).</summary>
    public double ThresholdMs { get; init; }

    /// <summary>Mediana usada como referencia del umbral.</summary>
    public double MedianMs { get; init; }

    /// <summary>Frames por encima del umbral (incluye pausas si no se excluyeron).</summary>
    public int HitchCount { get; init; }

    /// <summary>Porcentaje de frames que son tirones, sobre los frames analizados.</summary>
    public double HitchPercent { get; init; }

    /// <summary>El frame más largo de la captura, en ms.</summary>
    public double? LongestFrameMs { get; init; }

    /// <summary>Tirones por minuto, con la duración = suma de frametimes analizados.</summary>
    public double? HitchesPerMinute { get; init; }

    /// <summary>Frames por encima de <see cref="HitchAnalyzer.PauseThresholdMs"/>: se informan, no se ocultan.</summary>
    public int PauseCount { get; init; }

    /// <summary>Si las pausas se excluyeron del análisis. Se informa SIEMPRE junto a los números.</summary>
    public bool PausesExcluded { get; init; }

    /// <summary>Frames descartados por calentamiento (solo si se pidió).</summary>
    public int WarmupFramesDropped { get; init; }

    /// <summary>Segundos descartados por calentamiento.</summary>
    public double WarmupSecondsApplied { get; init; }

    /// <summary>Frames que quedaron tras aplicar calentamiento y exclusiones.</summary>
    public int AnalyzedFrames { get; init; }

    /// <summary>Avisos en texto para el informe (p. ej. "se excluyeron N pausas").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Detección de tirones y pausas. Funciones puras.</summary>
public static class HitchAnalyzer
{
    /// <summary>
    /// HEURÍSTICA NO VALIDADA. Un frame que dura 3 veces la mediana se percibe como tirón en la mayoría de
    /// juegos. El 3 no sale de un estudio con estos datos: es el valor habitual en la literatura de
    /// stutter y queda como parámetro revisable.
    /// </summary>
    public const double HitchFactor = 3.0;

    /// <summary>
    /// HEURÍSTICA NO VALIDADA. Por encima de 1000 ms (un segundo) el frame no es un tirón: es una pausa
    /// (carga, alt-tab, minimizado). Se informan aparte porque contaminan la media y el "peor 1 %" de forma
    /// desproporcionada. Por defecto SE INCLUYEN: quitarlas cambia el resultado y eso no puede pasar en
    /// silencio.
    /// </summary>
    public const double PauseThresholdMs = 1000.0;

    /// <summary>Valor por defecto de <c>--warmup-seconds</c>: sin descarte. Se aplica solo si se pide.</summary>
    public const double DefaultWarmupSeconds = 0.0;

    /// <summary>
    /// Analiza una serie. Devuelve el informe Y los frames efectivamente usados (tras calentamiento, y tras
    /// quitar pausas si <paramref name="excludePauses"/>), para que el resto de métricas use exactamente el
    /// mismo conjunto.
    /// </summary>
    public static (HitchReport Report, IReadOnlyList<double> AnalyzedFrames) Analyze(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds = DefaultWarmupSeconds,
        bool excludePauses = false)
    {
        var notes = new List<string>();

        // 1) Calentamiento: se descartan frames ENTEROS desde el principio hasta cubrir warmupSeconds.
        // OJO con las unidades: los frametimes vienen en MILISEGUNDOS y warmupSeconds en SEGUNDOS. Se compara
        // en ms (warmupSeconds * 1000); comparar ms contra s directamente descartaba casi todo el calentamiento
        // (con warmup 1 s y frames de 100 ms paraba en el primero, no en el décimo).
        var warmupMs = warmupSeconds * 1000.0;
        var dropped = 0;
        var elapsedMs = 0.0;
        while (dropped < frameTimesMs.Count && elapsedMs < warmupMs)
        {
            elapsedMs += frameTimesMs[dropped];
            dropped++;
        }
        if (warmupSeconds > 0)
            notes.Add("Calentamiento: se descartaron " + dropped.ToString(CultureInfo.InvariantCulture) + " frames (" + (elapsedMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " s) de " + frameTimesMs.Count.ToString(CultureInfo.InvariantCulture) + ".");

        var afterWarmup = new List<double>(frameTimesMs.Count - dropped);
        for (var i = dropped; i < frameTimesMs.Count; i++) afterWarmup.Add(frameTimesMs[i]);

        // 2) Pausas: se cuentan SIEMPRE; se quitan solo si se pidió, y se dice cuántas.
        var pauseCount = afterWarmup.Count(v => v > PauseThresholdMs);
        IReadOnlyList<double> analyzed = afterWarmup;
        if (excludePauses && pauseCount > 0)
        {
            analyzed = afterWarmup.Where(v => v <= PauseThresholdMs).ToArray();
            notes.Add("Se excluyeron " + pauseCount.ToString(CultureInfo.InvariantCulture) + " pausa(s) de más de " + PauseThresholdMs.ToString("0", CultureInfo.InvariantCulture) + " ms. Sin excluirlas el resultado sería distinto.");
        }
        else if (pauseCount > 0)
        {
            notes.Add("Hay " + pauseCount.ToString(CultureInfo.InvariantCulture) + " pausa(s) de más de " + PauseThresholdMs.ToString("0", CultureInfo.InvariantCulture) + " ms INCLUIDAS en las métricas. Usa --exclude-pauses para quitarlas.");
        }

        if (analyzed.Count == 0)
            return (new HitchReport
            {
                WarmupFramesDropped = dropped,
                WarmupSecondsApplied = warmupSeconds,
                PauseCount = pauseCount,
                PausesExcluded = excludePauses,
                AnalyzedFrames = 0,
                Notes = notes
            }, analyzed);

        var sorted = analyzed.OrderBy(v => v).ToArray();
        var median = FrameStatisticsCalculator.Percentile(sorted, 50.0);
        var threshold = HitchFactor * median;
        var hitches = analyzed.Count(v => v > threshold);
        var durationMs = analyzed.Sum();

        return (new HitchReport
        {
            ThresholdMs = threshold,
            MedianMs = median,
            HitchCount = hitches,
            HitchPercent = 100.0 * hitches / analyzed.Count,
            LongestFrameMs = sorted[^1],
            HitchesPerMinute = durationMs > 0 ? hitches / (durationMs / 60_000.0) : null,
            PauseCount = pauseCount,
            PausesExcluded = excludePauses,
            WarmupFramesDropped = dropped,
            WarmupSecondsApplied = warmupSeconds,
            AnalyzedFrames = analyzed.Count,
            Notes = notes
        }, analyzed);
    }
}
