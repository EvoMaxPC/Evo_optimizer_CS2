using System.Text.Json;
using FragForge.Actions.Tasks;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Ronda 5 — un objeto del catálogo que NO existe en el equipo es "no aplicable", no un error.
///
/// Bug real medido en la PC objetivo: el dry-run se cortaba con
/// <c>La tarea \Microsoft\Windows\Application Experience\Appraiser no existe.</c> porque Atlas Playbook
/// había eliminado esa tarea. El <c>ScanAsync</c> pasaba (ahí la excepción sí se atrapaba) pero el
/// <c>ApplyAsync</c> la dejaba escapar hasta el catch general y tumbaba el lote entero.
///
/// Todo se prueba con un controlador FALSO: sin hardware, sin Task Scheduler y sin Windows.
/// </summary>
public sealed class MissingObjectTests
{
    private const string ExistingTask = @"\Microsoft\Windows\Application Experience\Appraiser";
    private const string VanishedTask = @"\Microsoft\Windows\Application Experience\Vanished";

    // ---------------------------------------------------------------- dobles de IScheduledTaskStore

    /// <summary>Devuelve las tareas que existen y no hace nada más. Registra las escrituras.</summary>
    private sealed class StubTaskStore(bool failWithProcessError = false, string errorText = "") : IScheduledTaskStore
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Path, bool Enabled)> Writes { get; } = [];
        public int Reads { get; private set; }

        public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (failWithProcessError)
            {
                // Fallo REAL del sistema (permisos, servicio caído, timeout): NO es "no existe".
                throw new ProcessFailedException("powershell.exe", 1, errorText);
            }
            return Task.FromResult(Existing.Contains(path)
                ? new ScheduledTaskActionState { Path = path, Exists = true, Enabled = true }
                : new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false });
        }

        public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
        {
            Writes.Add((path, enabled));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Peor caso: una implementación que decide "no existe" mirando el TEXTO del error, como haría un
    /// atajo tentador. Si la lógica del motor dependiera del mensaje, este doble se comportaría distinto
    /// según el idioma. Con el arreglo, al motor le da igual: la decisión es del store, por dato.
    /// </summary>
    private sealed class TextSniffingTaskStore(string localizedText) : IScheduledTaskStore
    {
        public List<(string Path, bool Enabled)> Writes { get; } = [];

        public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default)
            => throw new ProcessFailedException("powershell.exe", 1, localizedText);

        public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
        {
            Writes.Add((path, enabled));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// La tarea EXISTE en las primeras <c>existingReads</c> lecturas y desaparece después. Modela la
    /// carrera real: el usuario borra la tarea entre la sonda del plan y la captura del snapshot.
    /// </summary>
    private sealed class VanishingTaskStore(int existingReads) : IScheduledTaskStore
    {
        public int Reads { get; private set; }
        public List<(string Path, bool Enabled)> Writes { get; } = [];

        public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Reads++;
            var exists = Reads <= existingReads;
            return Task.FromResult(new ScheduledTaskActionState { Path = path, Exists = exists, Enabled = exists });
        }

        public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
        {
            Writes.Add((path, enabled));
            return Task.CompletedTask;
        }
    }

    private sealed class StoreBackedFactory(IScheduledTaskStore store, IProtectionPolicy policy) : ITweakActionFactory
    {
        public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ScheduledTask" };

        public ITweakAction Create(ActionSpec specification) => specification.Kind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase)
            ? new ScheduledTaskAction(store, policy, specification)
            : throw new InvalidOperationException("Este doble solo crea ScheduledTask (test).");
    }

    private static ActionSpec TaskAction(string path, bool enabled = false) => new()
    {
        Kind = "ScheduledTask",
        Parameters = JsonSerializer.SerializeToElement(new { path, enabled }, JsonDefaults.Options)
    };

    private static TweakDefinition TaskTweak(string id, string path, bool enabled = false) => new()
    {
        Id = id,
        Category = "Debloat",
        Title = new LocalizedText { EsMx = "Tarea " + id, EnUs = "Task " + id },
        Description = new LocalizedText { EsMx = "Descripción", EnUs = "Description" },
        Evidence = EvidenceLevel.Low,
        Risk = RiskLevel.Safe,
        RequiresReboot = false,
        RequiresAdmin = true,
        Conditions = [],
        Actions = [TaskAction(path, enabled)],
        Tags = ["tarea"],
        ConflictsWith = [],
        DependsOn = [],
        ExpectedImpact = new ExpectedImpact(),
        References = ["https://example.invalid/test"],
        Notes = new LocalizedText { EsMx = "Notas", EnUs = "Notes" },
        WhenNotToApply = new LocalizedText { EsMx = "No aplicar", EnUs = "Do not apply" }
    };

    private static EngineTestHarness Harness(params TweakDefinition[] tweaks) => new EngineTestHarness().WithCatalog(tweaks);

    // ---------------------------------------------------------------- 1. scan

    [Fact]
    public async Task Scan_MissingTask_IsNotApplicable_NotUnknownAndNotAnError()
    {
        var store = new StubTaskStore();
        using var harness = Harness(TaskTweak("t.task", VanishedTask));
        var engine = harness.CreateEngine(factory: new StoreBackedFactory(store, harness.Policy));

        var scan = await engine.ScanAsync();

        var status = Assert.Single(scan.Tweaks);
        Assert.True(
            status.State == TweakState.NotApplicable,
            $"con la tarea inexistente el estado debe ser NotApplicable, dio {status.State}; motivo: {status.Reason}");
        Assert.True(
            status.Reason.Contains("no existe", StringComparison.OrdinalIgnoreCase),
            $"el motivo debe decir que no existe; dio: {status.Reason}");
        Assert.True(
            status.State != TweakState.Unknown,
            "inexistente es un HECHO, no una incógnita: no debe ser Unknown");
    }

    // ---------------------------------------------------------------- 2. dry-run

    [Fact]
    public async Task DryRun_MissingTask_IsSuccess_WithoutErrors_AndReportsNotApplicable()
    {
        var store = new StubTaskStore();
        using var harness = Harness(TaskTweak("t.task", VanishedTask));
        var engine = harness.CreateEngine(factory: new StoreBackedFactory(store, harness.Policy));

        var report = await engine.ApplyAsync(["t.task"], harness.Options(dryRun: true));

        Assert.True(
            report.Success,
            $"un dry-run con un objeto inexistente NO debe fallar; Success={report.Success}, Errors=[{string.Join(" | ", report.Errors)}]");
        Assert.True(
            report.DryRun,
            $"el reporte debe reflejar el dry-run pedido aunque el lote incluya un tweak omitido; DryRun={report.DryRun}");
        Assert.True(
            report.Errors.Count == 0,
            $"no debe haber errores; Errors=[{string.Join(" | ", report.Errors)}]");
        var result = Assert.Single(report.Results);
        Assert.True(
            result.State == TweakState.NotApplicable,
            $"el tweak debe quedar NotApplicable, dio {result.State}; mensaje: {result.Message}");
        Assert.True(
            store.Writes.Count == 0,
            $"un dry-run no escribe nada; escrituras: {string.Join(", ", store.Writes.Select(w => w.Path))}");
    }

    // ---------------------------------------------------------------- 3. apply real

    [Fact]
    public async Task Apply_MissingTask_IsNotAnError_AndWritesNothing()
    {
        var store = new StubTaskStore();
        using var harness = Harness(TaskTweak("t.task", VanishedTask));
        var engine = harness.CreateEngine(factory: new StoreBackedFactory(store, harness.Policy));

        var report = await engine.ApplyAsync(["t.task"], harness.Options());

        Assert.True(
            report.Success,
            $"aplicar un tweak cuyo objeto no existe NO debe fallar; Success={report.Success}, Errors=[{string.Join(" | ", report.Errors)}]");
        Assert.True(
            report.Errors.Count == 0,
            $"no debe haber errores; Errors=[{string.Join(" | ", report.Errors)}]");
        var result = Assert.Single(report.Results);
        Assert.True(
            result.State == TweakState.NotApplicable,
            $"debe reportarse NotApplicable, dio {result.State}; mensaje: {result.Message}");
        Assert.True(
            result.State != TweakState.Applied && result.State != TweakState.AppliedExternally,
            "NUNCA 'aplicado' para algo que no existe: sería inventar");
        Assert.True(
            store.Writes.Count == 0,
            $"no se debe escribir nada sobre una tarea que no existe; escrituras: {string.Join(", ", store.Writes.Select(w => w.Path))}");
    }

    // ---------------------------------------------------------------- 4. control negativo: fallo real

    [Fact]
    public async Task Apply_RealFailure_IsStillAnError_NotSwallowedAsMissing()
    {
        // Acceso denegado / Task Scheduler caído: NO es "no existe". Debe seguir rompiendo.
        var store = new StubTaskStore(failWithProcessError: true, errorText: "Access is denied. (0x80070005)");
        using var harness = Harness(TaskTweak("t.task", VanishedTask));
        var engine = harness.CreateEngine(factory: new StoreBackedFactory(store, harness.Policy));

        var report = await engine.ApplyAsync(["t.task"], harness.Options());

        Assert.True(
            !report.Success,
            "un fallo real del sistema debe seguir siendo error; el motor lo tragó y devolvió Success=true");
        Assert.True(
            report.Errors.Count > 0,
            "un fallo real debe aparecer en Errors; quedó vacío");
        Assert.True(
            report.Errors.Any(e => e.Contains("0x80070005", StringComparison.Ordinal) || e.Contains("denied", StringComparison.OrdinalIgnoreCase)),
            $"el error debe conservar la causa real; Errors=[{string.Join(" | ", report.Errors)}]");
        Assert.True(
            store.Writes.Count == 0,
            $"no se escribe si la lectura falló; escrituras: {string.Join(", ", store.Writes.Select(w => w.Path))}");
    }

    [Fact]
    public async Task Scan_RealFailure_IsUnknown_NotNotApplicable()
    {
        var store = new StubTaskStore(failWithProcessError: true, errorText: "Access is denied. (0x80070005)");
        using var harness = Harness(TaskTweak("t.task", ExistingTask));
        var engine = harness.CreateEngine(factory: new StoreBackedFactory(store, harness.Policy));

        var scan = await engine.ScanAsync();

        var status = Assert.Single(scan.Tweaks);
        Assert.True(
            status.State == TweakState.Unknown,
            $"un fallo real al leer no es 'no aplicable'; dio {status.State}; motivo: {status.Reason}");
    }

    // ---------------------------------------------------------------- 5. el idioma no decide nada

    [Theory]
    [InlineData("No se encuentra la tarea solicitada.")]              // es-MX
    [InlineData("The specified task does not exist.")]                 // en-US
    [InlineData("Der angegebene Task ist nicht vorhanden.")]           // de-DE
    public async Task ErrorText_DoesNotDecideTheLogic_SameOutcomeInEveryLanguage(string localizedText)
    {
        var store = new TextSniffingTaskStore(localizedText);
        using var harness = Harness(TaskTweak("t.task", VanishedTask));
        var engine = harness.CreateEngine(factory: new StoreBackedFactory(store, harness.Policy));

        var report = await engine.ApplyAsync(["t.task"], harness.Options());

        // Un fallo real es un fallo real en cualquier idioma: si la lógica mirara el texto, el alemán
        // (o el es-MX) cambiaría el resultado. Debe ser error SIEMPRE, con el mismo criterio.
        Assert.True(
            !report.Success,
            $"un fallo real debe ser error sin importar el idioma del mensaje; Success={report.Success}, " +
            $"Errors=[{string.Join(" | ", report.Errors)}]");
        Assert.True(
            report.Errors.Count > 0 && report.Errors.Any(e => e.Contains(localizedText, StringComparison.Ordinal)),
            $"el error debe propagar el mensaje original del sistema; Errors=[{string.Join(" | ", report.Errors)}]");
        Assert.True(
            store.Writes.Count == 0,
            "no se escribe nada cuando la lectura falla");
    }

    /// <summary>
    /// La detección de "no existe" es un DATO del store, no una interpretación del mensaje: el mismo
    /// <see cref="ScheduledTaskActionState"/> con <c>Exists=false</c> produce NotApplicable, y ningún
    /// texto se inspecciona. Se fija aquí para que no se "arregle" metiendo comparaciones de strings.
    /// </summary>
    [Fact]
    public void MissingIsDecidedByTheStateFlag_NotByAnyMessage()
    {
        var state = new ScheduledTaskActionState { Path = VanishedTask, Exists = false };
        Assert.False(state.Exists, "el doble debe modelar Exists=false");

        // El texto del motivo lo pone el motor/acción; si alguien lo localizara, la decisión seguiría
        // siendo Exists. Este test existe para documentar de dónde sale la decisión.
        FragForgeException exception = new ActionNotApplicableException("texto cualquiera, incluso traducido");
        Assert.True(
            exception.Code == "action_not_applicable",
            $"ActionNotApplicableException debe conservar su código estable (con el que el motor decide); dio {exception.Code}");
    }

    // ---------------------------------------------------------------- 7. desaparece entre sonda y captura

    [Fact]
    public async Task Apply_TaskThatVanishesBetweenProbeAndCapture_IsNotApplicable_AndIsNotWritten()
    {
        // Lecturas en apply real de ScheduledTaskAction: 1 = CaptureAsync de VerifyPlanAsync,
        // 2 = GetAsync de su VerifyAsync, 3 = CaptureAsync del snapshot. Con existingReads=2 la tarea
        // existe en las dos primeras y ya no está al capturar el snapshot.
        var store = new VanishingTaskStore(existingReads: 2);
        using var harness = Harness(TaskTweak("t.task", ExistingTask));
        var engine = harness.CreateEngine(factory: new StoreBackedFactory(store, harness.Policy));

        var report = await engine.ApplyAsync(["t.task"], harness.Options());

        Assert.True(
            report.Success,
            $"que el objetivo desaparezca a mitad de la operación no debe romper el lote; " +
            $"Success={report.Success}, Errors=[{string.Join(" | ", report.Errors)}]");
        Assert.True(
            report.Errors.Count == 0,
            $"no debe haber errores; Errors=[{string.Join(" | ", report.Errors)}]");
        var result = Assert.Single(report.Results);
        Assert.True(
            result.State == TweakState.NotApplicable,
            $"debe reportarse NotApplicable, dio {result.State}; mensaje: {result.Message}");
        Assert.True(
            store.Writes.Count == 0,
            $"no se escribe sobre una tarea que ya no existe; escrituras: {string.Join(", ", store.Writes.Select(w => w.Path))}");
        Assert.True(
            store.Reads >= 3,
            $"la carrera debe haberse producido (3 lecturas: verify-capture, verify, captura); hubo {store.Reads}");
    }

    // ---------------------------------------------------------------- 6. DryRun honesto al fallar

    [Fact]
    public async Task FailedDryRun_StillReportsDryRunTrue()
    {
        // Un dry-run que falla de verdad: el reporte del catch general debe decir DryRun=true y
        // Success=false. Antes decía DryRun=false y parecía que el dry-run se había ignorado.
        var store = new StubTaskStore(failWithProcessError: true, errorText: "The RPC server is unavailable. (0x800706BA)");
        using var harness = Harness(TaskTweak("t.task", VanishedTask));
        var engine = harness.CreateEngine(factory: new StoreBackedFactory(store, harness.Policy));

        var report = await engine.ApplyAsync(["t.task"], harness.Options(dryRun: true));

        Assert.True(
            !report.Success,
            "el fallo real debe seguir siendo fallo en dry-run");
        Assert.True(
            report.DryRun,
            $"el reporte del camino de excepción debe reflejar options.DryRun=true; DryRun={report.DryRun}, " +
            $"Errors=[{string.Join(" | ", report.Errors)}]");
        Assert.Null(report.SnapshotId);
    }
}
