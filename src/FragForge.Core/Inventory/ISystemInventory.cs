namespace FragForge.Core.Inventory;

/// <summary>
/// Inventario de solo lectura del sistema. Todo lo que devuelve es observación: la implementación
/// NO escribe nada, y si no puede leer algo lo dice en <see cref="SystemInventory.Warnings"/> en lugar
/// de devolver listas vacías como si no hubiera nada.
/// </summary>
public interface ISystemInventory
{
    Task<SystemInventory> BuildAsync(CancellationToken cancellationToken = default);
}

/// <summary>Inventario de servicios (incluye plantillas por usuario).</summary>
public interface IServiceInventorySource
{
    Task<(IReadOnlyList<ServiceEntry> Services, IReadOnlyList<UserServiceTemplate> Templates, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Inventario de tareas programadas.</summary>
public interface ITaskInventorySource
{
    Task<(IReadOnlyList<TaskEntry> Tasks, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Inventario de arranque: claves Run, carpeta Startup y StartupApproved.</summary>
public interface IStartupInventorySource
{
    /// <summary>
    /// <paramref name="interactiveUserSid"/> es el SID del usuario que juega, para leer SU hive
    /// (<c>HKU\&lt;SID&gt;</c>) en vez del HKCU del proceso elevado. Si es null o inválido, no se lee
    /// HKCU y el motivo queda en <c>Notes</c>. Parámetro OPCIONAL: las implementaciones sin estado
    /// (plataforma no soportada) lo ignoran.
    /// </summary>
    Task<(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes)> ReadAsync(string? interactiveUserSid, CancellationToken cancellationToken = default);
}

/// <summary>Inventario de paquetes Appx. Solo lectura: la aplicación NO desinstala nada.</summary>
public interface IAppxInventorySource
{
    Task<(IReadOnlyList<AppxEntry> Entries, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default);
}
