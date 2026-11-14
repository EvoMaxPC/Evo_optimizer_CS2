# Capa 1 — Ajuste al contrato real de PresentMon 2.6.0

**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**HEAD de partida:** `caa9ece` · **build de partida:** 0 errores, 404 tests
**Estado:** implementado, **sin compilar** (no hay `dotnet` en el entorno de trabajo)

---

## Contrato real de PresentMon 2.6.0

### Encabezado

28 columnas, en este orden, capturado en la PC objetivo con flags por defecto
(sin `--v1_metrics` / `--v2_metrics`):

```
Application, ProcessID, SwapChainAddress, PresentRuntime, SyncInterval, PresentFlags,
AllowsTearing, PresentMode, TimeInMs, MsBetweenSimulationStart, MsBetweenPresents,
MsBetweenDisplayChange, MsInPresentAPI, MsRenderPresentLatency, MsUntilDisplayed,
CPUStartTimeInMs, MsBetweenAppStart, MsCPUBusy, MsCPUWait, MsGPULatency, MsGPUTime,
MsGPUBusy, MsGPUWait, MsAnimationError, AnimationTime, MsFlipDelay,
MsAllInputToPhotonLatency, MsClickToPhotonLatency
```

### Valores ausentes

Se escriben **`NA`**, no vacío. Los decimales usan punto y llegan a 14 dígitos
(ruido de float). Se parsea con `CultureInfo.InvariantCulture`.

### Columnas que usa FragForge

| Columna | Campo | Obligatoria |
|---|---|---|
| `MsBetweenPresents` | `FrameTimesMs` | **sí** |
| `MsCPUBusy` | `CpuBusyMs` | no |
| `MsCPUWait` | `CpuWaitMs` | no |
| `MsGPUBusy` | `GpuBusyMs` | no |
| `MsGPUTime` | `GpuTimeMs` | no |
| `MsBetweenDisplayChange` | `DisplayChangeMs` | no |
| `MsUntilDisplayed` | `UntilDisplayedMs` | no |
| `MsInPresentAPI` | `PresentApiMs` | no |
| `PresentMode` | `PresentModes` | no |
| `Application` | `ApplicationName` | no |
| `ProcessID` | `ProcessId` | no |
| `SwapChainAddress` | `SwapChainAddress` | no |
| `TimeInMs` | `TimesMs` | no |
| `SyncInterval` | `SyncIntervals` | no |
| `AllowsTearing` | `AllowsTearing` | no |

`MsAllInputToPhotonLatency` y `MsClickToPhotonLatency` **no se leen**: vienen `NA`
salvo instrumentación especial, y no se usan para nada todavía.

### Qué NO se verificó (y por tanto no se afirma en código ni en tests)

- Comportamiento de `--session_name` / `--stop_existing_session`.
- Columnas que aparecen con `--v1_metrics` / `--v2_metrics`.
- Valores posibles de `PresentMode` en una partida real.
- Si `MsGPUBusy` da datos reales en la iGPU Intel.
- **Exit code de PresentMon**: no se observó. Por eso el éxito se decide por archivo.
- Causa de los ~8,6 FPS de la captura de referencia (probable ventana sin foco o
  pantalla de carga; **no confirmado**).

---

## Qué cambió

### 1. Parser (`PresentMonCsvParser`)

Reescrito contra el contrato. Todo por **nombre de columna**, `OrdinalIgnoreCase`,
nunca por posición.

**Corrección de fondo.** `MsInPresentAPI` estaba como candidato de **CPU**. En la
captura real no existe `CPUBusy` ni `MsCPUBusy`, así que el parser caía en
`MsInPresentAPI` y reportaba **1.62 ms de "CPU ocupada"** cuando la CPU ocupada
real era **113.78 ms** (columna `MsCPUBusy`). Ahora `MsInPresentAPI` se expone como
`PresentApiMs` y nunca se usa como CPU.

Otros defectos corregidos:
- `MsBetweenDisplayChange` ya no es candidato de frametime. En 2.6.0 viene `NA`
  sin seguimiento de display; tomarla como frametime convertiría "no medido" en
  "infinita fluidez".
- `SyncInterval` ya no es candidato de `PresentMode` (es un entero, no un modo).

Comportamiento nuevo:
- Columna obligatoria ausente ⇒ serie vacía marcada **con la lista de columnas
  encontradas** (`HeaderColumns`).
- `NA` y vacío ⇒ `null`. **Nunca 0.**
- Swapchains múltiples: se analiza la dominante por número de filas, las demás se
  reportan en `Warnings`. No se mezclan.
- GPU "no disponible": había columna pero todas las celdas fueron 0 o `NA` ⇒
  `GpuUnavailable = true`, no "GPU = 0".
- Fila truncada: se descarta entera, no se usa a medias.

### 2. Localización del exe (`PresentMonLocator`)

Orden: configuración explícita → `%ProgramFiles%\Intel\PresentMon\PresentMonConsoleApplication\PresentMon-*-x64.exe`
(la versión mayor más alta) → error claro. **Nunca PATH.**

La versión se obtiene de la primera línea de `--help` (`PresentMon 2.6.0`), sin gate
de versión: el contrato se valida en runtime por columnas.

Se comprobó que la ruta real es
`C:\Program Files\Intel\PresentMon\PresentMonConsoleApplication\PresentMon-2.6.0-x64.exe`
y que el exe no está en PATH.

### 3. Comando de captura

Se mantiene con `List<string>` → `ArgumentList` (sin shell, rutas con espacios
intactas):

```
--process_name <exe> --output_file <ruta> --timed <N> --terminate_after_timed --no_console_stats
```

`--no_console_stats` va **explícito** para que no aparezca el warning de stderr.

**`--stop_existing_session` no se implementó.** Ver `docs/descartados.md`.

### 4. Puerta de validez (`CaptureValidityGate`)

Campo aditivo `RunResult.Validity`:

| Validez | Cuándo | Efecto |
|---|---|---|
| `Invalid` | sin columna obligatoria, o 0 filas | no entra en el veredicto |
| `Suspect` | FPS mediana < 30; todo `PresentMode=Other` o display todo `NA`; GPU no disponible; >1 swapchain; <50 % de frames esperados | se guarda y se informa; **no bloquea** |
| `Valid` | resto | normal |

`ExperimentArm.IsValid` incluye `Validity.IsUsable`, de modo que `Verdict.Evaluate`
no declara mejora si alguna corrida es `Invalid`. El reporte HTML muestra la
validez de cada corrida con sus motivos.

---

## Archivos tocados

| Archivo | Cambio |
|---|---|
| `src/FragForge.Core/Experiments/FrameMetrics.cs` | +14 campos aditivos en `FrameSeries` |
| `src/FragForge.Core/Experiments/CaptureValidity.cs` | **nuevo** — enum, reporte y puerta |
| `src/FragForge.Core/Experiments/ExperimentModel.cs` | `RunResult.Validity`; `ExperimentArm.IsValid` |
| `src/FragForge.Core/Experiments/ExperimentRunner.cs` | evalúa la puerta y la guarda |
| `src/FragForge.Core/Experiments/ExperimentReport.cs` | columna de validez con motivos |
| `src/FragForge.Actions/Measurement/PresentMonCsvParser.cs` | reescrito |
| `src/FragForge.Actions/Measurement/PresentMonLocator.cs` | **nuevo** |
| `src/FragForge.Actions/Measurement/PresentMonCapture.cs` | localización + criterio de éxito |
| `tests/.../Measurement/CsvParserTests.cs` | fixtures reales + 7 tests nuevos |
| `tests/.../Measurement/CaptureValidityTests.cs` | **nuevo** — 10 tests |
| `tests/.../Measurement/PresentMonLocatorTests.cs` | **nuevo** — 5 tests |
| `tests/.../Measurement/PresentMonCaptureTests.cs` | +2 tests de criterio de éxito |
| `docs/descartados.md` | por qué no se implementó `--stop_existing_session` |

---

## Tests que fallarían sin el cambio (red-first)

| Test | Por qué falla antes |
|---|---|
| `Real260_CpuComesFromMsCPUBusy_NotFromMsInPresentAPI` | el parser tomaba `MsInPresentAPI` (1.62) como CPU |
| `V1CpuColumnIsAbsent_SoCpuIsNull_NotTakenFromMsInPresentAPI` | sin `MsCPUBusy` devolvía 0.40 en vez de null |
| `Real260_NaCells_BecomeNull_AndNeverZero` | no había tratamiento explícito de `NA` |
| `Real260_GpuAllZero_IsReportedUnavailable` | `GpuUnavailable` no existía |
| `Real260_ColumnOrderCanBePermuted_AndStillParses` | — (ya pasaba, ahora es regresión) |
| `TruncatedRow_IsDiscarded_NotPartiallyUsed` | ya pasaba; se conserva |
| `MultipleSwapChains_AnalysesDominant_AndWarns` | no se detectaban swapchains |
| `MissingRequiredColumn_ListsTheColumnsFound` | el mensaje no listaba columnas |
| `Real260Capture_IsSuspect_NotValid_NotInvalid` | la puerta no existía |
| `Validity_IsAdditiveInJson_AndOldDocumentsStillLoad` | el campo no existía |
| `Validity_RoundTripsThroughJson` | el campo no existía |
| `SuccessRequiresFileWithRequiredColumn_NotExitCodeOrStdout` | no estaba el criterio documentado |
| `MissingCsv_IsReported_EvenWithExitCodeZero` | idem |
| `ConfiguredPathWins_WhenItExists` | el localizador no existía |
| `MissingConfiguredPath_ReportsIt_AndDoesNotFallBackSilently` | idem |

---

## Lo que NO se pudo verificar

1. **No hay `dotnet` en el entorno.** Nada se compiló ni se ejecutó. El único chequeo
   automático aplicado fue el parser de sintaxis (tree-sitter) sobre los 12 archivos
   tocados: los 12 están sintácticamente válidos. Eso **no** es compilar.
2. **El comportamiento de PresentMon no se reprodujo aquí.** Todas las afirmaciones
   sobre 2.6.0 vienen de la captura que aportaste; no de una ejecución mía.
3. **`--stop_existing_session`** y las columnas de `--v1_metrics`/`--v2_metrics`
   siguen sin verificar.
4. **No hay test de integración real**: `PresentMonFrameCapture` se prueba con un
   lanzador falso. Una captura real de extremo a extremo no se ejecutó.
5. **La heurística de la puerta no está calibrada contra partidas reales.** Los
   umbrales (30 FPS, 50 % de frames) son cotas razonables, no resultados medidos.

**Siguiente paso obligatorio:** `dotnet build` en tu máquina con SDK 10.0.403 y
`dotnet test`. Si algo no compila, con la salida real lo corrijo.
