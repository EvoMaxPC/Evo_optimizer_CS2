# Capa 1 — Ajuste al contrato real de PresentMon 2.6.0

**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Estado:** implementado, **sin compilar** (no hay `dotnet` en el entorno de trabajo)
**Alcance:** solo parser CSV, comando de captura, ubicación del exe y puerta de validez.

> Este informe **incorpora la adenda 2**, que prevalece sobre lo escrito antes. Donde la
> adenda contradice una versión anterior, manda la adenda.
>
> **No existe adenda 3.** Se buscó en el historial de git (todos los commits y ramas), en el
> contenido de todos los commits y en los PRs de GitHub, y no hay rastro. Si existe, no llegó a
> este repositorio: hay que pegarla. Este informe refleja la adenda 2 más dos correcciones
> añadidas después (ver "Correcciones posteriores" al final).

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

**`NA` es Unknown; `0` es un dato real.** Ejemplo verificado: `MsInPresentAPI = 0` en
323 de 449 filas. Convertir ese 0 en "ausente" perdería el dato; convertir `NA` en 0
inventaría una medición.

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
| `MsAllInputToPhotonLatency` | `InputToPhotonMs` | no |
| `MsClickToPhotonLatency` | `ClickToPhotonMs` | no |

`MsAllInputToPhotonLatency` trae dato en 11 de 449 filas y `MsClickToPhotonLatency` en
0 de 449. Se **parsean como `double?`** para no perder el dato si aparece, pero
**todavía no se usan en ningún cálculo**.

### Dos swapchains en el mismo proceso

La captura de referencia tiene **dos swapchains del mismo `ProcessID`**:

| SwapChainAddress | PresentRuntime | PresentMode | MsBetweenPresents | display | GPU |
|---|---|---|---|---|---|
| `0x167400D5A60` | `DXGI` | `Other` | ~104 ms | todo `NA` | todo `0` |
| `0x0` | `Other` | `Hardware: Legacy Flip` | ~16,5 ms | con dato | con dato |

**La relación entre las dos NO está determinada.** No se afirma cuál es la del juego,
cuál la del compositor ni cuál es "la real". Lo único que hace el código es **elegir
una** con una heurística explícita y **no mezclarlas**.

Que `SwapChainAddress` valga `0x0` **no hace inválida la fila**: `0x0` es una
dirección, no un error.

---

## Qué cambió

### 1. Parser (`PresentMonCsvParser`)

Todo por **nombre de columna**, `OrdinalIgnoreCase`, nunca por posición.

**Corrección de fondo.** `MsInPresentAPI` estaba como candidato de **CPU**. En la
captura real no existe `CPUBusy`, así que el parser caía en `MsInPresentAPI` y
reportaba **1,62 ms de "CPU ocupada"** cuando la CPU ocupada real era **113,78 ms**
(columna `MsCPUBusy`). Ahora `MsInPresentAPI` se expone como `PresentApiMs` y nunca se
usa como CPU.

Otros defectos corregidos:

- `MsBetweenDisplayChange` ya no es candidato de frametime. Viene `NA` sin seguimiento
  de display; tomarla como frametime convertiría "no medido" en "infinita fluidez".
- `SyncInterval` ya no es candidato de `PresentMode` (es un entero, no un modo).

**Separación por swapchain.** Cada `SwapChainAddress` acumula sus propias filas en una
instancia independiente (`SwapChainRows`). Ninguna estadística cruza de una cadena a
otra: promediar un flujo de ~104 ms con uno de ~16,5 ms daría un número que no
describe ninguno de los dos.

**Heurística de elección** (constantes, documentada en el código):

1. Entre las swapchains del proceso, primero las que tienen **seguimiento de display**
   (al menos una celda `MsUntilDisplayed` con dato).
2. Entre esas, **la de más filas**.
3. Si **ninguna** tiene display, la de más filas entre todas.
4. Desempate final por `Address` solo para que el resultado sea determinista.

Con la captura real esto elige `0x0` (3 filas, con display) y descarta
`0x167400D5A60` (3 filas, mediana 117,7144 ms, sin display). La regla vieja —"la de más
filas"— habría empatado 3 a 3 y decidido por accidente.

Se guarda la cadena elegida, el **motivo** y, por cada descartada, sus filas y su
mediana de `MsBetweenPresents` (`SwapChainSummary`). El motivo queda también en
`Warnings` para que sea legible en el JSON.

**GPU "no disponible"** = **toda** la columna `MsGPUBusy` es 0 o `NA` **dentro de la
swapchain elegida**. Verificado: la iGPU Intel sí da `MsGPUBusy` real en `0x0`
(mediana 9,588 ms en 324 filas). Es decir, `0x0` **no** tiene la GPU apagada.

**Encabezados no estándar.** Con `--v1_metrics`/`--v2_metrics` (observados sobre
`dwm.exe`) no está `MsBetweenPresents`. En ese caso se acepta el frame time como
**fallback** y se marca `IsNonStandardLayout = true` ⇒ la corrida es `Suspect`
("formato de CSV no estándar"). El resto de las columnas solo se leen del formato por
defecto: no se inventan equivalencias entre v1/v2 y 2.6.0. **El comando de captura no
pasa esos flags.**

### 2. Localización del exe (`PresentMonLocator`)

Orden: configuración explícita → `%ProgramFiles%\Intel\PresentMon\PresentMonConsoleApplication\PresentMon-*-x64.exe`
(la versión mayor más alta) → error claro. **Nunca PATH.**

Ruta real comprobada:
`C:\Program Files\Intel\PresentMon\PresentMonConsoleApplication\PresentMon-2.6.0-x64.exe`.
El exe **no está en PATH**.

**Versión (`--help`).** VERIFICADO en la PC objetivo: `--help` escribe por **STDERR** y
termina con **exit code 1**. La primera versión de `ReadVersion` leía solo
`StandardOutput`, de modo que nunca habría obtenido la versión. Ahora se leen **ambos
flujos** y **no se mira el exit code**: usarlo como criterio haría imposible leer nada.
La extracción vive en `PresentMonLocator.ParseVersionOutput(stdout, stderr)`, una
función pura y testeable. No es un gate de versión: el contrato se valida en runtime por
columnas.

### 3. Comando de captura

Se mantiene `List<string>` → `ArgumentList` (sin shell, rutas con espacios intactas):

```
--process_name <exe> --output_file <ruta> --timed <N> --terminate_after_timed --no_console_stats --session_name <FragForge-runId>
```

`--no_console_stats` va **explícito** para que no aparezca el warning de stderr.
**No** se pasan `--v1_metrics` ni `--v2_metrics`.

**Éxito = archivo con la columna obligatoria y al menos una fila.** El exit code no es
criterio, precisamente porque `--help` sale con 1.

**`--stop_existing_session` no se implementó.** Ver `docs/descartados.md`. La adenda
verifica que el flag existe y funciona, pero un test de higiene del repo prohíbe el
literal en `src/`; la decisión de alcance sigue en pie.

### 4. Puerta de validez (`CaptureValidityGate`)

Campo aditivo `RunResult.Validity`:

| Validez | Cuándo | Efecto |
|---|---|---|
| `Invalid` | sin columna obligatoria, o 0 filas | no entra en el veredicto |
| `Suspect` | FPS mediana < 30; todo `PresentMode=Other` o display todo `NA`; GPU no disponible **en la cadena elegida**; `SwapChains.Count > 1`; formato no estándar; <50 % de frames esperados | se guarda y se informa; **no bloquea** |
| `Valid` | resto | normal |

`ExperimentArm.IsValid` incluye `Validity.IsUsable`, de modo que `Verdict.Evaluate` no
declara mejora si alguna corrida es `Invalid`. El reporte HTML muestra la validez de
cada corrida con sus motivos.

---

## Archivos tocados

| Archivo | Cambio |
|---|---|
| `src/FragForge.Core/Experiments/FrameMetrics.cs` | +19 campos aditivos en `FrameSeries`; `SwapChainSummary` |
| `src/FragForge.Core/Experiments/CaptureValidity.cs` | **nuevo** — enum, reporte y puerta |
| `src/FragForge.Core/Experiments/ExperimentModel.cs` | `RunResult.Validity`; `ExperimentArm.IsValid` |
| `src/FragForge.Core/Experiments/ExperimentRunner.cs` | evalúa la puerta y la guarda |
| `src/FragForge.Core/Experiments/ExperimentReport.cs` | columna de validez con motivos |
| `src/FragForge.Actions/Measurement/PresentMonCsvParser.cs` | reescrito; agrupación por swapchain |
| `src/FragForge.Actions/Measurement/PresentMonLocator.cs` | **nuevo**; `ReadVersion` por stdout+stderr |
| `src/FragForge.Actions/Measurement/PresentMonCapture.cs` | localización + criterio de éxito |
| `tests/.../Measurement/PresentMonFixtures.cs` | **nuevo** — fixtures reales compartidas |
| `tests/.../Measurement/CsvParserTests.cs` | reescrito; tests de dos swapchains |
| `tests/.../Measurement/CaptureValidityTests.cs` | **nuevo** |
| `tests/.../Measurement/PresentMonLocatorTests.cs` | **nuevo**; incluye versión por STDERR |
| `tests/.../Measurement/PresentMonCaptureTests.cs` | criterio de éxito por archivo |
| `docs/descartados.md` | por qué no se implementó `--stop_existing_session` |

---

## Tests que fallarían sin el cambio (red-first)

| Test | Por qué falla antes |
|---|---|
| `Real260_CpuComesFromMsCPUBusy_NotFromMsInPresentAPI` | el parser tomaba `MsInPresentAPI` (1,62) como CPU |
| `V1CpuColumnIsAbsent_SoCpuIsNull_NotTakenFromMsInPresentAPI` | sin `MsCPUBusy` devolvía 0,40 en vez de null |
| `Real260_NaCells_BecomeNull_NeverZero` | no había tratamiento explícito de `NA` |
| `Real260_GpuAllZeroInChosenChain_IsUnavailable` | `GpuUnavailable` no existía |
| `TwoSwapChains_ChoosesTheOneWithDisplayTracking_NotTheLargest` | la regla era "la de más filas" |
| `TwoChains_DisplayTrackingWinsOverHavingMoreRows` | idem: con 4 filas DXGI ganaba DXGI |
| `TwoSwapChains_RecordsChosenReason_AndEveryDiscardedChainWithItsNumbers` | no se guardaban motivo ni descartadas |
| `TwoSwapChains_StatisticsComeFromOneChainOnly_AndMixingWouldDiffer` | no se separaban las cadenas |
| `Real260_ZeroSwapChain_GpuIsAvailable_AndPresentModeIsLegacyFlip` | se creía que `0x0` era inválida / sin GPU |
| `ParseVersionOutput_ReadsTheVersionFromStderr_NotOnlyStdout` | se leía solo stdout y `--help` sale por stderr |
| `MultipleSwapChains_IsSuspect` | la puerta miraba el texto de `Warnings` |
| `NonStandardLayout_IsSuspect_WithItsOwnReason` | no existía `IsNonStandardLayout` |
| `Validity_IsAdditiveInJson_AndOldDocumentsStillLoad` | el campo no existía |
| `Validity_RoundTripsThroughJson` | el campo no existía |
| `SuccessRequiresFileWithRequiredColumn_NotExitCodeOrStdout` | no estaba el criterio documentado |
| `MissingCsv_IsReported_EvenWithExitCodeZero` | idem |
| `ConfiguredPathWins_WhenItExists` | el localizador no existía |
| `MissingConfiguredPath_ReportsIt_AndDoesNotFallBackSilently` | idem |

---

## Lo que NO se pudo verificar

1. **No hay `dotnet` en el entorno.** Nada se compiló ni se ejecutó. El único chequeo
   automático aplicado fue el parser de sintaxis (tree-sitter) sobre los archivos
   tocados. Eso **no** es compilar.
2. **El comportamiento de PresentMon no se reprodujo aquí.** Las afirmaciones sobre
   2.6.0 vienen de la captura que aportaste; no de una ejecución mía.
3. **Sigue sin verificar:**
   - valores de `PresentMode` distintos de `Other` y `Hardware: Legacy Flip`;
   - la semántica exacta de `MsGPUBusy` frente a `MsGPUTime`.
4. **No hay test de integración real**: `PresentMonFrameCapture` se prueba con un
   lanzador falso. Una captura real de extremo a extremo no se ejecutó.
5. **La heurística de la puerta no está calibrada contra partidas reales.** Los
   umbrales (30 FPS, 50 % de frames) son cotas razonables, no resultados medidos.
6. **La captura de referencia no es un baseline válido.** Se usa solo como fixture.
   **No se afirma que el juego corra a 60 FPS ni que la pantalla esté a 60 Hz**: hay dos
   swapchains y su relación no está determinada.

## Correcciones posteriores a la adenda 2

Dos defectos propios encontrados al revisar el parser después de entregarlo. Ambos con test
red-first y commit propio.

### 1. `SwapChainAddress` devolvía `""` cuando no había dirección

**Dónde:** `PresentMonCsvParser.cs`, construcción de `chosenAddress`.

**Qué pasaba.** Si el CSV no trae la columna `SwapChainAddress`, o la celda viene `NA`, todas las
filas caen en la misma clave interna (`""`). Agrupar así es correcto —no hay dos cadenas que
separar—, pero ese `""` se reportaba tal cual en `FrameSeries.SwapChainAddress`: se afirmaba una
dirección que no se conoce. R-L dice `null`.

**Arreglo.** `chosenAddress = chosen.Address.Length == 0 ? null : chosen.Address;`. El detalle de
`Warnings` muestra `(sin dirección)` en vez de `: filas=…`.

**Tests:** `WithoutSwapChainColumn_AddressIsNull_NotAnEmptyString` y
`RowsWithNaSwapChainAddress_AreGroupedSeparately_AndTheirAddressIsNull`. El segundo comprueba además
que las filas con `NA` **no se mezclan** con las de la cadena real.

### 2. El motivo de "GPU no disponible" no acotaba a la swapchain

**Dónde:** `CaptureValidity.cs:92`.

**Qué pasaba.** El parser calcula `GpuUnavailable` **solo dentro de la swapchain elegida** (adenda 2),
pero la puerta emitía *"todas las filas de MsGPUBusy son 0 o NA"*. En una captura con varias cadenas
eso es **falso**: la descartada puede tener la GPU en cero y la elegida no — exactamente el caso de la
captura real, donde `0x167400D5A60` tiene todo en 0 y `0x0` tiene GPU real. El motivo contradecía al
dato que la propia captura demostraba.

**Arreglo.** El motivo dice ahora *"GPU no disponible en la swapchain analizada"*.

**Test:** `GpuUnavailableReason_SaysTheChainAnalyzed_NotAllRows`.

---

## Sí verificado en la PC objetivo

- `--session_name FragForge` y `--stop_existing_session` **funcionan**: exit 0, sin
  procesos residuales.
- `--help` sale por **STDERR** con **exit code 1**.
- El exe está en la ruta de Intel indicada y **no** está en PATH.
- `MsGPUBusy` da datos reales en la iGPU Intel dentro de la swapchain `0x0`.
- `MsInPresentAPI = 0` es un valor real frecuente (323/449 filas), no un `NA` disfrazado.
