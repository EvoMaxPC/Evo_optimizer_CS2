namespace FragForge.Core.Localization;

/// <summary>
/// Elección de la cultura inicial de la UI.
///
/// Hotfix Capa 0: el combo de idioma aparecía sin selección. La lista de culturas y el valor
/// inicial deben salir de la MISMA fuente, y el valor elegido tiene que existir en esa lista.
/// Esta función es pura precisamente para poder probarla sin WPF.
/// </summary>
public static class CultureDefaults
{
    /// <summary>Culturas que el binario puede cargar (los JSON embebidos: es-MX y en-US).</summary>
    public static IReadOnlyList<string> Supported { get; } = ["es-MX", "en-US"];

    /// <summary>
    /// Cultura inicial: la de la UI si está soportada; si no, es-MX; si no, en-US.
    /// Devuelve null si la lista no tiene ninguna soportada, para NO seleccionar en falso.
    /// </summary>
    public static string? PickInitialCulture(IEnumerable<string> available, string? uiCulture)
    {
        var list = available.ToArray();
        if (list.Length == 0) return null;

        if (!string.IsNullOrWhiteSpace(uiCulture))
        {
            // Coincidencia exacta primero ("es-MX"), luego solo idioma ("es" -> es-MX).
            var exact = list.FirstOrDefault(c => string.Equals(c, uiCulture, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var language = uiCulture.Split('-')[0];
            var byLanguage = list.FirstOrDefault(c =>
                c.Split('-')[0].Equals(language, StringComparison.OrdinalIgnoreCase));
            if (byLanguage is not null) return byLanguage;
        }

        foreach (var fallback in new[] { "es-MX", "en-US" })
        {
            var match = list.FirstOrDefault(c => string.Equals(c, fallback, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return null;
    }
}
