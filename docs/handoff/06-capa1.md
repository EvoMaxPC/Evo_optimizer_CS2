## 6. Contrato para la Capa 1 (medición A/B y diagnóstico extendido)

La Capa 1 **no necesita cambiar el motor**: los cuatro puntos de extensión ya existen y están tipados.

### 6.1 Ganchos de experimento (`IExperimentHook`)

```csharp
public interface IExperimentHook
{
    int Order { get; }
    Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default);
}
public sealed record ExperimentContext(string OperationId, string? SnapshotId, ExperimentPhase Phase, TweakDefinition Tweak, HardwareProfile Hardware, DateTimeOffset Timestamp, string? ArtifactDirectory = null);
public enum ExperimentPhase { BeforeApply, AfterApply, AfterRevert }
```

Semántica garantizada por `TweakEngine`:

| Fase | Cuándo | Fallo |
|---|---|---|
| `BeforeApply` | Después del snapshot, **antes** de aplicar cualquier acción | **Fatal**: la aplicación se aborta y no se toca el sistema |
| `AfterApply` | Tras `Verify` correcto de todas las acciones del tweak | **Advertencia** en `TweakApplyResult.Warnings`; no revierte |
| `AfterRevert` | Tras `VerifyRevert` correcto | Advertencia |

Cada hook recibe `ArtifactDirectory` (dentro del snapshot) y devuelve `ExperimentArtifact(Name, Path, ContentType)`; el motor escribe `HookCompleted` en el journal con la cuenta de artefactos.

**Cómo enchufar PresentMon en Capa 1** (sin recompilar Core):

```csharp
public sealed class PresentMonBaselineHook(IProcessRunner runner) : IExperimentHook
{
    public int Order => 10;
    public async Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext ctx, CancellationToken ct)
    {
        var dir = ctx.ArtifactDirectory ?? throw new InvalidOperationException("sin directorio de artefactos");
        var csv = Path.Combine(dir, $"presentmon-{ctx.Phase}-{ctx.Tweak.Id}.csv");
        await runner.RunAsync(new ProcessRequest
        {
            FileName = "PresentMon.exe",
            Arguments = ["--process_name", "cs2.exe", "--output_file", csv, "--terminate_on_proc_exit", "--stop_existing_session"],
            Timeout = TimeSpan.FromMinutes(5)
        }, ct);
        return [new ExperimentArtifact("presentmon", csv, "text/csv")];
    }
}
// registro:
services.AddSingleton<IExperimentHook, PresentMonBaselineHook>();
```

> Regla de diseño para Capa 1: los hooks **no** pueden modificar el sistema. Si un hook necesita cambiar algo (p. ej. fijar afinidad para medir), debe ser un `ITweakAction` con captura/reversión, no un hook.

### 6.2 Línea base de diagnóstico ya implementada (`InspectOnly`)

El tweak `diagnostics.system-inspect` es **solo lectura** y alimenta el informe con `DiagnosticObservation(Key, Value, Source, Confidence, Note)`:

| Clave | Fuente | Nota de honestidad |
|---|---|---|
| `hags` | `HwSchMode` | Ausente ⇒ "comportamiento predeterminado del driver", no "desactivado" |
| `mpo` | `OverlayTestMode` | Solo `5` se interpreta como override conocido |
| `vbs` / `hvci` | DeviceGuard WMI + registro | `Unknown` si no hay evidencia |
| `secureBoot` | Registro + `Confirm-SecureBootUEFI` | `Unknown` si falla la consulta |
| `dualChannel` | `Win32_PhysicalMemory` | **Nunca** infiere dual desde 2 módulos |
| `powerLimits` | PL1/PL2 | `Unknown` si DTT/IPF gobierna (caso PN64) |
| `ssdHealth` | `MSFT_PhysicalDisk` + SMART | Salud básica, no sustituye análisis completo |

Para Capa 1 se añaden probes nuevos **sin tocar el motor**: implementan `IDiagnosticProbe` (`string Key`, `Task<DiagnosticObservation> ReadAsync(HardwareProfile, CancellationToken)`) y se registran en `DiagnosticProbeFactory`/DI. Candidatos: `dpcIsr` (ETW/xperf), `temperature` (LibreHardwareMonitor opcional, nunca driver propio), `frametime` (PresentMon), `powerDraw` (MSR solo si se decide explícitamente y con advertencia).

### 6.3 Informe listo para Capa 1 (`export-report`)

`ReportDocument` (JSON schema version 1) ya contiene todo lo que un motor de A/B necesita como contexto:

```csharp
public sealed record ReportDocument(
    int SchemaVersion, DateTimeOffset CreatedAt, string ApplicationVersion,
    HardwareProfile Hardware, IReadOnlyList<TweakStatus> Tweaks, PreflightReport Preflight,
    IReadOnlyList<SnapshotListItem> Snapshots, JournalIntegrity JournalIntegrity,
    IReadOnlyList<JournalEntry> RecentJournal);
```

`export-report --out x.html|json` produce ambos formatos. Los `TweakStatus` incluyen `Observations` (de `InspectOnly`), de modo que un experimento A/B puede correlacionar "antes/después" con el estado real de HAGS/VBS/plan de energía.

### 6.4 Datos que la Capa 1 debe añadir (contrato concreto)

1. **`BenchmarkSession`** ya está modelado (`Id`, `Label`, `Measurements`, `Artifacts`) y `BenchmarkMeasurement(Metric, Value, Unit, Metadata)`. Métricas previstas: `avgFps`, `p1Low`, `p01Low`, `frametimeP99`, `gpuBusyP95`, `cpuBusyP95`, `dpcTime`, `isrTime`, `cpuPackageTempC`, `packagePowerW`, `vramUsedMb`.
2. **Persistencia propuesta:** `%ProgramData%\FragForge\experiments\<sessionId>\` con `session.json` + CSV de PresentMon + captura de diagnóstico, referenciando `SnapshotId` y `OperationId`.
3. **Regla estadística mínima:** misma escena/demo, misma resolución y plan de energía, ≥ 3 repeticiones de 120 s, comparación por mediana y reporte de dispersión. **Sin esto, ningún tweak se promociona de Medium a High.**
4. **Promoción de evidencia:** un tweak solo sube de `EvidenceLevel` si un experimento A/B reproducible en el PN64 muestra mejora fuera del ruido; en caso contrario se degrada o se retira (política anti-placebo).
5. **Seguridad:** la Capa 1 no debe leer memoria de `cs2.exe` ni inyectar código. PresentMon usa ETW del sistema operativo (compatible con VAC/FACEIT); cualquier otra vía queda descartada por diseño.

### 6.5 Puntos de extensión que **no** deben romperse

* `ITweakAction` (7 métodos) y el discriminador `$actionState` de `ActionState`.
* `ITweakActionFactory.Create` y el registro de 10 tipos (`Registry`, `IfeoPerfOptions`, `Service`, `ScheduledTask`, `PowerPlan`, `Bcdedit`, `NetAdapterProperty`, `FileEdit`, `PowerShell`, `InspectOnly`).
* Formato de `SystemSnapshot` (`SchemaVersion = 1`) y de `JournalEntry` (cadena `PreviousHash`/`Hash`): cambiarlos exige migración.
* Rutas bajo `%ProgramData%\FragForge\` (`snapshots`, `journal`, `logs`, `reports`, `policy`, `.fragforge.lock`).
* Los códigos de salida del CLI (0/1/2/3/4/5) y la frase de riesgo literal `ACEPTO EL RIESGO`.
