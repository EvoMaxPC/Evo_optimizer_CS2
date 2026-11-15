namespace FragForge.Core.Analysis.Live;

/// <summary>Qué era una línea CSV procesada por <see cref="LiveFrameParser"/>.</summary>
public enum LiveLineKind
{
    /// <summary>La línea era el encabezado (la primera no vacía).</summary>
    Header,

    /// <summary>La línea era un frame válido.</summary>
    Frame,

    /// <summary>La línea está truncada o tiene tiempo o frametime inválido.</summary>
    Malformed,

    /// <summary>La columna de frametime trae NA o está vacía: no es un frame y no es 0 (R-L).</summary>
    Na,

    /// <summary>Línea vacía o en blanco: se ignora.</summary>
    Ignored,
}

/// <summary>Un frame ya parseado de una línea CSV de PresentMon.</summary>
/// <param name="Application">Columna Application, o la etiqueta de <c>FrameReader.NoApplication</c> si falta.</param>
/// <param name="SwapChain">Columna SwapChainAddress, o la etiqueta de <c>FrameReader.NoSwapChain</c> si falta.</param>
/// <param name="FrameTimeMs">MsBetweenPresents, en milisegundos.</param>
/// <param name="TimeMs">Tiempo del frame en milisegundos, o <c>null</c> si no hay columna de tiempo o el valor es NA.</param>
public sealed record LiveFrame(string Application, string SwapChain, double FrameTimeMs, double? TimeMs);

/// <summary>Resultado de <see cref="LiveFrameParser.Feed"/>.</summary>
/// <param name="Kind">Qué era la línea.</param>
/// <param name="Frame">El frame, solo cuando <paramref name="Kind"/> es <see cref="LiveLineKind.Frame"/>.</param>
public sealed record LiveLineResult(LiveLineKind Kind, LiveFrame? Frame);

/// <summary>
/// Parser incremental de líneas CSV de PresentMon para medición en vivo. Recibe una línea por vez y la
/// clasifica; la retención de línea parcial es trabajo de la capa que lee el stream (L2), no de aquí.
/// Mapea columnas por NOMBRE (<c>OrdinalIgnoreCase</c>), igual que <c>FrameReader</c>, y reutiliza su
/// splitter CSV y su parseo numérico invariante.
/// </summary>
public sealed class LiveFrameParser
{
    private const string ApplicationColumn = "Application";
    private const string SwapChainColumn = "SwapChainAddress";
    private const string TimeInMsColumn = "TimeInMs";
    private const string TimeInSecondsColumn = "TimeInSeconds";
    private const string NaMarker = "NA";

    private bool _headerSeen;
    private int _primaryIndex = -1;
    private int _applicationIndex = -1;
    private int _swapChainIndex = -1;
    private int _timeIndex = -1;
    private double _timeScale = 1.0;

    /// <summary>Líneas truncadas o con tiempo o frametime inválido.</summary>
    public long MalformedLines { get; private set; }

    /// <summary>Filas con NA o celda vacía en MsBetweenPresents: no son frames y no son 0.</summary>
    public long NaFrames { get; private set; }

    /// <summary>Filas con tiempo ausente, habiendo columna de tiempo.</summary>
    public long RowsWithoutTime { get; private set; }

    /// <summary><c>true</c> cuando ya se vio el encabezado (la primera línea no vacía).</summary>
    public bool HeaderSeen => _headerSeen;

    /// <summary><c>true</c> cuando el encabezado trae la columna de frametime (MsBetweenPresents).</summary>
    public bool HasFrameTimeColumn => _headerSeen && _primaryIndex >= 0;

    /// <summary><c>true</c> cuando el encabezado trae columna de tiempo (TimeInMs o TimeInSeconds).</summary>
    public bool HasTimeColumn => _headerSeen && _timeIndex >= 0;

    /// <summary>
    /// Procesa una línea. Acepta <c>\r\n</c> y <c>\n</c> (el CSV real termina en CRLF). La primera línea
    /// no vacía es el encabezado.
    /// </summary>
    public LiveLineResult Feed(string line)
    {
        var text = line.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
            return new LiveLineResult(LiveLineKind.Ignored, null);

        if (!_headerSeen)
        {
            ParseHeader(text);
            _headerSeen = true;
            return new LiveLineResult(LiveLineKind.Header, null);
        }

        return ParseDataLine(text);
    }

    private void ParseHeader(string line)
    {
        // Un BOM puede sobrevivir si el lector no lo detectó (paridad con FrameReader).
        var headers = FrameReader.SplitCsvLine(line.TrimStart('\uFEFF'));

        _primaryIndex = FindColumn(headers, FrameReader.PrimaryColumn);
        _applicationIndex = FindColumn(headers, ApplicationColumn);
        _swapChainIndex = FindColumn(headers, SwapChainColumn);

        // TimeInMs (ms) tiene prioridad; si no está, TimeInSeconds (s, se convierte con * 1000.0).
        var msIndex = FindColumn(headers, TimeInMsColumn);
        if (msIndex >= 0)
        {
            _timeIndex = msIndex;
            _timeScale = 1.0;
        }
        else
        {
            var secondsIndex = FindColumn(headers, TimeInSecondsColumn);
            _timeIndex = secondsIndex;
            _timeScale = secondsIndex >= 0 ? 1000.0 : 1.0;
        }
    }

    private LiveLineResult ParseDataLine(string line)
    {
        var cells = FrameReader.SplitCsvLine(line);

        // Fila truncada: no llega ni a la columna principal. Se cuenta, no aborta (paridad con FrameReader).
        if (_primaryIndex < 0 || cells.Length <= _primaryIndex)
        {
            MalformedLines++;
            return new LiveLineResult(LiveLineKind.Malformed, null);
        }

        double? timeMs = null;
        if (_timeIndex >= 0 && _timeIndex < cells.Length && !FrameReader.IsMissingNumber(cells[_timeIndex]))
        {
            if (!FrameReader.TryParseDouble(cells[_timeIndex], out var rawTime) || !double.IsFinite(rawTime * _timeScale))
            {
                MalformedLines++;
                return new LiveLineResult(LiveLineKind.Malformed, null);
            }
            timeMs = rawTime * _timeScale;
        }

        if (!FrameReader.TryParseDouble(cells[_primaryIndex], out var frameTimeMs))
        {
            if (FrameReader.IsMissingNumber(cells[_primaryIndex]))
            {
                NaFrames++;
                return new LiveLineResult(LiveLineKind.Na, null);
            }
            MalformedLines++;
            return new LiveLineResult(LiveLineKind.Malformed, null);
        }

        var application = ReadString(cells, _applicationIndex) ?? FrameReader.NoApplication;
        var swapChain = ReadString(cells, _swapChainIndex) ?? FrameReader.NoSwapChain;
        if (_timeIndex >= 0 && timeMs is null) RowsWithoutTime++;

        return new LiveLineResult(LiveLineKind.Frame, new LiveFrame(application, swapChain, frameTimeMs, timeMs));
    }

    private static int FindColumn(string[] headers, string name)
    {
        for (var i = 0; i < headers.Length; i++)
            if (string.Equals(headers[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string? ReadString(string[] cells, int index)
    {
        if (index < 0 || index >= cells.Length) return null;
        var value = cells[index].Trim();
        return value.Length == 0 || value.Equals(NaMarker, StringComparison.OrdinalIgnoreCase) ? null : value;
    }
}
