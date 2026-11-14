# Capa 11 — Informe: UI, instalador, firma, CI, E2E y deuda técnica

**Versión** 0.1.0 · **Rama** `arena/c1a5f702-evo-optimizer-cs2`

## ⚠️ Lo primero: qué NO se ha verificado

**En el entorno donde se escribió esta capa no hay `dotnet`, ni `mono`, ni `csc`, ni PowerShell, ni Inno Setup,
ni `signtool`, ni certificado.** Por lo tanto:

| Elemento | Verificado |
|---|---|
| Código compilable | **No.** Cero compilaciones. |
| Tests | **No ejecutados.** Existen y están escritos; no se sabe si pasan. |
| Instalador Inno Setup | **No compilado, no ejecutado.** |
| `publish.ps1` / `sign.ps1` | **Nunca ejecutados.** No hay PowerShell. |
| Firma de código | **Nunca ejecutada.** No hay certificado. |
| UI WPF | **Nunca abierta.** No hay Windows. |
| CI | **Nunca ejecutado.** El workflow está escrito; no hay evidencia de que corra verde. |

Lo que **sí** se hizo para compensar: cada identificador externo se auditó con `grep` contra el repositorio
(tabla de §6), y cada fichero `.cs` tocado pasó un chequeo de sintaxis con tree-sitter. Eso detecta errores de
forma, **no** errores de tipos ni de semántica.

---

## 1. Commits de la ronda

| Bloque | Hash | Contenido |
|---|---|---|
| `chore(deuda)` | `9d92f96` | 11.1: título desde assembly, CA1416, xUnit2031/2029/2000, CS0414, `CompositionOverrides` |
| `refactor(ui-core)` | `85088bb` | 11.2: ViewModels a `FragForge.UI.Core` (`net10.0`, sin WPF) + tests |
| `feat(ui)` | `a04893f` | 11.3: pestañas por capa, vista previa, revertir por operación |
| `feat(installer)` | `d3c6437` | 11.4/11.5/11.6: elevación documentada, `publish.ps1`, `.iss`, `sign.ps1`, `docs/firma.md` |
| `ci` | `41f2dcd` | 11.7: workflow con `-warnaserror`, publish solo en tags |
| `docs` | `7059d01` | 11.8/11.9: E2E, README, CHANGELOG, descartados, informe |

`9d92f96` es el HEAD de entrada; los otros cinco son de esta ronda.

---

## 2. 11.1 — Deuda técnica (`9d92f96`)

| Objetivo | Cómo quedó |
|---|---|
| Título con nombre + versión | `AssemblyInfoText.WindowTitle()` → `"FragForge 0.1.0"`, sin `+hash`. `VersionWithCommit()` lo conserva para quien lo quiera. |
| CA1416 | `[SupportedOSPlatform("windows")]` a nivel de clase añadido en **5** clases (`ArchitectureHotfixTests`, `TpmAndNetworkHotfixTests`, `StartupInventorySourceTests`, `TaskInventorySourceTests`, `ServiceInventoryTests`). Hay **8** clases de test con el atributo en total: las otras 3 (`NetAdapterPropertiesTests`, `ScheduledTaskScriptParsesTests`, `ScheduledTaskStoreDetectionTests`) ya lo tenían de capas anteriores; se comprobó con `grep -rln` antes de escribir el informe. **`AtlasProfileTests` no lo necesita**: es un modelo puro. |
| xUnit2031 | 6 sitios → `Assert.Single(col, pred)` (`CsvParserTests` ×4, `PrivilegeDegradationTests` ×2). |
| xUnit2029 | 2 sitios → `Assert.DoesNotContain` (`CsvParserTests`, `ExternalCatalogResilienceTests`). |
| xUnit2000 | 2 sitios en `Cs2ResolutionTests` → `Assert.True/False`. |
| CS0414 | Campo muerto eliminado (`SettingsJson` en `ActionTests.cs`). |
| Punto de inyección | `CompositionOverrides` (record `init`) como 3.er parámetro **opcional** de `CompositionRoot.BuildAsync`. Los 14 llamadores siguen compilando sin cambios. **R-A respetado**: el ctor de `TweakEngine` y las firmas de `Abstractions` no se tocaron. |
| Test de composición hermético | `ScanAndDryRun_WorkWithInjectedDoubles_WithoutTouchingTheRealMachine`, con dobles en las 6 dependencias. Los otros 4 tests de composición llevan `Trait("Category","Windows")`. |
| Test nuevo | `AssemblyInfoTextTests` (4 casos): el título empieza por el nombre del producto, no contiene "Capa"/"Layer", la versión sale del ensamblado de `TweakCatalog`, y el sufijo `+` no aparece. |

**Objetivo 0 warnings**: no verificable sin compilador. El CI lo intenta con `-warnaserror`.

**xUnit1031 / xUnit2013**: se investigó y **no había ningún caso real**. `launcher.Result` es una propiedad de
un doble, no `Task<T>.Result`; `Assert.Equal(n, coleccion.Count)` compara contra colecciones. No se tocó nada:
cambiarlo habría cambiado lo que prueban.

---

## 3. 11.2 — UI testeable (`85088bb`)

**El problema**: los ViewModels vivían en `FragForge.App` (`net10.0-windows`, `UseWPF`). El proyecto de tests es
`net10.0`. Consecuencia: **la UI no tenía ni un solo test**. Las rondas anteriores lo habían parcheado a medias
sacando `TweakCountFormatter` y `ScanStatusText` a Core; aquí se termina.

**La solución**: `src/FragForge.UI.Core`, `net10.0`, **sin `UseWPF`**.

| Fichero | Qué |
|---|---|
| `src/FragForge.UI.Core/MainViewModel.cs` | movido con `git mv` (historial intacto), namespace `FragForge.UI.Core` |
| `src/FragForge.UI.Core/TweakItemViewModel.cs` | idem |
| `src/FragForge.UI.Core/Services/UiServices.cs` | `IConfirmationService`, `IFileDialogService` (las **interfaces**) |
| `src/FragForge.App/Services/DialogServices.cs` | solo las **implementaciones** WPF |

**Tests** (`tests/FragForge.Tests/UI/MainViewModelTests.cs`, 6 casos): conteo por perfil sin fijar el tamaño
exacto del catálogo (R-N), monotonía al subir de perfil, textos en es-MX y en-US, notificaciones de
`PropertyChanged` con un espía, aviso anticheat por fila, y título desde el ensamblado.

> **Decisión de diseño registrada**: `Tweaks` **no** dispara `PropertyChanged`. Es una `ObservableCollection` y
> la UI se entera por `CollectionChanged`. Un test que exigiera `PropertyChanged("Tweaks")` habría fijado una
> notificación decorativa; en su lugar se comprueba que el **contenido** cambió.

---

## 4. 11.3 — UI (`a04893f`)

**Se auditó antes de escribir.** Lo que ya existía: una sola pantalla con todo apilado, comandos para
scan/dry-run/apply/revert/snapshot/export/doctor/seguridad/sesión, y el estilo de combos ya resuelto en
`App.xaml`. Lo que **faltaba** de verdad:

| Requisito del enunciado | Estado previo | Qué se hizo |
|---|---|---|
| Pestañas por capa | No | 8 pestañas enlazadas a comandos existentes. **Sin lógica nueva en el XAML.** |
| Dry-run con antes/después | El motor ya devolvía `TweakApplyResult.PlannedChanges`; **la UI no lo mostraba** | `PreviewCommand` + `PlannedChanges` + `PreviewSummary`. El texto dice explícitamente que no se ha modificado nada. |
| Botón Revertir **por operación** | Solo "último" o "los seleccionados" | `Operations` desde `ISnapshotStore.ListAsync` + `RevertOperationCommand`, que pasa el `SnapshotId` concreto |
| Aviso de reinicio visible | `RequiresReboot` existía, con una columna `True/False` colapsada | `RebootDisplay` con texto |
| Riesgo/evidencia por tweak | Ya estaban en la tabla | Se conservan; el aviso de riesgo ya estaba en el flujo de aplicar |
| Localización es-MX/en-US | Sin claves para la UI nueva | 29 claves nuevas en **ambos** ficheros (86 en cada uno; se comprobó que los conjuntos son idénticos) |
| Accesibilidad | Nada | `AutomationProperties.Name` en combos, tabla, registro y paneles; `KeyboardNavigation` en barra y pestañas |
| Sin telemetría | Ya | Sin cambios |

**Unificación**: `BuildApplyOptions` construye las opciones en un solo sitio para el dry-run de la vista previa
y para el apply real, de forma que no puedan divergir.

**Tests nuevos** (`tests/FragForge.Tests/UI/ApplyFlowTests.cs`, 5 casos): la vista previa pide un dry-run y no
aplica; sin selección avisa y no llama al motor; revertir por operación pasa el snapshot **concreto**; y si el
usuario rechaza la confirmación no se revierte nada.

---

## 5. 11.4 — Elevación (`docs/elevacion.md`)

**Decisión: `requireAdministrator`.** No se implementó nada: **ambos manifiestos ya lo pedían** antes de esta
ronda. Lo que faltaba era justificarlo por escrito.

Justificación con archivo:línea (en `docs/elevacion.md`):

| Necesita admin | Dónde |
|---|---|
| `Win32_Tpm` en `root\CIMV2\Security\MicrosoftTpm` | `src/FragForge.Actions/Platform/HardwareProfiler.cs:28` |
| 7 valores `HKLM` en el catálogo | `grep -c HKLM src/FragForge.Tweaks/Catalog/` → 7 |
| Servicios, tareas, BCD, adaptadores de red, punto de restauración | `src/FragForge.Actions/**` |

Y el preflight ya lo bloquea: `PreflightService` marca `admin` como `Blocking`, y `--force` **no** lo omite.

**Por qué no `asInvoker` + elevación bajo demanda**: (1) la lectura de TPM quedaría siempre en `Unknown`, que es
justo lo que la Capa 10 se propuso evitar; (2) aplicar no es una llamada sino un motor con snapshot y journal,
así que haría falta un segundo proceso con el plan serializado; (3) ese segundo proceso tendría que reconstruir
`TargetUserSid` para `HKCU` (R-H). Tres superficies de fallo nuevas para evitar un UAC.
**Por qué no `highestAvailable`**: en cuenta estándar se comporta como `asInvoker` y en cuenta de administrador
como `requireAdministrator`. No aporta nada y el usuario no sabe en qué modo está.

---

## 6. Tabla de auditoría (archivo:línea)

Cada API que se usó se comprobó contra el repositorio. Nunca de memoria.

| Identificador | Verificado en |
|---|---|
| `ITweakEngine.ApplyAsync/ScanAsync/RevertAsync/CreateSnapshotAsync/GetStateAsync` | `src/FragForge.Core/Abstractions/IContracts.cs` |
| `BatchReport.OperationId` (**`required`**) | `src/FragForge.Core/Models/Results.cs` — el doble del test lo asigna por esto |
| `TweakApplyResult.PlannedChanges` | `src/FragForge.Core/Models/Results.cs` |
| `DryRunChange(TweakId, ActionIndex, Kind, Description)` | `src/FragForge.Core/Models/Results.cs` |
| `RevertRequest.SnapshotId` | `src/FragForge.Core/Models/Results.cs` |
| `SnapshotListItem(Id, Label, CreatedAt, TweakCount, RestorePointSequence, Reverted)` | `src/FragForge.Core/Models/Snapshot.cs:49` |
| `ISnapshotStore.ListAsync/GetArtifactDirectory` | `src/FragForge.Core/Abstractions/IContracts.cs` |
| `HardwareProfile` instanciable con `new()` | `src/FragForge.Core/Models/HardwareProfile.cs:3` |
| `TweakStatus(id, state, reason, [], [])` | `src/FragForge.Core/Models/Results.cs:16` |
| `TweakActionFactory(registry, policy, services, tasks, network, runner, probes)` | `tests/FragForge.Tests/Catalog/CatalogTests.cs:25` |
| `JsonTweakCatalogLoader.ReadEmbeddedSchema/LoadEmbeddedAsync` | `src/FragForge.Tweaks/TweakCatalog.cs:34,50` |
| `LocalizationLoader.LoadAsync(culture, ct)` | `src/FragForge.Tweaks/Localization/LocalizationLoader.cs:7` |
| `Localizer.Culture` / `Localizer[string]` | `src/FragForge.Core/Localization/Localizer.cs:9,15` |
| `ProfileSelector.Select` | `src/FragForge.Core/Engine/ProfileSelector.cs:34` |
| `RebuildTweaks()` **no** notifica `Tweaks` | `src/FragForge.UI.Core/MainViewModel.cs` |
| Comandos del CLI (`apply --dry-run`, `revert --last`, `session start|stop|status`) | `src/FragForge.Cli/Program.cs:52,66,131`; `SessionCommands.cs:20` |
| Códigos de salida 0–6 | `src/FragForge.Cli/CommandLine.cs:36-45` |
| `ISnapshotStore` inyectable por propiedad | `MainViewModel.Snapshots` (no se toca el ctor: 10 deps, 11.1 fijó no cambiarlo) |

**Comprobaciones estáticas** (R-N): `FragForge.UI.Core` no contiene `System.Windows`, `WindowsIdentity` ni
`Microsoft.Win32` (`grep` vacío); `Directory.Packages.props` centraliza `CommunityToolkit.Mvvm` 8.4.1; los
recursos embebidos siguen en `<RootNamespace>.<ruta>`; `RealCatalog_PassesTheRealDenylist` sigue en pie y no se
debilitó la denylist; ningún test fija el tamaño exacto del catálogo.

**Chequeo de sintaxis**: los ficheros `.cs` tocados pasan tree-sitter. Los `.xaml`/`.json`/`.yml`/`.iss` se
validaron con `xml.etree`, `json.load` y comprobación de indentación. **Esto no sustituye a un compilador.**

---

## 7. 11.5 / 11.6 — Instalador y firma (`d3c6437`)

| Fichero | Qué hace | Probado |
|---|---|---|
| `scripts/publish.ps1` | `dotnet publish` self-contained win-x64 de App y CLI; versión desde `Directory.Build.props`; verifica que existen los `.exe` | **No** |
| `installer/FragForge.iss` | Program Files, accesos directos, CLI opcional en PATH, sin auto-update | **No** |
| `scripts/sign.ps1` | signtool SHA-256 + timestamp RFC 3161, certificado por entorno, verifica cada firma | **No** |
| `docs/firma.md` | Variables, limitaciones de SmartScreen, por qué firmar no sustituye al journal | — |

**El desinstalador es la parte que importa** y donde estaba el riesgo real:

1. **No revierte tweaks.** Avisa de que desinstalar ≠ revertir, dice cómo hacerlo (`fragforge revert --last`) y
   **aborta** si el usuario dice que no.
2. **No borra `%ProgramData%\FragForge` sin confirmación explícita**, con **NO** como botón por defecto. Ahí vive
   el journal, y perderlo convierte los cambios en irreversibles.
3. Las dos preguntas van en `usUninstall`, **antes** de borrar nada. (Primera versión las tenía en
   `usPostUninstall`: un `Abort()` allí llega con los ficheros ya eliminados y la pregunta no serviría de nada.
   Se corrigió.)

**Bug encontrado y corregido en la revisión**: `sign.ps1` usaba `$args` para los argumentos de signtool. `$args`
es una **variable automática** de PowerShell; asignarla rompe el scope. Ahora es `$signArgs`.

**PROHIBIDO commitear certificados**: se buscó en los ficheros nuevos (`grep` de `.pfx`, `password =`, `BEGIN
*PRIVATE*`) y no hay nada. `artifacts/` ya estaba en `.gitignore`.

---

## 8. 11.7 — CI (`41f2dcd`)

El workflow existía, pero incumplía el enunciado en dos puntos que importan:

| Problema | Antes | Ahora |
|---|---|---|
| Publish en cada push/PR | Sí (cientos de MB por ejecución) | `if: startsWith(github.ref, 'refs/tags/v')` |
| Warnings como errores | No | `-warnaserror` en el job de Linux (Core, Actions, Tweaks, Composition, UI.Core) |
| Cancelación de ejecuciones obsoletas | No | `concurrency` |

**Decisión registrada**: en el job de Windows **no** se pone `-warnaserror` global. La solución incluye App (WPF)
y CLI, que nunca se compilaron en este entorno; un aviso preexistente de WPF pondría el build rojo por algo que
no tiene que ver con esta capa. Core ya rompe por `Directory.Build.props`. Queda escrito en el propio workflow
para que nadie lo "arregle" quitando el flag sin leer el motivo. Ver `docs/descartados.md`.

Sin secretos. El workflow no firma: eso exigiría el certificado en CI (prohibido).

---

## 9. 11.8 / 11.9 — E2E y documentación

`docs/handoff/e2e-manual.md` (259 líneas), en el orden pedido: scan → dry-run de **cada** perfil → aplicar **un**
tweak → verificar → revertir → modo sesión con CS2 cerrado → **recuperación tras cerrar a la fuerza**. Cada paso
lleva el comando exacto, el resultado esperado y qué significa un fallo. Se añaden dos pruebas que el enunciado
no pedía pero que son donde el sistema se rompe de verdad: que el scan y el dry-run **no escriben nada** (se
comprueba con `snapshot list` antes y después) y que el modo sesión **no mata procesos** (se abre el Bloc de
notas antes y se comprueba el PID después).

También incluye la sección de UI (8 comprobaciones manuales) y una nota de cierre: **el instalador y la firma no
se han probado nunca**, y hay que probarlos en una máquina virtual antes de fiarse.

`README.md`: reescrito. Decía "Capa 0" y su árbol de carpetas era de hace diez capas. Ahora lleva el estado real
(0.1.0, capas 0–11), la tabla "qué hace y qué no", el árbol actual, cómo publicar/firmar/instalar y la tabla de
estado por capas con el aviso de lo no verificado.

`CHANGELOG.md`: nuevo, con la nota honesta arriba.

`docs/descartados.md`: se añade la sección de la Capa 11 (auto-update, telemetría, `asInvoker`,
`highestAvailable`, firmar en CI, `-warnaserror` global, lógica en el XAML, desinstalador propio).

---

## 10. Lo que queda pendiente de verdad

1. **Compilar.** Todo. Es lo primero.
2. **Ejecutar los tests.** Especialmente `ApplyFlowTests` y `MainViewModelTests`, que usan el catálogo embebido
   real y fueron escritos sin compilador.
3. **Verificar el objetivo 0 warnings.** El CI lo intentará; si sale rojo, se arreglan los avisos, no se quita el
   flag.
4. **Compilar y probar el instalador en una VM**, incluido el desinstalador respondiendo NO a borrar
   `%ProgramData%`.
5. **Firma**: hace falta un certificado. Sin él, SmartScreen avisará (y está documentado que eso es lo esperable).
6. **E2E manual** completo en el equipo de referencia (i3-1220P, Windows 11 25H2 es-MX).
7. **Decisión futura**: extender `-warnaserror` al job de Windows una vez que la solución haya compilado limpia
   al menos una vez.

## 11. Riesgos conocidos y honestidad

- **El código no ha compilado nunca.** Los errores más probables no son de sintaxis (tree-sitter los descarta)
  sino de tipos: una firma que cambió, un `using` que falta, un `required` sin asignar. Se auditaron las APIs
  una por una, pero **eso no sustituye al compilador**.
- **La UI nunca se ha visto.** El XAML es válido como XML y los bindings apuntan a propiedades que existen, pero
  un binding roto en WPF **no da error**: simplemente no muestra nada. Por eso el E2E manual tiene una sección
  dedicada a la UI.
- **El instalador no se ha compilado.** El código Pascal de `[Code]` se revisó a mano (balance de
  `begin`/`end` comprobado), pero Inno tiene reglas propias.
- **El proyecto no promete FPS.** Cada tweak declara su `evidence`; ver `docs/descartados.md` para lo que se
  decidió no hacer.
