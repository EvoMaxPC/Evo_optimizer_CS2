using System.Globalization;
using System.Text;

namespace FragForge.Core.Analysis;

/// <summary>
/// Lector de CSV de PresentMon, en streaming y por NOMBRE de columna.
/// </summary>
/// <remarks>
/// <para>
/// Por qué existe habiendo ya un <c>PresentMonCsvParser</c> en FragForge.Actions: ese parser **elige una
/// swapchain** y devuelve una sola serie (es lo correcto para una captura en vivo), y vive en Actions, que
/// Core no referencia. Aquí el contrato es otro: una serie POR CADA (aplicación, swapchain), sin elegir, y
/// sin dependencias de Actions.
/// </para>
/// <para>
/// Reglas heredadas y respetadas:
/// <list type="bullet">
/// <item>Columnas por NOMBRE, <see cref="StringComparison.OrdinalIgnoreCase"/>, nunca por posición (R-C).</item>
/// <item><c>NA</c> no es 0 (R-L): una celda NA cuenta como <see cref="FrameStream.NaFrames"/> y se excluye.</item>
/// <item>Números con <see cref="CultureInfo.InvariantCulture"/> (R-E): el CSV usa punto decimal.</item>
/// <item>Streaming: se lee línea a línea, no se carga el CSV entero en memoria.</item>
/// </list>
/// </para>
/// </remarks>
public static class FrameReader
{
    /// <summary>Columna principal. Verificada contra PresentMon 2.6.0 (28 columnas, flags por defecto).</summary>
    public const string PrimaryColumn = "MsBetweenPresents";

    /// <summary>
    /// Columna secundaria. Se lee SOLO si está en el encabezado; en 2.6.0 sin seguimiento de display viene
    /// NA y por eso nunca se usa como principal.
    /// </summary>
    public const string SecondaryColumn = "MsBetweenDisplayChange";

    private const string ApplicationColumn = "Application";
    private const string SwapChainColumn = "SwapChainAddress";
    private const string NaMarker = "NA";

    /// <summary>Etiqueta que se usa cuando el CSV no trae la columna de aplicación.</summary>
    public const string NoApplication = "(sin aplicación)";

    /// <summary>Etiqueta que se usa cuando el CSV no trae la columna de swapchain.</summary>
    public const string NoSwapChain = "(sin swapchain)";

    /// <summary>Lee un fichero. Propaga <see cref="IOException"/> si no se puede abrir: eso es un error, no un dato.</summary>
    public static FrameStream ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        // detectEncodingFromByteOrderMarks: tolera BOM UTF-8/UTF-16 sin que el BOM acabe dentro del primer nombre.
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }

    /// <summary>Lee de un <see cref="TextReader"/>. Acepta <c>\n</c> y <c>\r\n</c>: ReadLine se encarga de ambos.</summary>
    public static FrameStream Read(TextReader reader)
    {
        var headerLine = reader.ReadLine();
        if (headerLine is null || string.IsNullOrWhiteSpace(headerLine))
            return new FrameStream { UnavailableReason = "el CSV está vacío: no hay encabezado." };

        // Un BOM puede sobrevivir si el TextReader no lo detectó (p. ej. BOM en mitad de un flujo).
        var headers = SplitCsvLine(headerLine.TrimStart('\uFEFF'));

        var primaryIndex = FindColumn(headers, PrimaryColumn);
        if (primaryIndex < 0)
            return new FrameStream
            {
                HeaderColumns = headers,
                UnavailableReason = $"no hay columna '{PrimaryColumn}' en el encabezado (columnas: {string.Join(", ", headers)})."
            };

        var secondaryIndex = FindColumn(headers, SecondaryColumn);
        var applicationIndex = FindColumn(headers, ApplicationColumn);
        var swapChainIndex = FindColumn(headers, SwapChainColumn);

        // Clave = (app, swapchain). Un diccionario por serie; el orden final se fija al devolver.
        var builders = new Dictionary<(string App, string Chain), SeriesBuilder>();
        var naFrames = 0;
        var malformed = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var cells = SplitCsvLine(line);

            // Fila truncada: no llega ni a la columna principal. Se cuenta, no aborta.
            if (cells.Length <= primaryIndex) { malformed++; continue; }

            var application = ReadString(cells, applicationIndex) ?? NoApplication;
            var swapChain = ReadString(cells, swapChainIndex) ?? NoSwapChain;
            var key = (application, swapChain);

            if (!TryParseDouble(cells[primaryIndex], out var primary))
            {
                // NA (o basura numérica) en la columna principal: se cuenta como NA y se excluye.
                naFrames++;
                builders.TryAdd(key, new SeriesBuilder(secondaryIndex >= 0));
                continue;
            }

            if (!builders.TryGetValue(key, out var builder))
            {
                builder = new SeriesBuilder(secondaryIndex >= 0);
                builders[key] = builder;
            }
            builder.Primary.Add(primary);

            // La secundaria solo se guarda si la columna existe en el encabezado; si la celda es NA, null.
            // Queda alineada por índice con Primary: mismo frame, misma posición.
            if (builder.Secondary is not null)
                builder.Secondary.Add(ReadDouble(cells, secondaryIndex));
        }
        var series = builders
            .OrderBy(kv => kv.Key.App, StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key.Chain, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new FrameSeriesKey(kv.Key.App, kv.Key.Chain, kv.Value.Primary, kv.Value.Secondary))
            .ToArray();

        var reason = series.Length == 0 || series.All(s => s.Primary.Count == 0)
            ? string.Create(CultureInfo.InvariantCulture, $"no hay frames con dato en '{headers[primaryIndex]}' (líneas descartadas: {malformed}, celdas NA: {naFrames}).")
            : null;

        return new FrameStream
        {
            Series = series,
            NaFrames = naFrames,
            MalformedLines = malformed,
            HeaderColumns = headers,
            PrimaryColumn = headers[primaryIndex],
            SecondaryColumn = secondaryIndex >= 0 ? headers[secondaryIndex] : null,
            UnavailableReason = reason
        };
    }

    private sealed class SeriesBuilder(bool wantsSecondary)
    {
        public List<double> Primary { get; } = [];
        public List<double?>? Secondary { get; } = wantsSecondary ? [] : null;
    }

    private static int FindColumn(string[] headers, string name)
    {
        for (var i = 0; i < headers.Length; i++)
            if (string.Equals(headers[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string? ReadString(string[] cells, int index)
    {
        if (index < 0 || index >= cells.Length) return null;
        var value = cells[index].Trim();
        return value.Length == 0 || value.Equals(NaMarker, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    /// <summary>Lee una celda opcional: null si falta, está vacía o es NA. NUNCA 0 por ausencia (R-L).</summary>
    private static double? ReadDouble(string[] cells, int index) =>
        index >= 0 && index < cells.Length && TryParseDouble(cells[index], out var value) ? value : null;

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

    /// <summary>
    /// CSV mínimo con comillas dobles. Misma semántica que el parser histórico de Actions, reimplementada
    /// aquí porque ese tipo es <c>internal</c> y además Core no referencia ese ensamblado.
    /// </summary>
    internal static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
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
}
