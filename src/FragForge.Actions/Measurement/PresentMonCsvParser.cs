using System.Globalization;
using FragForge.Core.Experiments;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Parser de CSV de PresentMon. Localiza TODAS las columnas POR NOMBRE (nunca por posición),
/// comparando con <see cref="StringComparison.OrdinalIgnoreCase"/>; el orden puede cambiar entre
/// versiones y entre capturas.
///
/// Contrato verificado contra PresentMon 2.6.0 (capturado en la PC objetivo, 10 s sobre cs2.exe,
/// flags por defecto, sin <c>--v1_metrics</c>/<c>--v2_metrics</c>): 28 columnas, faltantes escritas
/// <c>NA</c>, decimales con punto y hasta 14 dígitos. Se parsea con <see cref="CultureInfo.InvariantCulture"/>.
///
/// Regla dura (R-L): una columna ausente o una celda <c>NA</c> produce <c>null</c> (Unknown), JAMÁS 0.
/// Un frametime de 0 o negativo se descarta como dato corrupto.
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
        "FrameTime",           // 1.x / 2.x con --v2_metrics (NO verificado contra salida real)
        "frameTime",           // variante de mayúsculas (NO verificada)
    ];

    // NOTA deliberada: MsBetweenDisplayChange NO es candidato de frametime. En 2.6.0 viene NA sin
    // seguimiento de display, y tomarla como frametime convertiría "no medido" en "infinita fluidez".

    /// <summary>CPU ocupada. El primero es el nombre real de 2.6.0.</summary>
    private static readonly string[] CpuBusyCandidates =
    [
        "MsCPUBusy",           // verificado 2.6.0
        "CPUBusy",             // 1.x / 2.x --v2_metrics (NO verificado contra salida real)
        "CPUUsage",            // variante histórica (NO verificada)
    ];

    // NOTA: MsInPresentAPI NO es CPU. Estaba mal clasificada y se corrigió: se lee como PresentApiMs.

    private static readonly string[] CpuWaitCandidates = ["MsCPUWait"];
    private static readonly string[] GpuBusyCandidates = ["MsGPUBusy", "GPUBusy"];
    private static readonly string[] GpuTimeCandidates = ["MsGPUTime", "GPUTime", "GpuTime"];
    private static readonly string[] DisplayChangeCandidates = ["MsBetweenDisplayChange"];
    private static readonly string[] UntilDisplayedCandidates = ["MsUntilDisplayed"];
    private static readonly string[] PresentApiCandidates = ["MsInPresentAPI", "msInPresentAPI"];
    private static readonly string[] PresentModeCandidates = ["PresentMode", "PresentModeName"];
    private static readonly string[] ApplicationCandidates = ["Application"];
    private static readonly string[] ProcessIdCandidates = ["ProcessID", "ProcessId"];
    private static readonly string[] SwapChainCandidates = ["SwapChainAddress"];
    private static readonly string[] TimeCandidates = ["TimeInMs", "TimeInSeconds"];

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
        var frameTimes = new List<double>();
        var cpuBusy = new List<double?>();
        var cpuWait = new List<double?>();
        var gpuBusy = new List<double?>();
        var gpuTime = new List<double?>();
        var displayChange = new List<double?>();
        var untilDisplayed = new List<double?>();
        var presentApi = new List<double?>();
        var modes = new List<string>();
        var times = new List<double?>();
        var syncIntervals = new List<int?>();
        var allowTearing = new List<bool?>();

        var application = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var swapChains = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string? line;
        string[]? lastCells = null;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var cells = SplitCsvLine(line);
            // Fila truncada: sin la celda de frametime no hay dato, se descarta entera.
            if (cells.Length <= frameTimeIndex) continue;
            if (!TryParseDouble(cells[frameTimeIndex], out var frameTime) || frameTime <= 0) continue;

            lastCells = cells;
            frameTimes.Add(frameTime);
            cpuBusy.Add(idx.ReadDouble(cells, idx.CpuBusy));
            cpuWait.Add(idx.ReadDouble(cells, idx.CpuWait));
            gpuBusy.Add(idx.ReadDouble(cells, idx.GpuBusy));
            gpuTime.Add(idx.ReadDouble(cells, idx.GpuTime));
            displayChange.Add(idx.ReadDouble(cells, idx.DisplayChange));
            untilDisplayed.Add(idx.ReadDouble(cells, idx.UntilDisplayed));
            presentApi.Add(idx.ReadDouble(cells, idx.PresentApi));
            modes.Add(idx.ReadString(cells, idx.PresentMode) ?? string.Empty);
            times.Add(idx.ReadDouble(cells, idx.Time));
            syncIntervals.Add(idx.ReadInt(cells, idx.SyncInterval));
            allowTearing.Add(idx.ReadBool(cells, idx.AllowsTearing));

            var app = idx.ReadString(cells, idx.Application);
            if (app is not null) application[app] = application.GetValueOrDefault(app) + 1;
            var swapChain = idx.ReadString(cells, idx.SwapChain);
            if (swapChain is not null) swapChains[swapChain] = swapChains.GetValueOrDefault(swapChain) + 1;
        }

        var warnings = new List<string>();

        // Swapchain dominante: la que más filas aporta. No se mezclan swapchains.
        string? dominantSwapChain = null;
        if (swapChains.Count > 0)
        {
            dominantSwapChain = swapChains.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;
            if (swapChains.Count > 1)
            {
                var detail = string.Join(", ", swapChains.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}"));
                warnings.Add($"varias swapchains: {detail}; se analiza {dominantSwapChain}");
            }
        }

        // GPU "no disponible": había columna pero todas las celdas fueron 0 o null.
        var gpuUnavailable = idx.GpuBusy >= 0 && gpuBusy.All(value => value is null or 0d);
        if (gpuUnavailable) warnings.Add("GPU no disponible: todas las filas de MsGPUBusy son 0 o NA");

        var dominantApplication = application.Count == 0
            ? null
            : application.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;

        return new FrameSeries
        {
            FrameTimesMs = frameTimes,
            CpuBusyMs = cpuBusy,
            CpuWaitMs = cpuWait,
            GpuBusyMs = gpuBusy,
            GpuTimeMs = gpuTime,
            DisplayChangeMs = displayChange,
            UntilDisplayedMs = untilDisplayed,
            PresentApiMs = presentApi,
            PresentModes = modes,
            TimesMs = times,
            SyncIntervals = syncIntervals,
            AllowsTearing = allowTearing,
            ApplicationName = dominantApplication,
            ProcessId = lastCells is null ? null : idx.ReadInt(lastCells, idx.ProcessId),
            SwapChainAddress = dominantSwapChain,
            GpuUnavailable = gpuUnavailable,
            Warnings = warnings,
            HeaderColumns = headers,
            SourceColumns = BuildSourceColumns(headers, idx)
        };
    }

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
