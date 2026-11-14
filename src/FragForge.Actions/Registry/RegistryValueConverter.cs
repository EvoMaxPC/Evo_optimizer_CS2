using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Registry;

public static class RegistryValueConverter
{
    public static readonly IReadOnlySet<string> SupportedKinds = new HashSet<string>(StringComparer.Ordinal) { "String", "ExpandString", "DWord", "QWord", "Binary", "MultiString" };
    public static RegistryWriteValue FromJson(string kind, JsonElement value)
    {
        if (!SupportedKinds.Contains(kind)) throw new CatalogValidationException($"Unsupported registry kind '{kind}'.", [kind]);
        object data = kind switch
        {
            "String" or "ExpandString" => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("Expected string."),
            "DWord" => value.TryGetInt32(out var i) ? i : throw new JsonException("Expected Int32."),
            "QWord" => value.TryGetInt64(out var l) ? l : throw new JsonException("Expected Int64."),
            "Binary" => Convert.FromBase64String(value.GetString() ?? throw new JsonException("Expected base64 string.")),
            "MultiString" => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.GetString() ?? throw new JsonException("Null string in array.")).ToArray() : throw new JsonException("Expected string array."),
            _ => throw new JsonException("Unsupported kind.")
        };
        return new RegistryWriteValue(kind, data);
    }
    public static RegistryValueCapture Capture(string key, string name, RegistryReadResult result, IReadOnlyList<string> missingAncestors) => new()
    {
        Key = key, Name = name, KeyExisted = result.KeyExists, ValueExisted = result.ValueExists, ValueKind = result.Kind,
        StringData = result.Data as string,
        IntegerData = result.Data is int i ? i : result.Data is long l ? l : null,
        MultiStringData = result.Data is string[] strings ? strings : null,
        BinaryBase64 = result.Data is byte[] bytes ? Convert.ToBase64String(bytes) : null,
        MissingAncestors = missingAncestors
    };
    public static RegistryWriteValue ToWriteValue(RegistryValueCapture capture) => capture.ValueKind switch
    {
        "String" or "ExpandString" => new(capture.ValueKind, capture.StringData ?? ""),
        "DWord" => new(capture.ValueKind, unchecked((int)(capture.IntegerData ?? 0))),
        "QWord" => new(capture.ValueKind, capture.IntegerData ?? 0L),
        "Binary" => new(capture.ValueKind, Convert.FromBase64String(capture.BinaryBase64 ?? "")),
        "MultiString" => new(capture.ValueKind, (capture.MultiStringData ?? []).ToArray()),
        _ => throw new InvalidDataException($"No se puede restaurar el tipo '{capture.ValueKind}'.")
    };
    public static bool DataEqual(RegistryWriteValue expected, RegistryReadResult actual)
    {
        if (!actual.ValueExists || !string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal)) return false;
        return expected.Data switch
        {
            string s => actual.Data is string a && s == a,
            int i => actual.Data is int ai && i == ai,
            long l => actual.Data is long al && l == al,
            byte[] b => actual.Data is byte[] ab && b.AsSpan().SequenceEqual(ab),
            string[] s => actual.Data is string[] aa && s.SequenceEqual(aa),
            _ => false
        };
    }
    public static string Describe(RegistryWriteValue value) => value.Data switch
    {
        byte[] bytes => Convert.ToBase64String(bytes),
        string[] strings => string.Join(" | ", strings),
        _ => Convert.ToString(value.Data, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };
}
