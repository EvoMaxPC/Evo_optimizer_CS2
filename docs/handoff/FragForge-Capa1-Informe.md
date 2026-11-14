# FragForge — Capa 1: diagnóstico, línea base y comparación A/B

**Fecha:** 2026-11-13
**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Base:** `434aec0` (Capa 0 cerrada, 139 archivos rastreados, 103 pruebas)
**Estado:** código y pruebas escritos y commiteados; **compilación y ejecución de pruebas NO verificadas en este entorno** (ver §0).

---

## 0. Advertencia previa: aquí no se compiló nada

Este entorno de trabajo **no tiene el SDK de .NET**. Cuatro comprobaciones independientes:

| Comprobación | Resultado |
|---|---|
| `which dotnet` / `find / -name dotnet.dll` | nada |
| `/usr/share/dotnet`, `/usr/lib/dotnet` | no existen |
| `apt-cache policy dotnet-sdk-10.0` | paquete inexistente |
| `mono` en `/usr/lib/mono` | no existe |

Además, las salidas de red hacia `builds.dotnet.microsoft.com`, `dotnetcli.azureedge.net`, `release-assets.githubusercontent.com` y `api.nuget.org` devuelven código de conexión 000 (bloqueadas). PyPI **no** ofrece el SDK (`dotnetcore2` es solo runtime).

**Consecuencia honesta:** de la Capa 1 hay **diseño, código y pruebas**, pero **cero evidencia de ejecución**. Todo lo que sigue está escrito como *lo que el código hace*, no como *lo que se observó al correrlo*. Las fases 1–6 se marcan como **NO EJECUTADO AQUÍ**. Lo único que se pudo validar por medios estáticos fue:

- Equilibrio de llaves/paréntesis/corchetes en los 118 archivos `.cs` (script de verificación propio), sin desbalances atribuibles a código nuevo.
- Búsqueda de APIs prohibidas (`OpenProcess`, `ReadProcessMemory`, `WriteProcessMemory`, `CreateRemoteThread`, `VirtualAllocEx`, `.MainModule`, `.Modules[`) en `src/`: **0 coincidencias**.
- Búsqueda de `Arguments = "` (concatenación de línea de comandos) en `src/`: **0 coincidencias**.
- Búsqueda de `HttpClient`, `WebClient`, `DownloadFile`, `DownloadStringAsync`, `Invoke-WebRequest` en `src/`: **0 coincidencias** (la única aparición de esos tokens está en el *denylist* de PowerShell, que es donde deben estar).

Los tres últimos puntos están además congelados como pruebas (`SourceHygieneTests`), de modo que la primera ejecución en un equipo con SDK los vuelve a comprobar.

---

## 1. Discrepancias entre el encargo y el código (el código gana)

Revisadas antes de escribir nada, según la regla de la Capa 1.

| # | El encargo decía | El código dice | Decisión |
|---|---|---|---|
| a | `FragForgePaths` debía tener `Experiments`/`Tools`/`Facts` | No los tenía | Añadidos + `ExperimentLockFile` y `LocalEvidence` (Fase 0.4) |
| b | Sondas de diagnóstico como servicios independientes | Estaban como una lista preconstruida (`DiagnosticProbeFactory.CreateDefaults`) | Reemplazado por `Registrations` con una entrada de contenedor por sonda (Fase 0.2) |
| c | Una sola `IProtectionPolicy` | Se registraba **dos veces** (CompositionRoot y AddFragForgeActions) | Queda una sola, la cargada de disco (Fase 0.3) |
| d | Los hooks del usuario debían llegar al motor | Había un `AddSingleton<IEnumerable<IExperimentHook>>([])` vacío que los eclipsaba | Eliminado; cada hook es un registro propio (Fase 0.1) |
| e | «`ICpuTopologyProbe` de `Abstractions/`» | Vive en `FragForge.Actions/Platform/CpuTopology.cs`, no en `Abstractions/` | No se movió: renombrar/mover está fuera del alcance de la Capa 1 |
| f | `BenchmarkSession` con `schemaVersion` | Existe en `Core/Models/Results.cs` **sin** versión de esquema | No se tocó. Los artefactos nuevos (sesiones de experimento, facts, evidencia local) sí llevan `SchemaVersion` |
| g | `EnvironmentSnapshotHook` recibía `IHardwareProfiler` | Lo recibía pero no lo usaba (el perfil llega en `ExperimentContext`) | Dependencia eliminada; el contenedor lo construye sin argumentos |

Ninguna otra discrepancia: el catálogo (10 tweaks) y los tipos de `ConditionFactory` están intactos desde la Capa 0.

---

## 2. Fases y estado real

| Fase | Contenido | Estado |
|---|---|---|
| 0 | Prep del motor (4 cambios + gate) | **Escrito**, 8 pruebas |
| 1 | Núcleo puro `Core/Experiments` | **Escrito**, 30 pruebas |
| 2 | Captura, sensores y sondas | **Escrito**, 16 pruebas |
| 2b | ETW DPC/ISR (opcional) | **No implementado** — decisión explícita, ver §7 |
| 3 | `IExperimentRunner` | **Escrito**, 12 pruebas |
| 4 | Persistencia e informe HTML | **Escrito**, 13 pruebas |
| 5 | CLI (y GUI) | **CLI escrito**; **GUI no** — decisión explícita, ver §7 |
| 6 | Pruebas | **113 pruebas nuevas, 203 en total** |
| 7 | Este informe | Hecho |

---

## 3. Decisiones de medición que el código deja fijadas

### 3.1 Métricas (definiciones exactas, no «aproximadas»)

| Métrica | Definición implementada |
|---|---|
| `avgFps` | frames ÷ tiempo transcurrido, **después** del recorte de calentamiento |
| `p1Low` | 1000 ÷ media de los frametimes del peor 1 % (los más altos) |
| `p01Low` | igual con el peor 0.1 % |
| `frameTimeP50/P99/P999` | percentil con interpolación lineal |
| `jitterMs` | `P99 − P50` (dispersión robusta, no depende de la media) |
| `frameTimeCv` | desviación estándar muestral ÷ media |
| `stutterPct` | % de frames con frametime > 2 × mediana móvil de ±60 frames |
| `cpuBusyP95` / `gpuBusyP95` | P95 del tiempo ocupado, solo si el CSV trae `CPUBusy`/`GPUBusy` |
| `presentMode` | distribución porcentual + modo dominante |

**Recorte de calentamiento:** 10 s por defecto (`FrameMetricsCalculator.DefaultWarmupSeconds`), y el valor efectivo se guarda en cada corrida (`WarmupSkippedSeconds`) para que el informe no mienta sobre qué ventana se analizó.

**Regla dura:** una métrica sin datos es `null` (=Unknown). Nunca 0. Un `p1Low = 0` significaría «fluidez infinita»; inventarlo sería mentir. Hay pruebas para el caso de columna ausente tanto en el parser como en las métricas.

### 3.2 Estadística

- Bootstrap de diferencia de medianas, **2000 iteraciones, semilla fija 20260101**: misma entrada ⇒ mismo resultado, bit a bit, en la misma versión del runtime.
- Test de permutación **exacto** cuando n ≤ 8 por brazo (enumeración de todas las particiones); Monte Carlo con semilla cuando n es mayor.
- Delta de Cliff para tamaño de efecto ordinal.
- **Vectores dorados verificados a mano en las pruebas:** percentil lineal sobre 1..10 ⇒ P25=3.25, P50=5.5, P90=9.1, P99=9.91; peor-1 % de 99 frames de 10 ms + 1 de 100 ms ⇒ 10.0 FPS; 200 Hz uniforme ⇒ jitter = 0; 3 vs 3 totalmente separados ⇒ p exacto = 0.1 (C(6,3)=20 particiones).

### 3.3 Tabla de veredictos (la que está implementada)

```
Regresión por jitter > piso                      → Empeora  (prima sobre todo lo demás)
IC95 de p1Low excluye 0 a favor
  AND |efecto| > piso de p1Low
  AND avgFps no cae más que su piso              → Mejora
  AND avgFps cae más que su piso                 → Inconcluso (intercambio, no mejora limpia)
IC95 de p1Low excluye 0 en contra                → Empeora
TODO el IC95 (en % de la base) dentro del piso
  AND al menos 3 corridas por brazo              → Sin diferencia
Cualquier otro caso                              → Inconcluso
```

**Piso de ruido:** `NoiseFloor.ConservativeDefault` = **5 % p1Low / 2 % avgFps / 5 % jitter**, con `IsEstimated = false`. Mientras no haya calibración A/A, el veredicto **nunca** puede ser «Confirmado». Un piso inventado a la ligera sería peor que no tener veredicto.

**Umbral de confirmación:** 5 corridas por brazo (`MinimumConfirmedRunsPerArm`). Con 3 vs 3 el p en el mejor caso es 0.1: afirmar «Confirmado» con esa muestra es estadísticamente imposible, y el código lo impide.

**Equivalencia honesta:** declarar «Sin diferencia» exige que *todo* el IC95, convertido a % de la mediana base, quepa dentro del piso, y al menos 3 corridas por brazo. Es un criterio de equivalencia práctica, no un «no encontré diferencias».

### 3.4 Aislamiento y anti-confusión

- **Agenda ABAB intercalada** con el brazo inicial aleatorizado por semilla. El modo bloque existe pero se marca como confundido con la deriva térmica en el informe.
- **El runner nunca toma el `IExecutionGate` del motor.** No lo recibe en el constructor (hay una prueba por reflexión que lo fija), aplica y revierte con `ITweakEngine`, y mide entre llamadas. Así una captura de 2 minutos no bloquea al motor ni deja el sistema a medias si el usuario cancela.
- **Lock propio:** `%ProgramData%\FragForge\.fragforge-experiment.lock`, distinto del lock del motor. Dos mediciones no se solapan; una medición no impide aplicar un tweak.
- **Verificación de estado:** después de cada Apply/Revert se consulta `GetStateAsync` para cada tweak. Si el estado no es el esperado, el experimento se **invalida** en lugar de medir una mentira.
- **Entorno comparable:** se capturan build, plan de energía, refresco real, resolución, reinicio pendiente y servicing. Si cambian entre el inicio y la reanudación, se invalida.

---

## 4. PresentMon: cómo se invoca y qué no se hace

- Ubicación fija: `%ProgramData%\FragForge\tools\PresentMon.exe`.
- **Verificación por SHA-256 contra una allowlist embebida que se entrega con `sha256: null`.**
  - Fichero ausente ⇒ `Missing`, captura deshabilitada, mensaje que dice que **FragForge no descarga binarios**.
  - Fichero presente con hash vacío ⇒ `HashPlaceholder`; el mensaje incluye el SHA-256 real calculado para que el usuario lo pegue en la allowlist tras verificarlo por su cuenta.
  - Hash distinto ⇒ `HashMismatch`; el binario **no se ejecuta**.
- **Sin hashes inventados.** La prueba `EmbeddedAllowlist_HasNoInventedHash` falla si alguien rellena el campo con cualquier cosa.
- Argumentos (`ProcessStartInfo.ArgumentList`, nunca cadena concatenada):
  `--process_name cs2.exe --output_file <csv> --session_name FragForge-<runId> --timed <s> --terminate_after_timed --no_console_stats`
- **No se usa la opción de detener sesión existente:** mataría sesiones ETW de otras herramientas del usuario.
- **Cancelación:** se mata el árbol de procesos (`Kill(entireProcessTree: true)`), se conserva el CSV parcial y la corrida se marca inválida.
- `Parser`: localiza columnas **por nombre**; soporta `FrameTime`/`MsBetweenPresents`, `CPUBusy`/`msInPresentAPI`, `GPUBusy`/`GPUTime`, `PresentMode`. CSV con comillas y comas internas soportado. Cualquier `<= 0` se descarta.

**Lo que no hay que confundir:** el parser está probado con CSV **sintéticos** que reproducen la forma de columna de 1.x y 2.x. No hay ni una captura real en este repositorio.

---

## 5. Sensores, ratón y memoria

### 5.1 Sensores (PDH)

- 14 contadores candidatos, descubiertos en **runtime**: si un contador no existe, se reporta como ausente y la métrica queda `null`.
- `cpuPackageTemp` **no es legible sin driver**. PawnIO está en el equipo y **NO se usa**. La zona térmica ACPI (`Thermal Zone Information`) se reporta como **proxy de confianza baja**, nunca como temperatura de núcleo.
- `Energy Meter` (RAPL) solo existe si Windows lo expone; en muchos equipos no está, y eso se dice en lugar de rellenar con 0.

### 5.2 `mouseRate`

- Ventana `HWND_MESSAGE` (solo mensajes, invisible, fuera de Alt+Tab), `RegisterRawInputDevices` con usage page 1 / usage 2 y `RIDEV_INPUTSINK`.
- **Solo se guardan timestamps de `QueryPerformanceCounter` y conteos.** Las coordenadas no se leen, ni se guardan, ni se registran.
- Resultado: mediana de Hz, p5, jitter (P99−P50) e histograma de intervalos.
- Válido solo con ≥ 100 intervalos; si no, se dice que muevas el ratón, no se inventa una cifra.

### 5.3 `memBandwidth`

- Buffer de 256 MiB recorrido con 1, 2 y 4 hilos; GB/s efectivos.
- Se combina con evidencia SMBIOS (localizadores distintos, capacidades iguales).
- **«Dual» solo con confianza Alta** (señal SMBIOS clara **y** dispersión < 12 %). En cualquier otro caso el veredicto de canal es `Unknown` con nota. Nunca se declara «Single» sin evidencia.
- Se persiste en `facts.json` como `{valor, confianza, método, fecha}`.

---

## 6. Persistencia

```
%ProgramData%\FragForge\
├─ .fragforge.lock                  (motor)
├─ .fragforge-experiment.lock       (medición)
├─ facts.json                       (hechos medidos)
├─ tools\PresentMon.exe             (verificado por hash)
└─ experiments\
   ├─ local-evidence.json           (sugerencias por tweak)
   └─ <sessionId>\
      ├─ session.json               (schemaVersion, plan, corridas, veredicto)
      └─ runs\
         ├─ run-01.csv              (frametimes crudos)
         └─ run-01-sensors.csv      (sensores)
```

- Escritura **atómica** (temporal + `File.Move`), sin `.tmp` huérfanos; hay prueba.
- Identificador de sesión validado por regex `^[A-Za-z0-9_-]{1,80}$`: `../../etc`, espacios y cadenas de 81 caracteres son rechazados antes de tocar el disco.
- JSON **estricto**: `UnmappedMemberHandling.Disallow`. Un campo desconocido en `session.json` es un error, no se ignora.
- El informe HTML es autocontenido: CSS y SVG propios, sin `<script>`, sin `<link>`, sin CDN. Todo texto del usuario pasa por `WebUtility.HtmlEncode` (probado con `<script>alert('x')</script>`). La línea de **5.00 ms está rotulada como referencia**, no como objetivo.

---

## 7. Lo que deliberadamente NO se hizo

| Elemento | Por qué |
|---|---|
| **GUI `MeasurementViewModel`** | El repositorio tiene 7 proyectos y ninguno es la GUI: no hay proyecto de interfaz donde colgarla. Crear un proyecto nuevo violaría la restricción de 7 proyectos. La lógica de medición es consumible tal cual desde el futuro host de UI. |
| **ETW DPC/ISR (fase 2b)** | Marcada como SHOULD. Requiere `Microsoft.Diagnostics.Tracing.TraceEvent` y una sesión ETW propia; sin compilador ni Windows no se puede validar ni lo más básico (creación de sesión, permisos). Se deja fuera antes que entregar código que no se puede ni ejecutar. |
| **Calibración A/A automática** | La estructura existe (`AaCalibration`, `NoiseFloor.IsEstimated`), pero calibrar el piso requiere ejecuciones reales. El piso por defecto es conservador y **bloquea «Confirmado»**, que es la decisión segura. |
| **Modo «aplicar tweak automáticamente»** | El runner aplica y revierte con el motor existente, con verificación de estado. No se añadió ninguna vía nueva de escritura en Windows. |

---

## 8. Comandos (los que el usuario ejecutará en el PN64)

```powershell
# 1. Verificación de la herramienta (esperando HashPlaceholder la primera vez)
fragforge tools verify

# 2. Refresco real del monitor (EnumDisplaySettings, no WMI)
fragforge probe monitorRefresh

# 3. Frecuencia real del ratón (10 s moviéndolo)
fragforge mouse-test

# 4. Línea base: 3 corridas de 120 s con 10 s de calentamiento
fragforge baseline --duration 120 --runs 3

# 5. Comparación A/B de un tweak
fragforge ab latency.game-mode-on --runs 5

# 6. Revisión y reanudación
fragforge experiment list
fragforge experiment show <sessionId>
fragforge experiment resume <sessionId>
fragforge experiment report <sessionId>
```

**Primer arranque esperado:** `tools verify` devolverá código **6** (medición inválida) con `HashPlaceholder` hasta que el usuario pegue el SHA-256 del binario oficial en `presentmon-allowlist.json`. Eso es el comportamiento correcto, no un fallo.

Códigos de salida: `0` éxito · `1` fallo · `2` uso · `3` preflight/catálogo · `4` cancelado · `5` rollback crítico · `6` **medición inválida** (nuevo en Capa 1).

---

## 9. Pruebas

| Archivo | Pruebas |
|---|---|
| `Experiments/StatisticsTests.cs` | 11 |
| `Experiments/FrameMetricsTests.cs` | 9 |
| `Experiments/VerdictTests.cs` | 10 |
| `Experiments/ExperimentPlanAndStoreTests.cs` | 15 |
| `Experiments/ExperimentRunnerTests.cs` | 12 |
| `Experiments/ReportAndLocalizationTests.cs` | 13 |
| `Experiments/DiagnosticsAndDiTests.cs` | 15 |
| `Experiments/SourceHygieneAndAnalysisTests.cs` | 12 |
| `Measurement/CsvParserTests.cs` | 10 |
| `Measurement/PresentMonCaptureTests.cs` | 6 |
| **Nuevas** | **113** |
| **Previas (Capa 0, intactas)** | **103** |
| **Total de métodos de prueba** | **203** |
| **Casos al ejecutar xUnit** | **≈216** (un `[Theory]` con 14 `[InlineData]` cuenta como 14) |

> Las 103 pruebas de la Capa 0 no se tocaron; las 113 nuevas viven en carpetas separadas (`Experiments/`, `Measurement/`). **Ninguna de las 203 se ha ejecutado aquí.**

Cobertura conceptual (no línea a línea): tabla de veredictos completa, vectores dorados de estadística, fixtures 1.x/2.x, columna ausente ⇒ Unknown, allowlist sin hash, ruta `HashPlaceholder` sin descarga, regex de id, escritura atómica y sin temporales, JSON estricto, runner sin gate, invalidación por estado no verificado, entorno cambiado, captura parcial, reanudación idempotente, render escapado y autocontenido, sugerencia de evidencia sin tocar catálogo, y escaneo estático de APIs prohibidas.

---

## 10. NO validado en el PN64

Lista literal de lo que **no** se ha comprobado en el equipo real (ASUS MiniPC PN64, i3-1220P, iGPU UHD, Win11 Pro 26200):

1. **Compilación.** No hay SDK en este entorno. Cualquier error de tipos o de resolución de sobrecargas es posible; el código se revisó a mano contra las firmas reales de `Abstractions/`, pero eso no sustituye a un `dotnet build`.
2. **Ejecución de las 216 pruebas.** Escritas, no ejecutadas.
3. **PresentMon.** No se ha ejecutado nunca. Los nombres de argumento (`--process_name`, `--output_file`, `--session_name`, `--timed`, `--terminate_after_timed`, `--no_console_stats`) se eligieron por documentación, no por comprobación en el binario. **El primer `fragforge baseline` puede fallar por un nombre de argumento distinto en la versión instalada.**
4. **Forma real del CSV de PresentMon 2.x** (orden, nombres exactos, presencia de `CPUBusy`). El parser está preparado para varias familias de nombres, pero no se ha enfrentado a un archivo auténtico.
5. **Contadores PDH.** No se sabe cuáles existen realmente en el PN64; `Energy Meter` y `Thermal Zone` son los más dudosos.
6. **`EnumDisplaySettings`** y el refresco real del monitor conectado.
7. **Raw Input** con el ratón del usuario: ni tasa real, ni que `RIDEV_INPUTSINK` reciba eventos con la ventana de solo mensajes en su configuración.
8. **`memBandwidth`**: ningún valor de referencia; el criterio de «dispersión < 12 % ⇒ estable» es un umbral de diseño, no medido.
9. **Latencia del propio runner**: el enfriamiento por defecto es de 30 s; no está calibrado contra el comportamiento térmico del PN64 (28 W compartidos entre CPU e iGPU).
10. **Comportamiento con Atlas Playbook v0.5.0 aplicado**: el precheck lee plan de energía y servicing, pero no se ha verificado que el plan activo tras Atlas sea el que el experimento espera.
11. **GUI**: no existe (ver §7).
12. **ETW/DPC/ISR**: no existe (ver §7).

---

## 11. Archivos de la Capa 1

### Nuevos (21)

| Ruta | Líneas | Función |
|---|---|---|
| `src/FragForge.Core/Experiments/Statistics.cs` | 201 | Percentiles, LowFps, Cliff, bootstrap, permutación |
| `src/FragForge.Core/Experiments/FrameMetrics.cs` | 178 | Métricas de corrida y recorte de calentamiento |
| `src/FragForge.Core/Experiments/Verdict.cs` | 185 | Tabla de veredictos y estimación de efecto |
| `src/FragForge.Core/Experiments/ExperimentModel.cs` | 182 | Plan, agenda, estado, entorno, facts |
| `src/FragForge.Core/Experiments/IMeasurement.cs` | 119 | Contratos de captura, sensores y hechos |
| `src/FragForge.Core/Experiments/Analysis.cs` | 113 | Cuello de botella y throttling |
| `src/FragForge.Core/Experiments/ExperimentStore.cs` | 136 | Almacén, lock de experimentos, facts |
| `src/FragForge.Core/Experiments/ExperimentRunner.cs` | 253 | Orquestador A/B |
| `src/FragForge.Core/Experiments/EnvironmentSnapshotHook.cs` | 53 | `environment-<fase>.json` |
| `src/FragForge.Core/Experiments/LocalEvidenceStore.cs` | 68 | `local-evidence.json` |
| `src/FragForge.Core/Experiments/ExperimentReport.cs` | 179 | Informe HTML autocontenido |
| `src/FragForge.Actions/Measurement/PresentMonCsvParser.cs` | 114 | Parser por nombre de columna |
| `src/FragForge.Actions/Measurement/PresentMonCapture.cs` | 175 | Invocación de PresentMon |
| `src/FragForge.Actions/Measurement/ToolManifest.cs` | 72 | Allowlist + verificación SHA-256 |
| `src/FragForge.Actions/Measurement/presentmon-allowlist.json` | 13 | Allowlist con hash vacío |
| `src/FragForge.Actions/Measurement/SensorSampler.cs` | 162 | PDH runtime |
| `src/FragForge.Actions/Measurement/DisplayProbe.cs` | 161 | `monitorRefresh` + sondas |
| `src/FragForge.Actions/Measurement/MouseRateProbe.cs` | 227 | Raw Input sin coordenadas |
| `src/FragForge.Actions/Measurement/MemoryBandwidthExperiment.cs` | 138 | Ancho de banda + SMBIOS |
| `src/FragForge.Actions/Measurement/EnvironmentProbe.cs` | 102 | Precheck de entorno |
| `src/FragForge.Composition/Layer1Registration.cs` | 68 | Cableado DI |
| `src/FragForge.Cli/MeasurementCommands.cs` | 248 | Verbos de medición |

### Modificados (9)

`FragForgePaths.cs`, `ServiceCollectionExtensions.cs`, `DiagnosticProbes.cs`, `CompositionRoot.cs`, `FragForge.Actions.csproj`, `Directory.Packages.props`, `CommandLine.cs`, `Program.cs`, `FragForgeException.cs`.

### Dependencia añadida

`System.Diagnostics.PerformanceCounter` 10.0.11 — la única permitida para la fase 2. `Microsoft.Diagnostics.Tracing.TraceEvent` **no** se añadió porque la fase 2b no se implementó.

---

## 12. Siguiente paso concreto

En un equipo con SDK 10.0.400:

```bash
dotnet build FragForge.sln -c Release
dotnet test tests/FragForge.Tests/FragForge.Tests.csproj -c Release
```

Esperar un primer pase con errores de compilación (es lo normal con 4,900 líneas escritas a ciegas) y corregirlos antes de tocar el PN64. Después, en el PN64, en este orden y parando en el primer fallo:

```powershell
fragforge tools verify          # esperar 6 + HashPlaceholder
fragforge probe monitorRefresh  # debe dar las Hz reales
fragforge mouse-test            # debe dar ~1000 Hz en un ratón de 1000 Hz
fragforge baseline --duration 120 --runs 3
```

Si `baseline` produce tres corridas con `p1Low` dentro del ±5 % entre sí, el piso de ruido por defecto es razonable para este equipo y se puede calibrar A/A con datos. Si no, el piso conservador ya está evitando que se declare una mejora inexistente.
