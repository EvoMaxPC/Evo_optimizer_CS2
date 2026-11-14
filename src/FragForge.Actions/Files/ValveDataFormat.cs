using System.Text;

namespace FragForge.Actions.Files;

public sealed class VdfNode
{
    public string? Value { get; init; }
    public IReadOnlyDictionary<string, VdfNode> Children { get; init; } = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
    public bool IsObject => Value is null;
}
public static class ValveDataFormat
{
    public static VdfNode Parse(string text)
    {
        if (text.Length > 1048576) throw new InvalidDataException("VDF exceeds 1 MiB.");
        var index = 0;
        var result = ParseObject(text, ref index, 0, true);
        SkipTrivia(text, ref index);
        if (index != text.Length) throw new InvalidDataException("Trailing characters in VDF.");
        return result;
    }
    private static VdfNode ParseObject(string text, ref int index, int depth, bool root)
    {
        if (depth > 64) throw new InvalidDataException("VDF nesting exceeds 64.");
        var children = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length) { if (root) return new VdfNode { Children = children }; throw new InvalidDataException("Unclosed VDF object."); }
            if (text[index] == '}') { index++; if (root) throw new InvalidDataException("Unexpected closing brace."); return new VdfNode { Children = children }; }
            var key = ParseToken(text, ref index);
            SkipTrivia(text, ref index);
            if (index >= text.Length) throw new InvalidDataException("Missing VDF value.");
            VdfNode value;
            if (text[index] == '{') { index++; value = ParseObject(text, ref index, depth + 1, false); }
            else value = new VdfNode { Value = ParseToken(text, ref index) };
            if (!children.TryAdd(key, value)) throw new InvalidDataException($"Duplicate VDF key '{key}'.");
        }
    }
    private static string ParseToken(string text, ref int index)
    {
        SkipTrivia(text, ref index);
        if (index >= text.Length) throw new InvalidDataException("Unexpected end of VDF.");
        if (text[index] == '"')
        {
            index++;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"') return builder.ToString();
                if (c == '\\')
                {
                    if (index >= text.Length) break;
                    var escape = text[index++];
                    builder.Append(escape switch { '\\' => '\\', '"' => '"', 'n' => '\n', 't' => '\t', _ => throw new InvalidDataException($"Unsupported VDF escape '\\{escape}'.") });
                }
                else builder.Append(c);
            }
            throw new InvalidDataException("Unclosed VDF string.");
        }
        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not '{' and not '}') index++;
        if (start == index) throw new InvalidDataException("Empty VDF token.");
        return text[start..index];
    }
    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index])) { index++; continue; }
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/') { while (index < text.Length && text[index] != '\n') index++; continue; }
            break;
        }
    }
}
