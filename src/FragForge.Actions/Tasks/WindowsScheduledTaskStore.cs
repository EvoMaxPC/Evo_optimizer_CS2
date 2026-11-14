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
    /// El script llega a powershell.exe como UN SOLO argumento de <c>-Command</c>, asi que no puede
    /// contener saltos de linea. Se construye como LISTA DE SENTENCIAS y se unen con un espacio, con
    /// <c>;</c> donde hace falta separar dos sentencias que PowerShell no separaria por si solo.
    ///
    /// Version anterior (ronda 5b): plantilla multilinea "aplanada" reemplazando los saltos por
    /// espacios. Eso producia <c>'Stop' [Console]...</c>, <c>) if (...)</c> y <c>} [pscustomobject]</c>,
    /// que NO son PowerShell valido: toda tarea habria fallado con ProcessFailedException. Los tests no
    /// lo detectaban porque usaban un runner falso. Ahora hay un test que PARSEA el script con el parser
    /// real de PowerShell.
    ///
    /// Reglas de separacion usadas:
    /// <list type="bullet">
    /// <item>Tras el <c>}</c> que CIERRA el <c>if</c> se emite <c>;</c> igualmente: no cuesta nada y
    /// hace explicito el limite. Entre <c>}</c> y <c>else</c>/<c>catch</c> NO va <c>;</c> (romperia
    /// la construccion).</item>
    /// <item>Tras el <c>)</c> de <c>@(...)</c> SI hace falta: <c>)</c> no cierra sentencia.</item>
    /// <item>Dentro de un bloque, <c>exit N</c> va precedido de <c>;</c> y seguido del <c>}</c>.</item>
    /// </list>
    /// </summary>
    public static string BuildGetScript(string folder, string name)
    {
        var taskPath = Literal(folder, nameof(folder));
        var taskName = Literal(name, nameof(name));

        var sentencias = new List<string>
        {
            // El modo de error se fija como sentencia propia: separado por ';' del resto.
            "$ErrorActionPreference='Stop';",
            // Idem: asignacion seguida de sentencia.
            "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new();",
            // Abre el try. La primera sentencia del bloque va tras '{'.
            "try {",
            // Termina en ';)': el ')' de @(...) NO cierra sentencia, hace falta el ';'.
            $"$t=@(Get-ScheduledTask -TaskPath '{taskPath}' -TaskName '{taskName}' -ErrorAction Stop);",
            // Bloque del caso "0 resultados". exit 3 va con ';' previo. El '}' que cierra el if
            // lleva ';' DETRAS a proposito: asi el limite entre el if y la sentencia siguiente es
            // explicito y no depende de que PowerShell acepte "} sentencia" sin separador.
            "if ($t.Count -eq 0) { [Console]::Error.WriteLine('Get-ScheduledTask devolvio 0 resultados sin excepcion; no se asume que la tarea no exista.'); exit 3 };",
            // Empieza limpia tras el ';' y acaba en pipeline.
            "[pscustomobject]@{exists=$true; enabled=[bool]$t[0].Settings.Enabled; xml=(Export-ScheduledTask -TaskPath $t[0].TaskPath -TaskName $t[0].TaskName)} | ConvertTo-Json -Compress -Depth 4",
            // '}' cierra el try; 'catch {' abre el handler. No hace falta ';' entre ellos.
            "} catch {",
            // if/else: el '}' del if va seguido de 'else' y el '{' del else ':' sin ';' de mas.
            "if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { '{\"exists\":false}' }",
            "else { [Console]::Error.WriteLine(($_.Exception.Message + ' | Category=' + $_.CategoryInfo.Category)); exit 2 }",
            // Cierra el catch.
            "}",
        };

        // Union con UN espacio. Cada sentencia trae su propio separador donde hace falta.
        return string.Join(' ', sentencias);
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
