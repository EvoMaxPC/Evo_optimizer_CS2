using System.Runtime.Versioning;
using FragForge.Actions.Platform;
using FragForge.Actions.Tasks;
using FragForge.Core.Abstractions;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Ronda 5c — el script tiene que ser PowerShell VÁLIDO de verdad.
///
/// La causa raíz de la ronda 5b fue no haber parseado nunca el script: una plantilla multilínea
/// "aplanada" reemplazando saltos de línea por espacios producía
/// <c>'Stop' [Console]...</c>, <c>) if (...)</c> y <c>} [pscustomobject]</c>, que no son PowerShell
/// válido. Con ese script, TODA tarea programada habría fallado con <c>ProcessFailedException</c>.
/// Los tests de la ronda 5b no lo detectaban porque usaban un <see cref="IProcessRunner"/> falso.
///
/// Aquí se usan el parser REAL de PowerShell y el ejecutable REAL. Son tests de Windows: fuera de
/// Windows no se ejecuta el cuerpo (el guard devuelve sin fallar). <c>[SupportedOSPlatform("windows")]</c>
/// evita CA1416 en el proyecto <c>net10.0</c>.
///
/// Nota: aquí SÍ hay dependencia de la máquina (powershell.exe), que es justo lo que se quiere medir.
/// Si el entorno no tuviera PowerShell, el test falla con el stderr completo en el mensaje.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskScriptParsesTests
{
    private const string TaskFolder = @"\Microsoft\Windows\Application Experience\";
    private const string TaskName = "Appraiser";

    /// <summary>
    /// <see cref="ProcessRequest"/> NO tiene propiedad de variables de entorno
    /// (src/FragForge.Core/Abstractions/IProcessRunner.cs:7-14: FileName, Arguments, WorkingDirectory,
    /// StandardInput, Timeout). Por eso el script se pasa por ARCHIVO TEMPORAL y se lee con
    /// <c>Get-Content -Raw</c>, como indicaste.
    /// </summary>
    private static readonly string ParserScript =
        "$e=$null;$t=$null;" +
        "[void][System.Management.Automation.Language.Parser]::ParseInput((Get-Content -Raw -LiteralPath $env:FF_SCRIPT_FILE),[ref]$t,[ref]$e);" +
        "if ($e.Count -gt 0) { $e | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }; exit 0";

    private static async Task<ProcessResult> RunPowerShellAsync(string command, TimeSpan? timeout = null)
    {
        var runner = new ProcessRunner();
        return await runner.RunAsync(new ProcessRequest
        {
            FileName = ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
            Timeout = timeout ?? TimeSpan.FromSeconds(90)
        });
    }

    /// <summary>
    /// Test de PARSEO REAL. Es el que habría atrapado el bug de la ronda 5b.
    /// </summary>
    [Fact]
    public async Task GeneratedScript_ParsesWithTheRealPowerShellParser()
    {
        if (!OperatingSystem.IsWindows()) return;

        var script = WindowsScheduledTaskStore.BuildGetScript(TaskFolder, TaskName);
        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, script);
            // El script del parser lee la ruta de una variable de entorno. Como ProcessRequest no tiene
            // Environment, la ruta se pasa como ARGUMENTO de -Command: se interpola en el comando.
            var command = ParserScript.Replace(
                "$env:FF_SCRIPT_FILE",
                "'" + temp.Replace("'", "''") + "'",
                StringComparison.Ordinal);

            var result = await RunPowerShellAsync(command);

            Assert.True(
                result.IsSuccess,
                "El script generado NO es PowerShell válido. " +
                $"ExitCode={result.ExitCode}, TimedOut={result.TimedOut}. " +
                $"stderr=[{result.StandardError.Trim()}]. " +
                $"stdout=[{result.StandardOutput.Trim()}]. " +
                $"SCRIPT=[{script}]");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* temporal: no debe tapar el assert */ }
        }
    }

    /// <summary>
    /// El script que realmente usa el store también tiene que parsear (no solo uno de ejemplo):
    /// se genera con la ruta del catálogo real y con una ruta hostil con comillas simples.
    /// </summary>
    [Theory]
    [InlineData(@"\Microsoft\Windows\Application Experience\", "Appraiser")]
    [InlineData(@"\X\", "O'Brien")]
    public async Task EveryGeneratedScript_Parses(string folder, string name)
    {
        if (!OperatingSystem.IsWindows()) return;

        var script = WindowsScheduledTaskStore.BuildGetScript(folder, name);
        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, script);
            var command = ParserScript.Replace(
                "$env:FF_SCRIPT_FILE",
                "'" + temp.Replace("'", "''") + "'",
                StringComparison.Ordinal);

            var result = await RunPowerShellAsync(command);

            Assert.True(
                result.IsSuccess,
                $"El script no parsea para folder='{folder}', name='{name}'. " +
                $"stderr=[{result.StandardError.Trim()}]. SCRIPT=[{script}]");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- contrato portable (sin PowerShell)

    [Fact]
    public void Script_HasNoLineBreaksAndKeepsTheContract()
    {
        var script = WindowsScheduledTaskStore.BuildGetScript(TaskFolder, TaskName);

        Assert.True(!script.Contains('\n'), "el script no puede contener \\n: va como un solo argumento de -Command. SCRIPT=[" + script + "]");
        Assert.True(!script.Contains('\r'), "el script no puede contener \\r. SCRIPT=[" + script + "]");
        Assert.True(script.Contains("-ErrorAction Stop", StringComparison.Ordinal), "debe usar -ErrorAction Stop. SCRIPT=[" + script + "]");
        Assert.True(!script.Contains("SilentlyContinue", StringComparison.Ordinal), "no debe volver SilentlyContinue: silencia fallos reales. SCRIPT=[" + script + "]");
        Assert.True(script.Contains("ObjectNotFound", StringComparison.Ordinal), "la decisión debe ser por categoría ObjectNotFound. SCRIPT=[" + script + "]");
    }

    /// <summary>
    /// Los separadores que faltaban en la ronda 5b. Este test los fija explícitamente: son los
    /// vecinos exactos que producía el aplanado roto.
    /// </summary>
    [Fact]
    public void Script_HasExplicitStatementSeparators()
    {
        var script = WindowsScheduledTaskStore.BuildGetScript(TaskFolder, TaskName);

        // 'Stop' seguido de la sentencia siguiente sin ';' era el bug.
        Assert.True(
            script.Contains("'Stop';", StringComparison.Ordinal),
            "después de la asignación del modo de error debe ir ';'. SCRIPT=[" + script + "]");
        // El ')' de @(...) no cierra sentencia.
        Assert.True(
            script.Contains("-ErrorAction Stop);", StringComparison.Ordinal),
            "después del ')' de @(...) debe ir ';'. SCRIPT=[" + script + "]");
        // El '}' que cierra el if no basta: debe seguir ';'.
        Assert.True(
            script.Contains("exit 3 };", StringComparison.Ordinal),
            "después del '}' que cierra el if debe ir ';'. SCRIPT=[" + script + "]");
        // Entre '}' y 'catch' NO va ';' (rompería la construcción).
        Assert.True(
            script.Contains("} catch {", StringComparison.Ordinal),
            "entre '}' y 'catch' no debe haber ';'. SCRIPT=[" + script + "]");
        // Y el caso concreto que fallaba: esta secuencia ya no puede aparecer.
        Assert.True(
            !script.Contains("'Stop' [Console]", StringComparison.Ordinal),
            "volvió el bug del aplanado: 'Stop' y la sentencia siguiente sin ';'. SCRIPT=[" + script + "]");
    }
}

/// <summary>
/// Ronda 5c — ejecución REAL, solo lectura, solo Windows.
///
/// Consulta una tarea que no existe con el <see cref="WindowsScheduledTaskStore"/> real (proceso
/// powershell.exe real) y exige <c>Exists == false</c> sin excepción. Es lo que valida de punta a
/// punta la regla de la ronda 5b: la categoría <c>ObjectNotFound</c> produce "no existe" y no un error.
///
/// Medido por el usuario en la PC objetivo: <c>Get-ScheduledTask -ErrorAction Stop</c> sobre una tarea
/// inexistente lanza CimJobException con <c>CategoryInfo.Category = ObjectNotFound</c>
/// (<c>FullyQualifiedErrorId = CmdletizationQuery_NotFound,Get-ScheduledTask</c>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskStoreRealExecutionTests
{
    private const string MissingTask = @"\FragForgeNoExiste\Tarea";

    [Fact]
    public async Task MissingTask_RealPowerShell_ReturnsExistsFalse_WithoutThrowing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var store = new WindowsScheduledTaskStore(new FragForge.Actions.Platform.ProcessRunner());

        FragForge.Core.Models.ScheduledTaskActionState? state = null;
        Exception? failure = null;
        try
        {
            state = await store.GetAsync(MissingTask);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Si falla, se quiere ver TODO: el tipo de excepción y su mensaje (que trae el stderr).
        Assert.True(
            failure is null,
            $"Consultar una tarea inexistente ('{MissingTask}') no debe lanzar: debe devolver Exists=false. " +
            $"Excepción {failure?.GetType().Name}: {failure?.Message}");
        Assert.True(
            state is not null && !state.Exists,
            $"la tarea '{MissingTask}' no existe en este equipo, así que Exists debe ser false. " +
            $"Exists={state?.Exists.ToString() ?? "null"}, Path={state?.Path}, Enabled={state?.Enabled}");
    }
}
