using FragForge.Core.Localization;

namespace FragForge.Tweaks.Localization;

public static class LocalizationLoader
{
    public static async Task<Localizer> LoadAsync(string culture, CancellationToken cancellationToken = default)
    {
        var normalized = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "es-MX";
        var resource = $"FragForge.Tweaks.Localization.{normalized}.json";
        using var stream = typeof(LocalizationLoader).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException($"Localization resource '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return Localizer.FromJson(normalized, await reader.ReadToEndAsync(cancellationToken));
    }
}
