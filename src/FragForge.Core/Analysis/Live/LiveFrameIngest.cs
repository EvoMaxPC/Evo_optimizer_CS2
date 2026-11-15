namespace FragForge.Core.Analysis.Live;

/// <summary>Foto de los contadores de ingesta en vivo, tomada bajo lock.</summary>
/// <param name="HeaderSeen">Ya se vio el encabezado.</param>
/// <param name="HasFrameTimeColumn">El encabezado trae MsBetweenPresents.</param>
/// <param name="HasTimeColumn">El encabezado trae TimeInMs o TimeInSeconds.</param>
/// <param name="FramesParsed">Frames válidos parseados (los que llegaron al acumulador).</param>
/// <param name="MalformedLines">Líneas truncadas o con tiempo o frametime inválido.</param>
/// <param name="NaFrames">Filas con NA o celda vacía en MsBetweenPresents.</param>
/// <param name="RowsWithoutTime">Frames con valor de tiempo ausente.</param>
/// <param name="OverlongLines">Líneas descartadas por superar el tope de longitud.</param>
/// <param name="DiscardedPartialLines">Línea parcial descartada al completar el stream (0 o 1).</param>
public sealed record LiveIngestCounters(
    bool HeaderSeen,
    bool HasFrameTimeColumn,
    bool HasTimeColumn,
    long FramesParsed,
    long MalformedLines,
    long NaFrames,
    long RowsWithoutTime,
    long OverlongLines,
    long DiscardedPartialLines);

/// <summary>
/// Puente entre el stream de PresentMon y el acumulador: parte trozos en líneas (<see cref="LiveLineSplitter"/>),
/// las pasa al parser (<see cref="LiveFrameParser"/>) y agrega los frames válidos al acumulador.
/// Thread-safe con un lock: un hilo hace <see cref="Push"/> y otro lee <see cref="Counters"/>.
/// Los frames de procesos distintos NO se mezclan acá: eso ya lo hace el acumulador por (Application, SwapChain).
/// </summary>
public sealed class LiveFrameIngest
{
    private readonly object _gate = new();
    private readonly LiveFrameParser _parser = new();
    private readonly LiveLineSplitter _splitter = new();
    private long _framesParsed;
    private long _discardedPartialLines;
    private bool _completed;

    /// <param name="targetDurationSeconds">Duración fija objetivo en segundos, o <c>null</c> para medir sin tope.</param>
    /// <param name="maxFrames">Tope de frames acumulados (límite de memoria, no validado).</param>
    public LiveFrameIngest(double? targetDurationSeconds = null, long maxFrames = LiveFrameAccumulator.DefaultMaxFrames)
    {
        Accumulator = new LiveFrameAccumulator(targetDurationSeconds, maxFrames);
    }

    /// <summary>El acumulador que recibe los frames parseados.</summary>
    public LiveFrameAccumulator Accumulator { get; }

    /// <summary>Agrega un trozo de texto del stream. Tras <see cref="Complete"/>, Push se ignora.</summary>
    public void Push(string chunk)
    {
        lock (_gate)
        {
            if (_completed) return;

            foreach (var line in _splitter.Push(chunk))
            {
                var result = _parser.Feed(line);
                if (result.Kind == LiveLineKind.Frame && result.Frame is not null)
                {
                    Accumulator.Add(result.Frame);
                    _framesParsed++;
                }
            }
        }
    }

    /// <summary>
    /// Fin del stream: descarta la línea parcial retenida (vía el splitter) y registra
    /// <c>DiscardedPartialLines</c> (0 o 1). Idempotente.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            if (_splitter.Complete())
                _discardedPartialLines = 1;
        }
    }

    /// <summary>Foto de los contadores, tomada bajo lock.</summary>
    public LiveIngestCounters Counters
    {
        get
        {
            lock (_gate)
            {
                // FrameStatistics.NaFrames de las vistas vale 0: el acumulador solo recibe frames válidos
                // (las filas NA nunca entran a una serie). Estos contadores son la fuente para mostrar
                // cuántas filas NA / malformadas / sin tiempo hubo: L3 los muestra desde acá.
                return new LiveIngestCounters(
                    _parser.HeaderSeen,
                    _parser.HasFrameTimeColumn,
                    _parser.HasTimeColumn,
                    _framesParsed,
                    _parser.MalformedLines,
                    _parser.NaFrames,
                    _parser.RowsWithoutTime,
                    _splitter.OverlongLines,
                    _discardedPartialLines);
            }
        }
    }
}
