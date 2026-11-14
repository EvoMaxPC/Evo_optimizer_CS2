# FragForge — Capa 6: iGPU Intel UHD (diagnóstico y tweaks opt-in)

**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Base:** `b8a6087` (Capa 9)
**Alcance:** diagnóstico de la iGPU Intel UHD (driver 32.0.101.7092) y tres palancas opt-in. Nada de NVIDIA/AMD: esta PC tiene una sola GPU compartida con la RAM.

> **Nada compilado ni ejecutado.** El entorno no tiene `dotnet`, `mono`, `csc` ni PowerShell. Todo lo que sigue
> se auditó con `grep` real y se cita `archivo:línea`. Los valores de WMI y de `powercfg` **no se han medido en esta PC**.

---

## 6.1 Diagnóstico de solo lectura

### Memoria compartida (lo que faltaba)

`GpuInfo.SharedMemoryBudgetBytes` existía en el modelo (`src/FragForge.Core/Models/HardwareProfile.cs:76-85`) pero
**siempre se escribía `null`** (`src/FragForge.Actions/Platform/HardwareProfiler.cs:107`, antes del cambio).

Ahora se rellena desde el contador de memoria del adaptador, **no** desde `AdapterRAM`:

- `AdapterRAM` es un `uint32` de firmware: por encima de 4 GiB se desborda. El propio modelo ya lo advierte en
  `HardwareProfile.cs:84` (`MemoryNote`). No sirve para presupuesto de memoria compartida.
- Se consulta `GpuPerformanceProbe.MemoryClass` y se toma `CommittedBytes ?? SharedBytes`.
- Si el contador no existe, queda `null`. **`null` es "no se sabe", no cero** (R-L).

### Canales de RAM

`MemoryInfo.Channels` sigue en `MemoryChannelState.Unknown` (`HardwareProfiler.cs:100`): es correcto, WMI no ve el
entrelazado del controlador.

Lo que cambia es el texto de evidencia, que antes era una constante genérica y ahora se calcula con
`DescribeChannels(modules)` (`HardwareProfiler.cs:391`):

| Módulos | Frase |
|---|---|
| 0 | `Win32_PhysicalMemory no devolvió módulos; no se puede decir nada sobre los canales.` |
| 1 | `... 1 módulo: single-channel. No se puede confirmar por WMI.` |
| N iguales | `... N módulos iguales, dual-channel probable, no confirmable por WMI.` |
| N distintos | `... N módulos con capacidad o velocidad distinta: no se puede afirmar dual-channel; ...` |

La frase **nunca** dice "es dual-channel" ni "confirmado". Hay un test que lo prohíbe literalmente.

### HAGS

Ya estaba implementado y **no se tocó**:

- `src/FragForge.Actions/Diagnostics/DiagnosticProbes.cs:30` — ausente → `"Unknown/default"`, `2` → `Enabled`, `1` → `Disabled`, otro → `Unknown (valor)`.
- `src/FragForge.Actions/Platform/HardwareProfiler.cs:320-322` → `SecurityInfo.Hags`.

Coincide con lo pedido: `2` activo, `1` inactivo, cualquier otra cosa es `Unknown`, y **ausente no es "desactivado"**.

---

## 6.2 Contadores de la iGPU por WMI (R-F)

Nuevo `src/FragForge.Actions/Measurement/GpuPerformanceProbe.cs`.

| Clase | Uso | Filtro |
|---|---|---|
| `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine` | uso por motor | `EngineType` = `engtype_3D` / `3D` |
| `Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory` | memoria dedicada/compartida | — |

- Son `Win32_PerfFormattedData_*`, como exige R-F. WMI ya entrega el porcentaje formateado: **no se calculan tasas a mano** (eso exigiría dos muestreos e inventar el intervalo).
- El filtro acepta las dos formas conocidas (`3D` y `engtype_3D`). Es comparación de **token**, no de etiqueta localizada (R-C).
- Clase ausente o consulta que lanza → `FeatureState.Unknown` **y un aviso que nombra la clase**:
  `No se pudo consultar <clase>: <mensaje>. La métrica queda Unknown, no en cero.`
- Consulta que **no lanza pero devuelve 0 filas** → también `Unknown`. Sin filas no se sabe nada.
- El porcentaje se parsea con `CultureInfo.InvariantCulture` (R-E) y se descarta fuera de `[0,100]`.

**Solo métricas.** No hay ninguna frase de "cuello de botella" en el código: decidir si la iGPU limita un juego
requiere medir el juego, no leer un porcentaje aislado.

---

## 6.3 Tweaks (todos opt-in, evidencia baja)

### `gpu.hags-toggle` — implementado

`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers` → `HwSchMode` (DWord), valor **2** por defecto.

- **Solo escribe 1 o 2.** Hay un test que falla si el valor es otro.
- Requiere reinicio y administrador.
- `profiles: []` → no entra en ningún perfil; solo se aplica por id.
- Si el controlador no expone `HwSchMode`, el estado es **ausente** y eso **no** es "desactivado": el tweak es para A/B, no una recomendación.
- Para probar el otro lado se edita el JSON a `1`. No hay parámetro de usuario, así que no se inventa una UI.

### `gpu.intel-graphics-power-plan-max` — implementado, **no verificado**

`PowerPlan` en modo `settingsOnly` sobre el plan existente `FragForge Performance`:

- subgrupo `44f3beca-a7c0-460e-9df2-bb8b99e0cba6`
- ajuste `3619c3f2-afb2-4afc-b0e9-e7fef372de36`
- `acValue`/`dcValue` = **2** (máximo rendimiento)

Claves de la implementación:

- `skipUnenumeratedSettings: true` → se apoya en el mecanismo que ya existe (`src/FragForge.Actions/Power/PowerPlanAction.cs:344`, usado en `:50`, `:89`, `:125`, `:147`). Si `powercfg` no enumera el ajuste, **se salta y no se escribe nada** (R-L). No se inventó código nuevo para esto.
- Se escribe **por GUID**, nunca por la etiqueta `Intel(R) Graphics Settings` (R-C).
- `activate: false` → no cambia el plan activo, solo escribe el ajuste.
- Se comprobó que estos GUID **no estaban** en `src/`: ni Capa 3 ni `power.fragforge-performance` los cubren (`power.fragforge-performance` solo escribe `893dee8e-…` y `0cc5b647-…`). No hay duplicado.

> ⚠️ **No verificado en esta PC.** No se ha ejecutado `powercfg /qh`. Si los GUID no aparecen en el equipo, el tweak
> no escribe nada. Los tres valores (0/1/2) existen en el esquema de índices de energía de Windows; este tweak solo
> escribe el `2`.

### `DirectXUserGlobalSettings` / `SwapEffectUpgradeEnable` — **NO implementado**

Pedido en 6.3, **rechazado por un defecto real de la acción existente**:

- El valor es una cadena **separada por `;`** (`SwapEffectUpgradeEnable=1;`).
- La acción `AddToken` une y separa tokens **por espacio**:
  - escribe con `original + " " + token` → `src/FragForge.Actions/Registry/RegistryAction.cs:75`
  - comprueba con `value.Split(' ')` → `src/FragForge.Actions/Registry/RegistryAction.cs:143`

Con `;` el resultado sería una cadena corrupta y el `verify` no la reconocería. Hacerlo bien exige **cambiar la
acción** (un separador configurable) o escribir un `String` completo, que pisaría las demás claves del usuario
(prohibido por el propio encargo: "fusionar sin pisar"). **No se implementa** y queda documentado en
`docs/descartados.md`. Cambiar `RegistryAction` es un refactor fuera del alcance de esta ronda.

### Descartados explícitamente

| Candidato | Motivo |
|---|---|
| MPO / `OverlayTestMode` | Corrige parpadeos conocidos. **No aporta FPS.** Ya existe como sonda de solo lectura (`DiagnosticProbes.cs:31`). |
| Claves de clase `{4d36e968-…}` | Toca el driver de pantalla; R-J exige `RecoveryWriter` + preflight BitLocker y aquí no hay forma de dejarlo verificable. |
| Forzar pantalla / refresco | **Se lee, no se fuerza.** Riesgo de dejar la pantalla en negro y no hardcodear 200 Hz / 5 ms. |

---

## 6.4 Recomendación (solo texto, no escribe nada)

No se ha añadido código: son recomendaciones para el informe, sin prometer mejoras.

1. **Dual-channel.** Con 2 módulos iguales el texto dirá "probable, no confirmable". WMI no lo prueba. Si el usuario quiere certeza, hace falta una herramienta de topología de memoria; FragForge **no** lo afirma.
2. **Intel Graphics Software** (no lo instala ni lo toca FragForge): el usuario puede revisar por su cuenta V-Sync y el modo de latencia. **No hay evidencia** de que cambiarlos mejore el rendimiento en esta iGPU, y no se ha medido.
3. **HAGS.** Solo tiene sentido como A/B: aplicar, medir, revertir. No se recomienda por defecto.
4. **Presupuesto de memoria compartida.** Si el contador no existe en esta PC, el informe dirá `null`/Unknown. No se rellena con una estimación.

---

## Tabla de auditoría de identificadores

Todo verificado con `grep` en el repo; nada de memoria.

| Identificador | Archivo:línea | Uso |
|---|---|---|
| `GpuInfo.SharedMemoryBudgetBytes` | `src/FragForge.Core/Models/HardwareProfile.cs:76-85` | campo que se rellena |
| `GpuInfo.MemoryNote` | `src/FragForge.Core/Models/HardwareProfile.cs:84` | por qué `AdapterRAM` no sirve |
| `MemoryModuleInfo` | `src/FragForge.Core/Models/HardwareProfile.cs:64-74` | entrada de `DescribeChannels` |
| `MemoryInfo.ChannelEvidence` | `src/FragForge.Core/Models/HardwareProfile.cs:59` | texto que se sustituye |
| `MemoryChannelState` | `src/FragForge.Core/Models/Enums.cs:9` | se queda en `Unknown` |
| `FeatureState` | `src/FragForge.Core/Models/Enums.cs:8` | `Unknown` en el probe |
| `IWmiQuery.QueryAsync` | `src/FragForge.Core/Abstractions/IContracts.cs:19-22` | firma del doble |
| `WindowsHardwareProfiler` (ctor) | `src/FragForge.Actions/Platform/HardwareProfiler.cs:21` | **no se cambió** (R-A) |
| `WmiValues.UInt64/String/Int32` | `src/FragForge.Actions/Platform/HardwareProfiler.cs:11` | lectura de filas |
| `DescribeChannels` | `src/FragForge.Actions/Platform/HardwareProfiler.cs:391` | nuevo, `public static` |
| `HwSchMode` lectura | `src/FragForge.Actions/Diagnostics/DiagnosticProbes.cs:30` | ya existía |
| `HwSchMode` perfil | `src/FragForge.Actions/Platform/HardwareProfiler.cs:320-322` | ya existía |
| `SkipUnenumeratedSettings` | `src/FragForge.Actions/Power/PowerPlanAction.cs:344` | usado en `:50`, `:89`, `:125`, `:147` |
| `ActionNotApplicableException` | `src/FragForge.Actions/Power/PowerPlanAction.cs:43-53` | no enumerado → no aplica |
| `AddToken` separador espacio | `src/FragForge.Actions/Registry/RegistryAction.cs:75`, `:143` | por qué DirectX no se implementa |
| `SupportedKinds` | `src/FragForge.Actions/Registry/RegistryValueConverter.cs:9` | `Binary`/`String` son válidos |
| `GpuVendor` enum | `src/FragForge.Core/Models/Enums.cs:10` | condición `gpuVendor` |
| `GpuParameters` | `src/FragForge.Core/Conditions/ConditionFactory.cs:47` | `Vendor` es enum |
| `GpuVendorCondition` | `src/FragForge.Core/Conditions/ConditionFactory.cs:99-106` | evalúa `IsIntegrated` |
| `ConditionFactory.Types` | `src/FragForge.Core/Conditions/ConditionFactory.cs:11` | `gpuVendor` es válido |
| `EvidenceLevel` | `src/FragForge.Core/Models/Enums.cs:3` | `Low` en los dos tweaks |
| `ProtectedTargetException` | `src/FragForge.Core/Models/FragForgeException.cs:12` | control positivo del test |

**Nota sobre `InternalsVisibleTo`:** no existe en ningún `.cs`/`.csproj`/`.props` del repo (verificado). Por eso
`DescribeChannels` e `IsThreeDEngine`/`ParsePercent` se declararon `public` en vez de `internal`; si se hubieran
dejado `internal`, los tests no compilarían. Esto además cierra la duda histórica sobre `SteamCs2Resolver`: sus
métodos usados por `Cs2ResolutionTests` son `public` (`src/FragForge.Actions/Files/SteamCs2Resolver.cs:24`), así
que nunca hizo falta `InternalsVisibleTo`.

---

## Tests añadidos

| Archivo | Qué fija |
|---|---|
| `tests/FragForge.Tests/Gpu/GpuPerformanceProbeTests.cs` | clase ausente → `Unknown` + aviso que **nombra la clase**; 0 filas → `Unknown`; filtro 3D; porcentaje invariante; nombres de clase R-F |
| `tests/FragForge.Tests/Gpu/MemoryChannelDescriptionTests.cs` | "probable, no confirmable"; nunca "confirmado"; 1 módulo → single-channel; módulos distintos → no sugiere |

`GpuPerformanceProbeTests` usa un doble de `IWmiQuery` propio: `tests/FragForge.Tests/Fakes/FakePlatformServices.cs:75-78`
siempre devuelve `[]` y aquí hace falta devolver filas o lanzar.

---

## Qué NO se verificó

- **Nada compilado ni ejecutado.** Sin `dotnet`/`mono`/`csc`/PowerShell.
- **HAGS en esta GPU**: no se leyó `HwSchMode` en la PC real.
- **GUID de gráficos Intel**: no se ejecutó `powercfg /qh`; si no los expone, el tweak queda No aplicable.
- **Clases WMI de GPU**: no se comprobó que `GPUEngine`/`GPUAdapterMemory` existan en esta máquina. Si no existen, el resultado es `Unknown` con aviso, que es justo lo que se implementó.
- **Presupuesto de memoria compartida**: no se midió; queda `null` si el contador no existe.
- **Canales de RAM**: no se confirmó el entrelazado; por diseño no se afirma.
- **Efecto en FPS**: no medido. **No se promete ninguno.**
