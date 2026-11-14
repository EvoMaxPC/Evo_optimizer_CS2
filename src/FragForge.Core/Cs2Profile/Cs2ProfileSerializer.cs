using System.Text.Json;
using System.Text.Json.Serialization;
using FragForge.Core.Logging;

namespace FragForge.Core.Cs2Profile;

/// <summary>Error de importación, ya en texto listo para mostrar. No hay excepciones genéricas al usuario.</summary>
public sealed class Cs2ProfileImportException(string message) : Exception(message);

/// <summary>
/// Serializa y deserializa el perfil (JSON). El import es ESTRICTO: un archivo con campos desconocidos o
/// una versión que no se entiende se rechaza con un mensaje claro, en vez de cargarse a medias.
/// </summary>
/// <remarks>
/// Estricto por una razón práctica: un perfil a medias produciría un .cfg que parece correcto y no lo es.
/// Es preferible decir "este archivo no es de esta versión" a generar un archivo con la mitad de los valores.
/// </remarks>
public static class Cs2ProfileSerializer
{
    /// <summary>Opciones de JSON del perfil: enums por NOMBRE (nunca su número) y campos desconocidos prohibidos.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter<Cs2ProfileObjective>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<FpsCapMode>(allowIntegerValues: false));
        return options;
    }

    public static string Serialize(Cs2ProfilePreferences preferences) =>
        JsonSerializer.Serialize(preferences, Options) + "\n";

    public static Cs2ProfilePreferences Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new Cs2ProfileImportException("El archivo del perfil está vacío.");

        Cs2ProfilePreferences? preferences;
        try
        {
            preferences = JsonSerializer.Deserialize<Cs2ProfilePreferences>(json, Options);
        }
        catch (JsonException ex)
        {
            // El mensaje de System.Text.Json ya dice la línea y el campo; se conserva tal cual.
            throw new Cs2ProfileImportException("El archivo no es un perfil de FragForge válido: " + ex.Message);
        }

        if (preferences is null) throw new Cs2ProfileImportException("El archivo del perfil está vacío.");

        if (preferences.FormatVersion != Cs2ProfilePreferences.CurrentFormatVersion)
            throw new Cs2ProfileImportException(
                $"Versión de perfil {preferences.FormatVersion} no soportada; esta versión de FragForge lee la {Cs2ProfilePreferences.CurrentFormatVersion}.");

        Validate(preferences);
        return preferences;
    }

    /// <summary>Coherencia interna: un modo de tope sin su dato no se acepta al importar.</summary>
    private static void Validate(Cs2ProfilePreferences preferences)
    {
        var errors = new List<string>();

        foreach (var option in preferences.Options)
            if (string.IsNullOrWhiteSpace(option.Id)) errors.Add("Hay una opción seleccionada sin id.");

        var duplicate = preferences.Options
            .GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) errors.Add($"La opción '{duplicate.Key}' aparece más de una vez.");

        switch (preferences.FpsCap.Mode)
        {
            case FpsCapMode.Fijo when preferences.FpsCap.FixedFps is null or <= 0:
                errors.Add("El tope de FPS es 'Fijo' pero no tiene un valor mayor que 0.");
                break;
            case FpsCapMode.DerivadoDelRefresco when preferences.FpsCap.RefreshFactor is <= 0:
                errors.Add("El tope derivado del refresco tiene un factor que no es mayor que 0.");
                break;
        }

        if (preferences.MeasuredRefreshHz is <= 0)
            errors.Add("La tasa de refresco medida debe ser mayor que 0 si se indica (usa null si no se conoce).");

        if (errors.Count > 0) throw new Cs2ProfileImportException("El perfil no es coherente: " + string.Join(" ", errors));
    }
}
