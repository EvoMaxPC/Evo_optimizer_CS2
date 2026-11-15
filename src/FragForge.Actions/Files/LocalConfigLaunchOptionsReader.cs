using System.Text;

namespace FragForge.Actions.Files;

/// <summary>Lee solo el valor de lanzamiento de la app 730; no conserva ni informa otras claves.</summary>
internal static class LocalConfigLaunchOptionsReader
{
    private const int MaxBytes = 16 * 1024 * 1024;
    internal sealed record Result(bool Success, bool Found, string? Value, string? Error);

    public static async Task<Result> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > MaxBytes) return new(false, false, null, "localconfig.vdf supera el límite de 16 MiB.");
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await file.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + count > MaxBytes) return new(false, false, null, "localconfig.vdf supera el límite de 16 MiB.");
                memory.Write(buffer, 0, count);
            }
            var text = new UTF8Encoding(false, true).GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
            if (text.Length > 0 && text[0] == '\ufeff') text = text[1..];
            return new Parser(text).Parse();
        }
        catch (FileNotFoundException) { return new(false, false, null, "No se encontró localconfig.vdf."); }
        catch (DirectoryNotFoundException) { return new(false, false, null, "No se encontró localconfig.vdf."); }
        catch (UnauthorizedAccessException) { return new(false, false, null, "No se pudo leer localconfig.vdf: acceso denegado."); }
        catch (InvalidDataException) { return new(false, false, null, "localconfig.vdf está vacío, truncado, corrupto o tiene claves ambiguas en la ruta de la app 730."); }
        catch (IOException) { return new(false, false, null, "No se pudo leer localconfig.vdf por un error de entrada/salida."); }
        catch (DecoderFallbackException) { return new(false, false, null, "localconfig.vdf no tiene codificación UTF-8 válida."); }
    }

    private sealed class Parser(string text)
    {
        private static readonly string[] PathKeys = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "730", "LaunchOptions"];
        private readonly bool[] _seen = new bool[PathKeys.Length];
        private int _index;
        private string? _value;
        private bool _found;
        private int _rootEntries;

        public Result Parse()
        {
            ParseBlock(0, 0, true);
            if (_rootEntries == 0) throw new InvalidDataException();
            return new(true, _found, _value, null);
        }

        private void ParseBlock(int level, int pathIndex, bool root)
        {
            if (level > 64) throw new InvalidDataException();
            while (true)
            {
                SkipTrivia();
                if (_index == text.Length)
                {
                    if (!root) throw new InvalidDataException();
                    return;
                }
                if (text[_index] == '}')
                {
                    if (root) throw new InvalidDataException();
                    _index++;
                    return;
                }
                var key = Token(true)!;
                if (root) _rootEntries++;
                var matches = pathIndex >= 0 && pathIndex < PathKeys.Length && key.Equals(PathKeys[pathIndex], StringComparison.OrdinalIgnoreCase);
                if (matches && _seen[pathIndex]) throw new InvalidDataException();
                if (matches) _seen[pathIndex] = true;
                SkipTrivia();
                if (_index == text.Length) throw new InvalidDataException();
                if (text[_index] == '{')
                {
                    if (matches && pathIndex == PathKeys.Length - 1) throw new InvalidDataException();
                    _index++;
                    ParseBlock(level + 1, matches ? pathIndex + 1 : -1, false);
                }
                else
                {
                    if (matches && pathIndex != PathKeys.Length - 1) throw new InvalidDataException();
                    var value = Token(matches);
                    if (matches) { _found = true; _value = value; }
                }
            }
        }

        private void SkipTrivia()
        {
            while (_index < text.Length)
            {
                if (char.IsWhiteSpace(text[_index])) { _index++; continue; }
                if (text[_index] == '/' && _index + 1 < text.Length && text[_index + 1] == '/')
                {
                    while (_index < text.Length && text[_index] != '\n') _index++;
                    continue;
                }
                break;
            }
        }

        private string? Token(bool retain)
        {
            SkipTrivia();
            if (_index >= text.Length || text[_index] is '{' or '}') throw new InvalidDataException();
            var builder = retain ? new StringBuilder() : null;
            if (text[_index] != '"')
            {
                while (_index < text.Length && !char.IsWhiteSpace(text[_index]) && text[_index] is not '{' and not '}')
                {
                    var character = text[_index++];
                    builder?.Append(character);
                }
                return builder?.ToString();
            }
            _index++;
            while (_index < text.Length)
            {
                var c = text[_index++];
                if (c == '"') return builder?.ToString();
                if (c == '\\')
                {
                    if (_index == text.Length) throw new InvalidDataException();
                    var escaped = text[_index++];
                    if (escaped is '\\' or '"') builder?.Append(escaped);
                    else if (escaped == 'n') builder?.Append('\n');
                    else if (escaped == 't') builder?.Append('\t');
                    else { builder?.Append('\\'); builder?.Append(escaped); }
                }
                else builder?.Append(c);
            }
            throw new InvalidDataException();
        }
    }
}
