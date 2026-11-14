using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Inventory;
using FragForge.Core.Models;

namespace FragForge.Actions.Inventory;

/// <summary>
/// Inventario de tareas programadas con un script FIJO propio (no parametrizable desde el catálogo),
/// ejecutado por <see cref="IProcessRunner"/> y autorizado por <see cref="IProtectionPolicy.EnsurePowerShellAllowed"/>.
///
/// Solo lectura: <c>Get-ScheduledTask</c> / <c>Get-ScheduledTaskInfo</c>. La app nunca registra,
/// modifica ni borra una tarea desde este código.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskInventorySource(IProcessRunner runner, IProtectionPolicy policy) : ITaskInventorySource
{
    private const string Script = """
        $ErrorActionPreference='Stop'
        [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new()
        $items = Get-ScheduledTask | ForEach-Object {
            $info = $null
            try { $info = $_ | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue } catch { }
            [pscustomobject]@{
                path = $_.TaskPath
                name = $_.TaskName
                state = [string]$_.State
                enabled = [bool]$_.Settings.Enabled
                author = [string]$_.Author
                lastResult = if ($null -ne $info) { [string]$info.LastTaskResult } else { $null }
            }
        }
        $items | ConvertTo-Json -Compress -Depth 4
        """;

    public async Task<(IReadOnlyList<TaskEntry> Tasks, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        try { policy.EnsurePowerShellAllowed(Script); }
        catch (ProtectedTargetException ex)
        {
            warnings.Add("El script de inventario fue rechazado por la política: " + ex.Message);
            return ([], warnings);
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(new ProcessRequest
            {
                FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", Script],
                Timeout = TimeSpan.FromMinutes(2)
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add("Get-ScheduledTask: " + ex.Message);
            return ([], warnings);
        }

        if (!result.IsSuccess)
        {
            warnings.Add($"Get-ScheduledTask falló (exit {result.ExitCode}{(result.TimedOut ? ", timeout" : "")}): {Trim(result.StandardError)}");
            return ([], warnings);
        }

        var tasks = new List<TaskEntry>();
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
            var root = document.RootElement;
            // ConvertTo-Json devuelve un objeto, no un array, cuando solo hay un elemento.
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
            foreach (var item in items)
            {
                var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "\\" : "\\";
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                tasks.Add(new TaskEntry
                {
                    Path = (path.TrimEnd('\\') + "\\" + name),
                    Folder = path,
                    State = item.TryGetProperty("state", out var s) ? s.GetString() ?? "Unknown" : "Unknown",
                    Enabled = item.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True,
                    Author = item.TryGetProperty("author", out var a) ? a.GetString() : null,
                    LastResult = item.TryGetProperty("lastResult", out var l) ? l.GetString() : null
                });
            }
        }
        catch (JsonException ex)
        {
            warnings.Add("JSON de Get-ScheduledTask ilegible: " + ex.Message);
            return ([], warnings);
        }

        return (tasks.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }

    private static string Trim(string value) => value.Length <= 200 ? value.Trim() : value[..200].Trim();
}

/// <summary>Resultado de una tarea que no se puede leer: se reporta como vacío con motivo, nunca como "todo bien".</summary>
public sealed class UnsupportedTaskInventorySource : ITaskInventorySource
{
    public Task<(IReadOnlyList<TaskEntry> Tasks, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(((IReadOnlyList<TaskEntry>)[], (IReadOnlyList<string>)["El inventario de tareas programadas requiere Windows."]));
}
