# FragForge — Capa 9: modo sesión

**Fecha:** 2026-11-13
**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Estado:** código y tests **escritos y commiteados**; **NADA compilado ni ejecutado**.

---

## 0. Advertencia previa

Sin SDK de .NET en el entorno: no hay `dotnet`, no hay Windows y no se puede ejecutar ningún test. Todo lo
que sigue describe *lo que el código hace*, no *lo que se observó*.

Lo que sí se comprobó:

- Cada identificador externo, con grep real y **archivo:línea** (§5).
- Todos los archivos C# pasan tree-sitter (**sintaxis**, no compilación).
- El catálogo real se cargó con su schema y se verificó qué tweaks quedan marcados elegibles.
- Los cambios en JSON se compararon contra `git show HEAD:` para demostrar que **solo** se añadió lo
  declarado (sin claves perdidas ni valores cambiados).

---

## 1. Modelo: `sessionEligible` (9.1)

Campo **aditivo** y opcional en `TweakDefinition` (`src/FragForge.Core/Models/TweakDefinition.cs:45`),
`false` por defecto. Añadido también al schema
(`src/FragForge.Tweaks/Schema/tweak.schema.json`, propiedad `sessionEligible`), que tenía
`additionalProperties: false`: **sin este cambio el catálogo no habría cargado**.

### Auditoría: qué tweaks califican y por qué

Criterio: efecto **inmediato** (sin reinicio), **totalmente reversible** y sin dejar el equipo en un estado
que necesite reinicio para arreglarse.

| Tweak | JSON | ¿Califica? | Motivo |
|---|---|---|---|
| `power.fragforge-performance` | `Catalog/Power/power.fragforge-performance.json:59` | **Sí** | Plan de energía: efecto inmediato, `requiresReboot: false`, reversible por valores |
| `cpu.epp-performance` | `Catalog/Cpu/cpu.epp-performance.json:68` | **Sí** | EPP del procesador: inmediato, `requiresReboot: false`, reversible |
| `cpu.core-parking-off` | `Catalog/Cpu/cpu.core-parking-off.json:56` | **Sí** | Core parking: inmediato, `requiresReboot: false`, reversible |
| `cs2.cpu-priority-high` | — | **No** | IFEO solo aplica **al crear el proceso**: si CS2 ya corre, no surte efecto hasta reiniciarlo, y no se puede revertir "al cerrar el juego" de forma limpia |
| `latency.game-mode-on`, `latency.mouse-acceleration-off`, `latency.global-timer-resolution`, `cpu.power-throttling-off`, `latency.game-dvr-capture-off` | — | **No** | `requiresReboot: true`: no se pueden revertir al cerrar el juego |
| `cs2.fullscreen-optimizations-off` | — | **No** | Exige reiniciar el juego y es `Experimental` |
| `debloat.*` (servicios y tareas) | — | **No** | No son de rendimiento en partida y tocan servicios del sistema |
| `network.i225v-eee-off`, `power.pcie-aspm-off`, `power.usb-selective-suspend-off` | — | **No** | Asociado a hardware/energía con efecto diferido; no hay certeza de efecto inmediato |

**Segunda barrera en código:** `GameSessionService.EligibleIds`
(`src/FragForge.Core/Sessions/GameSessionService.cs:54`) filtra además `!d.RequiresReboot`. Aunque un JSON
marcara por error `sessionEligible: true` en un tweak que necesita reinicio, **queda fuera**. Hay un test
para eso (`EligibleIds_TakesOnlySessionEligibleTweaksWithoutReboot`).

---

## 2. `GameSessionService` (9.2)

`src/FragForge.Core/Sessions/GameSessionService.cs:27`. Vive en **Core**, sin ninguna referencia a WPF.

| Pieza | Archivo:línea | Qué hace |
|---|---|---|
| `StartAsync(profile)` | `GameSessionService.cs:68` | Aplica los elegibles por `ITweakEngine.ApplyAsync` **sin punto de restauración** y escribe el marcador |
| `StopAsync()` | `:122` | Revierte con `RevertAsync` **por el snapshotId** del marcador y borra el marcador |
| `StatusAsync()` | `:165` | Estado + si CS2 corre |
| `WatchAsync(probe, delay, token)` | `:233` | Polling cada 2 s con `Cs2Process`; revierte cuando el juego **estuvo abierto y ya no está** |
| `RecoverAsync()` | `:201` | Recuperación al arrancar (9.3) |
| Marcador | `GameSession.cs:33` | `operationId`, `snapshotId`, `tweakIds`, `startedAt`, `profile`, `reverted` |

Decisiones que importan:

- **Revert por snapshot, no por ids.** Se usa `RevertRequest { SnapshotId = ... }`, que es el mismo camino
  que cualquier otra reversión del motor. Una segunda implementación habría podido divergir.
- **Marcador atómico** (`AtomicFile`, `src/FragForge.Core/Persistence/AtomicFile.cs:5`): se escribe a un
  temporal y se mueve. Una caída a mitad de escritura no deja un JSON truncado que impida recuperar.
- **El marcador es la memoria que sobrevive a la caída.** Sin él, "revertir al cerrar el juego" solo
  funcionaría mientras el proceso vive.
- **Polling, no eventos ni WMI.** No abre handles a cs2.exe (compatible con el anticheat) y no puede
  quedarse colgado esperando a un proceso protegido.
- **`CancellationToken` en todo.** `WatchAsync` lo comprueba al principio de cada vuelta, y la UI cancela
  al apagar el interruptor y en `Dispose` (`MainViewModel.cs:353`). No hay hilos que sobrevivan.
- **Archivo corrupto → no se borra.** `ReadMarkerAsync` (`GameSessionService.cs:281`) lanza `InvalidDataException` con la ruta:
  borrar a ciegas podría perder un revert pendiente.

### Escritura del marcador: `%ProgramData%\FragForge\session.json`

`GameSessionPaths.MarkerFile` (`GameSession.cs:97`) lo calcula sobre `FragForgePaths`
(`src/FragForge.Core/Persistence/FragForgePaths.cs:20`, que por defecto es
`%ProgramData%\FragForge`). Se usa `FragForgePaths` y no una ruta fija para que `--data-root` y los tests
funcionen sin tocar `%ProgramData%`.

---

## 3. Recuperación (9.3)

`RecoverAsync` (`GameSessionService.cs:201`):

- Sin marcador → nada que hacer (`AlreadyReverted`), idempotente.
- Marcador con `reverted: true` → solo se limpia. **No se revierte dos veces.**
- CS2 corriendo → **no se toca nada**: la vigilancia se retoma aparte.
- CS2 no corre → revierte y limpia.
- **No se pudo comprobar si CS2 corre (`null`)** → **no se revierte**. Revertir con el juego abierto sería
  una sorpresa en mitad de una partida; se informa y se conserva el marcador.

Se llama al arrancar la App (`src/FragForge.App/App.xaml.cs`, tras resolver el `MainViewModel`) y está
disponible para el CLI.

---

## 4. CLI y UI (9.4)

### CLI: `fragforge session start|stop|status`

`src/FragForge.Cli/SessionCommands.cs`, despachado en `src/FragForge.Cli/Program.cs:131`.

- `start [--profile X]` → aplica y escribe el marcador. Sale con `Preflight` si no se pudo.
- `stop` → revierte y limpia; con `Failure` si el revert **no** se completó (el marcador se conserva).
- `status` → estado, operación, snapshot, perfil, número de tweaks y si CS2 corre
  (`sí` / `no` / `desconocido`).

**No hay verbo de vigilancia en el CLI**: el vigilante tiene que sobrevivir al comando, y un proceso de
consola esperando no es la forma. El CLI deja la sesión preparada y la recuperación del siguiente arranque
la cierra; la vigilancia continua es de la App, que sí vive mientras se juega.

### UI: interruptor con estado visible

- `MainWindow.xaml:33-36` — `ToggleButton` con `Content="{Binding SessionStateText}"` y
  `Command="{Binding ToggleSessionCommand}"`. **No se tocaron los combos** ni `App.xaml`.
- `MainViewModel.cs:124` — `[ObservableProperty] private GameSessionState sessionState`. El texto se
  recalcula al cambiar el estado (`:126`) y sale del localizador.
- Estados visibles: **apagado / esperando CS2 / activo / revirtiendo** (`session.state.*`).
- La vigilancia corre en segundo plano y actualiza el estado por `PostToUi`, que ya existía.
- Si `Sessions` es `null` (diseño, tests), el interruptor no rompe nada: muestra
  `session.unavailable`.

**Por qué el `Sessions` se inyecta por propiedad y no por constructor:** el constructor de `MainViewModel`
es una lista larga de dependencias y tocarlo habría cambiado todos los llamadores. Se asigna desde
`App.xaml.cs`. Es una concesión consciente: menos limpia que el constructor, pero fuera de alcance cambiarlo
en esta ronda.

### Textos (es-MX y en-US)

Añadidos **7 pares** a `src/FragForge.Tweaks/Localization/es-MX.json` y `en-US.json`:
`session.state.inactive`, `session.state.waiting`, `session.state.active`, `session.state.reverting`,
`session.stopped`, `session.unavailable`, `session.tooltip`. Ambas culturas quedan con **57 claves, el
mismo conjunto exacto** (comprobado).

---

## 5. Auditoría de identificadores (archivo:línea)

Todos localizados con grep real, ninguno de memoria.

| Identificador | Definido en | Usado en |
|---|---|---|
| `ICs2Resolver` | `src/FragForge.Core/Abstractions/IContracts.cs:104` | `SteamCs2Resolver.cs:24`, `Cs2Inspector.cs:31` |
| `Cs2Resolution` | `src/FragForge.Core/Models/Cs2Resolution.cs:10` | `SteamCs2Resolver`, `Cs2Inspector` |
| `Cs2Inspection` | `src/FragForge.Core/Models/Cs2Inspection.cs:10` | `Cs2Inspector`, `Cs2Commands` |
| `Cs2ConfigParser` | `src/FragForge.Core/Models/Cs2ConfigParser.cs:20` | `Cs2Inspector` |
| `Cs2Inspector` | `src/FragForge.Actions/Files/Cs2Inspector.cs:31` | `Cs2Commands.cs`, `CompositionRoot.cs:71` |
| `SteamCs2Resolver` | `src/FragForge.Actions/Files/SteamCs2Resolver.cs:24` | `CompositionRoot.cs:69` |
| `ValveDataFormat` | `src/FragForge.Actions/Files/ValveDataFormat.cs:11` | `SteamCs2Resolver.ParseLibraryFolders` |
| `VdfNode` | `src/FragForge.Actions/Files/ValveDataFormat.cs:5` | idem |
| `IRegistry` | `src/FragForge.Core/Abstractions/IRegistry.cs:3` | `SteamCs2Resolver(.ctor)`, `Cs2Inspector` |
| `RegistryReadResult` | `src/FragForge.Core/Abstractions/IRegistry.cs:13` | `ReadActiveAccountAsync` |
| `RegistryPath.ResolveUserHive` | `src/FragForge.Core/Persistence/RegistryPath.cs:30` | cadena HKCU de `RegistryAction` (tweak de 8.4, ya existente) |
| `Cs2Process` | `src/FragForge.Core/Engine/Cs2Process.cs:32` | `GameSessionService.cs` (`WatchAsync`/`RecoverAsync`) |
| `GameSessionService` | `src/FragForge.Core/Sessions/GameSessionService.cs:27` | `SessionCommands.cs`, `CompositionRoot.cs:73`, `MainViewModel` |
| `GameSessionMarker` | `src/FragForge.Core/Sessions/GameSession.cs:33` | `GameSessionService` |
| `GameSessionPaths` | `src/FragForge.Core/Sessions/GameSession.cs:94` | `GameSessionService`, tests |
| `GameSessionState` | `src/FragForge.Core/Sessions/GameSession.cs:8` | `MainViewModel`, `SessionCommands` |
| `GameSessionStatus` / `StartResult` / `StopResult` | `GameSession.cs:63` / `:74` / `:82` | `GameSessionService`, CLI, tests |
| `AtomicFile.WriteAllTextAsync` | `src/FragForge.Core/Persistence/AtomicFile.cs:5` | `GameSessionService.WriteMarkerAsync` |
| `FragForgePaths.Root` | `src/FragForge.Core/Persistence/FragForgePaths.cs:5` | `GameSessionPaths.MarkerFile` |
| `ITweakEngine.ApplyAsync` | `src/FragForge.Core/Abstractions/IContracts.cs:33` | `GameSessionService.StartAsync` |
| `ITweakEngine.RevertAsync` | `src/FragForge.Core/Abstractions/IContracts.cs:34` | `GameSessionService.StopAsync` |
| `ITweakCatalog.Definitions` | `src/FragForge.Core/Abstractions/IContracts.cs:25` | `EligibleIds` |
| `ProfileSelector.Select` | `src/FragForge.Core/Engine/ProfileSelector.cs:34` | `EligibleIds` |
| `ApplyOptions.CreateRestorePoint` | `src/FragForge.Core/Models/Results.cs:25` | `StartAsync` (se pone `false`) |
| `RevertRequest.SnapshotId` | `src/FragForge.Core/Models/Results.cs:45` | `StopAsync` |
| `BatchReport.SnapshotId` / `.OperationId` / `.Success` / `.Errors` | `src/FragForge.Core/Models/Results.cs:66/65/68/73` | `StartAsync` / `StopAsync` |
| `TweakDefinition.SessionEligible` | `src/FragForge.Core/Models/TweakDefinition.cs:45` | `EligibleIds`, tests |
| `FileSnapshotStore` (ruta `<snapshots>\<id>\snapshot.json`) | `src/FragForge.Core/Persistence/FileSnapshotStore.cs:17,24` | test del revert parcial |
| `Process.GetProcesses` | uso previo en `src/FragForge.Actions/Measurement/EnvironmentProbe.cs:24` | patrón copiado en `Cs2Process.IsRunningOnThisMachine` |
| `ExitCodes.Preflight` / `.Failure` / `.Success` | `src/FragForge.Cli/CommandLine.cs` | `SessionCommands` |
| `JsonDefaults.Options` | `src/FragForge.Core/Logging/JsonDefaults.cs:6` | `SessionCommands` (`--json`) |

**R-A cumplido:** `TweakEngine` no se tocó (`git diff` vacío) y en `IContracts.cs` **solo** se añadió
`ICs2Resolver`; ninguna firma existente cambió.

---

## 6. Tests (9.6)

`tests/FragForge.Tests/Sessions/GameSessionServiceTests.cs`. Motor y snapshots **reales**; acciones falsas
(`FakeActionFactory`), así que el ciclo apply → revert es el de verdad pero no toca el sistema. El vigilante
recibe sondeo y retraso inyectados: no se esperan 2 s reales.

| Caso exigido | Test |
|---|---|
| Ciclo Start → CS2 aparece → CS2 se cierra → revert | `Watch_RevertsWhenTheGameAppearsAndThenCloses` |
| App cae con marcador → recuperación | `Recover_AfterACrashWithTheGameClosed_RevertsAndCleansUp` |
| Revert falla parcialmente → se reporta, el marcador **no** se borra | `Stop_WhenTheRevertFails_KeepsTheMarkerAndReports` |
| Doble Start | `StartTwice_DoesNotStackASecondSession` |
| Stop sin sesión | `Stop_WithoutASession_DoesNotFail` |
| (extra) sin snapshot no se inventa un revert | `Stop_WithoutASnapshot_DoesNotInventAReversion` |
| (extra) nunca revierte si el juego no se vio | `Watch_DoesNotRevertWhileTheGameWasNeverRunning` |
| (extra) sondeo desconocido → no revierte | `Watch_WithUnknownProbe_DoesNotRevert` |
| (extra) recuperación idempotente | `Recover_IsIdempotent_WhenThereIsNoMarker` |
| (extra) marcador ya revertido → solo limpia | `Recover_WithAMarkerAlreadyReverted_OnlyCleansUp_WithoutRevertingAgain` |
| (extra) catálogo real: exactamente 3 elegibles, ninguno con reinicio | `RealCatalog_HasExactlyTheThreeImmediateReversibleTweaksMarkedEligible` |

El test del catálogo real **no afirma tamaños exactos del catálogo** (R-N): afirma **qué ids** están
marcados, que es una propiedad del cambio, no del tamaño.

---

## 7. Qué NO se verificó

- **Nada fue compilado ni ejecutado.** No hay `dotnet`. Los 11 tests de sesión están escritos, no pasados.
- **No se probó el modo sesión contra CS2 real.** No se comprobó que el polling detecte el proceso de
  verdad, ni el comportamiento al cerrar el juego, ni qué pasa si CS2 se reinicia.
- **No se verificó la caída real de la app.** La recuperación se prueba con un marcador en disco y un
  servicio nuevo, no matando el proceso de verdad.
- **No se comprobó que revertir por snapshotId funcione contra un snapshot real** de una sesión real: los
  snapshots de los tests vienen de acciones falsas.
- **IFEO y AppCompat siguen sin probarse en Windows real** (venían de la capa anterior).
- **No se ejecutó la UI.** No se vio el interruptor, ni que el texto cambie de estado, ni que el hilo de
  fondo actualice la UI sin excepción. El `ToggleButton` se deja **sin** `IsChecked` enlazado a propósito:
  enlazarlo al enum necesitaría un converter que no existe en el repo, y añadirlo sin poder compilarlo
  habría sido peor que no tenerlo.
- **No se verificó el comportamiento si `session.json` existe pero el snapshot fue borrado a mano.**
  `StopAsync` lo reportaría como fallo de revert y conservaría el marcador, pero no se observó.
- **No se midió ningún efecto de rendimiento** de los tres tweaks elegibles. `EvidenceLevel` sigue siendo el
  que ya tenían; esta capa no cambia ninguno de esos valores (R-K).
