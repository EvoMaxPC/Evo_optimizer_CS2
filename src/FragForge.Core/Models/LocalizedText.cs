using System.Text.Json.Serialization;

namespace FragForge.Core.Models;

public sealed record LocalizedText
{
    [JsonPropertyName("es-MX")]
    public required string EsMx { get; init; }
    [JsonPropertyName("en-US")]
    public required string EnUs { get; init; }
    public string Resolve(string? culture) => culture?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? EnUs : EsMx;
}
