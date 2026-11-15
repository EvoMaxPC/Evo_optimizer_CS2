using System.Text;
using FragForge.Core.Models;

namespace FragForge.Actions.Files;

public sealed record StoredConvarReadResult(IReadOnlyDictionary<string, string> Values, bool Known);

/// <summary>Lee exclusivamente los dos archivos locales de la cuenta ya resuelta.</summary>
public static class StoredConvarReader
{
    private const int Limit = 2 * 1024 * 1024;
    private static readonly string[] Names = ["cs2_machine_convars.vcfg", "cs2_user_convars_0_slot0.vcfg"];
    private static readonly HashSet<string> Allowed = new(StoredConvarNames.Ordered, StringComparer.Ordinal);

    public static async Task<StoredConvarReadResult> ReadAsync(string cfg, IList<string> notes, CancellationToken cancellationToken = default)
    {
        var combined = new Dictionary<string, string>(StringComparer.Ordinal);
        var omitted = new HashSet<string>(StringComparer.Ordinal);
        bool known = true;
        foreach (var name in Names)
        {
            var path = Path.Combine(cfg, name);
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > Limit) { notes.Add($"{name}: no se pudo leer (supera 2 MiB)."); known = false; continue; }
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                while (true)
                {
                    var count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, Limit + 1 - (int)buffer.Length)), cancellationToken);
                    if (count == 0) break;
                    buffer.Write(chunk, 0, count);
                    if (buffer.Length > Limit) break;
                }
                if (buffer.Length > Limit) { notes.Add($"{name}: no se pudo leer (supera 2 MiB)."); known = false; continue; }
                var text = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
                var parsed = new Parser(text).Parse();
                foreach (var invalid in parsed.Invalid)
                {
                    known = false; combined.Remove(invalid); omitted.Add(invalid);
                    notes.Add($"{name}: valor malformado para {invalid}.");
                }
                foreach (var pair in parsed.Values)
                {
                    if (omitted.Contains(pair.Key)) continue;
                    if (combined.TryGetValue(pair.Key, out var existing) && !string.Equals(existing, pair.Value, StringComparison.Ordinal))
                    {
                        combined.Remove(pair.Key); omitted.Add(pair.Key); known = false;
                        notes.Add($"conflicto entre archivos guardados para {pair.Key}");
                    }
                    else combined[pair.Key] = pair.Value;
                }
            }
            catch (FileNotFoundException) { notes.Add($"{name}: no se encontró."); known = false; }
            catch (DirectoryNotFoundException) { notes.Add($"{name}: no se encontró."); known = false; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException or DecoderFallbackException or ArgumentException or NotSupportedException)
            { notes.Add($"{name}: no se pudo leer o está malformado."); known = false; }
        }
        return new StoredConvarReadResult(combined, known);
    }

    private sealed class Parser(string text)
    {
        private int _index;
        private bool _found;
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _invalid = new(StringComparer.Ordinal);
        public (Dictionary<string, string> Values, HashSet<string> Invalid) Parse()
        {
            if (!string.Equals(Token(), "config", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            Require('{'); Block(0, false);
            Skip(); if (_index != text.Length || !_found) throw new InvalidDataException();
            return (_values, _invalid);
        }
        private void Block(int depth, bool convars)
        {
            if (depth > 32) throw new InvalidDataException();
            while (true)
            {
                Skip(); if (_index >= text.Length) throw new InvalidDataException();
                if (text[_index] == '}') { _index++; return; }
                var key = Token(); Skip();
                if (_index < text.Length && text[_index] == '{')
                {
                    _index++;
                    var selected = depth == 0 && string.Equals(key, "convars", StringComparison.OrdinalIgnoreCase);
                    if (selected && _found) throw new InvalidDataException();
                    if (selected) _found = true;
                    Block(depth + 1, selected);
                }
                else
                {
                    var value = Token();
                    if (!convars || !Allowed.Contains(key)) continue;
                    if (value.Length > 64 || value.Any(char.IsControl) || (_values.TryGetValue(key, out var old) && old != value))
                    { _values.Remove(key); _invalid.Add(key); }
                    else if (!_invalid.Contains(key)) _values[key] = value;
                }
            }
        }
        private string Token()
        {
            Skip();
            if (_index >= text.Length || text[_index++] != '"') throw new InvalidDataException();
            var start = _index;
            while (_index < text.Length && text[_index] != '"')
            {
                if (text[_index] == '\\' && _index + 1 < text.Length && text[_index + 1] == '"') _index++;
                _index++;
            }
            if (_index >= text.Length) throw new InvalidDataException();
            var result = text[start.._index]; _index++; return result;
        }
        private void Require(char c) { Skip(); if (_index >= text.Length || text[_index++] != c) throw new InvalidDataException(); }
        private void Skip()
        {
            while (_index < text.Length)
            {
                if (char.IsWhiteSpace(text[_index])) { _index++; continue; }
                if (_index + 1 < text.Length && text[_index] == '/' && text[_index + 1] == '/')
                { while (_index < text.Length && text[_index] != '\n') _index++; continue; }
                break;
            }
        }
    }
}
