namespace FragForge.Core.Inventory;

/// <summary>Tier de riesgo operativo de un elemento del sistema. Ver <c>service-tiers.json</c>.</summary>
public enum SystemTier
{
    /// <summary>T0: núcleo o protegido. NUNCA se toca.</summary>
    NeverTouch = 0,
    /// <summary>T1: seguro y sin dependientes conocidos.</summary>
    Safe = 1,
    /// <summary>T2: opcional según el uso que el usuario haga de la función.</summary>
    Optional = 2,
    /// <summary>T3: solo medir; no se recomienda tocarlo.</summary>
    MeasureOnly = 3,
    /// <summary>T4: solo inspección.</summary>
    InspectOnly = 4
}

public sealed record ServiceDependency(string Service, bool IsDependent, string Source);

/// <summary>Un servicio tal como está en el equipo ahora. Solo lectura.</summary>
public sealed record ServiceEntry
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public string StartMode { get; init; } = "Unknown";
    public string State { get; init; } = "Unknown";
    public bool? DelayedAutoStart { get; init; }
    /// <summary>Cuenta de inicio (LocalSystem, LocalService, NetworkService o usuario). Nunca su contraseña.</summary>
    public string? Account { get; init; }
    public string? ExecutablePath { get; init; }
    /// <summary>Dependencias declaradas por este servicio (para arrancar).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    /// <summary>Servicios que dependen de este.</summary>
    public IReadOnlyList<string> Dependents { get; init; } = [];
    /// <summary>true si el servicio ya está en el estado que el catálogo pediría.</summary>
    public bool AlreadyOptimized { get; init; }
    public IReadOnlyList<ServiceDependency> DependencyEvidence { get; init; } = [];
}

/// <summary>
/// Un servicio por usuario. Windows crea instancias con sufijo variable (p. ej. <c>CDPUserSvc_5f2a1</c>);
/// el catálogo apunta a la PLANTILLA y la evidencia se toma de la clave <c>Start</c> del servicio plantilla.
/// </summary>
public sealed record UserServiceTemplate
{
    public required string Template { get; init; }
    public string? StartMode { get; init; }
    public IReadOnlyList<string> Instances { get; init; } = [];
    public string RegistryKey { get; init; } = "";
}

public sealed record TaskEntry
{
    public required string Path { get; init; }
    public string? Folder { get; init; }
    public string State { get; init; } = "Unknown";
    public string? LastResult { get; init; }
    public string? Author { get; init; }
    public bool Enabled { get; init; }
}

public enum StartupLocation { RunHklm, RunHkcu, RunWow6432, StartupFolderHklm, StartupFolderHkcu }

public sealed record StartupEntry
{
    public required string Name { get; init; }
    public required StartupLocation Location { get; init; }
    public string? Command { get; init; }
    /// <summary>Estado según StartupApproved (mecanismo del Administrador de tareas).</summary>
    public bool? ApprovedEnabled { get; init; }
    public string ApprovalKey { get; init; } = "";
    /// <summary>Byte crudo del valor binario de StartupApproved; 0x02/0x06/0x0E significan habilitado.</summary>
    public int? ApprovalFirstByte { get; init; }
}

public sealed record AppxEntry
{
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? PackageFullName { get; init; }
    public bool? IsFramework { get; init; }
}

/// <summary>
/// Inventario completo. Es DATO tipado, no <see cref="FragForge.Core.Models.DiagnosticObservation"/>:
/// se serializa en <c>reports\audit-&lt;fecha&gt;.json</c> con su propio esquema.
/// </summary>
public sealed record SystemInventory
{
    public int SchemaVersion { get; init; } = 1;
    public required string MachineName { get; init; }
    public string OsBuild { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ServiceEntry> Services { get; init; } = [];
    public IReadOnlyList<UserServiceTemplate> UserServiceTemplates { get; init; } = [];
    public IReadOnlyList<TaskEntry> Tasks { get; init; } = [];
    public IReadOnlyList<StartupEntry> Startup { get; init; } = [];
    public IReadOnlyList<AppxEntry> Appx { get; init; } = [];
    /// <summary>Lo que NO se pudo leer, con el motivo. Ausencia de dato nunca es dato.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>
    /// Observaciones informativas que NO son un fallo de lectura (ámbito del hive, por ejemplo).
    /// Campo ADITIVO y opcional: un inventario con solo notas sigue siendo completo.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Solo los fallos de lectura cuentan; una nota informativa no invalida el inventario.</summary>
    public bool IsComplete => Warnings.Count == 0;
}

/// <summary>Informe de auditoría tal como se guarda en disco.</summary>
public sealed record AuditReport
{
    public int SchemaVersion { get; init; } = 1;
    public required SystemInventory Inventory { get; init; }
    public IReadOnlyList<SystemTier> TiersApplied { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Servicios presentes en el equipo que el catálogo clasifica como T1/T2 (los únicos aplicables).</summary>
    public IReadOnlyList<string> ApplicableServices { get; init; } = [];
    /// <summary>Servicios presentes en el equipo que NO están en la tabla: por omisión son T0.</summary>
    public IReadOnlyList<string> UnknownServices { get; init; } = [];
    /// <summary>Servicios de la tabla que no existen en el equipo (no es un error: es información).</summary>
    public IReadOnlyList<string> CatalogOnlyServices { get; init; } = [];

    /// <summary>Construye el informe cruzando el inventario con la tabla de tiers.</summary>
    public static AuditReport From(SystemInventory inventory, Tiers.ServiceTierCatalog tiers)
    {
        var present = inventory.Services.Select(s => s.Name).ToArray();
        var applicable = present.Where(tiers.IsApplicable).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        var unknown = present.Where(n => !tiers.IsKnown(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        var catalogOnly = tiers.Entries.Select(e => e.Name).Where(n => !present.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        // El operador de propagación (..) SOLO es válido dentro de una expresión de colección ([...]).
        // En un inicializador de colección (new List<T> { ... }) no existe: el compilador lo lee como
        // un Index (como en lista[^1]) y falla con CS0029. Se usa una expresión de colección.
        List<string> notes =
        [
            "Inventario de solo lectura: FragForge no modificó nada para generarlo.",
            $"{unknown.Length} servicios no figuran en service-tiers.json y se tratan como T0 (nunca aplicables).",
            .. inventory.Notes
        ];
        return new AuditReport
        {
            Inventory = inventory,
            TiersApplied = [SystemTier.NeverTouch, SystemTier.Safe, SystemTier.Optional, SystemTier.MeasureOnly, SystemTier.InspectOnly],
            ApplicableServices = applicable,
            UnknownServices = unknown,
            CatalogOnlyServices = catalogOnly,
            Notes = notes
        };
    }
}
