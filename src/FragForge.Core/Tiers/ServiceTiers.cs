using System.Text.Json;
using FragForge.Core.Inventory;
using FragForge.Core.Logging;

namespace FragForge.Core.Tiers;

/// <summary>Una entrada de <c>service-tiers.json</c>. Es DATO, no código.</summary>
public sealed record ServiceTierEntry
{
    public required string Name { get; init; }
    public required SystemTier Tier { get; init; }
    public required string Reason { get; init; }
    public required string Reference { get; init; }
    public string? Recommendation { get; init; }
    /// <summary>Si es true, el tweak correspondiente debe marcarse opt-in.</summary>
    public bool OptIn { get; init; }
}

public sealed record ServiceTierDocument
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<ServiceTierEntryDto> Services { get; init; } = [];
}

/// <summary>
/// Forma del JSON tal cual. El tier llega como TEXTO ("T0".."T4") y se mapea a mano: el convertidor
/// de enums de System.Text.Json compara contra el NOMBRE del miembro, así que un "T1" en el archivo
/// nunca casaría con <see cref="SystemTier.Safe"/>. Mapearlo aquí también permite un error legible.
/// </summary>
public sealed record ServiceTierEntryDto
{
    public string Name { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Reference { get; init; } = "";
    public string? Recommendation { get; init; }
    public bool OptIn { get; init; }
}

/// <summary>
/// Clasificación de servicios por tiers. Se carga del recurso embebido y se VALIDA al cargar:
/// una entrada sin motivo, sin referencia o con un tier ilegible hace fallar la carga entera.
/// Un servicio sin entrada se considera <see cref="SystemTier.NeverTouch"/> por omisión segura:
/// el validador del catálogo solo acepta T1/T2, así que lo desconocido nunca es aplicable.
/// </summary>
public sealed class ServiceTierCatalog
{
    private readonly IReadOnlyDictionary<string, ServiceTierEntry> _entries;

    private ServiceTierCatalog(IReadOnlyDictionary<string, ServiceTierEntry> entries) => _entries = entries;

    public IReadOnlyCollection<ServiceTierEntry> Entries => _entries.Values.ToArray();

    public static ServiceTierCatalog Empty { get; } = new(new Dictionary<string, ServiceTierEntry>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Servicios del núcleo que este catálogo garantiza como T0 aunque falten en el JSON.</summary>
    public static IReadOnlyList<string> CoreNeverTouch { get; } =
    [
        "RpcSs", "DcomLaunch", "PlugPlay", "Power", "EventLog", "CryptSvc", "Winmgmt", "Schedule",
        "Themes", "Audiosrv", "AudioEndpointBuilder", "Dnscache", "NlaSvc", "LanmanWorkstation",
        "UserManager", "ProfSvc", "TrustedInstaller",
        "ipfsvc", "jhi_service", "cplspcon", "WMIRegistrationService"
    ];

    public SystemTier TierOf(string service) => _entries.TryGetValue(service, out var entry) ? entry.Tier : SystemTier.NeverTouch;

    public ServiceTierEntry? Find(string service) => _entries.TryGetValue(service, out var entry) ? entry : null;

    public bool IsKnown(string service) => _entries.ContainsKey(service);

    /// <summary>Solo T1 y T2 son aplicables. T0, T3, T4 y lo desconocido no lo son.</summary>
    public bool IsApplicable(string service) => TierOf(service) is SystemTier.Safe or SystemTier.Optional;

    /// <summary>Mapea el texto del JSON al enum. Acepta también el nombre del miembro, por comodidad.</summary>
    public static bool TryParseTier(string? text, out SystemTier tier)
    {
        switch (text?.Trim().ToUpperInvariant())
        {
            case "T0" or "NEVERTOUCH": tier = SystemTier.NeverTouch; return true;
            case "T1" or "SAFE": tier = SystemTier.Safe; return true;
            case "T2" or "OPTIONAL": tier = SystemTier.Optional; return true;
            case "T3" or "MEASUREONLY": tier = SystemTier.MeasureOnly; return true;
            case "T4" or "INSPECTONLY": tier = SystemTier.InspectOnly; return true;
            default: tier = SystemTier.NeverTouch; return false;
        }
    }

    public static ServiceTierCatalog LoadEmbedded()
    {
        using var stream = typeof(ServiceTierCatalog).Assembly.GetManifestResourceStream("FragForge.Core.Tiers.service-tiers.json")
            ?? throw new InvalidDataException("Falta el recurso embebido service-tiers.json.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Parsea y valida. Lanza <see cref="InvalidDataException"/> con TODOS los problemas encontrados,
    /// no solo el primero, para que un JSON mal editado se arregle de una vez.
    /// </summary>
    public static ServiceTierCatalog Parse(string json)
    {
        ServiceTierDocument? document;
        try { document = JsonSerializer.Deserialize<ServiceTierDocument>(json, JsonDefaults.Options); }
        catch (JsonException ex) { throw new InvalidDataException("service-tiers.json no es JSON válido: " + ex.Message, ex); }
        if (document is null) throw new InvalidDataException("service-tiers.json está vacío.");
        if (document.SchemaVersion != 1) throw new InvalidDataException($"schemaVersion {document.SchemaVersion} no soportada.");

        var errors = new List<string>();
        var map = new Dictionary<string, ServiceTierEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in document.Services)
        {
            if (string.IsNullOrWhiteSpace(dto.Name)) { errors.Add("Entrada sin nombre."); continue; }
            if (dto.Name.Contains('_', StringComparison.Ordinal) && !dto.Name.Contains("Svc", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{dto.Name}: las instancias con sufijo variable no van en el catálogo; usa la plantilla.");
            if (string.IsNullOrWhiteSpace(dto.Reason) || dto.Reason.Length < 10) errors.Add($"{dto.Name}: motivo ausente o demasiado corto.");
            if (string.IsNullOrWhiteSpace(dto.Reference)) errors.Add($"{dto.Name}: falta la referencia.");
            if (!TryParseTier(dto.Tier, out var tier)) { errors.Add($"{dto.Name}: tier '{dto.Tier}' ilegible; se espera T0, T1, T2, T3 o T4."); continue; }
            if (tier == SystemTier.NeverTouch) errors.Add($"{dto.Name}: T0 nunca se declara en el JSON; es el valor por omisión.");
            var entry = new ServiceTierEntry { Name = dto.Name, Tier = tier, Reason = dto.Reason, Reference = dto.Reference, Recommendation = dto.Recommendation, OptIn = dto.OptIn };
            if (!map.TryAdd(dto.Name, entry)) errors.Add($"{dto.Name}: entrada duplicada.");
        }
        foreach (var core in CoreNeverTouch)
            if (map.TryGetValue(core, out var entry) && entry.Tier != SystemTier.NeverTouch)
                errors.Add($"{core}: es núcleo de Windows/Intel y no puede tener tier {entry.Tier}.");

        if (errors.Count > 0) throw new InvalidDataException("service-tiers.json inválido: " + string.Join(" | ", errors));
        return new ServiceTierCatalog(map);
    }
}
