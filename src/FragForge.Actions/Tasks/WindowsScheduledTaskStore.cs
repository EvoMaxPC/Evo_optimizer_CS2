using System.Runtime.Versioning;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Tasks;

[SupportedOSPlatform("windows")]
public sealed class WindowsScheduledTaskStore(IProcessRunner runner) : IScheduledTaskStore
{
    public async Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var (folder, name) = Split(path);
        var script = BuildGetScript(folder, name);
        var result = await RunAsync(script, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var root = document.RootElement;
        return new ScheduledTaskActionState { Path = path, Exists = root.GetProperty("exists").GetBoolean(), Enabled = root.TryGetProperty("enabled", out var e) && e.GetBoolean(), XmlDefinition = root.TryGetProperty("xml", out var x) ? x.GetString() : null };
    }
    /// <summary>
    /// Script de consulta. Contrato de salida, que es lo que hace fiable la deteccion:
    ///
    /// 1. La tarea existe  -> JSON con <c>exists=true</c>.
    /// 2. La tarea NO existe -> JSON con <c>exists=false</c>. Se decide SOLO por
    ///    <c>$_.CategoryInfo.Category -eq 'ObjectNotFound'</c>: la categoria de la excepcion, un valor
    ///    de enumeracion estable de PowerShell. NO se usa el texto del mensaje (esta localizado), ni el
    ///    <c>FullyQualifiedErrorId</c> (cambia entre versiones), ni <c>Contains(...)</c>.
    /// 3. Cualquier otro fallo (Programador de tareas caido, RPC, permisos) NO emite <c>exists</c>:
    ///    el script escribe el error por stderr y sale con codigo distinto de 0, y <see cref="RunAsync"/>
    ///    lanza <see cref="ProcessFailedException"/>. Sigue siendo un error real: R-L, "no se" nunca se
    ///    convierte en "no existe".
    ///
    /// Por que NO se usa <c>-ErrorAction SilentlyContinue</c> (como antes): silencia TODOS los errores
    /// no terminantes, asi que un fallo real devolvia <c>$null</c> con exit 0 y se leia como
    /// "no existe". Con <c>-ErrorAction Stop</c> dentro de <c>try/catch</c> solo un <c>ObjectNotFound</c>
    /// produce <c>exists=false</c>.
    ///
    /// Caso "exito con 0 resultados sin lanzar": se trata como ERROR a proposito (<c>exit 3</c>). Si
    /// <c>Get-ScheduledTask</c> devolviera una coleccion vacia en vez de lanzar, no habria forma de
    /// distinguir "la tarea no existe" de "el proveedor no respondio"; se prefiere el error antes que
    /// afirmar que no existe.
    ///
    /// El script se aplana a UNA linea (los saltos pasan a espacios) para que el paso de argumentos a
    /// powershell.exe no dependa del entrecomillado de saltos de linea.
    /// </summary>
    public static string BuildGetScript(string folder, string name)
    {
        const string plantilla = """
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new()
try {
$t=@(Get-ScheduledTask -TaskPath '__TASKPATH__' -TaskName '__TASKNAME__' -ErrorAction Stop)
if ($t.Count -eq 0) { [Console]::Error.WriteLine('Get-ScheduledTask devolvio 0 resultados sin excepcion; no se asume que la tarea no exista.'); exit 3 }
[pscustomobject]@{exists=$true; enabled=[bool]$t[0].Settings.Enabled; xml=(Export-ScheduledTask -TaskPath $t[0].TaskPath -TaskName $t[0].TaskName)} | ConvertTo-Json -Compress -Depth 4
} catch {
if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { '{"exists":false}' }
else { [Console]::Error.WriteLine(($_.Exception.Message + ' | Category=' + $_.CategoryInfo.Category)); exit 2 }
}
""";
        // 1) Aplanar PRIMERO. Si se aplanara despues de sustituir, un salto de linea dentro del
        //    nombre de la tarea se convertiria en un espacio y consultariamos OTRA tarea en silencio.
        var plano = plantilla.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        // 2) Sustituir. Literal() dobla las comillas simples, asi que el valor no puede cerrar la
        //    cadena de PowerShell. Se rechazan los caracteres de control en vez de tragarlos.
        return plano
            .Replace("__TASKPATH__", Literal(folder, nameof(folder)), StringComparison.Ordinal)
            .Replace("__TASKNAME__", Literal(name, nameof(name)), StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// Valor listo para ir entre comillas simples de PowerShell: dobla las comillas simples (unico
    /// escape posible dentro de una cadena literal) y RECHAZA caracteres de control y los marcadores de
    /// la plantilla. Un nombre de tarea con un salto de linea o con <c>__TASKNAME__</c> dentro no puede
    /// producir una consulta distinta de la pedida.
    /// </summary>
    private static string Literal(string value, string parameter)
    {
        if (value.Any(char.IsControl)) throw new ArgumentException($"The scheduled task {parameter} contains control characters.", parameter);
        if (value.Contains("__TASK", StringComparison.Ordinal)) throw new ArgumentException($"The scheduled task {parameter} contains a reserved marker.", parameter);
        return value.Replace("'", "''");
    }

    public async Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
    {
        var (folder, name) = Split(path);
        var cmdlet = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        await RunAsync($"$ErrorActionPreference='Stop'; {cmdlet} -TaskPath '{Escape(folder)}' -TaskName '{Escape(name)}' -Confirm:$false | Out-Null", cancellationToken);
    }
    private async Task<ProcessResult> RunAsync(string script, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
    private static (string Folder, string Name) Split(string path)
    {
        var normalized = path.Replace('/', '\\');
        var index = normalized.LastIndexOf('\\');
        if (index <= 0 || index == normalized.Length - 1) throw new ArgumentException("Invalid scheduled task path.");
        return (normalized[..(index + 1)], normalized[(index + 1)..]);
    }
    private static string Escape(string value) => value.Replace("'", "''");
}
