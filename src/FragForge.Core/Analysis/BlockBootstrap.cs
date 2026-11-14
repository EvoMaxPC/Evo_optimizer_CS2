using System.Globalization;

namespace FragForge.Core.Analysis;

/// <summary>Intervalo de confianza al 95 %, o null con motivo si no se puede calcular.</summary>
public sealed record ConfidenceInterval(double Low, double High, int Iterations)
{
    public double Width => High - Low;
}

/// <summary>IC de una corrida: uno por métrica, cada uno puede faltar por su propio motivo.</summary>
public sealed record RunConfidenceIntervals
{
    public ConfidenceInterval? P99FrameTimeMs { get; init; }
    public string? P99UnavailableReason { get; init; }
    public ConfidenceInterval? Low1PercentAverageFps { get; init; }
    public string? Low1PercentUnavailableReason { get; init; }

    /// <summary>Longitud del bloque en frames derivada del frametime medio, para poder auditar el cálculo.</summary>
    public int BlockLengthFrames { get; init; }

    /// <summary>Iteraciones efectivamente ejecutadas.</summary>
    public int Iterations { get; init; }
}

/// <summary>
/// Bootstrap de bloques móviles para dar un IC del 95 % A UNA SOLA CORRIDA.
/// </summary>
/// <remarks>
/// <para>
/// <b>Qué mide y qué NO mide.</b> Este intervalo cuantifica la incertidumbre DENTRO de una captura: cuánto
/// cambiaría el número si esta misma sesión se hubiera grabado en otro tramo de tiempo. NO mide la
/// variación ENTRE corridas (otro arranque del juego, otra temperatura, otro estado del sistema), que suele
/// ser mayor. Por eso el veredicto de A vs B NO se apoya en estos intervalos: se decide comparando los
/// valores de cada corrida como conjunto (ver <see cref="RunSetComparer"/>). Aquí el IC es información de
/// apoyo por corrida, nada más.
/// </para>
/// <para>
/// <b>Por qué bloques y no frames sueltos.</b> Los frametimes están autocorrelacionados: un frame lento
/// suele arrastrar a los siguientes. Remuestrear frames sueltos rompería esa estructura y produciría un IC
/// artificialmente estrecho. Se remuestrean bloques contiguos de <see cref="BlockSeconds"/> segundos, que
/// conservan las rachas locales de tirones.
/// </para>
/// </remarks>
public static class BlockBootstrap
{
    /// <summary>
    /// Longitud del bloque en SEGUNDOS. Se convierte a frames con el frametime medio de la propia captura,
    /// porque el número de frames por segundo cambia entre corridas y fijarlo en frames haría que el bloque
    /// durara distinto en cada una. HEURÍSTICA NO VALIDADA: 1 s es un compromiso entre preservar las rachas
    /// y tener suficientes bloques distintos.
    /// </summary>
    public const double BlockSeconds = 1.0;

    /// <summary>
    /// HEURÍSTICA NO VALIDADA. 1000 remuestreos es el mínimo habitual para cuantiles al 2.5/97.5 sin que el
    /// propio remuestreo domine la variación. No se validó contra estos datos.
    /// </summary>
    public const int BootstrapIterations = 1000;

    /// <summary>Nivel del intervalo: 95 %. Fijo, con nombre, para que aparezca en el DTO y en el texto.</summary>
    public const double ConfidenceLevel = 0.95;

    /// <summary>
    /// Calcula los IC. <paramref name="seed"/> es obligatorio: sin semilla, dos ejecuciones del mismo
    /// fichero darían números distintos y el informe dejaría de ser reproducible.
    /// </summary>
    public static RunConfidenceIntervals Compute(IReadOnlyList<double> frameTimesMs, int seed)
    {
        if (frameTimesMs.Count == 0)
            return new RunConfidenceIntervals
            {
                P99UnavailableReason = "sin frames.",
                Low1PercentUnavailableReason = "sin frames."
            };

        var meanMs = frameTimesMs.Sum() / frameTimesMs.Count;
        if (meanMs <= 0)
            return new RunConfidenceIntervals
            {
                P99UnavailableReason = "frametime medio no positivo: no se puede derivar el tamaño de bloque.",
                Low1PercentUnavailableReason = "frametime medio no positivo: no se puede derivar el tamaño de bloque."
            };

        // frames por bloque = BlockSeconds * (1000 / meanMs); al menos 1 y nunca más que la serie entera.
        var derived = (int)Math.Round(BlockSeconds * (1000.0 / meanMs));
        var blockLength = Math.Clamp(derived, 1, frameTimesMs.Count);
        var blockCount = frameTimesMs.Count / blockLength;

        if (blockCount < 2)
            return new RunConfidenceIntervals
            {
                BlockLengthFrames = blockLength,
                P99UnavailableReason = Insufficient(frameTimesMs.Count, blockCount, blockLength),
                Low1PercentUnavailableReason = Insufficient(frameTimesMs.Count, blockCount, blockLength)
            };

        var random = new Random(seed);
        var p99Samples = new List<double>(BootstrapIterations);
        var low1Samples = new List<double>(BootstrapIterations);
        var worstCount = (int)Math.Ceiling(0.01 * frameTimesMs.Count);

        var resampled = new double[blockCount * blockLength];
        for (var iteration = 0; iteration < BootstrapIterations; iteration++)
        {
            // Bloques móviles: cada uno empieza en una posición al azar entre 0 y N - blockLength.
            for (var b = 0; b < blockCount; b++)
            {
                var start = random.Next(0, frameTimesMs.Count - blockLength + 1);
                for (var i = 0; i < blockLength; i++) resampled[b * blockLength + i] = frameTimesMs[start + i];
            }

            Array.Sort(resampled);
            p99Samples.Add(FrameStatisticsCalculator.Percentile(resampled, 99.0));
            if (worstCount > 0 && resampled.Length >= FrameStatisticsCalculator.MinFramesForLow1Percent)
            {
                // El 1 % low solo se remuestrea si la corrida ya tenía frames suficientes: si no, el IC de
                // una métrica que de todos modos es NULL no aporta nada.
                var average = resampled[(resampled.Length - worstCount)..].Sum() / worstCount;
                if (average > 0) low1Samples.Add(1000.0 / average);
            }
        }

        var p99 = QuantileInterval(p99Samples);
        var low1 = low1Samples.Count == 0
            ? null
            : QuantileInterval(low1Samples);

        return new RunConfidenceIntervals
        {
            P99FrameTimeMs = p99,
            Low1PercentAverageFps = low1,
            Low1PercentUnavailableReason = low1 is null
                ? "insuficiente: " + frameTimesMs.Count.ToString(CultureInfo.InvariantCulture) + " frames, se necesitan " + FrameStatisticsCalculator.MinFramesForLow1Percent.ToString(CultureInfo.InvariantCulture) + " para el peor 1 %."
                : null,
            BlockLengthFrames = blockLength,
            Iterations = BootstrapIterations
        };
    }

    private static string Insufficient(int frames, int blocks, int blockLength)
    {
        var culture = CultureInfo.InvariantCulture;
        return "insuficiente: " + frames.ToString(culture) + " frames dan " + blocks.ToString(culture) +
               " bloque(s) de " + blockLength.ToString(culture) + " frames, se necesitan 2.";
    }

    /// <summary>IC por cuantiles 2.5 % y 97.5 % de las muestras bootstrap, con el mismo método de percentil.</summary>
    private static ConfidenceInterval QuantileInterval(List<double> samples)
    {
        var ordered = samples.OrderBy(v => v).ToArray();
        var alpha = (1 - ConfidenceLevel) / 2 * 100;
        return new ConfidenceInterval(
            FrameStatisticsCalculator.Percentile(ordered, alpha),
            FrameStatisticsCalculator.Percentile(ordered, 100 - alpha),
            ordered.Length);
    }
}
