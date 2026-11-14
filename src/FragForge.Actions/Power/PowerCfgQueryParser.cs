using System.Globalization;
using System.Text.RegularExpressions;

namespace FragForge.Actions.Power;

/// <summary>
/// Parser de <c>powercfg /qh</c> (query con detalle). Regla R-C: NUNCA por etiquetas localizadas.
///
/// Forma de un bloque (es-MX y en-US comparten estructura; solo cambian los textos):
/// <code>
///   GUID del alias de configuración de energía: 54533251-82be-4824-96c1-47b60b740d00  (Procesador)
///     GUID de configuración de energía: 36687f9e-e3a5-4dbf-b1dc-15eb381c6863  (rendimiento mínimo del procesador)
///       Límites de configuración de energía mínimos posibles: 0x00000000
///       Límites de configuración de energía máximos posibles: 0x00000064
///       Incremento de configuración de energía posible: 0x00000001
///       Valores de configuración de energía posibles:
///         000
///         001
///       Índice de configuración de energía actual de CA: 0x00000005
///       Índice de configuración de energía actual de CC: 0x00000000
/// </code>
///
/// Reglas de extracción, deliberadamente sin etiquetas:
/// <list type="bullet">
/// <item>Los GUID se capturan con regex de GUID (8-4-4-4-12).</item>
/// <item>Los dos ÚLTIMOS tokens <c>0x########</c> del bloque son AC y DC.</item>
/// <item>Si hay TRES o más tokens <c>0x</c> antes de los dos últimos, son mínimo, máximo e incremento.</item>
/// <item>Los índices posibles son las líneas que terminan en <c>: \d{3}</c> (el bloque enumerado).</item>
/// </list>
/// </summary>
public static class PowerCfgQueryParser
{
    private static readonly Regex GuidPattern = new(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.CultureInvariant);
    private static readonly Regex HexToken = new(@"0x([0-9a-fA-F]{1,8})\b", RegexOptions.CultureInvariant);
    // Índice posible. Dos formas en la salida real de powercfg:
    //   - línea suelta "        000"  (lo habitual en el bloque enumerado)
    //   - línea terminada en ": 001"  (cuando el índice va precedido de texto)
    // Ambas exigen EXACTAMENTE tres dígitos: los índices AC/DC van en hex ("0x...") y no casan.
    private static readonly Regex IndexLine = new(@"^\s*(\d{3})\s*$|:\s*(\d{3})\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Extrae todos los bloques de setting de la salida completa de <c>/qh</c>. La salida mezcla
    /// bloques de subgrupo (los que solo llevan alias) con bloques de setting: solo estos últimos
    /// tienen índices AC/DC.
    /// </summary>
    public static IReadOnlyList<PowerSettingQuery> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var results = new List<PowerSettingQuery>();
        List<string>? current = null;
        var currentSubgroup = "";
        var currentIndent = 0;

        void Flush()
        {
            if (current is null || current.Count == 0) return;
            var parsed = ParseBlock(current, currentSubgroup);
            if (parsed is not null) results.Add(parsed);
            current = null;
        }

        foreach (var rawLine in output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var indent = line.Length - line.TrimStart().Length;
            var guids = GuidPattern.Matches(line).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToArray();

            // Línea con GUID y sangría pequeña (0-3): cabecera de SUBGRUPO. Cierra el bloque anterior.
            if (guids.Length >= 1 && indent <= 3)
            {
                Flush();
                currentSubgroup = guids[0];
                currentIndent = indent;
                continue;
            }

            // Línea con GUID y sangría mayor que la del subgrupo: cabecera de SETTING.
            if (guids.Length >= 1 && indent > currentIndent)
            {
                Flush();
                current = [line];
                continue;
            }

            // Cualquier otra línea pertenece al bloque en curso (rangos, índices, descripciones).
            current?.Add(line);
        }
        Flush();
        return results;
    }

    /// <summary>Busca un setting concreto dentro de la salida de <c>/qh</c>.</summary>
    public static PowerSettingQuery? Find(string output, string subgroupGuid, string settingGuid) =>
        Parse(output).FirstOrDefault(q =>
            string.Equals(q.SubgroupGuid, subgroupGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(q.SettingGuid, settingGuid, StringComparison.OrdinalIgnoreCase));

    private static PowerSettingQuery? ParseBlock(IReadOnlyList<string> block, string subgroupGuid)
    {
        var headerGuids = GuidPattern.Matches(block[0]).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToArray();
        if (headerGuids.Length == 0) return null;
        var settingGuid = headerGuids[0];

        // Todos los tokens 0x del bloque, en orden de aparición.
        var hex = new List<uint>();
        foreach (var line in block)
        foreach (Match match in HexToken.Matches(line))
            hex.Add(uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        // Los dos últimos son AC y DC. Sin al menos dos tokens no hay nada que leer.
        if (hex.Count < 2) return null;
        var ac = (int)hex[^2];
        var dc = (int)hex[^1];

        // Tres o más tokens ANTES de los dos últimos => mínimo, máximo, incremento.
        int? minimum = null, maximum = null, increment = null;
        if (hex.Count >= 5)
        {
            minimum = (int)hex[^5];
            maximum = (int)hex[^4];
            increment = (int)hex[^3];
        }

        // Índices posibles: líneas que terminan exactamente en ": NNN".
        var possible = new List<int>();
        foreach (var line in block)
        {
            var match = IndexLine.Match(line);
            if (!match.Success) continue;
            var digits = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            possible.Add(int.Parse(digits, CultureInfo.InvariantCulture));
        }

        return new PowerSettingQuery(settingGuid, subgroupGuid, ac, dc, minimum, maximum, increment, possible);
    }
}

/// <summary>
/// Un setting tal como lo describe <c>powercfg /qh</c>. Los valores son los índices que powercfg
/// acepta para ESE equipo: no hay lista fija en el código (regla R-L).
/// </summary>
public sealed record PowerSettingQuery(
    string SettingGuid,
    string SubgroupGuid,
    int AcIndex,
    int DcIndex,
    int? Minimum,
    int? Maximum,
    int? Increment,
    IReadOnlyList<int> PossibleIndices)
{
    /// <summary>Bloque con rango numérico (mín/máx/incremento).</summary>
    public bool IsRange => Minimum is not null && Maximum is not null;

    /// <summary>Bloque enumerado (lista cerrada de índices posibles).</summary>
    public bool IsEnumerated => PossibleIndices.Count > 0;

    /// <summary>
    /// ¿El valor está dentro de lo que el sistema enumera? Para rango, entre mínimo y máximo (y
    /// alineado al incremento cuando existe). Para bloque enumerado, tiene que estar en la lista.
    /// </summary>
    public bool Allows(int value)
    {
        if (IsEnumerated) return PossibleIndices.Contains(value);
        if (Minimum is not null && Maximum is not null)
        {
            if (value < Minimum || value > Maximum) return false;
            return Increment is null or 0 || (value - Minimum) % Increment == 0;
        }
        return false;
    }

    /// <summary>Descripción legible de lo permitido, para mensajes de error y notas.</summary>
    public string Describe() => IsEnumerated
        ? $"lista [{string.Join(", ", PossibleIndices)}]"
        : IsRange ? $"rango {Minimum}..{Maximum} (incremento {Increment})" : "sin rango ni lista legibles";
}
