using System.Globalization;
using FragForge.Core.Experiments;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Parser de CSV de PresentMon. Localiza TODAS las columnas POR NOMBRE (nunca por posición),
/// comparando con <see cref="StringComparison.OrdinalIgnoreCase"/>; el orden puede cambiar entre
/// versiones y entre capturas.
///
/// Contrato verificado contra PresentMon 2.6.0 (capturado en la PC objetivo, flags por defecto,
/// sin <c>--v1_metrics</c>/<c>--v2_metrics</c>): 28 columnas, faltantes escritas <c>NA</c>, decimales
/// con punto y hasta 14 dígitos. Se parsea con <see cref="CultureInfo.InvariantCulture"/>.
///
/// Reglas duras:
/// <list type="bullet">
/// <item>Una columna ausente o una celda <c>NA</c> produce <c>null</c> (Unknown), JAMÁS 0.</item>
/// <item>Un 0 SÍ es un dato real (verificado: <c>MsInPresentAPI</c>=0 en 323 de 449 filas).</item>
/// <item>Las filas de swapchains distintas NUNCA se mezclan en las estadísticas.</item>
/// </list>
/// </summary>
public static class PresentMonCsvParser
{
    /// <summary>Columna obligatoria: sin ella no hay métricas y se devuelve una serie vacía marcada.</summary>
    public const string FrameTimeColumn = "MsBetweenPresents";

    /// <summary>
    /// Candidatos de frametime. El primero es el nombre real de 2.6.0. Los demás son variantes de otras
    /// versiones y NO están verificados contra salida real (se conservan como fallback documentado).
    /// </summary>
    private static readonly string[] FrameTimeCandidates =
    [
        FrameTimeColumn,       // verificado 2.6.0
        "FrameTime",           // 2.x con --v2_metrics (formato NO estándar)
        "frameTime",           // variante de mayúsculas (NO verificada)
        "msBetweenPresents",   // 1.x con --v1_metrics (formato NO estándar)
    ];

    // NOTA deliberada: MsBetweenDisplayChange NO es candidato de frametime. En 2.6.0 viene NA sin
    // seguimiento de display, y tomarla como frametime convertiría "no medido" en "infinita fluidez".

    /// <summary>CPU ocupada. El primero es el nombre real de 2.6.0.</summary>
    private static readonly string[] CpuBusyCandidates =
    [
        "MsCPUBusy",           // verificado 2.6.0
        "CPUBusy",             // 2.x --v2_metrics (NO verificado contra salida real)
    ];

    // NOTA: MsInPresentAPI NO es CPU. Se lee como PresentApiMs.

    private static readonly string[] CpuWaitCandidates = ["MsCPUWait", "CPUWait"];
    private static readonly string[] GpuBusyCandidates = ["MsGPUBusy", "GPUBusy"];
    private static readonly string[] GpuTimeCandidates = ["MsGPUTime", "GPUTime", "GpuTime"];
    private static readonly string[] DisplayChangeCandidates = ["MsBetweenDisplayChange", "msBetweenDisplayChange"];
    private static readonly string[] UntilDisplayedCandidates = ["MsUntilDisplayed", "msUntilDisplayed"];
    private static readonly string[] PresentApiCandidates = ["MsInPresentAPI", "msInPresentAPI"];
    private static readonly string[] PresentModeCandidates = ["PresentMode", "PresentModeName"];
    private static readonly string[] ApplicationCandidates = ["Application"];
    private static readonly string[] ProcessIdCandidates = ["ProcessID", "ProcessId"];
    private static readonly string[] SwapChainCandidates = ["SwapChainAddress"];
    private static readonly string[] TimeCandidates = ["TimeInMs", "TimeInSeconds"];
    private static readonly string[] InputToPhotonCandidates = ["MsAllInputToPhotonLatency", "AllInputToPhotonLatency"];
    private static readonly string[] ClickToPhotonCandidates = ["MsClickToPhotonLatency", "ClickToPhotonLatency"];

    /// <summary>Marcador de dato ausente en PresentMon.</summary>
    private const string NaMarker = "NA";

    public static FrameSeries ParseFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    public static FrameSeries Parse(TextReader reader)
    {
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            return new FrameSeries { IsPartial = false, SourceColumns = "empty" };

        var headers = SplitCsvLine(headerLine);
        var frameTimeIndex = FindColumn(headers, FrameTimeCandidates);

        if (frameTimeIndex < 0)
        {
            // Sin columna de frametime no hay métricas: serie vacía marcada con la lista real de columnas.
            return new FrameSeries
            {
                SourceColumns = "missing-frame-time:" + string.Join(",", headers),
                HeaderColumns = headers
            };
        }

        var idx = new ColumnMap(headers);
        var isNonStandardLayout = !headers.Any(h => string.Equals(h.Trim(), FrameTimeColumn, StringComparison.OrdinalIgnoreCase));

        // Cada swapchain acumula sus PROPIAS filas. Nunca se mezclan: promediar dos flujos de
        // presentación distintos (~104 ms y ~16.5 ms en la captura real) daría un número que no
        // corresponde a ninguno de los dos.
        var perSwapChain = new Dictionary<string, SwapChainRows>(StringComparer.OrdinalIgnoreCase);
        var application = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var cells = SplitCsvLine(line);
            // Fila truncada: sin la celda de frametime no hay dato, se descarta entera.
            if (cells.Length <= frameTimeIndex) continue;
            if (!TryParseDouble(cells[frameTimeIndex], out var frameTime) || frameTime <= 0) continue;

            var swapChain = idx.ReadString(cells, idx.SwapChain) ?? string.Empty;
            if (!perSwapChain.TryGetValue(swapChain, out var rows))
            {
                rows = new SwapChainRows(swapChain);
                perSwapChain[swapChain] = rows;
            }
            rows.Add(cells, frameTime, idx);

            var app = idx.ReadString(cells, idx.Application);
            if (app is not null) application[app] = application.GetValueOrDefault(app) + 1;
        }

        var warnings = new List<string>();

        // ---- Elección de swapchain (heurística documentada) ----
        // 1) Primero las que tienen seguimiento de display (al menos una celda MsUntilDisplayed != NA).
        // 2) Entre esas (o entre todas si ninguna lo tiene), la de más filas.
        // Desempate final por dirección, solo para que sea determinista.
        var all = perSwapChain.Values.ToArray();
        var withDisplay = all.Where(r => r.HasDisplayTracking).ToArray();
        var pool = withDisplay.Length > 0 ? withDisplay : all;
        var chosen = pool
            .OrderByDescending(r => r.FrameCount)
            .ThenBy(r => r.Address, StringComparer.Ordinal)
            .FirstOrDefault();

        // Una clave vacía significa "no había dirección en el CSV" (columna ausente o celda NA).
        // Se usa "" internamente para agrupar, pero NUNCA se reporta como dirección: R-L.
        string? chosenAddress = null;
        if (chosen is not null)
        {
            chosenAddress = chosen.Address.Length == 0 ? null : chosen.Address;

            if (all.Length > 1)
            {
                var reason = withDisplay.Length > 0
                    ? "varias swapchains en el proceso: se elige la de más filas con seguimiento de display"
                    : "varias swapchains en el proceso: ninguna tiene seguimiento de display; se elige la de más filas";
                // Motivo de la elección + por cada descartada, filas y mediana de MsBetweenPresents.
                var detail = string.Join(" | ", all
                    .OrderByDescending(r => r.FrameCount)
                    .Select(r => $"{(r.Address.Length == 0 ? "(sin dirección)" : r.Address)}: filas={r.FrameCount}, medianaMsBetweenPresents={Format(r.MedianFrameTime)}, display={(r.HasDisplayTracking ? "sí" : "no")}{(ReferenceEquals(r, chosen) ? " [elegida]" : " [descartada]")}"));
                warnings.Add(reason + " -> " + detail);
            }
        }

        var series = chosen ?? new SwapChainRows(string.Empty);

        // GPU "no disponible": TODA la columna en 0 o NA DENTRO DE LA SWAPCHAIN ELEGIDA.
        // Un 0 aislado es un dato; una columna entera en cero dentro de la swapchain elegida no lo es.
        var gpuUnavailable = idx.GpuBusy >= 0 && series.GpuBusy.All(value => value is null or 0d);
        if (gpuUnavailable) warnings.Add("GPU no disponible en la swapchain elegida: todas las filas de MsGPUBusy son 0 o NA");

        if (isNonStandardLayout) warnings.Add("formato de CSV no estándar: no está la columna MsBetweenPresents");

        var dominantApplication = application.Count == 0
            ? null
            : application.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;

        return new FrameSeries
        {
            FrameTimesMs = series.FrameTimes,
            CpuBusyMs = series.CpuBusy,
            CpuWaitMs = series.CpuWait,
            GpuBusyMs = series.GpuBusy,
            GpuTimeMs = series.GpuTime,
            DisplayChangeMs = series.DisplayChange,
            UntilDisplayedMs = series.UntilDisplayed,
            PresentApiMs = series.PresentApi,
            PresentModes = series.PresentModes,
            TimesMs = series.Times,
            SyncIntervals = series.SyncIntervals,
            AllowsTearing = series.AllowsTearing,
            InputToPhotonMs = series.InputToPhoton,
            ClickToPhotonMs = series.ClickToPhoton,
            ApplicationName = dominantApplication,
            ProcessId = series.ProcessId,
            SwapChainAddress = chosenAddress,
            SwapChainReason = chosen is not null && all.Length > 1 ? warnings.FirstOrDefault(w => w.StartsWith("varias swapchains", StringComparison.Ordinal)) : null,
            SwapChains = all
                .OrderByDescending(r => r.FrameCount)
                .Select(r => new SwapChainSummary(r.Address, r.FrameCount, r.MedianFrameTime, r.HasDisplayTracking, ReferenceEquals(r, chosen)))
                .ToArray(),
            GpuUnavailable = gpuUnavailable,
            IsNonStandardLayout = isNonStandardLayout,
            Warnings = warnings,
            HeaderColumns = headers,
            SourceColumns = BuildSourceColumns(headers, idx)
        };
    }

    private static string Format(double? value) => value is null ? "NA" : value.Value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string BuildSourceColumns(string[] headers, ColumnMap idx) =>
        $"frameTime={Name(headers, idx.FrameTime)}" +
        $";cpu={Name(headers, idx.CpuBusy)}" +
        $";cpuWait={Name(headers, idx.CpuWait)}" +
        $";gpu={Name(headers, idx.GpuBusy)}" +
        $";gpuTime={Name(headers, idx.GpuTime)}" +
        $";display={Name(headers, idx.DisplayChange)}" +
        $";mode={Name(headers, idx.PresentMode)}";

    private static string Name(string[] headers, int index) => index >= 0 ? headers[index] : "none";

    private static int FindColumn(string[] headers, string[] candidates)
    {
        foreach (var candidate in candidates)
            for (var i = 0; i < headers.Length; i++)
                if (string.Equals(headers[i].Trim(), candidate, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Acumula las filas de UNA swapchain. Cada instancia es independiente por diseño.</summary>
    private sealed class SwapChainRows(string address)
    {
        public string Address { get; } = address;
        public List<double> FrameTimes { get; } = [];
        public List<double?> CpuBusy { get; } = [];
        public List<double?> CpuWait { get; } = [];
        public List<double?> GpuBusy { get; } = [];
        public List<double?> GpuTime { get; } = [];
        public List<double?> DisplayChange { get; } = [];
        public List<double?> UntilDisplayed { get; } = [];
        public List<double?> PresentApi { get; } = [];
        public List<string> PresentModes { get; } = [];
        public List<double?> Times { get; } = [];
        public List<int?> SyncIntervals { get; } = [];
        public List<bool?> AllowsTearing { get; } = [];
        public List<double?> InputToPhoton { get; } = [];
        public List<double?> ClickToPhoton { get; } = [];
        public int? ProcessId { get; private set; }

        public int FrameCount => FrameTimes.Count;

        /// <summary>True si al menos una celda de MsUntilDisplayed trae dato (seguimiento de display activo).</summary>
        public bool HasDisplayTracking => UntilDisplayed.Any(v => v is not null);

        public double? MedianFrameTime
        {
            get
            {
                if (FrameTimes.Count == 0) return null;
                var ordered = FrameTimes.OrderBy(v => v).ToArray();
                var middle = ordered.Length / 2;
                return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2.0;
            }
        }

        public void Add(string[] cells, double frameTime, ColumnMap idx)
        {
            FrameTimes.Add(frameTime);
            CpuBusy.Add(idx.ReadDouble(cells, idx.CpuBusy));
            CpuWait.Add(idx.ReadDouble(cells, idx.CpuWait));
            GpuBusy.Add(idx.ReadDouble(cells, idx.GpuBusy));
            GpuTime.Add(idx.ReadDouble(cells, idx.GpuTime));
            DisplayChange.Add(idx.ReadDouble(cells, idx.DisplayChange));
            UntilDisplayed.Add(idx.ReadDouble(cells, idx.UntilDisplayed));
            PresentApi.Add(idx.ReadDouble(cells, idx.PresentApi));
            PresentModes.Add(idx.ReadString(cells, idx.PresentMode) ?? string.Empty);
            Times.Add(idx.ReadDouble(cells, idx.Time));
            SyncIntervals.Add(idx.ReadInt(cells, idx.SyncInterval));
            AllowsTearing.Add(idx.ReadBool(cells, idx.AllowsTearing));
            InputToPhoton.Add(idx.ReadDouble(cells, idx.InputToPhoton));
            ClickToPhoton.Add(idx.ReadDouble(cells, idx.ClickToPhoton));
            ProcessId ??= idx.ReadInt(cells, idx.ProcessId);
        }
    }

    private sealed class ColumnMap(string[] headers)
    {
        public int FrameTime { get; } = FindColumn(headers, FrameTimeCandidates);
        public int CpuBusy { get; } = FindColumn(headers, CpuBusyCandidates);
        public int CpuWait { get; } = FindColumn(headers, CpuWaitCandidates);
        public int GpuBusy { get; } = FindColumn(headers, GpuBusyCandidates);
        public int GpuTime { get; } = FindColumn(headers, GpuTimeCandidates);
        public int DisplayChange { get; } = FindColumn(headers, DisplayChangeCandidates);
        public int UntilDisplayed { get; } = FindColumn(headers, UntilDisplayedCandidates);
        public int PresentApi { get; } = FindColumn(headers, PresentApiCandidates);
        public int PresentMode { get; } = FindColumn(headers, PresentModeCandidates);
        public int Application { get; } = FindColumn(headers, ApplicationCandidates);
        public int ProcessId { get; } = FindColumn(headers, ProcessIdCandidates);
        public int SwapChain { get; } = FindColumn(headers, SwapChainCandidates);
        public int Time { get; } = FindColumn(headers, TimeCandidates);
        public int SyncInterval { get; } = FindColumn(headers, ["SyncInterval"]);
        public int AllowsTearing { get; } = FindColumn(headers, ["AllowsTearing"]);
        public int InputToPhoton { get; } = FindColumn(headers, InputToPhotonCandidates);
        public int ClickToPhoton { get; } = FindColumn(headers, ClickToPhotonCandidates);

        public double? ReadDouble(string[] cells, int index) =>
            index >= 0 && index < cells.Length && TryParseDouble(cells[index], out var value) ? value : null;

        public int? ReadInt(string[] cells, int index) =>
            index >= 0 && index < cells.Length && int.TryParse(cells[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        public bool? ReadBool(string[] cells, int index)
        {
            if (index < 0 || index >= cells.Length) return null;
            return cells[index].Trim() switch
            {
                "1" => true,
                "0" => false,
                _ => null
            };
        }

        public string? ReadString(string[] cells, int index)
        {
            if (index < 0 || index >= cells.Length) return null;
            var value = cells[index].Trim();
            return value.Length == 0 || value.Equals(NaMarker, StringComparison.OrdinalIgnoreCase) ? null : value;
        }
    }

    /// <summary>CSV mínimo con comillas dobles; soporta comas y comillas escapadas dentro de campo.</summary>
    internal static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    /// <summary>
    /// Parsea un número del CSV. "NA" y vacío devuelven false (⇒ null aguas arriba), nunca 0.
    /// InvariantCulture es obligatorio: la captura real usa punto decimal.
    /// </summary>
    internal static bool TryParseDouble(string raw, out double value)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Equals(NaMarker, StringComparison.OrdinalIgnoreCase))
        {
            value = 0;
            return false;
        }
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
