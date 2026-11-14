using System.Text.Json;
using System.Text.Json.Serialization;
using FragForge.Core.Logging;

namespace FragForge.Core.Cs2Profile;

/// <summary>Tipo de una opción: o es una convar del .cfg, o una opción de lanzamiento de Steam.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Cs2OptionKind>))]
public enum Cs2OptionKind
{
    /// <summary>Comando de consola que va dentro de un .cfg.</summary>
    Cfg,

    /// <summary>Argumento que se pega en Steam, NO va en el .cfg.</summary>
    LaunchOption
}

/// <summary>Tipo de valor. <see cref="None"/> es una opción sin valor (los argumentos -novid/-nojoy).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Cs2OptionValueType>))]
public enum Cs2OptionValueType { Bool, Int, Enum, None }

/// <summary>
/// Nivel de evidencia. <see cref="Verified"/> exige una fuente citable; sin ella, la opción NO puede
/// declararse verificada aunque "todo el mundo sepa" que funciona. No hay promesas de FPS aquí.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Cs2Evidence>))]
public enum Cs2Evidence { Verified, Observed, Unverified }

/// <summary>Descripción en los dos idiomas. La clave de cada uno es fija: no hay idiomas "extra".</summary>
public sealed record BilingualText
{
    [JsonPropertyName("esMx")] public string EsMx { get; init; } = "";
    [JsonPropertyName("enUs")] public string EnUs { get; init; } = "";

    /// <summary>Texto del idioma pedido, con la misma regla que <see cref="Localization.Localizer"/>: en* -> inglés.</summary>
    public string Resolve(string culture) =>
        culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? EnUs : EsMx;

    public bool IsComplete => !string.IsNullOrWhiteSpace(EsMx) && !string.IsNullOrWhiteSpace(EnUs);
}

/// <summary>Una opción del catálogo, tal cual viene del JSON embebido.</summary>
public sealed record Cs2Option
{
    public string Id { get; init; } = "";
    public Cs2OptionKind Kind { get; init; }
    /// <summary>Comando exacto que se escribe en el .cfg o argumento exacto de Steam.</summary>
    public string Text { get; init; } = "";
    public Cs2OptionValueType ValueType { get; init; }
    /// <summary>Valor sugerido de partida, o null si no hay uno razonable.</summary>
    public string? DefaultValue { get; init; }
    public BilingualText Description { get; init; } = new();
    /// <summary>Efecto esperado, SIN cifras: describir no es prometer.</summary>
    public BilingualText ExpectedEffect { get; init; } = new();
    public Cs2Evidence Evidence { get; init; }
    public string Source { get; init; } = "";
    /// <summary>Ids de otras opciones que no tienen sentido a la vez que esta.</summary>
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];

    /// <summary>Solo las opciones con fuente citable pueden declararse verificadas.</summary>
    public bool IsVerifiable => Evidence == Cs2Evidence.Verified && !string.IsNullOrWhiteSpace(Source);
}

/// <summary>Documento raíz del JSON. La versión se comprueba: un esquema desconocido no se adivina.</summary>
public sealed record Cs2OptionDocument
{
    public int SchemaVersion { get; init; }
    public IReadOnlyList<Cs2Option> Options { get; init; } = [];
}

/// <summary>
/// Catálogo de opciones de CS2. Se carga de un recurso EMBEBIDO: no lee nada del disco del usuario ni de
/// Steam. Cargar el catálogo no toca la máquina.
/// </summary>
/// <remarks>
/// Semilla cerrada de esta ronda: seis convars y dos opciones de lanzamiento, todas <c>Observed</c> con
/// fuente "observado en la configuración real del usuario (solo lectura)". Ninguna es <c>Verified</c> porque
/// no hay fuente citable en esta ronda; una opción sin fuente no se promociona a Verified.
/// </remarks>
public sealed class Cs2OptionCatalog
{
    private readonly IReadOnlyList<Cs2Option> _options;
    private Cs2OptionCatalog(IReadOnlyList<Cs2Option> options) => _options = options;

    public IReadOnlyList<Cs2Option> Options => _options;

    public Cs2Option? Find(string id) => _options.FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static Cs2OptionCatalog LoadEmbedded()
    {
        using var stream = typeof(Cs2OptionCatalog).Assembly.GetManifestResourceStream("FragForge.Core.Cs2Profile.cs2-options.json")
            ?? throw new InvalidDataException("Falta el recurso embebido cs2-options.json.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static Cs2OptionCatalog Parse(string json)
    {
        Cs2OptionDocument? document;
        try { document = JsonSerializer.Deserialize<Cs2OptionDocument>(json, JsonDefaults.Options); }
        catch (JsonException ex) { throw new InvalidDataException("cs2-options.json no es JSON válido: " + ex.Message, ex); }
        if (document is null) throw new InvalidDataException("cs2-options.json está vacío.");
        if (document.SchemaVersion != 1) throw new InvalidDataException($"schemaVersion {document.SchemaVersion} no soportada.");

        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in document.Options)
        {
            if (string.IsNullOrWhiteSpace(option.Id)) { errors.Add("Opción sin id."); continue; }
            if (!ids.Add(option.Id)) errors.Add($"{option.Id}: id duplicado.");
            if (string.IsNullOrWhiteSpace(option.Text)) errors.Add($"{option.Id}: sin comando/texto.");
            if (!option.Description.IsComplete) errors.Add($"{option.Id}: descripción sin los dos idiomas.");
            if (!option.ExpectedEffect.IsComplete) errors.Add($"{option.Id}: efecto esperado sin los dos idiomas.");
            // Una opción marcada Verified sin fuente sería exactamente lo que esta ronda prohíbe.
            if (option.Evidence == Cs2Evidence.Verified && string.IsNullOrWhiteSpace(option.Source))
                errors.Add($"{option.Id}: se declara Verified sin fuente citable.");
        }

        // Los conflictos deben apuntar a ids que existen y ser simétricos: si A choca con B, B choca con A.
        foreach (var option in document.Options)
        foreach (var other in option.ConflictsWith)
        {
            if (ids.Contains(other) is false) { errors.Add($"{option.Id}: conflictsWith apunta a un id inexistente ({other})."); continue; }
            if (other.Equals(option.Id, StringComparison.OrdinalIgnoreCase)) { errors.Add($"{option.Id}: se declara en conflicto consigo misma."); continue; }
            var target = document.Options.FirstOrDefault(o => o.Id.Equals(other, StringComparison.OrdinalIgnoreCase));
            if (target is not null && !target.ConflictsWith.Contains(option.Id, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{option.Id} declara conflicto con {other}, pero no al revés.");
        }

        if (errors.Count > 0) throw new InvalidDataException("cs2-options.json inválido: " + string.Join(" ", errors));
        return new Cs2OptionCatalog(document.Options);
    }
}
