# FragForge — Seguridad y anticheat

Este documento es el contrato de seguridad de FragForge (Capa 10). Describe **qué hace la app**, **qué no
hace** y **por qué se puede comprobar**. Todo lo que aqui se afirma tiene un test o una cita de archivo:línea
en `docs/handoff/FragForge-Capa10-Informe.md`.

> Regla de lectura: cuando algo es `Unknown`, se dice `Unknown`. Un `Unknown` **nunca** se presenta como
> "desactivado" ni como "seguro".

---

## 1. El principio, escrito

FragForge **no inyecta**. No abre handles a `cs2.exe`. No modifica archivos del juego. No toca Steam ni
procesos de anticheat. No lee memoria de ningún proceso del juego.

Ese principio no es una promesa de marketing: es la restricción de diseño de todo el código. Las acciones
que existen son de sistema operativo (registro, servicios, tareas programadas, BCD, planes de energía,
propiedades de adaptador de red). Ninguna abre un proceso ajeno para escribir dentro.

Lo que sí hace, y conviene decirlo con la misma claridad: **escribe en el registro de Windows y en la
configuración del sistema**, con privilegios de administrador, después de un preflight, con snapshot previo,
journal encadenado por hash y revert verificado. Eso es una app de tweaks: el riesgo real no es "meter código
en el juego", es "dejar el sistema en un estado que el usuario no recuerde cómo deshacer". El diseño ataca
eso, no un ataque de anticheat.

### El contrato anticheat, literal

El texto que la app muestra (y que ningún tweak puede contradecir) es:

> FragForge no inyecta, no abre handles a cs2.exe, no modifica archivos del juego y no toca Steam ni procesos
> de anticheat. Los requisitos de cada anticheat (VAC, FACEIT, etc.) los define su proveedor: revisa los
> requisitos del proveedor. Esta app no afirma compatibilidad con ninguno.

**Lo que este documento NO afirma, a propósito:** que FragForge sea "seguro con VAC", "compatible con
FACEIT" ni "detectable/no detectable". No hay fuente que lo respalde, y una app que promete eso está
mintiendo. Los requisitos los define cada proveedor y cambian sin avisar.

---

## 2. Qué NO hace la app (lista cerrada)

| No hace | Cómo se garantiza |
|---|---|
| Inyectar DLL/código en procesos | No existe ninguna acción de inyección en `TweakActionFactory.SupportedKinds` |
| Abrir handles a `cs2.exe` | No hay `OpenProcess` en el código; la única lectura de procesos es de nombres |
| Matar procesos del juego/anticheat/Steam | **No existe ninguna acción de matar procesos** en el código (ni `KillProcess` ni `OpenProcess`): no hay nada que pueda matarlos. R-M cubre el caso de que algún día se añada |
| Leer/escribir memoria de un juego | No hay `ReadProcessMemory`/`WriteProcessMemory` en el repositorio |
| Modificar archivos del juego | Las acciones de archivo están limitadas por `filePrefixes` de la denylist |
| Tocar Secure Boot o TPM | `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot` está en la denylist, y no existe acción de firmware |
| Desactivar Defender | `HKLM\SOFTWARE\Microsoft\Windows Defender` (y su rama de políticas) está en la denylist |
| Desactivar la Integridad de memoria (HVCI) | `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard` está en la denylist — ver §6 |
| Descargar binarios | `ToolVerifier` verifica hash de un archivo que el usuario pone; nunca descarga |
| Debilitar la denylist | La denylist embebida solo se AMPLÍA desde `%ProgramData%`; nunca se sustituye |

La denylist no se puede desactivar desde la UI ni con `--force`. `Force` solo omite idempotencia y drift.

---

## 3. Modelo de amenazas

La pregunta correcta no es "¿me banea esto?" sino "¿qué puede salir mal y qué hace la app al respecto?".

### A. El usuario se equivoca y deja el equipo peor

La amenaza más probable y la única que se puede atacar de verdad.

- **Snapshot antes de escribir**: se capturan TODAS las propiedades que se van a tocar, no solo las que
  cambian (`TweakEngine.CaptureAndPersistAsync`).
- **Revert verificado**: cada acción tiene `VerifyRevertAsync`; si no coincide, el resultado es
  `RollbackFailed`, no "hecho".
- **Journal encadenado**: cada entrada lleva el hash de la anterior. Verificar detecta una línea cambiada.
- **Bloqueado hasta cablear la recuperación fuera de banda (R-J).** El motor **no escribe** el
  script de recuperación (`RecoveryWriter` solo se construye en su propio fichero; `TweakEngine` no
  lo menciona). Por eso el preflight emite una comprobación **Blocking** para todo tweak que pueda
  dejar el equipo sin red o sin arranque: `restartsNetworkLink`, acciones sobre propiedades del
  adaptador de red (`NetAdapterProperty`), `Bcdedit`, y cualquier `Kind` de driver/dispositivo
  (`PreflightService.RecoveryCoverage`). Que un Blocking impide aplicar no es una suposición:
  `PreflightReport.Passed` pone `CanApply=false` si hay alguno (`Results.cs:93`) y `TweakEngine`
  lanza `PreflightFailedException` (`TweakEngine.cs:161`).
  - **Matiz importante: quién ve esa excepción.** El motor la lanza *dentro* de `ApplyAsync`, pero el
    `try` de ese método empieza en `TweakEngine.cs:83` y su `catch (Exception ex)` está en `:277`: la
    excepción se recoge ahí, el mensaje pasa a `report.Errors` (`:280`) y el método **devuelve** un
    `BatchReport` con `Success=false` (`:284`, `:289`). Así que quien llama a `ApplyAsync` **no** recibe
    la excepción: recibe el informe con el motivo en `Errors`. Por eso la UI lo muestra en la barra de
    estado (`MainViewModel.cs:425`) y el CLI imprime los `Errors` por stderr (`ConsoleOutput.cs:63`)
    terminando con código 1 (`ExitCodes.Failure`). Los tests lo fijan así (`EngineTests.cs:258`).
    `ScanAsync`, en cambio, **no** tiene ese `try`: su `throw` de `TweakEngine.cs:58` sí propaga al
    llamador.
  - **Se juzga lo SELECCIONADO, no el catálogo.** El motor rellena `ApplyOptions.SelectedIds` con los
    planes ya resueltos (`TweakEngine.cs:154-158`) y el preflight mira solo esos ids. Tener un tweak de
    red en el catálogo **no** impide aplicar otros: si no está entre los seleccionados, no bloquea.
  - **Sin ids no se bloquea.** `SelectedIds` vacío = «no consta qué se aplica» (llamada vieja, dry-run,
    informe). Bloquear ahí volvería a romper la app por algo que nadie pidió.
  - *Estado hoy*: **`network.i225v-eee-off` queda bloqueado al seleccionarlo** (es el único de los 32
    con `NetAdapterProperty`/`restartsNetworkLink`). Se aplica a mano con la herramienta del
    fabricante o espera a la ronda 10, cuando se cablee `RecoveryWriter`.
  - **Sesión de juego**: un tweak que necesite recuperación fuera de banda tampoco entra en una sesión,
    con el mismo predicado (`GameSessionService.cs:54-66`).
- **Snapshot con hash de definición, pero NO obligatorio al leer.** El motor calcula
  `DefinitionSha256` al capturar (`TweakEngine.cs:469`) y al revertir lo compara con la definición
  guardada; si no coincide, no revierte (`TweakEngine.cs:656-660`). Ahora bien: un snapshot
  **sin** ese campo —de una versión anterior— **se acepta** y se revierte igual, porque
  `TweakEngine.cs:656` hace `if (string.IsNullOrWhiteSpace(...)) continue;`.
  - *Consecuencia*: la integridad protege contra un snapshot manipulado **a partir de la versión que
    empezó a escribir el hash**. Un snapshot viejo (o uno editado a mano para borrar el campo) se
    revierte sin esa comprobación. Se acepta a propósito: rechazarlo rompería la recuperación de
    datos históricos, que es justo cuando más falta hace (`TweakEngine.cs:648-649`).
  - `DefinitionSha256` es `required` en C# (`Snapshot.cs:8`), pero eso solo obliga a quien construye
    el objeto en código: un JSON que no trae el campo deserializa con `null` y pasa por la rama
    `continue`. No confundir "required de compilación" con "obligatorio al leer".
- **Perfil por defecto conservador**: lo agresivo es opt-in, por id y con confirmación escrita.

### B. El usuario aplica algo que no entiende

- `EvidenceLevel` honesto y `risk` por tweak; sin promesas de FPS (R-K).
- Aviso anticheat en cada tweak (§4).
- Textos es-MX/en-US completos: si falta un idioma, el catálogo no valida.

### C. Un catálogo externo malicioso o roto

El usuario puede poner JSON en `%ProgramData%\FragForge\catalog`. Un archivo así **no puede** colar un
tweak que toque algo protegido ni tumbar la app.

- La denylist se aplica **al cargar**: la entrada se rechaza con motivo visible y el resto carga (§7).
- Un id que choque con uno embebido se rechaza: un externo no puede **reemplazar** un tweak auditado.
- JSON inválido → esa entrada fuera, las demás dentro.

### D. Alguien con escritura en disco manipula el journal o los snapshots

Esta es la amenaza que **no se puede cerrar** con lo que hay, y hay que decirlo.

- El journal es **tamper-evident, no autenticado**: no hay clave. Quien pueda editar una línea y recalcular
  la cadena obtiene un journal "válido". Hay un test que lo demuestra explícitamente
  (`IntegrityTests.TheJournal_IsTamperEvident_ButNotAuthenticated`).
- Por eso **no se presenta el journal como defensa contra un atacante local**. Su valor es detectar
  corrupción y ediciones accidentales, y dar una cadena de auditoría coherente.
- Los snapshots sí validan `DefinitionSha256` al revertir: una edición ciega del snapshot hace fallar la
  reversión en vez de aplicarla en silencio. Pero si el atacante recalcula el hash, vuelve a pasar. No es una
  firma.
- **Conclusión honesta**: si un atacante ya tiene escritura en `%ProgramData%` como el mismo usuario, puede
  manipular el histórico. Lo que **no** puede es hacer que FragForge escriba en un destino de la denylist ni
  inyectar en el juego: eso está cerrado en código, no en disco.

### E. Un anticheat interpreta mal un cambio legítimo del sistema

Fuera del control de la app. La mitigación es informativa: el aviso anticheat por tweak, el contrato de §1 y
la recomendación de revisar los requisitos del proveedor. **No se promete nada.**

### F. La app se ejecuta sin admin

Degradación limpia (§5): lee, no aplica, y lo dice.

---

## 4. Aviso anticheat por tweak (10.2)

Cada tweak puede llevar `anticheatNote` (es-MX/en-US, campo aditivo y opcional). Si no lo lleva, la UI
muestra el contrato común en vez de una celda vacía.

El caso paradigmático es `cs2.cpu-priority-high`: escribe **IFEO `PerfOptions`** para `cs2.exe`. Eso es un
ajuste del sistema operativo que se aplica **antes** de que el juego arranque; no abre el proceso. Aun así,
un anticheat podría mirar el estado del sistema, así que el tweak lleva su salvedad explícita y remite al
proveedor.

Prohibido en el catálogo (y hay un test que lo comprueba sobre el catálogo real): "seguro con", "compatible
con", "safe with", "allowed by".

---

## 5. Privilegios: qué necesita admin y qué no (10.6)

| Ruta | Sin admin | Con admin |
|---|---|---|
| Escanear / leer perfil | **Sí** | Sí |
| `security status` (postura) | **Sí**; lo que no se pueda leer sale `Unknown` | Sí |
| `net diagnose` / `net probe` | **Sí** (solo lectura) | Sí |
| Inventario, mitos, atlas, drift | **Sí** | Sí |
| Dry-run de una aplicación | **Sí**: no escribe nada | Sí |
| Aplicar un tweak | **No**: preflight `admin` = Blocking con remediación | Sí |
| Revertir | **No** (misma comprobación de privilegios) | Sí |

El bloqueo es del **preflight**, no de cada acción: así el usuario ve el motivo antes de que se toque nada,
con la remediación ("Acepta UAC o inicia la terminal como administrador"). `--force` **no** omite el
preflight de admin.

---

## 6. Por qué NO hay tweak de "Integridad de memoria off" (10.3)

Se evaluó y **se descartó**. El motivo es de una línea:

> La clave `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard` está en la denylist embebida, y la denylist no
> se debilita.

`HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity` cuelga de ese
prefijo. Para implementar el tweak habría que quitar el prefijo de la denylist, y eso abriría de golpe todo
DeviceGuard: no solo HVCI, también VBS y sus escenarios. Añadir además que el cambio exige reinicio y que su
revert no es verificable en el momento (el estado real solo se ve tras reiniciar), y R-J no se puede cumplir
en su parte de "revert verificable".

**Alternativa que sí existe**: `security status` **informa** del estado de HVCI (WMI `SecurityServicesRunning`
y el escenario de registro, ambos de solo lectura). El usuario que quiera cambiarlo lo hace a mano, sabiendo
lo que hace, fuera de FragForge.

Está en `docs/descartados.md` con el detalle completo.

---

## 7. Resiliencia del catálogo externo (10.4)

`%ProgramData%\FragForge\catalog` es del usuario. Un archivo malo no puede dejar la app sin arrancar.

| Entrada | Resultado |
|---|---|
| JSON inválido | Rechazada por nombre de archivo, sin id |
| Esquema roto | Rechazada con el error del esquema |
| ID duplicado de uno embebido | Rechazada; **la primera definición gana**, el embebido no se puede reemplazar |
| Destino protegido (`Debugger`, servicio protegido, prefijo Memory Management) | Rechazada por política, con el destino en el motivo |
| Depende de algo rechazado | Rechazada también, para no dejar la dependencia colgando |

Los rechazos se registran en el log del arranque y se exponen en `CatalogLoadReport`. El catálogo **embebido**
sigue siendo estricto: si algo del binario no valida, la carga falla. Un bug de la entrega no se degrada a
"un tweak menos".

---

## 8. Integridad: qué se verifica de verdad (10.5)

| Pieza | Qué garantiza | Test |
|---|---|---|
| Journal | Detecta una línea cambiada (cadena de hashes). **No autentica** | `IntegrityTests.TheJournal_DetectsAChangedLine` |
| Journal (límite) | Recalcular la cadena es posible sin clave | `IntegrityTests.TheJournal_IsTamperEvident_ButNotAuthenticated` |
| Snapshot | `DefinitionSha256` verificado en el revert: una definición editada bloquea la reversión | `IntegrityTests.Revert_RejectsASnapshotWhoseDefinitionWasEdited` |
| Recovery index | SHA-256 por archivo; detecta artefacto manipulado | `IntegrityTests.TheRecoveryIndex_DetectsATamperedArtifact` |
| Herramientas externas | `ToolVerifier` verifica SHA-256 antes de ejecutar PresentMon | `DiagnosticsAndDiTests.ToolVerifierTests` |

---

## 9. Lo que sigue pendiente y no se puede cerrar aquí

- **Valores reales de DeviceGuard en esta PC**: no se ejecutó la consulta. El parseo está probado con dobles.
- **Comportamiento de anticheats reales**: no se probó ninguno. No se afirma nada.
- **Firma criptográfica del journal**: requeriría gestión de claves y no está implementada. Hoy es
  tamper-evident, y así se presenta.
- **RecoveryWriter no lo invoca el motor**: existe y se prueba, pero `TweakEngine` todavía no lo llama en el
  flujo de aplicación. Se documenta como hueco conocido; el snapshot y el journal sí se escriben.

---

## Lectura de la ventana de cs2 y hotkeys

La observación usa `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsIconic`, `GetWindowRect`, `GetClientRect`, `MonitorFromWindow`, `GetMonitorInfo` y un contexto DPI temporal. No lee memoria ni inyecta en cs2. No usa `OpenProcess`, `ReadProcessMemory`, `WriteProcessMemory`, `SetWindowsHookEx`, sondeo con `GetAsyncKeyState` o `GetKeyState`, ni `SendInput`. Las hotkeys usan `RegisterHotKey`; una tecla registrada no llega al juego mientras dure el registro, que debe limitarse a la medición.

La resolución «observada» es el área cliente de la ventana: no prueba el modo de pantalla ni la resolución interna de render. No está verificado en vivo que estas APIs sean aceptables para el anticheat; no hay fuente citada para afirmarlo.

Por defecto el log se escribe bajo `%ProgramData%\FragForge\matches`; `MatchLogWriter` acepta un directorio inyectado (usado por tests). Las guardas de segmentos `steamapps` y `userdata` se aplican a cualquier directorio. Por defecto no se guarda nada. La resolución observada es el área cliente y no prueba el modo de pantalla.

El detector identifica candidatos de PresentMon propio, pero FragForge no cierra automáticamente procesos anteriores. En una observación en una sola PC, terminar PresentMon dejó activa su sesión ETW; no se midió el costo de dejarla abierta.

`match` intenta cerrar su sesión ETW con `logman` al terminar y verificar que ya no figure; al iniciar ofrece cerrar sesiones anteriores con confirmación. Solo se admiten nombres `FragForgeLive-` seguidos de exactamente ocho dígitos hexadecimales minúsculos. Si falla, se informa cómo intentar el cierre manual; el comportamiento nuevo no está verificado en vivo.

FragForge puede terminar únicamente el `logman.exe` que él mismo lanzó para esa invocación si vence el timeout de 10 s o se cancela; no termina ningún otro proceso en esa operación. Esto no es una afirmación de seguridad frente al anticheat.

## 10. Limitaciones conocidas

**ACL observada en la PC de pruebas:** según la lectura de `icacls` aportada por el usuario, `C:\ProgramData\FragForge\tools` hereda de ProgramData; Usuarios tiene RX sobre `PresentMon.exe` y (WD, AD, WEA, WA) para crear archivos en la carpeta, sin borrar ni modificar los existentes. Es una lectura de ACL, no una prueba de escritura; en otra PC puede diferir.

- El hash de PresentMon se verifica antes de lanzarlo, pero la verificación y el arranque no son atómicos. FragForge no establece ni verifica las ACL de `%ProgramData%\FragForge\tools`; no se ha verificado si un usuario sin privilegios podría reemplazar el archivo entre ambos pasos.

Hechos verificados leyendo el código. No son promesas de cuándo se cierran.

**Los mensajes del motor y del resto del preflight están en español fijo.** No pasan por `Localizer`, así
que con la aplicación en `en-US` se ven en español. Sitios concretos:

- `TweakEngine.cs`: `:138` (prefijo `"NO SE PODRÁ APLICAR — "` del dry-run), `:143`, `:150` (AVISO de
  reinicio de red), `:161` (prefijo `"El preflight bloqueó la aplicación: "`), `:190` (los dos avisos de
  "nada que aplicar"), `:202`, `:268`, `:274`, `:380`, `:578`.
- `PreflightService.cs`: los mensajes de los checks de `:29`, `:42`, `:43`, `:49`, `:50`, `:51`, `:53`
  (ids `dry-run`, `reboot`, `servicing`, `restore`). Solo `recovery` está traducido, en sus dos piezas:
  `:124` (message, clave `preflight.recovery`) y `:127` (remediation, clave
  `preflight.recovery.remediation`).
- `GameSessionService.cs`: `:85`, `:95`, `:120`.
- `ScanStatusText.cs:16-17`: el mensaje de escaneo se forma en español dentro del helper, sin usar
  `Localizer`. No le falta ninguna clave: no usa localización en absoluto.

**Cambiar el idioma en la App no cambia el `Localizer` que usa el preflight.** `CompositionRoot.cs:77`
carga un `Localizer` y `:95` lo registra como singleton; `PreflightService` lo captura al construirse
(`PreflightService.cs:7-11`). El combo de idioma, en cambio, crea uno **nuevo** y lo guarda en un campo
del ViewModel (`MainViewModel.cs:514`), sin tocar el del contenedor. Consecuencia: la interfaz cambia de
idioma en vivo, el motivo del bloqueo R-J no, hasta reiniciar.

**En dry-run solo puede aparecer el check `recovery`.** `PreflightService.cs:37` retorna dentro de la rama
`if (options.DryRun)` (`:27`), antes de los checks de `:39-58`. Por eso el dry-run nunca evalúa `admin`,
`platform`, `os`, `disk` ni `catalog`, y en consecuencia no avisa de que falten privilegios: el check
`admin` Blocking está en `:41`, después del `return`. La App exige elevación por manifiesto
(`app.manifest:5`, `requireAdministrator`), así que en el uso normal no se da; el CLI sí puede ejecutarse
sin elevación.

**El export de CS2 no se registra en el journal.** `Cs2ProfileExporter` y `Cs2ProfileCommands` no escriben
ni una línea en el journal: no hay llamada a `JournalEventType` en `src/FragForge.Core/Cs2Profile/` ni en
`src/FragForge.Cli/Cs2ProfileCommands.cs`. El journal solo tiene eventos de aplicación de tweaks
(`OperationCompleted` es el más cercano), y forzar uno de ellos para una escritura de archivo habría
descrito mal lo ocurrido. La recuperación no depende del journal: antes de tocar `autoexec.cfg` se crea una
copia `autoexec.cfg.fragforge-backup-<yyyyMMdd-HHmmss>` (con sufijo `-02`…`-99` si ese segundo ya tenía
copia) y `fragforge cs2 restore-autoexec` la devuelve. **Qué verifica ese comando y qué no:** comprueba que
la copia que va a restaurar es idéntica al backup elegido (y que el backup no está vacío); lo que **no**
puede comprobar es que el backup no haya sido alterado *después* de crearse, porque no existe ningún hash
del original guardado en ningún sitio. Es una verificación de integridad de la copia restaurada, no una
garantía de que el contenido sea el que había antes. Tampoco hay manifiesto.

**Un fallo de disco al escribir deja mensaje, pero solo para E/S y permisos, y solo en dos de las tres
etapas se puede afirmar que el autoexec quedó igual.** El exportador y `restore-autoexec` capturan
`UnauthorizedAccessException` e `IOException`. La escritura se hace en tres etapas, y el mensaje cambia
según dónde falle: (1) si falla la **copia de seguridad**, se dice que `autoexec.cfg` no se ha modificado y
que se aborta sin tocarlo; (2) si falla el **temporal**, igual, porque el archivo real aún no se ha tocado;
(3) si falla el **movimiento final**, NO se afirma nada sobre el autoexec: el mensaje pide verificarlo y dice
dónde está la copia de seguridad, porque en esa etapa el archivo puede haber quedado a medias. El temporal
se retira en (2) y (3); **ninguna copia de seguridad se borra nunca**, ni siquiera la que se acaba de crear,
porque es el único original recuperable y una copia idéntica al autoexec actual es inofensiva. Cualquier
**otra** excepción sigue llegando al `catch (Exception)` de `Program.cs:180`, que imprime
`Error inesperado: <tipo>: <mensaje>` y sale con código 1: no hay stack trace, pero tampoco un mensaje que
explique qué ruta falló.

**La precedencia entre `autoexec.cfg` y los convars guardados por la cuenta no está verificada.** Cuando
el valor de `autoexec` y el que Steam guarda en `cs2_convars.vcfg` discrepan, `Cs2ProfileComparer` informa
de los dos valores y añade «no está verificada»; no afirma cuál gana. No hay fuente citable para el orden
de aplicación, y depende de cuándo se ejecute `autoexec` y de si el convar se escribe al salir. Cualquier
texto que diga «el valor del autoexec manda» sería una suposición.

### Medición `match` (no verificada en vivo)

- Solo cierra el PresentMon que lanzó, mediante la sesión.
- Ctrl+C termina con resumen; cerrar la consola con la X puede dejar PresentMon huérfano: el próximo `match` lo detecta y avisa, si puede identificarlo.
- Las hotkeys F9–F11 se registran solo durante la medición.

La resolución configurada se lee de `setting.defaultres` y `setting.defaultresheight` de `cs2_video.txt`: claves observadas en una instalación real, no documentadas por Valve. No implica que coincida con lo visible en pantalla.
Si hay cuentas Steam con resoluciones distintas y no se sabe cuál está activa, se informa «no se sabe».
Con cuenta activa desconocida se leen las cuentas de userdata en solo lectura y solo se informa una resolución si todas coinciden y son legibles y válidas.
