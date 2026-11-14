using System.Text.Json;
using System.Text.Json.Serialization;

namespace FragForge.Core.Logging;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();
    public static JsonSerializerOptions Create(bool indented = true) => new()
    {
        WriteIndented = indented,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(null, allowIntegerValues: false) },
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
