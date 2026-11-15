using System.Globalization;

namespace FragForge.Core.Analysis.Live;

/// <summary>Identidad de una serie en vivo: (Application, SwapChain). Las series nunca se suman.</summary>
public sealed record LiveSeriesKey(string Application, string SwapChain);

/// <summary>Foto de una vista del acumulador en vivo.</summary>
/// <param name="Label">Qué vista es: "total", "últimos N s" o "segmento".</param>
/// <param name="SeriesKey">La serie mostrada (la de más frames), o <c>null</c> si no hay frames.</param>
/// <param name="FrameCount">Frames de la serie mostrada en esta vista.</param>
/// <param name="SpanSeconds">Duración entre el primer y el último frame con tiempo, o <c>null</c> si no hay tiempo.</param>
/// <param name="Statistics">Estadísticas completas, o <c>null</c> si la vista no está disponible.</param>
/// <param name="Hitches">Tirones y pausas, o <c>null</c> si la vista no los calcula.</param>
/// <param name="Notes">Notas en texto (series extra, fuera de orden, topes, motivos de no disponibilidad).</param>
public sealed record LiveViewSnapshot(
    string Label,
    LiveSeriesKey? SeriesKey,
    long FrameCount,
    double? SpanSeconds,
    FrameStatistics? Statistics,
    HitchReport? Hitches,
    IReadOnlyList<string> Notes);

/// <summary>
/// Acumulador de frames en vivo para <c>fragforge match</c>. Un hilo alimenta (<see cref="Add"/>), otro lee
/// las vistas. Thread-safe: las vistas copian el tramo necesario bajo lock y calculan fuera del lock.
/// El tiempo interno es MILISEGUNDOS (<c>double</c>); la API pública lo expone en segundos.
/// </summary>
public sealed class LiveFrameAccumulator
{
    /// <summary>Límite de memoria, no validado. Tope de frames acumulados en total.</summary>
    public const long DefaultMaxFrames = 10_000_000;

    /// <summary>Ventana por defecto de <see cref="GetLastWindow"/>, en segundos.</summary>
    public const double DefaultWindowSeconds = 30.0;

    /// <summary>Texto cuando no hay columna de tiempo (ni TimeInMs ni TimeInSeconds).</summary>
    public const string NoTimeColumnNote = "no disponible: el CSV no tiene columna de tiempo (TimeInMs ni TimeInSeconds)";

    private readonly object _gate = new();
    private readonly Dictionary<(string Application, string SwapChain), SeriesData> _series = new();
    private readonly List<SeriesData> _seriesOrder = new();

    private long _totalStored;
    private double? _t0Ms;
    private bool _isComplete;
    private long _ignoredAfterCompletion;
    private long _outOfOrderTotal;
    private bool _reachedMaxFrames;
    private Dictionary<(string Application, string SwapChain), long>? _segmentStartCounts;
    private Dictionary<(string Application, string SwapChain), long>? _segmentEndCounts;

    /// <param name="targetDurationSeconds">Duración fija objetivo en segundos, o <c>null</c> para medir sin tope.</param>
    /// <param name="maxFrames">Tope de frames acumulados (límite de memoria, no validado).</param>
    public LiveFrameAccumulator(double? targetDurationSeconds = null, long maxFrames = DefaultMaxFrames)
    {
        TargetDurationSeconds = targetDurationSeconds;
        MaxFrames = maxFrames;
    }

    /// <summary>Duración fija objetivo en segundos, o <c>null</c> si no hay tope de duración.</summary>
    public double? TargetDurationSeconds { get; }

    /// <summary>Tope de frames acumulados (límite de memoria, no validado).</summary>
    public long MaxFrames { get; }

    /// <summary><c>true</c> cuando la duración fija se alcanzó: ya no se acumula más.</summary>
    public bool IsComplete
    {
        get { lock (_gate) return _isComplete; }
    }

    /// <summary>Frames recibidos después de completada la duración fija (no acumulados).</summary>
    public long IgnoredAfterCompletion
    {
        get { lock (_gate) return _ignoredAfterCompletion; }
    }

    /// <summary>Filas con tiempo menor que el anterior de su serie (se agregan igual).</summary>
    public long OutOfOrderRows
    {
        get { lock (_gate) return _outOfOrderTotal; }
    }

    /// <summary><c>true</c> cuando se alcanzó el tope <see cref="MaxFrames"/>: ya no se acumula más.</summary>
    public bool ReachedMaxFrames
    {
        get { lock (_gate) return _reachedMaxFrames; }
    }

    /// <summary>Agrega un frame ya parseado. Thread-safe.</summary>
    public void Add(LiveFrame frame)
    {
        lock (_gate)
        {
            if (_isComplete)
            {
                _ignoredAfterCompletion++;
                return;
            }

            // Duración fija: t0 es el tiempo del primer frame válido (con tiempo). El primero que falla
            // marca IsComplete; ese y el resto se cuentan en IgnoredAfterCompletion.
            if (TargetDurationSeconds is double targetSeconds && frame.TimeMs is double tMs)
            {
                _t0Ms ??= tMs;
                if (tMs - _t0Ms.Value >= targetSeconds * 1000.0)
                {
                    _isComplete = true;
                    _ignoredAfterCompletion++;
                    return;
                }
            }

            if (_totalStored >= MaxFrames)
            {
                _reachedMaxFrames = true;
                return;
            }

            var key = (frame.Application, frame.SwapChain);
            if (!_series.TryGetValue(key, out var series))
            {
                series = new SeriesData(frame.Application, frame.SwapChain);
                _series[key] = series;
                _seriesOrder.Add(series);
            }

            series.FrameTimesMs.Add(frame.FrameTimeMs);
            series.TimesMs.Add(frame.TimeMs);
            series.Sum += frame.FrameTimeMs;
            if (frame.FrameTimeMs < series.Min) series.Min = frame.FrameTimeMs;
            if (frame.FrameTimeMs > series.Max) series.Max = frame.FrameTimeMs;

            if (frame.TimeMs is double t)
            {
                series.TimedCount++;
                series.FirstTimeMs ??= t;
                series.LastTimeMs = t;
                if (series.PrevTimeMs is double prev && t < prev)
                {
                    series.OutOfOrder++;
                    _outOfOrderTotal++;
                }
                series.PrevTimeMs = t;
            }
            else
            {
                series.NullTimeCount++;
            }

            _totalStored++;
            if (_totalStored >= MaxFrames)
                _reachedMaxFrames = true;
        }
    }

    /// <summary>Marca el inicio del segmento (por cantidad de frames recibidos). Un nuevo MarkStart reinicia.</summary>
    public void MarkStart()
    {
        lock (_gate)
        {
            _segmentStartCounts = SnapshotCountsLocked();
            _segmentEndCounts = null;
        }
    }

    /// <summary>
    /// Marca el fin del segmento. Sin <see cref="MarkStart"/> previo es un no-op, sin excepción:
    /// <see cref="GetSegment"/> lo reporta como no disponible.
    /// </summary>
    public void MarkEnd()
    {
        lock (_gate)
        {
            if (_segmentStartCounts is null) return;
            _segmentEndCounts = SnapshotCountsLocked();
        }
    }

    /// <summary>Vista total rápida, O(1): frames, duración, media, mín, máx. Sin percentiles ni tirones.</summary>
    public LiveViewSnapshot GetTotalQuick()
    {
        lock (_gate)
        {
            var shown = PickShownSeriesLocked();
            var notes = BuildNotesLocked(shown);
            if (shown is null)
            {
                notes.Add("no disponible: no hay frames");
                return new LiveViewSnapshot("total", null, 0, null, null, null, notes);
            }

            var frameCount = shown.FrameTimesMs.Count;
            var spanSeconds = ComputeSpanSeconds(shown.FirstTimeMs, shown.LastTimeMs);
            if (shown.TimedCount == 0)
                notes.Add(NoTimeColumnNote);
            notes.Add("vista rápida: sin percentiles ni tirones (usa GetTotal)");

            var statistics = QuickStatistics(shown, frameCount);
            return new LiveViewSnapshot("total", KeyOf(shown), frameCount, spanSeconds, statistics, null, notes);
        }
    }

    /// <summary>Vista total completa, bajo demanda: con percentiles y tirones.</summary>
    public LiveViewSnapshot GetTotal()
    {
        List<double> frames;
        SeriesData? shown;
        List<string> notes;
        double? spanSeconds;

        lock (_gate)
        {
            shown = PickShownSeriesLocked();
            notes = BuildNotesLocked(shown);
            if (shown is null)
            {
                notes.Add("no disponible: no hay frames");
                return new LiveViewSnapshot("total", null, 0, null, null, null, notes);
            }

            frames = new List<double>(shown.FrameTimesMs);
            spanSeconds = ComputeSpanSeconds(shown.FirstTimeMs, shown.LastTimeMs);
            if (shown.TimedCount == 0)
                notes.Add(NoTimeColumnNote);
        }

        var statistics = FrameStatisticsCalculator.Compute(frames, 0);
        var hitches = HitchAnalyzer.Analyze(frames).Report;
        return new LiveViewSnapshot("total", KeyOf(shown), frames.Count, spanSeconds, statistics, hitches, notes);
    }

    /// <summary>
    /// Últimos <paramref name="seconds"/> segundos (deslizante). Se aceptan frames con
    /// <c>t_ms &gt; ultimo_ms - seconds * 1000.0</c> (estrictamente mayor).
    /// </summary>
    public LiveViewSnapshot GetLastWindow(double seconds = DefaultWindowSeconds)
    {
        var label = LabelForWindow(seconds);
        List<double> frames;
        SeriesData? shown;
        List<string> notes;
        double? spanSeconds;

        lock (_gate)
        {
            shown = PickShownSeriesLocked();
            notes = BuildNotesLocked(shown);
            if (shown is null || shown.TimedCount == 0)
            {
                notes.Add(shown is null ? "no disponible: no hay frames" : NoTimeColumnNote);
                return new LiveViewSnapshot(label, KeyOf(shown), 0, null, null, null, notes);
            }

            var lastMs = shown.LastTimeMs!.Value;
            var cutMs = lastMs - seconds * 1000.0;
            frames = new List<double>();
            double? firstInWindowMs = null;
            for (var i = 0; i < shown.TimesMs.Count; i++)
            {
                var t = shown.TimesMs[i];
                if (t is double tv && tv > cutMs)
                {
                    frames.Add(shown.FrameTimesMs[i]);
                    firstInWindowMs ??= tv;
                }
            }
            spanSeconds = firstInWindowMs is double firstMs ? (lastMs - firstMs) / 1000.0 : null;
        }

        var statistics = FrameStatisticsCalculator.Compute(frames, 0);
        var hitches = HitchAnalyzer.Analyze(frames).Report;
        return new LiveViewSnapshot(label, KeyOf(shown), frames.Count, spanSeconds, statistics, hitches, notes);
    }

    /// <summary>
    /// Segmento entre <see cref="MarkStart"/> y <see cref="MarkEnd"/>, por cantidad de frames recibidos,
    /// intervalo [inicio, fin). Sin marcar o sin cerrar: "no disponible" con motivo, sin excepción.
    /// </summary>
    public LiveViewSnapshot GetSegment()
    {
        List<double> frames;
        SeriesData? shown;
        List<string> notes;
        double? spanSeconds;

        lock (_gate)
        {
            shown = PickShownSeriesLocked();
            notes = BuildNotesLocked(shown);

            if (_segmentStartCounts is null)
            {
                notes.Add("no disponible: segmento no iniciado (falta MarkStart)");
                return new LiveViewSnapshot("segmento", KeyOf(shown), 0, null, null, null, notes);
            }
            if (_segmentEndCounts is null)
            {
                notes.Add("no disponible: segmento sin cerrar (falta MarkEnd)");
                return new LiveViewSnapshot("segmento", KeyOf(shown), 0, null, null, null, notes);
            }
            if (shown is null)
            {
                notes.Add("no disponible: no hay frames");
                return new LiveViewSnapshot("segmento", null, 0, null, null, null, notes);
            }

            var key = (shown.Application, shown.SwapChain);
            var startCount = _segmentStartCounts.TryGetValue(key, out var sc) ? sc : 0;
            var endCount = _segmentEndCounts.TryGetValue(key, out var ec) ? ec : 0;
            var total = shown.FrameTimesMs.Count;
            var start = startCount >= total ? total : (int)startCount;
            var end = endCount >= total ? total : (int)endCount;
            if (end < start) end = start;

            frames = new List<double>();
            double? firstMs = null;
            double? lastMs = null;
            for (var i = start; i < end; i++)
            {
                frames.Add(shown.FrameTimesMs[i]);
                var t = shown.TimesMs[i];
                if (t is double tv)
                {
                    firstMs ??= tv;
                    lastMs = tv;
                }
            }
            spanSeconds = firstMs is double f && lastMs is double l ? (l - f) / 1000.0 : null;
            if (shown.TimedCount == 0)
                notes.Add(NoTimeColumnNote);
        }

        var statistics = FrameStatisticsCalculator.Compute(frames, 0);
        var hitches = HitchAnalyzer.Analyze(frames).Report;
        return new LiveViewSnapshot("segmento", KeyOf(shown), frames.Count, spanSeconds, statistics, hitches, notes);
    }

    private Dictionary<(string Application, string SwapChain), long> SnapshotCountsLocked()
    {
        var counts = new Dictionary<(string Application, string SwapChain), long>();
        foreach (var pair in _series)
            counts[pair.Key] = pair.Value.FrameTimesMs.Count;
        return counts;
    }

    /// <summary>La serie con más frames. En empate gana la primera vista (orden de aparición).</summary>
    private SeriesData? PickShownSeriesLocked()
    {
        SeriesData? best = null;
        foreach (var candidate in _seriesOrder)
        {
            if (best is null || candidate.FrameTimesMs.Count > best.FrameTimesMs.Count)
                best = candidate;
        }
        return best;
    }

    private List<string> BuildNotesLocked(SeriesData? shown)
    {
        var notes = new List<string>();
        if (shown is not null && _seriesOrder.Count > 1)
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"hay {_seriesOrder.Count - 1} series más"));
        if (shown is not null && shown.OutOfOrder > 0)
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"hay {shown.OutOfOrder} filas fuera de orden"));
        if (shown is not null && shown.TimedCount > 0 && shown.NullTimeCount > 0)
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"hay {shown.NullTimeCount} frames sin tiempo"));
        if (_reachedMaxFrames)
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"tope de {MaxFrames} frames alcanzado: no se acumula más"));
        return notes;
    }

    private static LiveSeriesKey? KeyOf(SeriesData? shown) =>
        shown is null ? null : new LiveSeriesKey(shown.Application, shown.SwapChain);

    private static double? ComputeSpanSeconds(double? firstMs, double? lastMs) =>
        firstMs is double f && lastMs is double l ? (l - f) / 1000.0 : null;

    private static string LabelForWindow(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"últimos {seconds} s");

    /// <summary>Estadísticas O(1) para la vista rápida: sin percentiles (esos van en <see cref="GetTotal"/>).</summary>
    private static FrameStatistics QuickStatistics(SeriesData shown, int frameCount)
    {
        const string reason = "vista rápida: percentiles solo en GetTotal";
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(FrameStatistics.MedianMs)] = reason,
            [nameof(FrameStatistics.StdDevMs)] = reason,
            [nameof(FrameStatistics.P99FrameTimeMs)] = reason,
            [nameof(FrameStatistics.P999FrameTimeMs)] = reason,
            [nameof(FrameStatistics.P99Fps)] = reason,
            [nameof(FrameStatistics.P999Fps)] = reason,
            [nameof(FrameStatistics.Low1PercentAverageFps)] = reason,
            [nameof(FrameStatistics.Low01PercentAverageFps)] = reason,
        };

        return new FrameStatistics
        {
            FrameCount = frameCount,
            NaFrames = 0,
            MeanMs = frameCount > 0 ? shown.Sum / frameCount : null,
            MinMs = frameCount > 0 ? shown.Min : null,
            MaxMs = frameCount > 0 ? shown.Max : null,
            TotalDurationMs = shown.Sum,
            UnavailableReasons = reasons,
        };
    }

    private sealed class SeriesData
    {
        public SeriesData(string application, string swapChain)
        {
            Application = application;
            SwapChain = swapChain;
        }

        public string Application { get; }
        public string SwapChain { get; }
        public List<double> FrameTimesMs { get; } = new();
        public List<double?> TimesMs { get; } = new();
        public double Sum { get; set; }
        public double Min { get; set; } = double.PositiveInfinity;
        public double Max { get; set; } = double.NegativeInfinity;
        public double? FirstTimeMs { get; set; }
        public double? LastTimeMs { get; set; }
        public long TimedCount { get; set; }
        public long NullTimeCount { get; set; }
        public long OutOfOrder { get; set; }
        public double? PrevTimeMs { get; set; }
    }
}
