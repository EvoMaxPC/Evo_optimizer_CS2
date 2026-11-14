using System.Globalization;
using System.Text.Json;
using FragForge.Core.Analysis;
using FragForge.Core.Logging;

namespace FragForge.Cli;

/// <summary>
/// <c>fragforge analyze</c>: lee capturas de PresentMon ya hechas y describe lo que hay en ellas.
/// </summary>
/// <remarks>
/// <para>
/// <b>Solo lectura.</b> Este comando abre ficheros CSV y escribe por consola. No lanza PresentMon, no toca
/// el registro, no para ni arranca servicios, no modifica nada y no necesita administrador.
/// </para>
/// <para>
/// <b>Mide y describe; no promete.</b> No dice "+X FPS" ni porcentajes de mejora, no diagnostica cuellos de
/// botella y no afirma que una condición vaya a ser mejor en el futuro. Un veredicto
/// <see cref="OverallVerdict.Mejora"/> significa "en estas corridas, los conjuntos no se solapan así", nada más.
/// </para>
/// <para>
/// <b>Salida inyectable.</b> Todo el texto va a un <see cref="TextWriter"/> que por defecto es
/// <see cref="Console.Out"/>. Es lo que permite probar la salida sin redirigir el <c>Console</c> global, que
/// en xUnit es estado compartido entre tests que corren en paralelo.
/// </para>
/// </remarks>
public static class AnalyzeCommands
{
    /// <summary>Semilla del bootstrap. Fija para que el mismo CSV dé siempre el mismo informe.</summary>
    public const int BootstrapSeed = 20260101;

    /// <summary>Punto de entrada desde <c>Program</c>. Escribe en <see cref="Console.Out"/> y <see cref="Console.Error"/>.</summary>
    public static int Analyze(ParsedArgs parsed, IReadOnlyList<string> argv) =>
        Analyze(parsed, argv, Console.Out, Console.Error);

    /// <summary>
    /// Igual que <see cref="Analyze(ParsedArgs, IReadOnlyList{string})"/> pero con flujos explícitos, para
    /// que un test pueda capturar el texto sin tocar el <c>Console</c> del proceso.
    /// </summary>
    public static int Analyze(ParsedArgs parsed, IReadOnlyList<string> argv, TextWriter output, TextWriter error)
    {
        // --versus toma VARIOS CSV seguidos: la línea se parte ANTES de interpretarla, porque el parser
        // metería los CSV de B entre los posicionales de A (ver CommandLineParser.SplitAt). Con la opción
        // ausente, SplitAt("...") == [] y "lo de antes" es la línea entera: una sola rama para ambos casos.
        var (before, versus) = CommandLineParser.SplitAt("versus", argv.ToArray());
        var paths = before.Positionals;
        // Las opciones globales (--json, --warmup-seconds, --exclude-pauses) pueden ir antes o después de
        // --versus: se miran en ambas mitades y gana la segunda si está en las dos.
        var warmupRaw = parsed.Value("warmup-seconds") ?? before.Value("warmup-seconds");
        var warmup = ParseWarmup(warmupRaw);
        var excludePauses = parsed.Has("exclude-pauses") || before.Has("exclude-pauses");
        var json = parsed.Has("json") || before.Has("json");

        if (versus.Count == 0)
        {
            if (paths.Count == 0)
            {
                error.WriteLine("Falta el CSV: fragforge analyze <captura.csv>");
                return ExitCodes.Failure;
            }
            foreach (var path in paths)
            {
                var report = BuildReport(path, warmup, excludePauses, error);
                if (report is null) return ExitCodes.Failure;
                if (json) WriteJson(output, new { run = report });
                else WriteRun(report, output);
            }
            return ExitCodes.Success;
        }

        if (paths.Count == 0)
        {
            error.WriteLine("Con --versus hay que indicar al menos un CSV de la condición A.");
            return ExitCodes.Failure;
        }

        var a = new List<RunReport>();
        foreach (var path in paths)
        {
            var report = BuildReport(path, warmup, excludePauses, error);
            if (report is null) return ExitCodes.Failure;
            a.Add(report);
        }
        var b = new List<RunReport>();
        foreach (var path in versus)
        {
            var report = BuildReport(path, warmup, excludePauses, error);
            if (report is null) return ExitCodes.Failure;
            b.Add(report);
        }

        // Con varias (aplicación, swapchain) en un mismo CSV, comparar "una de ellas" sería elegir en
        // silencio cuál. Se rechaza y se pide separar las capturas: adivinar aquí invalidaría el veredicto.
        var mixed = a.Concat(b).Where(r => r.Series.Count > 1).Select(r => r.File).ToArray();
        if (mixed.Length > 0)
        {
            error.WriteLine("Estos CSV mezclan varias (aplicación, swapchain): " + string.Join(", ", mixed));
            error.WriteLine("Para comparar A vs B, cada CSV debe tener UNA sola serie. Analízalos por separado con 'fragforge analyze <csv>' para ver todas.");
            return ExitCodes.Failure;
        }
        if (a.Any(r => r.Series.Count == 0) || b.Any(r => r.Series.Count == 0))
        {
            error.WriteLine("Alguna captura no tiene frames utilizables: no se puede comparar.");
            return ExitCodes.Failure;
        }

        var comparison = RunSetComparer.Compare(
            a.Select(r => r.Metrics).ToArray(),
            b.Select(r => r.Metrics).ToArray(),
            a.Sum(r => r.Metrics.DurationSeconds),
            b.Sum(r => r.Metrics.DurationSeconds));

        if (json) WriteJson(output, new { a, b, comparison });
        else WriteComparison(a, b, comparison, output);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Serializa con la MISMA configuración que el resto del CLI (<see cref="JsonDefaults.Options"/>: camelCase,
    /// enums por NOMBRE de miembro — nunca su número — e ignora nulos). Se escribe al flujo inyectado en vez de
    /// usar <c>ConsoleOutput.Json</c> para que el test no dependa de <c>Console</c>.
    /// </summary>
    private static void WriteJson<T>(TextWriter output, T value) =>
        output.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));

    /// <summary>Lee un CSV y calcula todo. Null (con el motivo escrito en <paramref name="error"/>) si no sirve.</summary>
    private static RunReport? BuildReport(string path, double warmup, bool excludePauses, TextWriter error)
    {
        if (!File.Exists(path))
        {
            error.WriteLine("No existe el archivo: " + path);
            return null;
        }

        var stream = FrameReader.ReadFile(path);
        if (stream.UnavailableReason is { } reason)
        {
            // Sin columnas válidas o sin frames: Failure con mensaje, sin excepción.
            error.WriteLine("No se pueden analizar frames de " + path + ": " + reason);
            error.WriteLine("No se ha modificado nada. Revisa que sea un CSV de PresentMon con las columnas por defecto.");
            return null;
        }

        // Un CSV con varias aplicaciones o swapchains da varias series. Se analiza CADA UNA, sin mezclar.
        var series = new List<SeriesReport>();
        foreach (var key in stream.Series.Where(s => s.Primary.Count > 0))
        {
            var (hitch, analyzed) = HitchAnalyzer.Analyze(key.Primary, warmup, excludePauses);
            var stats = FrameStatisticsCalculator.Compute(analyzed, stream.NaFrames);
            var intervals = BlockBootstrap.Compute(analyzed, BootstrapSeed);
            series.Add(new SeriesReport(key.Application, key.SwapChain, stats, hitch, intervals));
        }

        return new RunReport(Path.GetFileName(path), stream.PrimaryColumn ?? FrameReader.PrimaryColumn, stream.SecondaryColumn, stream.NaFrames, stream.MalformedLines, series);
    }

    private static double ParseWarmup(string? raw)
    {
        if (raw is null) return HitchAnalyzer.DefaultWarmupSeconds;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new ArgumentException("--warmup-seconds debe ser un número >= 0 (por ejemplo 10 o 2.5).");
        return value;
    }

    private static string N(double? value, string format = "0.###") =>
        value is null ? "no disponible" : value.Value.ToString(format, CultureInfo.InvariantCulture);

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void WriteRun(RunReport report, TextWriter output)
    {
        output.WriteLine("Archivo: " + report.File);
        output.WriteLine(report.SecondaryColumn is null
            ? $"Columna principal: {report.PrimaryColumn} · la secundaria ({FrameReader.SecondaryColumn}) no está en el encabezado"
            : $"Columna principal: {report.PrimaryColumn} · columna secundaria presente: {report.SecondaryColumn}");
        foreach (var series in report.Series)
        {
            output.WriteLine();
            output.WriteLine($"=== {series.Application} · swapchain {series.SwapChain} ===");
            WriteStatistics(series.Stats, output);
            WriteHitch(series.Hitch, output);
            WriteConfidence(series.Intervals, output);
        }
        output.WriteLine();
        output.WriteLine($"Filas NA en la columna principal: {I(report.NaFrames)} · líneas mal formadas: {I(report.MalformedLines)}");
    }

    private static void WriteStatistics(FrameStatistics stats, TextWriter output)
    {
        output.WriteLine($"Frames analizados: {I(stats.FrameCount)} · NA: {I(stats.NaFrames)}");
        output.WriteLine($"Frametime medio: {N(stats.MeanMs)} ms · mediana: {N(stats.MedianMs)} ms · desvío estándar: {N(stats.StdDevMs)} ms");
        output.WriteLine($"Frametime mínimo: {N(stats.MinMs)} ms · máximo: {N(stats.MaxMs)} ms");
        output.WriteLine($"P99FrameTime (percentil 99 del frametime, interpolación lineal tipo 7): {N(stats.P99FrameTimeMs)} ms · {N(stats.P99Fps)} FPS");
        output.WriteLine($"P999FrameTime (percentil 99.9, mismo método): {N(stats.P999FrameTimeMs)} ms · {N(stats.P999Fps)} FPS");
        output.WriteLine($"Low1PercentAverage (media del peor 1 % de frames): {N(stats.Low1PercentAverageFps)} FPS");
        output.WriteLine($"Low01PercentAverage (media del peor 0.1 % de frames): {N(stats.Low01PercentAverageFps)} FPS");
        foreach (var (key, reason) in stats.UnavailableReasons) output.WriteLine($"  · {key}: no disponible — {reason}");
    }

    private static void WriteHitch(HitchReport hitch, TextWriter output)
    {
        output.WriteLine($"Umbral de tirón: {N(hitch.ThresholdMs)} ms ({HitchAnalyzer.HitchFactor.ToString("0.#", CultureInfo.InvariantCulture)} × mediana {N(hitch.MedianMs)} ms)");
        output.WriteLine($"Tirones: {I(hitch.HitchCount)} ({N(hitch.HitchPercent, "0.##")} % de los frames) · frame más largo: {N(hitch.LongestFrameMs)} ms · tirones por minuto: {N(hitch.HitchesPerMinute, "0.##")}");
        output.WriteLine($"Pausas (> {HitchAnalyzer.PauseThresholdMs.ToString("0", CultureInfo.InvariantCulture)} ms): {I(hitch.PauseCount)} · {(hitch.PausesExcluded ? "excluidas" : "incluidas")}");
        if (hitch.WarmupFramesDropped > 0) output.WriteLine($"Calentamiento descartado: {I(hitch.WarmupFramesDropped)} frames");
        foreach (var note in hitch.Notes) output.WriteLine("  · " + note);
    }

    private static void WriteConfidence(RunConfidenceIntervals intervals, TextWriter output)
    {
        output.WriteLine($"IC 95 % por bootstrap de bloques ({I(intervals.Iterations)} iteraciones, bloques de {I(intervals.BlockLengthFrames)} frames ≈ {BlockBootstrap.BlockSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s):");
        output.WriteLine("  · P99FrameTime: " + (intervals.P99FrameTimeMs is null
            ? "no disponible — " + intervals.P99UnavailableReason
            : $"{N(intervals.P99FrameTimeMs.Low)} a {N(intervals.P99FrameTimeMs.High)} ms"));
        output.WriteLine("  · Low1PercentAverage: " + (intervals.Low1PercentAverageFps is null
            ? "no disponible — " + intervals.Low1PercentUnavailableReason
            : $"{N(intervals.Low1PercentAverageFps.Low)} a {N(intervals.Low1PercentAverageFps.High)} FPS"));
        output.WriteLine("    Este intervalo mide la incertidumbre DENTRO de esta captura, no la variación entre corridas. No decide el veredicto.");
    }

    private static void WriteComparison(IReadOnlyList<RunReport> a, IReadOnlyList<RunReport> b, RunSetComparison comparison, TextWriter output)
    {
        output.WriteLine("Comparación por conjuntos de corridas");
        output.WriteLine();
        WriteCondition("A", a, output);
        WriteCondition("B", b, output);
        output.WriteLine();
        WriteMetric(comparison.Low1Percent, output);
        WriteMetric(comparison.P99FrameTime, output);
        WriteMetric(comparison.HitchesPerMinute, output);
        output.WriteLine();
        output.WriteLine("Veredicto global: " + comparison.Overall);
        output.WriteLine("  " + comparison.OverallReason);
        output.WriteLine();
        output.WriteLine("Avisos:");
        foreach (var warning in comparison.Warnings) output.WriteLine("  · " + warning);
    }

    private static void WriteCondition(string label, IReadOnlyList<RunReport> runs, TextWriter output)
    {
        output.WriteLine($"Condición {label}: {I(runs.Count)} corrida(s)");
        foreach (var run in runs)
        foreach (var series in run.Series)
            output.WriteLine($"  · {run.File} — Low1PercentAverage {N(series.Stats.Low1PercentAverageFps)} FPS · P99FrameTime {N(series.Stats.P99FrameTimeMs)} ms · media {N(series.Stats.MeanMs)} ms");
    }

    private static void WriteMetric(MetricComparison metric, TextWriter output)
    {
        output.WriteLine($"{metric.Name} [{(metric.Direction == BetterDirection.Higher ? "más alto es mejor" : "más bajo es mejor")}]");
        WriteConditionSummary(metric.A, output);
        WriteConditionSummary(metric.B, output);
        output.WriteLine($"  Veredicto: {metric.Verdict}" + (metric.BetterCondition is null ? "" : $" (mejor: {metric.BetterCondition})"));
        output.WriteLine("  " + metric.Reason);
    }

    private static void WriteConditionSummary(ConditionValues values, TextWriter output)
    {
        var items = values.Values.Select(v => N(v)).ToArray();
        output.WriteLine($"  {values.Condition}: " + string.Join(", ", items) +
                         $" · mediana {N(values.Median)} · rango {N(values.Min)}–{N(values.Max)}" +
                         (values.MissingRuns > 0 ? $" · {I(values.MissingRuns)} sin dato" : ""));
    }
}

/// <summary>Informe de un CSV. <c>Series</c> tiene una entrada por (aplicación, swapchain): nunca se mezclan.</summary>
public sealed record RunReport(
    string File,
    string PrimaryColumn,
    string? SecondaryColumn,
    int NaFrames,
    int MalformedLines,
    IReadOnlyList<SeriesReport> Series)
{
    /// <summary>
    /// Métricas de la PRIMERA serie. Solo se usa para comparar A/B, y <c>Analyze</c> rechaza antes los CSV
    /// con más de una serie: nunca se elige una swapchain en silencio para decidir un veredicto.
    /// </summary>
    public RunMetricSet Metrics => Series.Count == 0
        ? new RunMetricSet(File, null, null, null, 0)
        : new RunMetricSet(
            File,
            Series[0].Stats.Low1PercentAverageFps,
            Series[0].Stats.P99FrameTimeMs,
            Series[0].Hitch.HitchesPerMinute,
            Series[0].Stats.TotalDurationMs / 1000.0);
}

/// <summary>Una (aplicación, swapchain) con sus estadísticas, tirones e intervalos.</summary>
public sealed record SeriesReport(
    string Application,
    string SwapChain,
    FrameStatistics Stats,
    HitchReport Hitch,
    RunConfidenceIntervals Intervals);
