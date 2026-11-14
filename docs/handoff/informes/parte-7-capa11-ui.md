# Parte 7 — Capa 11: UI, instalador, firma, CI y E2E

**Commits** `9d92f96` (deuda) · `85088bb` (ui-core) · `a04893f` (ui) · `d3c6437` (installer) · `41f2dcd` (ci) ·
`7059d01` + `0fe7068` (docs)
**Rango** `07f7b9d..0fe7068` — 7 commits, 37 ficheros, +2346 / −144, **+16 tests** (651 → 667)
**Informe de detalle** `../FragForge-Capa11-Informe.md`

---

## 11.1 Deuda técnica (`9d92f96`)

| Objetivo | Cómo quedó |
|---|---|
| Título con nombre + versión | `AssemblyInfoText.WindowTitle()` → `"FragForge 0.1.0"`, sin `+hash` |
| CA1416 | `[SupportedOSPlatform("windows")]` a nivel de clase en **5** clases de test (**8** en total: 3 ya lo tenían de capas anteriores) |
| xUnit2031 | 6 sitios → `Assert.Single(col, pred)` |
| xUnit2029 | 2 sitios → `Assert.DoesNotContain` |
| xUnit2000 | 2 sitios en `Cs2ResolutionTests` |
| CS0414 | Campo muerto eliminado |
| Punto de inyección | `CompositionOverrides` como **3.er parámetro opcional** de `CompositionRoot.BuildAsync`. Los 14 llamadores compilan sin cambios. **R-A respetado** |
| Test hermético | `ScanAndDryRun_WorkWithInjectedDoubles_WithoutTouchingTheRealMachine` con 6 dobles; los otros 4 de composición → `Trait("Category","Windows")` |

**xUnit1031 / xUnit2013**: se investigó y **no había ningún caso real**. `launcher.Result` es una propiedad de un
doble, no `Task<T>.Result`; `Assert.Equal(n, coleccion.Count)` compara contra colecciones. No se tocó: cambiarlo
habría cambiado lo que prueban.

**Objetivo 0 warnings**: no verificable sin compilador. El CI lo intenta con `-warnaserror`.

## 11.2 UI testeable (`85088bb`) — la capa que arregla un problema de once capas

**El problema**: los ViewModels vivían en `FragForge.App` (`net10.0-windows`, `UseWPF`). El proyecto de tests es
`net10.0`. Consecuencia directa: **la UI no tenía ni un solo test**. Las capas anteriores lo habían parcheado a
medias sacando lógica pura a Core (`TweakCountFormatter`, `ScanStatusText`).

**La solución**: `src/FragForge.UI.Core`, `net10.0`, **sin `UseWPF`**.

| Fichero | Qué |
|---|---|
| `FragForge.UI.Core/MainViewModel.cs` | movido con `git mv` (historial intacto) |
| `FragForge.UI.Core/TweakItemViewModel.cs` | idem |
| `FragForge.UI.Core/Services/UiServices.cs` | `IConfirmationService`, `IFileDialogService` (interfaces) |
| `FragForge.App/Services/DialogServices.cs` | solo las **implementaciones** WPF |

Resultado: **+6 tests** de UI (`MainViewModelTests`) sobre el catálogo **embebido real**.

**Decisión registrada**: `Tweaks` **no** dispara `PropertyChanged` a propósito: es una `ObservableCollection` y la
UI se entera por `CollectionChanged`. La primera versión del test asertaba `PropertyChanged("Tweaks")` y habría
fallado **siempre**; se detectó al leer el código, antes de commitear. El test comprueba el **contenido**.

## 11.3 UI (`a04893f`)

**Se auditó antes de escribir.** Lo que faltaba de verdad:

| Requisito | Estado previo | Qué se hizo |
|---|---|---|
| Pestañas por capa | No | 8 pestañas enlazadas a comandos existentes. **Sin lógica nueva en el XAML** |
| Dry-run con antes/después | El motor **ya devolvía** `PlannedChanges`; la UI no lo mostraba | `PreviewCommand` + `PlannedChanges` + `PreviewSummary` |
| Revertir **por operación** | Solo "último" o "seleccionados" | `Operations` desde `ISnapshotStore.ListAsync` + `RevertOperationCommand` con el `SnapshotId` concreto |
| Aviso de reinicio visible | Columna `True/False` **colapsada** | `RebootDisplay` con texto |
| Localización | Sin claves para la UI nueva | 29 claves nuevas en **ambos** ficheros (86 en cada uno, conjuntos idénticos) |
| Accesibilidad | Nada | `AutomationProperties.Name` + `KeyboardNavigation` |
| Sin telemetría | Ya | Sin cambios |

`BuildApplyOptions` unifica las opciones del dry-run y del apply real para que la vista previa **no pueda
divergir** de lo que se aplica. **+5 tests** (`ApplyFlowTests`).

## 11.4 Elevación (`docs/elevacion.md`)

**Decisión: `requireAdministrator`.** No se implementó nada: **ambos manifiestos ya lo pedían**. Faltaba
justificarlo por escrito, con archivo:línea:

| Necesita admin | Dónde |
|---|---|
| `Win32_Tpm` en `root\CIMV2\Security\MicrosoftTpm` | `HardwareProfiler.cs:28` |
| 7 valores `HKLM` del catálogo | `grep -c HKLM src/FragForge.Tweaks/Catalog/` → 7 |
| Servicios, tareas, BCD, red, punto de restauración | `src/FragForge.Actions/**` |

**Por qué no `asInvoker`**: (1) el TPM saldría siempre `Unknown`; (2) aplicar es un motor con snapshot y journal,
partirlo exigiría serializar el plan a un segundo proceso; (3) ese proceso tendría que reconstruir `TargetUserSid`
para `HKCU`. Tres superficies de fallo nuevas para evitar un UAC.
**Por qué no `highestAvailable`**: en cuenta estándar se comporta como `asInvoker`.

## 11.5 / 11.6 Instalador y firma (`d3c6437`)

| Fichero | Qué |
|---|---|
| `scripts/publish.ps1` | publish self-contained win-x64 de App y CLI; versión desde `Directory.Build.props`; verifica que los `.exe` existen |
| `installer/FragForge.iss` | Program Files, accesos directos, CLI opcional en PATH, sin auto-update |
| `scripts/sign.ps1` | signtool SHA-256 + timestamp RFC 3161, certificado **por entorno**, verifica cada firma |
| `docs/firma.md` | SmartScreen, por qué firmar no sustituye al journal, qué no arregla |

**El desinstalador es donde estaba el riesgo**:

1. **No revierte tweaks.** Avisa de que desinstalar ≠ revertir, dice cómo hacerlo y **aborta** si el usuario dice
   que no.
2. **No borra `%ProgramData%\FragForge` sin confirmación explícita**, con **NO** por defecto. Ahí vive el journal.
3. Las dos preguntas van en `usUninstall`, **antes** de borrar nada. (La primera versión las tenía en
   `usPostUninstall`: un `Abort()` allí llega con los ficheros ya eliminados. Se corrigió antes de commitear.)

**Bug encontrado en revisión**: `sign.ps1` usaba `$args`, que es una **variable automática** de PowerShell.
Renombrada a `$signArgs`. **PROHIBIDO commitear certificados**: se buscó (`.pfx`, `password =`, `BEGIN *PRIVATE*`)
y no hay nada; `artifacts/` ya estaba en `.gitignore`.

## 11.7 CI (`41f2dcd`)

El workflow existía pero incumplía el enunciado en dos puntos que importan:

| Problema | Antes | Ahora |
|---|---|---|
| Publish en cada push/PR (cientos de MB) | Sí | Solo en tags `v*` |
| Warnings como errores | No | `-warnaserror` en el job de Linux |
| Cancelación de ejecuciones obsoletas | No | `concurrency` |

**Decisión registrada**: en Windows **no** se pone `-warnaserror` global, porque App (WPF) y CLI nunca se
compilaron y un aviso preexistente pondría el build rojo por algo ajeno a la capa. Queda escrito en el propio
workflow para que nadie lo quite sin leer el motivo.

## 11.8 / 11.9 E2E y documentación

`docs/handoff/e2e-manual.md` (259 líneas): scan → dry-run de **cada** perfil → aplicar **un** tweak → verificar →
revertir → modo sesión con CS2 cerrado → **recuperación tras cerrar a la fuerza**. Cada paso con comando exacto,
resultado esperado y qué significa un fallo. Dos pruebas que el enunciado no pedía pero donde el sistema se rompe:
que **el scan y el dry-run no escriben nada** (con `snapshot list` antes y después) y que **el modo sesión no mata
procesos** (PID del Bloc de notas antes y después).

`README.md` reescrito (decía "Capa 0" con diez capas de retraso), `CHANGELOG.md` nuevo, sección de Capa 11 en
`docs/descartados.md`.

## Qué no se verificó

**Todo.** No hay `dotnet`, ni PowerShell, ni Inno Setup, ni `signtool`, ni certificado, ni Windows: **nada
compilado, nada ejecutado, ni un test pasado**. Se compensó auditando cada identificador con `grep` (tabla
archivo:línea completa en el informe de la capa) y validando la forma de los ficheros. Eso descarta errores de
sintaxis, **no de tipos ni de semántica**.

Riesgos concretos: los 12 tests de UI usan el catálogo embebido real y se escribieron sin compilador; un binding
roto en WPF **no da error, simplemente no muestra nada**; y el código Pascal de `[Code]` del `.iss` se revisó a
mano (balance de `begin`/`end` comprobado), pero Inno tiene reglas propias.
