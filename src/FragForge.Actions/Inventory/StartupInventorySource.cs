using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Inventory;
using FragForge.Core.Persistence;

namespace FragForge.Actions.Inventory;

/// <summary>
/// Inventario de arranque. Lee las ubicaciones Run (HKLM, Wow6432Node, y el hive DEL USUARIO
/// INTERACTIVO) y las carpetas Startup, y cruza cada entrada con <c>StartupApproved</c>, el mecanismo
/// real que usa el Administrador de tareas.
///
/// Regla R-H: <c>HKCU</c> NUNCA se lee como el hive del proceso. Se resuelve con
/// <see cref="RegistryPath.ResolveUserHive"/> contra el SID del usuario interactivo
/// (<c>HKU\&lt;SID&gt;\...</c>). Si el SID no se conoce, no se lee HKCU y se deja constancia en
/// <c>Notes</c>: no es un fallo de lectura, es un ámbito que no se puede determinar.
///
/// Solo lectura. La habilitación/deshabilitación se hace con una acción Registry binaria sobre el valor
/// de StartupApproved, no renombrando ni borrando la entrada.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupInventorySource(IRegistry registry) : IStartupInventorySource
{
    private const string RunKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalRun = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovalRun32 = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovalFolder = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public async Task<(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes)> ReadAsync(string? interactiveUserSid, CancellationToken cancellationToken = default)
    {
        var entries = new List<StartupEntry>();
        var warnings = new List<string>();
        var notes = new List<string>();

        // StartupApproved es SIEMPRE por usuario, también para las entradas de HKLM: el estado
        // habilitado/deshabilitado del arranque de máquina lo decide el usuario que inicia sesión.
        // Sin SID, esas entradas quedan con estado "desconocido" (null), que es la verdad.
        var haveUserHive = TryResolveUserKeys(interactiveUserSid, out var userKeys, out var sidProblem);
        if (haveUserHive) notes.Add($"HKCU se lee del hive del usuario interactivo ({interactiveUserSid}); el hive del proceso no se usa.");
        else notes.Add(sidProblem!);

        // --- HKLM: la clave es de máquina; su aprobación se cruza con el hive del usuario si existe.
        // Wow6432Node se aprueba en StartupApproved\Run32, no en Run.
        await ReadRunKeyAsync(StartupLocation.RunHklm, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", userKeys?.ApprovalRun, entries, warnings, cancellationToken);
        await ReadRunKeyAsync(StartupLocation.RunWow6432, @"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", userKeys?.ApprovalRun32, entries, warnings, cancellationToken);

        // --- Hive del usuario interactivo: solo si el SID es utilizable.
        if (userKeys is not null) await ReadRunKeyAsync(StartupLocation.RunHkcu, userKeys.Run, userKeys.ApprovalRun, entries, warnings, cancellationToken);

        // --- Carpetas Startup. La del sistema es independiente del usuario.
        await ReadStartupFolderAsync(StartupLocation.StartupFolderHklm, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), userKeys?.ApprovalFolder, entries, warnings, cancellationToken);

        // La carpeta Startup del usuario es una RUTA DE DISCO, no una clave: el SID no la determina y no
        // se puede resolver "por el SID" sin inventar una ruta bajo C:\Users. Se usa la del perfil del
        // proceso y se DECLARA el ámbito (nota), igual que antes: aquí no se gana ni se pierde.
        var userStartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(userStartupFolder))
            notes.Add("La carpeta Startup del usuario del proceso no se pudo resolver; no se leyó.");
        else
            await ReadStartupFolderAsync(StartupLocation.StartupFolderHkcu, userStartupFolder, userKeys?.ApprovalFolder ?? ApprovalFolder, entries, warnings, cancellationToken);

        return (entries.OrderBy(e => e.Location).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(), warnings, notes);
    }

    /// <summary>Claves del usuario interactivo ya resueltas a su hive real.</summary>
    private sealed record UserKeys(string Run, string ApprovalRun, string ApprovalRun32, string ApprovalFolder);

    /// <summary>
    /// Traduce las claves HKCU del usuario interactivo a <c>HKU\&lt;SID&gt;\...</c>. Devuelve null con
    /// un motivo legible si el SID falta o no sirve: sin SID no se adivina, no se lee.
    /// </summary>
    private static bool TryResolveUserKeys(string? sid, out UserKeys? keys, out string? problem)
    {
        keys = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(sid))
        {
            problem = "SID del usuario interactivo desconocido: no se leyó el arranque HKCU (el hive del proceso elevado no es el del usuario).";
            return false;
        }
        try
        {
            keys = new UserKeys(
                RegistryPath.ResolveUserHive(RunKey, sid),
                RegistryPath.ResolveUserHive(ApprovalRun, sid),
                RegistryPath.ResolveUserHive(ApprovalRun32, sid),
                RegistryPath.ResolveUserHive(ApprovalFolder, sid));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            problem = "SID del usuario interactivo inválido (" + ex.Message + "): no se leyó el arranque HKCU.";
            return false;
        }
    }

    private async Task ReadStartupFolderAsync(StartupLocation location, string folder, string? approvalKey, List<StartupEntry> entries, List<string> warnings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var approval = approvalKey is null ? new RegistryReadResult(false, false, null, null) : await registry.ReadValueAsync(approvalKey, name, cancellationToken);
                entries.Add(new StartupEntry
                {
                    Name = name,
                    Location = location,
                    Command = file,
                    ApprovalKey = approvalKey ?? "",
                    ApprovedEnabled = approval.ValueExists ? StartupApproval.IsEnabled(approval.Data as byte[] ?? []) : null,
                    ApprovalFirstByte = approval.ValueExists && approval.Data is byte[] { Length: > 0 } bytes ? bytes[0] : null
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Carpeta de arranque {folder}: {ex.Message}");
        }
    }

    private async Task ReadRunKeyAsync(StartupLocation location, string key, string? approvalKey, List<StartupEntry> entries, List<string> warnings, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names;
        try { names = await registry.GetValueNamesAsync(key, cancellationToken); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { warnings.Add($"{key}: {ex.Message}"); return; }

        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await registry.ReadValueAsync(key, name, cancellationToken);
            var approval = approvalKey is null ? new RegistryReadResult(false, false, null, null) : await registry.ReadValueAsync(approvalKey, name, cancellationToken);
            entries.Add(new StartupEntry
            {
                Name = name,
                Location = location,
                Command = value.Data as string,
                ApprovalKey = approvalKey ?? "",
                ApprovedEnabled = approval.ValueExists ? StartupApproval.IsEnabled(approval.Data as byte[] ?? []) : null,
                ApprovalFirstByte = approval.ValueExists && approval.Data is byte[] { Length: > 0 } bytes ? bytes[0] : null
            });
        }
    }
}

public sealed class UnsupportedStartupInventorySource : IStartupInventorySource
{
    public Task<(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes)> ReadAsync(string? interactiveUserSid, CancellationToken cancellationToken = default) =>
        Task.FromResult(((IReadOnlyList<StartupEntry>)[], (IReadOnlyList<string>)["El inventario de arranque requiere Windows."], (IReadOnlyList<string>)[]));
}
