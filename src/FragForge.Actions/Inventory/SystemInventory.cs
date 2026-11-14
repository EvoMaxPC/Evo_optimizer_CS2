using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Inventory;
using FragForge.Core.Models;

namespace FragForge.Actions.Inventory;

/// <summary>
/// Compone el inventario completo a partir de las cuatro fuentes. Si una fuente falla, el inventario
/// se devuelve INCOMPLETO con el motivo en <c>SystemInventory.Warnings</c>.
///
/// Regla: un inventario vacío por error no puede parecer un sistema limpio.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemInventory(
    IServiceInventorySource services,
    ITaskInventorySource tasks,
    IStartupInventorySource startup,
    IAppxInventorySource appx,
    IHardwareProfiler profiler) : ISystemInventory
{
    public async Task<SystemInventory> BuildAsync(CancellationToken cancellationToken = default)
    {
        var hardware = await profiler.ProfileAsync(cancellationToken);
        return await InventoryComposer.ComposeAsync(hardware, services, tasks, startup, appx, [], [], cancellationToken);
    }
}

/// <summary>
/// Composición compartida por la versión Windows y la versión Unsupported, para que ambas devuelvan la
/// MISMA forma. Vive fuera de los tipos marcados <c>[SupportedOSPlatform]</c> para que la versión no-Windows
/// no herede la restricción de plataforma.
/// </summary>
public static class InventoryComposer
{
    public static async Task<SystemInventory> ComposeAsync(HardwareProfile hardware, IServiceInventorySource serviceSource, ITaskInventorySource taskSource, IStartupInventorySource startupSource, IAppxInventorySource appxSource, List<string> warnings, List<string> notes, CancellationToken cancellationToken)
    {
        IReadOnlyList<ServiceEntry> serviceList = [];
        IReadOnlyList<UserServiceTemplate> templates = [];
        IReadOnlyList<TaskEntry> taskList = [];
        IReadOnlyList<StartupEntry> startupList = [];
        IReadOnlyList<AppxEntry> appxList = [];

        try { var result = await serviceSource.ReadAsync(cancellationToken); serviceList = result.Services; templates = result.Templates; warnings.AddRange(result.Warnings); }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Servicios: " + ex.Message); }

        try { var result = await taskSource.ReadAsync(cancellationToken); taskList = result.Tasks; warnings.AddRange(result.Warnings); }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Tareas: " + ex.Message); }

        // El inventario de arranque necesita el SID del usuario interactivo: su hive, no el del proceso (R-H).
        try
        {
            var result = await startupSource.ReadAsync(hardware.InteractiveUserSid, cancellationToken);
            startupList = result.Entries;
            warnings.AddRange(result.Warnings);
            notes.AddRange(result.Notes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Arranque: " + ex.Message); }

        try { var result = await appxSource.ReadAsync(cancellationToken); appxList = result.Entries; warnings.AddRange(result.Warnings); }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Appx: " + ex.Message); }

        return new SystemInventory
        {
            MachineName = hardware.MachineName,
            OsBuild = hardware.Os.Build.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OsVersion = hardware.Os.Version,
            Services = serviceList,
            UserServiceTemplates = templates,
            Tasks = taskList,
            Startup = startupList,
            Appx = appxList,
            Warnings = warnings,
            Notes = notes
        };
    }
}

/// <summary>Inventario en plataformas sin Windows: cada sección vacía, cada motivo declarado.</summary>
public sealed class UnsupportedSystemInventory(IHardwareProfiler profiler) : ISystemInventory
{
    public async Task<SystemInventory> BuildAsync(CancellationToken cancellationToken = default)
    {
        var hardware = await profiler.ProfileAsync(cancellationToken);
        return await InventoryComposer.ComposeAsync(hardware, new UnsupportedServiceInventorySource(), new UnsupportedTaskInventorySource(), new UnsupportedStartupInventorySource(), new UnsupportedAppxInventorySource(), [], [], cancellationToken);
    }
}

public sealed class UnsupportedServiceInventorySource : IServiceInventorySource
{
    public Task<(IReadOnlyList<ServiceEntry> Services, IReadOnlyList<UserServiceTemplate> Templates, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(((IReadOnlyList<ServiceEntry>)[], (IReadOnlyList<UserServiceTemplate>)[], (IReadOnlyList<string>)["El inventario de servicios requiere Windows."]));
}
