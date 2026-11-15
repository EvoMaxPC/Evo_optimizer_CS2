# Etapa 0 — Línea base de PresentMon (solo lectura)

**HEAD auditado:** `caa9ece` · árbol limpio · sin `dotnet` en el sandbox.

---

## (a) Qué columnas lee hoy `PresentMonCsvParser` y cómo se llaman los candidatos

`src/FragForge.Actions/Measurement/PresentMonCsvParser.cs:21-24`

| Uso interno | Candidatos (en orden de búsqueda) | Línea |
|---|---|---|
| FrameTime → `FrameTimesMs` | `FrameTime`, `MsBetweenPresents`, `MsBetweenDisplayChange`, `msBetweenPresents`, `frameTime` | `:21` |
| CPU → `CpuBusyMs` | `CPUBusy`, `MsCPUBusy`, `msInPresentAPI`, `CPUUsage` | `:22` |
| GPU → `GpuBusyMs` | `GPUBusy`, `MsGPUBusy`, `GPUTime`, `GpuTime` | `:23` |
| Modo → `PresentModes` | `PresentMode`, `PresentModeName`, `SyncInterval` | `:24` |

La localización es **por nombre**, `OrdinalIgnoreCase` (`FindColumn`, `:78-84`). Eso ya cumple R-C.

**Defectos frente al contrato real 2.6.0:**

1. `MsInPresentAPI` está como candidato de **CPU** (`:22`). Con el CSV real, `CPUBusy`/`MsCPUBusy` no existen, así que el parser cae en `msInPresentAPI` y reporta *tiempo dentro de la API de present* como si fuera CPU ocupada. **Es exactamente el error que el contrato manda corregir.**
2. `MsBetweenDisplayChange` aparece como candidato de **frametime** (`:21`) y en 2.6.0 viene `NA`. Si `MsBetweenPresents` faltara, el parser tomaría una columna vacía como frametime.
3. `SyncInterval` como candidato de **PresentMode** (`:24`) es un entero, no un modo. Con el CSV real `PresentMode` existe, así que no se dispara hoy, pero es un fallback engañoso.
4. No se leen: `MsCPUWait`, `MsGPUTime`, `MsUntilDisplayed`, `MsInPresentAPI` (como métrica propia), `Application`, `ProcessID`, `SwapChainAddress`, `TimeInMs`, `AllowsTearing`.

---

## (b) Cómo trata "NA", vacíos y filas cortas

- **"NA"**: `TryParseDouble` (`:112-113`) usa `double.TryParse` con `NumberStyles.Float` + `InvariantCulture`. `"NA"` no parsea → devuelve `false`. En frametime ⇒ **la fila se descarta** (`:61`). En CPU/GPU ⇒ **null** (`:63-64`, por el `TryParseDouble(...) && ...`). Correcto por accidente: no hay tratamiento explícito de "NA", funciona porque no es un número.
- **Vacíos**: mismo camino que "NA" (no parsean). En frametime descarta la fila; en CPU/GPU ⇒ null.
- **Filas con menos columnas**: `if (cells.Length <= frameTimeIndex) continue;` (`:60`) descarta la fila. Para columnas opcionales hay guardas `index < cells.Length` (`:63-65`). Correcto.
- **Filas con más columnas**: no se detecta. Se ignoran las extra en silencio.
- **Comillas**: `SplitCsvLine` (`:87-110`) soporta comas y comillas escapadas dentro de campo. Correcto.
- **CRLF**: `reader.ReadLine()` lo absorbe. Correcto.

**Carencia:** no hay distinción entre "columna ausente" y "celda NA" en `SourceColumns`; el resultado no dice si GPU vino vacía por falta de columna o por NA.

---

## (c) Dónde se arma el comando de captura y cómo se localiza el exe

**Comando:** `src/FragForge.Actions/Measurement/PresentMonCapture.cs:119-127`

```
--process_name <ProcessName>.exe
--output_file <csvPath>
--session_name FragForge-<runId>
--timed <segundos>
--terminate_after_timed
--no_console_stats
```

Se usa `List<string>` → `ProcessStartInfo.ArgumentList` (`:45`). No hay concatenación de cadena: cumple R-M y el test `NoProductCodeBuildsCommandLineStrings`.

**Localización del exe:** `ToolVerifier.Verify` (`ToolManifest.cs:43-55`) busca `Path.Combine(toolsDirectory, tool.FileName)` donde `tool.FileName = "PresentMon.exe"` (allowlist embebida) y `toolsDirectory = FragForgePaths.Tools` = `%ProgramData%\FragForge\tools`. **Nunca usa PATH.** Cumple el "nunca PATH", pero **no** busca en `%ProgramFiles%\Intel\PresentMon\...` y exigía un hash en la allowlist, que estaba vacío en esta auditoría ⇒ `HashPlaceholder` ⇒ la captura no se habilitaba. Actualización L2d: la allowlist incluye el hash aportado por el usuario para PresentMon 2.6.0 x64.

**Faltan del contrato:** `--stop_existing_session` y la búsqueda en `%ProgramFiles%`. Y `process_name` recibe `request.ProcessName + ".exe"` — si `ProcessName` ya trae extensión, se duplica.

---

## (d) Cómo se decide que una captura salió bien (hoy)

`PresentMonCapture.cs:141-158`

- **Exit code:** se lee (`result.ExitCode`) pero **solo se usa para el mensaje** cuando no hay CSV (`:147`). No decide éxito. ✅ coincide con el contrato.
- **Texto de stdout:** no se inspecciona. ✅
- **Archivo:** `if (!File.Exists(csvPath))` ⇒ serie con mensaje `sin CSV` (`:147`). Ese es el criterio principal.
- Luego `PresentMonCsvParser.ParseFile` (`:148`).
- `FrameCount < request.MinimumFrames` (default 300) ⇒ añade `;frames=N<min300` a `SourceColumns` (`:157`). **No marca `IsPartial` ni invalida nada**: solo escribe una nota. Es un aviso silencioso, no una puerta.
- `result.TimedOut || result.Killed` ⇒ `IsPartial = true` (`:142-146`). ✅

**No existe** hoy: validación de encabezado obligatorio, umbral de ≥1 fila como criterio duro, ni un campo de validez tipado.

---

## (e) Qué usa `CsvParserTests` como `SourceColumns`

`tests/FragForge.Tests/Measurement/CsvParserTests.cs`

| Línea | Aserción |
|---|---|
| `:32` | `Assert.Contains("MsBetweenPresents", series.SourceColumns)` — fixture **V1** |
| `:33` | `Assert.Contains("MsInPresentAPI", series.SourceColumns)` — **la aserción a cambiar**: hoy valida que CPU se leyó de `MsInPresentAPI` |
| `:66` | `Assert.StartsWith("missing-frame-time", ...)` |
| `:82` | `Assert.Equal("empty", ...)` |

Y `PresentMonCaptureTests.cs:116` usa `Assert.Contains("frames=2<min300", series.SourceColumns)`.

Las fixtures actuales (`V1Csv` `:13-18`, `V2Csv` `:20-24`) son **sintéticas** (`:8-9` lo dice). Usan `FrameTime`/`CPUBusy`/`GPUBusy`, que **no** son los nombres de 2.6.0 (`MsBetweenPresents`/`MsCPUBusy`/`MsGPUBusy`). El test `ParsesV2Columns_ByColumnName_NotPosition` (`:37-44`) prueba la ruta 2.x **con nombres equivocados para 2.6.0**.

---

## Lo que NO existe hoy

| Elemento pedido | Estado |
|---|---|
| Lectura de `MsCPUBusy`/`MsCPUWait`/`MsGPUTime`/`MsUntilDisplayed`/`MsInPresentAPI` como métricas propias | **no existe** |
| `Application`, `ProcessID`, `SwapChainAddress`, `TimeInMs`, `AllowTearing` expuestos | **no existe** |
| Detección de múltiples swapchains | **no existe** |
| Marcado "GPU no disponible" | **no existe** |
| Búsqueda en `%ProgramFiles%\Intel\PresentMon\...` | **no existe** |
| Obtención de versión desde la 1ª línea de `--help` | **no existe** |
| Gate "encabezado obligatorio + ≥1 fila" como criterio de éxito | **no existe** |
| Preflight de elevación para la captura | existe en `PreflightService:19` (`admin` blocking), pero **no** lo consulta `PresentMonFrameCapture` |
| Puerta de validez `Valid/Suspect/Invalid` | **no existe** |
| `RunResult` con campo de validez tipado | **no existe** (solo `IsValid` bool + `InvalidReason` string) |

---

## CONFLICTO BLOQUEANTE detectado antes de escribir código

**`stop_existing_session` está explícitamente prohibido por un test de higiene.**

`tests/FragForge.Tests/Experiments/SourceHygieneAndAnalysisTests.cs:47-56`

```csharp
public void PresentMonIsNeverInvokedWithStopExistingSession()
{
    // falla si CUALQUIER .cs de src/ contiene el literal "stop_existing_session"
}
```

Y `PresentMonCapture.cs:84` lo documenta como decisión deliberada: *"NO se usa la opción de detener sesión existente: mataría sesiones ETW de otras herramientas."*

El contrato de esta ronda pide añadir `--stop_existing_session`. Eso:
1. Rompe ese test (404/404 → fallo).
2. Choca con R-M (nunca matar lo ajeno sin confirmación).

**Además:** `--stop_existing_session` opera sobre `--session_name`. Como FragForge usa un nombre único por corrida (`FragForge-<runId>`), la opción **no tendría efecto útil**: no hay sesión previa que detener con ese nombre.

**Propuesta:** mantener la opción fuera y anotarlo en `docs/descartados.md`. Si aun así se quiere, hay que cambiar primero el test de higiene a propósito, y eso es una decisión tuya, no mía.
