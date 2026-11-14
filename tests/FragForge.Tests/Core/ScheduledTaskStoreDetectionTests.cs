using System.Runtime.Versioning;
using FragForge.Actions.Tasks;
using FragForge.Core.Abstractions;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Ronda 5b — la detección de "la tarea no existe" tiene que distinguirla de un fallo real.
///
/// Antes: <c>Get-ScheduledTask -ErrorAction SilentlyContinue</c> y <c>$null</c> ⇒
/// <c>{"exists":false}</c>. SilentlyContinue silencia TODOS los errores no terminantes, así que un
/// Programador de tareas caído, un problema de RPC o un permiso denegado podían devolver <c>$null</c>
/// con exit 0 y leerse como "no existe". Eso viola R-L.
///
/// Ahora la única señal de ausencia es <c>$_.CategoryInfo.Category -eq 'ObjectNotFound'</c>.
///
/// Todo con un <see cref="IProcessRunner"/> falso: sin PowerShell, sin Windows y sin Task Scheduler.
/// Esto prueba el CONTRATO (qué hace el código con cada salida), no el comportamiento real de
/// PowerShell. Ver "No verificado" en el informe.
///
/// <see cref="WindowsScheduledTaskStore"/> está marcada <c>[SupportedOSPlatform("windows")]</c>: sin
/// esta marca en el test, cada uso daría CA1416 en un proyecto <c>net10.0</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskStoreDetectionTests
{
    private const string TaskPath = @"\Microsoft\Windows\Application Experience\Appraiser";

    /// <summary>Devuelve una salida fija y no ejecuta nada.</summary>
    private sealed class FixedRunner(ProcessResult result) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private static WindowsScheduledTaskStore Store(ProcessResult result, out FixedRunner runner)
    {
        runner = new FixedRunner(result);
        return new WindowsScheduledTaskStore(runner);
    }

    // ---------------------------------------------------------------- 1. existe

    [Fact]
    public async Task TaskExists_ReturnsExistsTrue_WithEnabledAndXml()
    {
        var json = """{"exists":true,"enabled":false,"xml":"<Task/>"}""";
        var store = Store(new ProcessResult(0, json, "", false), out _);

        var state = await store.GetAsync(TaskPath);

        Assert.True(state.Exists, $"la tarea existe; Exists={state.Exists}, Path={state.Path}");
        Assert.True(!state.Enabled, "enabled=false en el JSON debe reflejarse en Enabled");
        Assert.Equal("<Task/>", state.XmlDefinition);
    }

    // ---------------------------------------------------------------- 2. no existe (la señal buena)

    [Fact]
    public async Task TaskNotFoundByCategory_ReturnsExistsFalse()
    {
        var store = Store(new ProcessResult(0, """{"exists":false}""", "", false), out _);

        var state = await store.GetAsync(TaskPath);

        Assert.True(
            !state.Exists,
            $"el script solo emite exists=false con Category=ObjectNotFound; Exists={state.Exists}");
    }

    // ---------------------------------------------------------------- 3. control negativo: fallo real

    [Fact]
    public async Task RealFailure_NonZeroExit_Throws_AndIsNeverReadAsMissing()
    {
        // El script nuevo, ante un error que NO es ObjectNotFound, escribe por stderr y sale con 2.
        var store = Store(new ProcessResult(2, "", "The RPC server is unavailable. | Category=ResourceUnavailable", false), out _);

        var exception = await Assert.ThrowsAsync<FragForge.Core.Models.ProcessFailedException>(() => store.GetAsync(TaskPath));

        Assert.True(
            exception.Message.Contains("RPC server is unavailable", StringComparison.Ordinal),
            $"el error real debe propagarse, no convertirse en 'no existe'. Mensaje: {exception.Message}");
        Assert.True(
            exception.Code == "process_failed",
            $"debe ser process_failed (error), no action_not_applicable; dio {exception.Code}");
    }

    [Fact]
    public async Task Timeout_Throws_AndIsNeverReadAsMissing()
    {
        var store = Store(new ProcessResult(0, "", "", TimedOut: true), out _);

        var exception = await Assert.ThrowsAsync<FragForge.Core.Models.ProcessFailedException>(() => store.GetAsync(TaskPath));

        Assert.True(
            exception.Message.Contains("timeout", StringComparison.Ordinal),
            $"un timeout no es 'no existe'; mensaje: {exception.Message}");
    }

    [Fact]
    public async Task ZeroResultsForcedExit3_Throws_AndIsNeverReadAsMissing()
    {
        // "Éxito con 0 resultados sin lanzar" se convierte en exit 3 dentro del script: debe llegar
        // como error, nunca como exists=false.
        var store = Store(new ProcessResult(3, "", "Get-ScheduledTask devolvio 0 resultados sin excepcion", false), out _);

        await Assert.ThrowsAsync<FragForge.Core.Models.ProcessFailedException>(() => store.GetAsync(TaskPath));
    }

    // ---------------------------------------------------------------- 4. el idioma no decide nada

    [Theory]
    [InlineData("No se encuentra la tarea solicitada. | Category=ObjectNotFound")]   // es-MX
    [InlineData("The specified task does not exist. | Category=ObjectNotFound")]     // en-US
    [InlineData("Der angegebene Task ist nicht vorhanden. | Category=ObjectNotFound")] // de-DE
    public void LocalizedMessages_DoNotChangeTheContract(string localizedText)
    {
        // En el script nuevo el TEXTO del mensaje no interviene: solo la categoría decide, y la
        // categoría no está localizada. El mensaje localizado, si aparece, va por stderr en el camino
        // de error. Este test fija que el script generado NO contiene comparaciones de texto.
        var script = WindowsScheduledTaskStore.BuildGetScript(
            @"\Microsoft\Windows\Application Experience\", "Appraiser");

        Assert.True(
            script.Contains("CategoryInfo.Category -eq 'ObjectNotFound'", StringComparison.Ordinal),
            "la decisión debe ser por categoría; script generado: " + script);
        Assert.True(
            !script.Contains("SilentlyContinue", StringComparison.Ordinal),
            "no debe volver a usarse SilentlyContinue (silencia fallos reales)");
        // El texto localizado nunca se usa como regla.
        Assert.True(
            !script.Contains(localizedText, StringComparison.Ordinal),
            $"el script no debe inspeccionar texto localizado ({localizedText.Length} chars)");
    }

    // ---------------------------------------------------------------- 5. forma del script

    [Fact]
    public void Script_IsSingleLine_AndUsesStop()
    {
        var script = WindowsScheduledTaskStore.BuildGetScript(@"\Microsoft\Windows\", "Appraiser");

        Assert.True(!script.Contains('\n') && !script.Contains('\r'), "el script debe ir en una sola línea para el paso de argumentos");
        Assert.True(script.Contains("-ErrorAction Stop", StringComparison.Ordinal), "debe usar -ErrorAction Stop dentro del try");
        Assert.True(script.Contains("exit 3", StringComparison.Ordinal), "0 resultados sin excepción ⇒ error (exit 3)");
        Assert.True(script.Contains("exit 2", StringComparison.Ordinal), "error real ⇒ exit 2");
        Assert.True(
            script.Contains(@"-TaskPath '\Microsoft\Windows\'", StringComparison.Ordinal),
            "la ruta debe ir entre comillas simples; script: " + script);
        Assert.True(script.Contains("-TaskName 'Appraiser'", StringComparison.Ordinal), "el nombre debe ir entre comillas simples");
    }

    [Fact]
    public void Script_EscapesSingleQuotesInTheName()
    {
        // Un nombre con comilla simple no puede cerrar la cadena de PowerShell e inyectar código.
        var script = WindowsScheduledTaskStore.BuildGetScript(@"\X\", "O'Brien");

        Assert.True(
            script.Contains("-TaskName 'O''Brien'", StringComparison.Ordinal),
            "la comilla simple debe doblarse; script: " + script);
    }

    [Theory]
    [InlineData("Bad\nName")]
    [InlineData("Bad\rName")]
    [InlineData("__TASKNAME__")]
    public void Script_RejectsControlCharactersAndTemplateMarkers(string hostile)
    {
        // Un salto de línea o el marcador de la plantilla dentro del nombre no puede cambiar la consulta.
        Assert.Throws<ArgumentException>(() => WindowsScheduledTaskStore.BuildGetScript(@"\X\", hostile));
    }

    // ---------------------------------------------------------------- 6. el motor lo traduce a NotApplicable

    [Fact]
    public async Task MissingTask_FlowsToNotApplicable_NotAnError()
    {
        var store = Store(new ProcessResult(0, """{"exists":false}""", "", false), out _);
        var action = new ScheduledTaskAction(store, FragForge.Core.Persistence.ProtectionPolicy.CreateDefault(), Spec.Action("ScheduledTask", new { path = TaskPath, enabled = false }));

        // OperationId y Hardware son 'required': el contexto no se puede construir a la ligera.
        var context = new FragForge.Core.Models.ActionExecutionContext
        {
            OperationId = "test-op",
            Hardware = TestProfiles.Pn64()
        };

        var exception = await Assert.ThrowsAsync<FragForge.Core.Models.ActionNotApplicableException>(
            () => action.CaptureAsync(context));

        Assert.True(
            exception.Code == "action_not_applicable",
            $"el motor distingue por este código; dio {exception.Code}");
        Assert.True(
            exception.Message.Contains("no existe en este equipo", StringComparison.Ordinal),
            $"el motivo debe decirlo; dio: {exception.Message}");
    }
}
