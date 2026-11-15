using System.Globalization;

namespace FragForge.Core.Models;

/// <summary>
/// Parsers de los archivos de configuración de CS2 (Capa 8.6), como funciones PURAS.
///
/// Regla R-C aplicada a archivos de juego: se parsea **por clave**, nunca por posición ni contando líneas.
/// Los tres formatos que se leen aquí son distintos y se tratan por separado:
///
/// <list type="bullet">
/// <item><c>cs2_video.txt</c>: bloques <c>"clave" { ... "subclave" "valor" }</c>. Es VDF de Valve; los
///       ajustes viven dentro de un bloque, así que se recorre el árbol en vez de leer línea a línea.</item>
/// <item><c>cs2_convars.vcfg</c>: <c>"nombre" "valor"</c> dentro de un bloque raíz.</item>
/// <item><c>autoexec.cfg</c>: texto tipo consola, <c>nombre valor</c> por línea, con <c>//</c> de comentario.</item>
/// </list>
///
/// Ninguna función escribe ni requiere Windows: todo es <see cref="string"/> → diccionario.
/// </summary>
public static class Cs2ConfigParser
{
    /// <summary>
    /// <c>autoexec.cfg</c> → convars. Ignora comentarios (<c>//</c>), líneas vacías y los comandos que no
    /// son pares clave/valor. Los valores entre comillas se despojan de ellas.
    ///
    /// <c>+comando</c> al principio de línea también se acepta: en CS2 equivale a ejecutar el comando.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseAutoexec(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;

            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment > 0) line = line[..comment].Trim();
            if (line.Length == 0) continue;

            while (line.StartsWith('+')) line = line[1..].Trim();
            if (line.Length == 0) continue;

            var split = IndexOfWhitespace(line);
            if (split < 0) continue;
            var key = line[..split].Trim();
            var value = line[split..].Trim().Trim('"');
            if (key.Length == 0 || value.Length == 0) continue;
            values[key] = value;
        }
        return values;
    }

    /// <summary>
    /// <c>cs2_convars.vcfg</c> → convars. El archivo es VDF con un bloque raíz; las claves de convar están
    /// al nivel del bloque que las contiene (normalmente <c>convars</c>). Se recorre el árbol y se recogen
    /// todos los pares clave/valor que sean hojas, sin asumir la profundidad.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseVcfg(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in ParseVdfLoose(text))
        {
            if (leaf.Value is not null) values[leaf.Key] = leaf.Value;
        }
        return values;
    }

    /// <summary>
    /// <c>cs2_video.txt</c> → ajustes. Igual que <c>.vcfg</c> pero el resultado incluye la ruta de claves
    /// anidada (<c>bloque.subclave</c>) para poder distinguir <c>setting.refreshrate_numerator</c> de
    /// otros bloques con la misma hoja.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseVideoSettings(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in ParseVdfLoose(text))
        {
            if (leaf.Value is not null && leaf.Path is { } path) values[path] = leaf.Value;
        }
        return values;
    }

    /// <summary>
    /// Busca una hoja por nombre EXACTO en cualquiera de los niveles, devolviendo el primer valor no nulo.
    /// Es lo que se usa para <c>refreshrate_numerator</c>: no importa en qué bloque esté, importa la clave.
    /// </summary>
    public static string? FindLeaf(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (settings.TryGetValue(key, out var direct)) return direct;
        foreach (var pair in settings)
        {
            var lastDot = pair.Key.LastIndexOf('.');
            if (lastDot >= 0 && pair.Key.AsSpan(lastDot + 1).Equals(key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }
        return null;
    }

    /// <summary>
    /// Refresh configurado en Hz, a partir de las claves de CS2.
    ///
    /// Se acepta <c>refreshrate_numerator</c> solo, o <c>numerator/denominator</c> cuando ambos existen y el
    /// denominador no es cero (CS2 lo guarda como fracción). Un valor no numérico o no positivo devuelve
    /// <c>null</c>: no se adivina el refresco del sistema.
    ///
    /// R-E: se parsea con <see cref="CultureInfo.InvariantCulture"/>, nunca con la cultura de la UI.
    /// </summary>
    public static int? ParseRefreshHz(IReadOnlyDictionary<string, string> settings)
    {
        var numerator = FindLeaf(settings, "refreshrate_numerator");
        if (numerator is null) return null;
        if (!int.TryParse(numerator.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) || num <= 0) return null;

        var denominator = FindLeaf(settings, "refreshrate_denominator");
        if (denominator is null) return num;
        if (!int.TryParse(denominator.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var den) || den <= 0) return num;
        return den == 1 ? num : num / den;
    }

    /// <summary>
    /// Conflictos entre lo guardado por CS2 y el <c>autoexec.cfg</c>: la misma convar con distinto valor.
    ///
    /// Ejemplo: <c>fps_max</c> guardado en <c>0</c> y en el autoexec en <c>64</c>.
    /// La precedencia entre autoexec y los archivos de usuario no está verificada.
    /// Se comparan valores como TEXTO normalizado (R-E), no como
    /// números: un valor puede ser un enum (<c>1</c>, <c>0</c>, <c>true</c>).
    /// </summary>
    public static IReadOnlyList<string> FindConvarConflicts(
        IReadOnlyDictionary<string, string> stored,
        IReadOnlyDictionary<string, string> autoexec)
    {
        var conflicts = new List<string>();
        foreach (var pair in autoexec)
        {
            if (!stored.TryGetValue(pair.Key, out var storedValue)) continue;
            if (string.Equals(storedValue.Trim(), pair.Value.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            conflicts.Add($"'{pair.Key}': CS2 guarda '{storedValue}' y autoexec.cfg dice '{pair.Value}'.");
        }
        return conflicts;
    }

    // ------------------------------------------------------------------ VDF permisivo

    private sealed record Leaf(string Key, string? Value, string? Path);

    /// <summary>
    /// VDF tolerante con los archivos de configuración de CS2: no exige que TODO el archivo sea un objeto
    /// cerrado (los archivos de CS2 a veces traen un bloque raíz sin envolver y comentarios), pero sí exige
    /// que las comillas estén balanceadas. Un archivo corrupto lanza <see cref="InvalidDataException"/> y el
    /// llamador lo anota; no se inventan valores.
    /// </summary>
    private static IReadOnlyList<Leaf> ParseVdfLoose(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Archivo de configuración demasiado grande.");
        var leaves = new List<Leaf>();
        var index = 0;
        ParseBlock(text, ref index, "", 0, leaves, root: true);
        return leaves;
    }

    private static void ParseBlock(string text, ref int index, string prefix, int depth, List<Leaf> leaves, bool root)
    {
        if (depth > 32) throw new InvalidDataException("Anidamiento VDF excesivo.");
        while (true)
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length) { if (root) return; throw new InvalidDataException("Bloque VDF sin cerrar."); }
            if (text[index] == '}') { index++; return; }

            var key = ParseToken(text, ref index) ?? throw new InvalidDataException("Se esperaba una clave VDF.");
            SkipTrivia(text, ref index);
            if (index >= text.Length) { leaves.Add(new Leaf(key, "", prefix + key)); return; }

            if (text[index] == '{')
            {
                index++;
                ParseBlock(text, ref index, prefix + key + ".", depth + 1, leaves, root: false);
                continue;
            }

            var value = ParseToken(text, ref index);
            leaves.Add(new Leaf(key, value, prefix + key));
        }
    }

    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c)) { index++; continue; }
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n') index++;
                continue;
            }
            break;
        }
    }

    private static string? ParseToken(string text, ref int index)
    {
        SkipTrivia(text, ref index);
        if (index >= text.Length) return null;
        if (text[index] != '"') return null;   // token sin comillas: no es un archivo de CS2 válido
        index++;
        var start = index;
        while (index < text.Length && text[index] != '"')
        {
            if (text[index] == '\\' && index + 1 < text.Length) index += 2;
            else index++;
        }
        if (index >= text.Length) throw new InvalidDataException("Cadena VDF sin cerrar.");
        var raw = text[start..index];
        index++;
        return raw.Length == 0 ? "" : Unescape(raw);
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal)) return value;
        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length) { builder.Append(value[i]); continue; }
            var next = value[++i];
            builder.Append(next switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => next });
        }
        return builder.ToString();
    }

    private static int IndexOfWhitespace(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i])) return i;
        }
        return -1;
    }
}
