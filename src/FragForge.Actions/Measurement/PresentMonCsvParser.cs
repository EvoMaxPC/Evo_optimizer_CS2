using System.Globalization;
using FragForge.Core.Experiments;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Parser de CSV de PresentMon tolerante a versiones. Localiza las columnas POR NOMBRE, nunca por posición,
/// porque el orden cambia entre 1.x y 2.x.
///
/// Familias de columnas soportadas:
/// <list type="bullet">
/// <item>1.x: <c>MsBetweenPresents</c>, <c>MsBetweenDisplayChange</c>, <c>TimeInSeconds</c>, <c>Dropped</c>, <c>msInPresentAPI</c>.</item>
/// <item>2.x: <c>FrameTime</c>, <c>CPUBusy</c>, <c>GPUBusy</c>, <c>GPUTime</c>, <c>PresentMode</c>, <c>CPUStartTime</c>, <c>DisplayedTime</c>.</item>
/// </list>
///
/// Regla dura: una columna ausente produce <c>null</c> (Unknown), jamás 0. Un 0 en p1Low significa "infinita fluidez";
/// inventarlo sería mentir. Un frametime de 0 o negativo se descarta como dato corrupto.
/// </summary>
public static class PresentMonCsvParser
{
    private static readonly string[] FrameTimeCandidates = ["FrameTime", "MsBetweenPresents", "MsBetweenDisplayChange", "msBetweenPresents", "frameTime"];
    private static readonly string[] CpuBusyCandidates = ["CPUBusy", "MsCPUBusy", "msInPresentAPI", "CPUUsage"];
    private static readonly string[] GpuBusyCandidates = ["GPUBusy", "MsGPUBusy", "GPUTime", "GpuTime"];
    private static readonly string[] PresentModeCandidates = ["PresentMode", "PresentModeName", "SyncInterval"];

    public static FrameSeries ParseFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    public static FrameSeries Parse(TextReader reader)
    {
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) return new FrameSeries { IsPartial = false, SourceColumns = "empty" };

        var headers = SplitCsvLine(headerLine);
        var frameTimeIndex = FindColumn(headers, FrameTimeCandidates);
        var cpuIndex = FindColumn(headers, CpuBusyCandidates);
        var gpuIndex = FindColumn(headers, GpuBusyCandidates);
        var modeIndex = FindColumn(headers, PresentModeCandidates);

        var columns = string.Join(",", headers);
        if (frameTimeIndex < 0)
        {
            // Sin columna de frametime no hay métricas: se devuelve una serie vacía marcada, no un cero.
            return new FrameSeries { SourceColumns = "missing-frame-time:" + columns };
        }

        var frameTimes = new List<double>();
        var cpu = new List<double?>();
        var gpu = new List<double?>();
        var modes = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var cells = SplitCsvLine(line);
            if (cells.Length <= frameTimeIndex) continue;
            if (!TryParseDouble(cells[frameTimeIndex], out var frameTime) || frameTime <= 0) continue;
            frameTimes.Add(frameTime);
            cpu.Add(cpuIndex >= 0 && cpuIndex < cells.Length && TryParseDouble(cells[cpuIndex], out var cpuValue) && cpuValue >= 0 ? cpuValue : null);
            gpu.Add(gpuIndex >= 0 && gpuIndex < cells.Length && TryParseDouble(cells[gpuIndex], out var gpuValue) && gpuValue >= 0 ? gpuValue : null);
            modes.Add(modeIndex >= 0 && modeIndex < cells.Length ? cells[modeIndex] : string.Empty);
        }

        return new FrameSeries
        {
            FrameTimesMs = frameTimes,
            CpuBusyMs = cpu,
            GpuBusyMs = gpu,
            PresentModes = modes,
            SourceColumns = $"frameTime={headers[frameTimeIndex]};cpu={(cpuIndex >= 0 ? headers[cpuIndex] : "none")};gpu={(gpuIndex >= 0 ? headers[gpuIndex] : "none")};mode={(modeIndex >= 0 ? headers[modeIndex] : "none")}"
        };
    }

    private static int FindColumn(string[] headers, string[] candidates)
    {
        foreach (var candidate in candidates)
            for (var i = 0; i < headers.Length; i++)
                if (string.Equals(headers[i].Trim(), candidate, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
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

    private static bool TryParseDouble(string raw, out double value) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
