using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Inventory;

namespace FragForge.Actions.Inventory;

/// <summary>
/// Inventario de arranque. Lee las cuatro ubicaciones Run (HKLM, HKCU, Wow6432Node en ambas) y las
/// carpetas Startup, y cruza cada entrada con <c>StartupApproved</c>, que es el mecanismo real que usa
/// el Administrador de tareas (<c>StartupApproved\Run</c>, <c>StartupApproved\StartupFolder</c>,
/// <c>StartupApproved\Run32</c>).
///
/// Solo lectura. La habilitación/deshabilitación se hace con una acción Registry binaria sobre el valor
/// de StartupApproved, no renombrando ni borrando la entrada.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupInventorySource(IRegistry registry) : IStartupInventorySource
{
    private const string ApprovalRun = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovalRun32 = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovalFolder = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private static readonly (StartupLocation Location, string Key, string ApprovalKey)[] RunLocations =
    [
        (StartupLocation.RunHkcu, @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ApprovalRun),
        (StartupLocation.RunHklm, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ApprovalRun),
        (StartupLocation.RunWow6432, @"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", ApprovalRun32)
    ];

    public async Task<(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<StartupEntry>();
        var warnings = new List<string>();

        // HKCU lo resuelve el propio proceso: si FragForge está elevado con otro administrador, esto NO es
        // el arranque del usuario que juega. Se dice explícitamente en vez de presentarlo como suyo.
        warnings.Add("Las entradas HKCU corresponden al usuario del proceso que ejecuta FragForge. Si lo ejecutas elevado con otra cuenta, no verás el arranque del usuario interactivo.");

        foreach (var (location, key, approvalKey) in RunLocations)
        {
            await ReadRunKeyAsync(location, key, approvalKey, entries, warnings, cancellationToken);
        }

        foreach (var (location, folder, approvalKey) in new[]
        {
            (StartupLocation.StartupFolderHkcu, Environment.GetFolderPath(Environment.SpecialFolder.Startup), ApprovalFolder),
            (StartupLocation.StartupFolderHklm, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), ApprovalFolder)
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(file);
                    var approval = await registry.ReadValueAsync(approvalKey, name, cancellationToken);
                    entries.Add(new StartupEntry
                    {
                        Name = name,
                        Location = location,
                        Command = file,
                        ApprovalKey = approvalKey,
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

        return (entries.OrderBy(e => e.Location).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }

    private async Task ReadRunKeyAsync(StartupLocation location, string key, string approvalKey, List<StartupEntry> entries, List<string> warnings, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names;
        try { names = await registry.GetValueNamesAsync(key, cancellationToken); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { warnings.Add($"{key}: {ex.Message}"); return; }

        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await registry.ReadValueAsync(key, name, cancellationToken);
            var approval = await registry.ReadValueAsync(approvalKey, name, cancellationToken);
            entries.Add(new StartupEntry
            {
                Name = name,
                Location = location,
                Command = value.Data as string,
                ApprovalKey = approvalKey,
                ApprovedEnabled = approval.ValueExists ? StartupApproval.IsEnabled(approval.Data as byte[] ?? []) : null,
                ApprovalFirstByte = approval.ValueExists && approval.Data is byte[] { Length: > 0 } bytes ? bytes[0] : null
            });
        }
    }
}

public sealed class UnsupportedStartupInventorySource : IStartupInventorySource
{
    public Task<(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(((IReadOnlyList<StartupEntry>)[], (IReadOnlyList<string>)["El inventario de arranque requiere Windows."]));
}
