using System.Text.Json;
using FragForge.Core.Logging;

namespace FragForge.Core.Localization;

public sealed class Localizer
{
    private readonly IReadOnlyDictionary<string, string> _strings;
    public string Culture { get; }
    public Localizer(string culture, IReadOnlyDictionary<string, string> strings)
    {
        Culture = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "es-MX";
        _strings = strings;
    }
    public string this[string key] => _strings.TryGetValue(key, out var value) ? value : key;
    public string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.GetCultureInfo(Culture), this[key], args);
    public static Localizer FromJson(string culture, string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Options) ?? throw new InvalidDataException("Localization file is empty.");
        return new Localizer(culture, values);
    }
}
