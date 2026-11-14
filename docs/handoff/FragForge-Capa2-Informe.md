# FragForge — Capa 2: inventario, tiers y debloat honesto

**Fecha:** 2026-11-13
**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Base:** `399852d` (Capa 1 cerrada)
**Estado:** código y pruebas **escritos y commiteados**; **NADA compilado ni ejecutado** (ver §0).

---

## 0. Advertencia previa, otra vez

Este entorno sigue sin SDK de .NET. Es la **séptima** comprobación y el resultado es el mismo:
no hay `dotnet`, no hay paquete apt disponible y no hay salida de red hacia los hosts de
distribución (`builds.dotnet.microsoft.com`, `dotnetcli.azureedge.net`, `api.nuget.org`,
`release-assets.githubusercontent.com` — todos devuelven conexión 000).

Por lo tanto, de la Capa 2 hay **diseño, código y pruebas**, pero **cero evidencia de ejecución**.
Todo lo que sigue describe *lo que el código hace*, no *lo que se observó al correrlo*.

Lo que **sí** se pudo verificar estáticamente, y se hizo:

| Comprobación | Resultado |
|---|---|
| Llaves `{`/`}` balanceadas en los 15 archivos `.cs` nuevos | 15/15 OK |
| Métodos `async` sin `await` (CS1998) en `src/` | 0 |
| APIs de memoria de proceso en `src/` | 0 |
| `Arguments = "` (concatenación de línea de comandos) | 0 |
| Descargas (`HttpClient`, `WebClient`, etc.) | 0 fuera de la denylist |
| Tokens PowerShell prohibidos dentro de los scripts fijos nuevos | 0 |
| JSON de tiers/mitos/catálogo con `UnmappedMemberHandling.Disallow` | campos revisados uno por uno |
| IDs del catálogo contra el patrón del esquema | 16/16 cumplen `^[a-z0-9][a-z0-9.-]{2,80}$` |

**No puedo afirmar que compile.** Nadie debería creerme si lo dijera.

---

## 1. La premisa, escrita antes que el código

La Capa 2 parte de una suposición incómoda y la dice en voz alta:

> Tu equipo ya pasó por Atlas v0.5.0. El debloat evidente ya está hecho.
> Esta capa **no promete FPS**. Sirve para saber qué hay, qué se puede tocar sin romper nada,
> y cómo deshacerlo si sale mal.

De ahí salen tres decisiones que atraviesan todo el código:

1. **Nada de nombres inventados.** Si un servicio, tarea o clave no se puede verificar contra
   Windows 11 25H2, se inventaría; no se toca. `service-tiers.json` tiene 17 entradas y cada una
   tiene motivo y referencia. Lo que no está, es T0 por omisión.
2. **`ExpectedImpact` honesto.** Los 16 tweaks dicen literalmente «Sin efecto medible esperado en
   FPS». Uno solo (`allowtelemetry-minimum`) declara evidencia `Medium`; el resto `Low`.
3. **La denylist no se negocia.** Si una ruta legítima choca con la denylist, se descarta el tweak.
   Pasó una vez y se descartó (ver §7).

---

## 2. Inventario de solo lectura

`ISystemInventory.BuildAsync` compone cuatro fuentes independientes:

| Fuente | Implementación | Qué lee |
|---|---|---|
| Servicios | `WindowsServiceInventorySource` | WMI `Win32_Service` + `DelayedAutostart` del registro |
| Tareas | `WindowsTaskInventorySource` | script fijo `Get-ScheduledTask` → JSON |
| Arranque | `WindowsStartupInventorySource` | Run (HKLM/HKCU/Wow6432) + carpetas Startup + `StartupApproved` |
| Appx | `WindowsAppxInventorySource` | script fijo `Get-AppxPackage` → JSON |

Reglas que se implementaron explícitamente:

- **Un fallo no es una lista vacía.** Cada fuente devuelve `(items, Warnings)`. Si falla, el
  inventario sale con `IsComplete == false` y el motivo escrito. Un sistema «limpio» por error es
  peor que un error declarado.
- **Servicios por usuario.** Windows crea `CDPUserSvc_5f2a1` y compañía. El catálogo apunta a la
  plantilla; el inventario agrupa las instancias con `_[0-9a-fA-F]{4,}$` y lee `Start` de la clave
  de la plantilla.
- **`StartupApproved` sin adivinar.** `0x02/0x06/0x0E` = habilitado, `0x03/0x07/0x0F` =
  deshabilitado, cualquier otro byte = `null`. Un valor ausente **no** significa habilitado:
  significa que nadie lo tocó, y el estado efectivo lo da la propia entrada Run.
- **HKCU es el del proceso.** Si FragForge corre elevado con otra cuenta, el inventario HKCU **no**
  es el del usuario que juega. Se emite un aviso permanente en vez de presentarlo como suyo.
- **Appx: solo inventario.** No hay —ni habrá— un tweak de desinstalación AppX. Desinstalar un
  paquete no es exactamente reversible y eso rompe el invariante. El código lista y se detiene.

Todo se guarda en `reports\audit-<fecha>.json` con su propio `schemaVersion`, sin tocar
`ReportDocument` (que sigue en 1).

---

## 3. Tiers T0–T4 y la regla del validador

`service-tiers.json` va embebido y **se valida al cargar**. Una entrada sin motivo, sin referencia,
con tier ilegible o duplicada hace fallar la carga entera, y el mensaje lista **todos** los
problemas, no el primero.

| Tier | Significado | ¿Aplicable? |
|---|---|---|
| T0 | Núcleo o protegido | **Nunca** |
| T1 | Seguro, sin dependientes conocidos | Sí |
| T2 | Opcional según el uso | Sí |
| T3 | Solo medir | No |
| T4 | Solo inspección | No |

Dos detalles que costaron pensarlos:

- **El JSON dice `"T1"`, no `"Safe"`.** El convertidor de enums de `System.Text.Json` compara contra
  el *nombre del miembro*, así que `"T1"` nunca habría casado con `SystemTier.Safe`. Se introdujo un
  DTO con `string Tier` y un `TryParseTier` explícito. Sin esto, el archivo entero fallaba al cargar
  con un error que no explicaba nada.
- **T0 no se declara en el JSON.** Es el valor por omisión. Escribirlo sería redundante y, peor,
  invitaría a alguien a «ascender» un servicio de núcleo editando una línea. El parser **rechaza**
  cualquier entrada T0 explícita y rechaza también que un servicio de `CoreNeverTouch` aparezca con
  otro tier.

La regla nueva del validador:

> Un tweak con acción `Service` solo es válido si el servicio figura en `service-tiers.json` con
> tier T1 o T2. Cualquier otra cosa —T0, T3, T4, servicio desconocido o parámetro ilegible— hace
> fallar la carga del catálogo.

Se implementó como parámetro **opcional** (`ServiceTierCatalog? tiers = null`) para que las pruebas
de Capa 0/1 sigan pasando sin cambios. En `CompositionRoot` se inyecta siempre, así que en
producción la regla nunca está ausente.

---

## 4. Simulador de impacto

`ImpactSimulator` recorre el grafo de dependencias del inventario real antes de tocar nada:

- Servicio T0, T3/T4 o desconocido → **bloqueo**.
- Si un servicio T0 depende, **directa o transitivamente**, del servicio que se quiere tocar →
  **bloqueo**. Se probó con un ciclo y no se cuelga.
- Dependientes no-T0 → se listan como advertencia, no bloquean.
- Servicio que no existe en este equipo → nota: el tweak quedará `NotApplicable`, que no es un fallo.

`fragforge simulate <ids...>` lo expone desde la CLI, con código de salida 3 (preflight) cuando hay
bloqueo.

---

## 5. Catálogo: 16 tweaks

16 tweaks `Debloat`, todos con `whenNotToApply` bilingüe y `ExpectedImpact` que dice la verdad:

| Tipo | Cantidad | Ejemplos |
|---|---|---|
| Servicios (T1/T2, condición Atlas) | 8 | `DiagTrack` → Disabled, `dmwappushservice` → Disabled |
| Tareas programadas | 7 | CEIP Consolidator/UsbCeip, KernelCeipTask, Appraiser |
| Política de registro | 1 | `AllowTelemetry` = 0 (evidencia Medium) |

Decisiones concretas:

- **`ScheduledDefrag` no se toca** (TRIM). Hay una prueba que escanea el catálogo y falla si alguien
  lo introduce.
- **Nada de `Remove-AppxPackage`.** Otra prueba lo escanea.
- Los tweaks de servicio llevan condición `atlas` además de `windows`: solo aplican si el equipo
  pasó por Atlas, que es la premisa de la capa.
- `WbioSrvc` (biometría) está en la tabla de tiers como T2 **opt-in** pero no se incluyó como tweak:
  deshabilitar biometría puede dejar a alguien sin forma de iniciar sesión. Está en el catálogo de
  datos, no en el de acciones.

---

## 6. Drift, mitos, huella y recuperación

### `fragforge drift [--reapply]`
Compara la **intención** (journal) contra el **estado actual** (motor):

- Aplicado por FragForge y ya no aplicado → `Lost`.
- Revertido por FragForge y vuelve a estar aplicado → `Reappeared`.
- Estado ilegible → `Unknown` (y `HasDrift` es false: no se finge que hay drift).
- Si el build de Windows cambió desde la captura, la explicación lo atribuye a la actualización de
  características **con los dos builds en el texto**, no como suposición.
- `--reapply` reaplica **solo** los `Lost`, pasando por el motor completo (preflight, condiciones,
  snapshot). Nunca salta la denylist.

`SystemSnapshot` ganó un campo **aditivo y opcional** `HardwareBuild`. Los snapshots viejos siguen
cargando (probado).

### `fragforge myths`
8 mitos, cada uno con veredicto, explicación bilingüe y **referencia obligatoria** (el loader
rechaza un mito sin URL). Entre ellos: HPET/`useplatformclock`, `TcpAckFrequency` en un juego UDP,
limpiadores de RAM, desactivar el archivo de paginación, prioridad Realtime (que el propio FragForge
no usa: tope `High`).

### `fragforge footprint`
60 s a 1 Hz: procesos, working set, CPU/DPC/interrupciones por PDH, top 10 por CPU y por memoria.
**Privacidad:** de cada proceso se guarda nombre y PID, nunca ruta ni línea de comandos.
Un contador ausente se reporta como nota; **no se escribe 0**.

### `RecoveryWriter`: la red que importa
Escribe en `recovery\<fecha>\`:

- `manual-revert.md` bilingüe, con la ruta **offline** (`ControlSet001`) para WinRE.
- `registry-original.reg` (reusa `RegistryBackupExporter`: un valor ausente se representa como
  **borrado del valor**, nunca de la clave).
- `services-original.csv` con el modo original y la instrucción equivalente exacta.
- `tasks-original.xml` con las definiciones XML.
- Copia de los `.pow` / `.bcd` del snapshot.
- `recovery-index.json` con **SHA-256 de cada archivo**, verificable con `VerifyAsync` y copiable a
  un USB con `CopyToAsync`.

Hay 11 pruebas sobre este archivo, incluidas: hash manipulado → detectado, archivo borrado →
detectado, `.reg` sin `[-HKEY`, XML bien formado, y que **todo se escriba dentro del directorio de
datos**.

---

## 7. Lo que descarté, y por qué

Esto es tan importante como lo que entregué.

| Descartado | Motivo |
|---|---|
| Tweak de `PcaSvc` | T3 en mi propia tabla: no tengo evidencia de efecto y sí de que oculta problemas de compatibilidad. Se queda como dato. |
| Tweak de `WbioSrvc` | Riesgo de dejar el equipo sin inicio de sesión. Dato sí, acción no. |
| Tweak de `DoSvc` | Está en la denylist. Ni siquiera lo intenté. |
| Desinstalación de AppX | No es reversible. Viola el invariante. |
| `ScheduledDefrag` | Rompe TRIM. Hay prueba que lo impide. |
| Tweaks de `systemresponsiveness` / `networkthrottling` | Sin mecanismo creíble para CS2 y sin medición. Quedan como *mito explicado*, no como tweak. |
| `bcdedit` de temporizadores | La Capa 2 no añade tweaks de arranque. El mito de HPET se explica en `myths`. |

Y dos **falsos positivos que encontré y corregí** durante el trabajo:

1. **La condición `atlas` daba por detectado Atlas con solo ver servicios parados.** Cualquier
   usuario que hubiera parado `DiagTrack` a mano habría visto los tweaks como «ya aplicados por
   Atlas». Ahora la detección exige un **marcador fuerte** (organización o carpeta `ProgramData\Atlas`)
   y nunca pasa de confianza *Medium*. Los servicios parados se guardan como **desconocido**, no
   como detectado.
2. **`Get-AppxPackage` devuelve un objeto, no un array, cuando hay un solo paquete.** El primer
   código lo trataba como error. Corregido en Appx y ya estaba bien en tareas.

---

## 8. Pruebas

**95 métodos nuevos** en 5 archivos (`tests/FragForge.Tests/Inventory/`), sobre 203 previos: 298 en
total. Reparto:

| Archivo | Casos | Cubre |
|---|---|---|
| `InventoryModelTests.cs` | 26 | `StartupApproval` (incl. base64 y byte desconocido), serialización estricta, tiers, `ImpactSimulator` (incl. ciclos) |
| `RecoveryAndDriftTests.cs` | 22 | `RecoveryWriter` (11) y `DriftService` (11) |
| `InventorySourceTests.cs` | 21 | fuentes de arranque/tareas reales contra `FakeRegistry`/`FakeProcessRunner`, `AtlasProfile` |
| `CatalogAndMythTests.cs` | 19 | mitos, catálogo de debloat contra el disco, `FootprintComparer` |
| `TierValidatorIntegrationTests.cs` | 7 | validador real + tiers reales + los 26 tweaks embebidos |

**Sin ejecutar.** No hay SDK. Cuando lo haya, lo primero que hay que correr es
`dotnet test` y esperar sorpresas: 95 métodos escritos a ciegas no salen limpios a la primera.

---

## 9. Archivos

**Nuevos — Core (7)**
`Inventory/{InventoryModels,ISystemInventory,StartupApproval}.cs`,
`Tiers/{ServiceTiers,ImpactSimulator}.cs` + `Tiers/service-tiers.json`,
`Myths/MythCatalog.cs` + `Myths/myths.json`,
`Drift/DriftService.cs`, `Recovery/RecoveryWriter.cs`, `Sessions/BenchmarkSessions.cs`,
`Atlas/AtlasProfile.cs`

**Nuevos — Actions (6)**
`Inventory/{ServiceInventorySource,TaskInventorySource,StartupInventorySource,AppxInventorySource,SystemInventory,FootprintProbe}.cs`

**Nuevos — Composition / CLI (2)**
`Layer2Registration.cs`, `InventoryCommands.cs`

**Nuevos — Catálogo (16)**
`Tweaks/Catalog/Debloat/debloat.{service,task,policy}.*.json`

**Modificados (10)**
`Snapshot.cs` (`HardwareBuild` aditivo), `TweakEngine.cs` (lo rellena),
`ConditionFactory.cs` (`atlas` con marcadores fuertes), `TweakCatalogValidator.cs` (regla T1/T2),
`WindowsServiceController.cs` + `FragForgeException.cs` (`ServiceChangeRejectedException`),
`CompositionRoot.cs`, `Program.cs`, `CommandLine.cs`, `FragForge.Core.csproj`,
localizaciones `es-MX`/`en-US` (35 → 50 claves).

---

---

## Ronda 5: un objeto del catálogo que NO existe no debe romper el lote

### El síntoma, medido en la PC real

`Integration.CompositionIntegrationTests.ScanAndDryRun_...` fallaba con:

```
DryRun=False, Success=False, Cancelled=False, HasCriticalFailure=False, Results=18,
Errors=[La tarea \Microsoft\Windows\Application Experience\Appraiser no existe.], Warnings=[]
```

**Es un bug de producción, no del test.** La PC tiene Atlas Playbook aplicado, que eliminó esa tarea.
El resto de la suite pasaba: 514 de 515.

### Causa (traza)

1. **Qué tweak.** `debloat.task.appraiser` (`src/FragForge.Tweaks/Catalog/Debloat/debloat.task.appraiser.json:30`)
   declara una acción `ScheduledTask` sobre `\Microsoft\Windows\Application Experience\Appraiser`.
2. **Quién genera el mensaje.** `src/FragForge.Actions/Tasks/ScheduledTaskAction.cs:29` (era la línea 20 antes del arreglo):
   `if (!state.Exists) throw new ActionNotApplicableException(...)`.
3. **Cómo se detecta que no existe.** `WindowsScheduledTaskStore.GetAsync` lanza
   `powershell.exe -Command "Get-ScheduledTask ... -ErrorAction SilentlyContinue; if ($null -eq $t) { '{\"exists\":false}' } else {...}"`
   y parsea **JSON**. La decisión es el campo booleano `exists`, **no el texto**. No es `schtasks`,
   ni COM `ITaskService`, ni WMI.
4. **En qué fase se lanzaba.** En `CaptureAsync`, y `ApplyAsync` la llamaba desde **dos sitios sin
   proteger**:
   - `TweakEngine.cs:510` (`VerifyPlanAsync` → `action.CaptureAsync`), que `ApplyAsync` invoca en la
     línea 139 **antes** de capturar el snapshot;
   - `TweakEngine.cs:445` (`CaptureAndPersistAsync`, la captura real del snapshot).
   La excepción subía hasta el `catch (Exception)` general (`:250`) y **tumbaba el lote entero**.
5. **Por qué el scan pasaba y el apply no.** `TweakEngine.cs:386` (dentro de `EvaluateAsync`, que usa
   el scan) **sí** atrapaba `ActionNotApplicableException` y lo convertía en `NotApplicable`. El
   apply no tenía ese trato. Mismo dato, dos políticas distintas.
6. **¿Se detiene al primer error?** Sí: el `catch` está fuera del bucle de tweaks, así que aborta
   todo lo que quedaba. `Results=18` de 30 = los 12 restantes nunca se evaluaron.

### Tabla: qué pasa si el objeto NO existe

| Tipo de objeto | Quién decide "no existe" | Dónde | Antes de la ronda 5 |
|---|---|---|---|
| **Tarea programada** | `WindowsScheduledTaskStore` (JSON `exists`) | **`CaptureAsync`** | **ROMPÍA EL LOTE** ← el bug |
| Servicio | `WindowsServiceController.Find` (WMI, 0 instancias) | `PreflightAsync` | NotApplicable (ya bien) |
| Registro (clave/valor) | no aplica: un valor ausente **se captura** como ausente | `CaptureAsync` | correcto por diseño |
| Power plan | `PowerPlanAction` | `PreflightAsync` / `ApplyAsync` | NotApplicable (ya bien); `CaptureAsync` no lanza |
| Adaptador / propiedad de red | `NetAdapterPropertyAction` | `PreflightAsync` | NotApplicable (ya bien) |
| Appx / script / archivo | no hay comprobación de existencia de appx en el catálogo actual | — | sin cambios |

**Conclusión de la tabla:** `ScheduledTask` era el **único** caso que decidía "no existe" dentro de
`CaptureAsync`, y por eso el único que se escapaba. Las demás acciones ya lo decidían en
`PreflightAsync`, que `TryBuildPlanAsync` sí protegía. **No tocaba cambiar nada más**: el mismo bug
con la misma regla no existía en otro sitio.

### La corrección

**`TweakEngine.VerifyPlanAsync`** (el punto por el que escapaba) ahora atrapa
`ActionNotApplicableException` y devuelve `TweakStatus NotApplicable`. `ApplyAsync` lo reporta como
omitido y **no lo mete en el snapshot**.

Por qué ahí y no en `TryBuildPlanAsync`: la alternativa era sondear `CaptureAsync` al construir el
plan, pero eso **paga una captura extra de cada acción en cada apply**, y `PowerShellAction.CaptureAsync`
**ejecuta un script**. `VerifyPlanAsync` ya se llamaba antes de capturar, así que la comprobación sale
gratis.

**`CaptureAndPersistAsync`**: defensa en profundidad por si el objeto desaparece **entre** el verify y
la captura (una carrera real: el usuario borra la tarea en ese instante). El tweak queda fuera del
snapshot, se reporta `NotApplicable` y no se aplica.

**Solo se traga `ActionNotApplicableException`.** Un fallo real —acceso denegado, Task Scheduler
caído, timeout— es otra excepción y **sigue siendo error**. Es el control negativo que exige R-L.

**`ScheduledTaskAction`**: el mensaje pasa a `La tarea no existe en este equipo: <ruta>`, y el
comentario fija que la decisión la toma `Exists`, no el texto.

### Detección sin depender del idioma

La regla es un **dato**, no una interpretación:

- `Get-ScheduledTask -ErrorAction SilentlyContinue` no escribe error cuando falta: devuelve `$null`.
- El script emite `{"exists":false}` y `GetAsync` lee **ese booleano**.
- No hay `Contains("no existe")` ni comparación con texto de Windows en ninguna parte del camino.

Por eso el mismo binario se comporta igual en es-MX, en-US o de-DE. El test
`ErrorText_DoesNotDecideTheLogic_SameOutcomeInEveryLanguage` lo fija con tres idiomas.

**Por qué es seguro**: la señal no es "el comando falló", es "consulté y el objeto no está". Un fallo
de verdad (permisos, RPC caído) hace que `RunAsync` lance `ProcessFailedException` por exit code ≠ 0,
y eso **no** se confunde con ausencia.

### Reporte de fallo honesto

`BatchReport` del camino de excepción (`TweakEngine.cs:262`) fijaba `DryRun` por defecto (`false`)
aunque se hubiera pedido dry-run, y parecía que el dry-run se había ignorado. Ahora fija
`DryRun = options.DryRun`. Un dry-run fallido sigue siendo un dry-run: `Success=false` es lo que dice
que falló. Test: `FailedDryRun_StillReportsDryRunTrue`.

### Tests (9, controlador falso, sin hardware)

`tests/FragForge.Tests/Core/MissingObjectTests.cs`, con un `IScheduledTaskStore` doble:

| Test | Fija |
|---|---|
| `Scan_MissingTask_IsNotApplicable_...` | scan → `NotApplicable`, no `Unknown` |
| `DryRun_MissingTask_IsSuccess_...` | dry-run → `Success=true`, 0 errores, `NotApplicable`, 0 escrituras |
| `Apply_MissingTask_IsNotAnError_AndWritesNothing` | apply real → ni error ni "aplicado", 0 escrituras |
| `Apply_RealFailure_IsStillAnError_...` | **control negativo**: acceso denegado **sigue siendo error** |
| `Scan_RealFailure_IsUnknown_NotNotApplicable` | fallo real al leer → `Unknown`, no `NotApplicable` |
| `ErrorText_DoesNotDecideTheLogic_...` | es-MX / en-US / de-DE: mismo resultado |
| `MissingIsDecidedByTheStateFlag_NotByAnyMessage` | la decisión es `Exists`, no un texto |
| `Apply_TaskThatVanishesBetweenProbeAndCapture_...` | carrera verify→captura |
| `FailedDryRun_StillReportsDryRunTrue` | `DryRun=true` en el camino de excepción |

Se añadió un parámetro opcional `factory` a `EngineTestHarness.CreateEngine` para inyectar la acción
real contra el doble, sin tocar el `FakeActionFactory` que usan los demás tests.

**No se tocó `CompositionIntegrationTests`.**

### Cómo dejaría hermético el test de composición (sin hacerlo)

El test real construye la composición entera y por tanto perfila el hardware de verdad. Para que sea
hermético, en otra ronda: exponer en `CompositionRoot.BuildAsync` un punto de sustitución de servicios
de plataforma (o un `ShellOptions` que reciba un `Action<IServiceCollection>` **después** del registro
de plataforma), y registrar allí los dobles de `IHardwareProfiler`, `IWmiQuery` e `IScheduledTaskStore`.
Así el test probaría la composición sin depender de la PC. Requiere tocar `CompositionRoot`, que es
más de lo que autoriza esta ronda: **queda listado, no hecho**.


### Ronda 5b: un error de compilación y una detección que no era segura

**El build de `818a9a9` falló con 1 error** (0 warnings nuevos, 42; y **no corrió ningún test**, así
que la corrección de la ronda 5 no llegó a verificarse):

```
tests\FragForge.Tests\Core\MissingObjectTests.cs(108,79): error CS0103:
El nombre 'JsonDefaults' no existe en el contexto actual
```

**Causa.** `JsonDefaults` **sí existe** (`src/FragForge.Core/Logging/JsonDefaults.cs:6`); lo que
faltaba era `using FragForge.Core.Logging;` en el archivo. No era un tipo inventado, era un `using`
omitido: escribí el helper de serialización a mano en vez de reutilizar el que ya existía.

**Arreglo.** Se eliminó el helper y el test usa `Spec.Action(kind, parameters)`
(`tests/FragForge.Tests/Fakes/TestSupport.cs:10`), que ya serializa con `JsonDefaults.Options` y
compila. Así no hay dos configuraciones de serialización que puedan divergir. Se quitó también
`using System.Text.Json;`, que quedó sin uso.

Se auditó cada identificador externo de los dos archivos (ver la tabla en el informe de Capa 2).

### Detección de "no existe": de `SilentlyContinue` a la categoría

**El problema.** `Get-ScheduledTask -ErrorAction SilentlyContinue` + `$null ⇒ {"exists":false}`.
`SilentlyContinue` silencia **todos** los errores no terminantes. Un Programador de tareas caído, un
fallo de RPC o un permiso denegado podían dar `$null` con exit 0 y **leerse como "no existe"**. Eso
viola R-L: "no sé" se convertía en una afirmación.

**El cambio.** El script ahora usa `-ErrorAction Stop` dentro de `try/catch` y emite `exists=false`
**solo** si `$_.CategoryInfo.Category -eq 'ObjectNotFound'`. Cualquier otra excepción escribe el error
por stderr y sale con 2 ⇒ `RunAsync` lanza `ProcessFailedException` (error real, el lote se corta).
Y "éxito con 0 resultados sin lanzar" sale con 3: es error, **no** `exists=false`, porque no hay forma
de distinguir "no existe" de "el proveedor no respondió".

La señal es la **categoría**, un valor de enumeración de PowerShell, no el texto: no se usa
`Contains`, ni el mensaje localizado, ni `FullyQualifiedErrorId` como regla única.

**NO VERIFICADO contra Windows real: la categoría `ObjectNotFound` para una tarea inexistente se
confirma con una sonda que corre el usuario.** Los tests usan un `IProcessRunner` falso: prueban qué
hace el código con cada salida, no lo que PowerShell devuelve de verdad.

**Sonda sugerida** (para el usuario, en la PC, con una tarea que se sabe eliminada):

```powershell
try { Get-ScheduledTask -TaskPath '\Microsoft\Windows\Application Experience\' `
      -TaskName 'Appraiser' -ErrorAction Stop } catch {
  $_.CategoryInfo.Category   # se espera: ObjectNotFound
  $_.FullyQualifiedErrorId
}
```
Si la categoría **no** fuera `ObjectNotFound`, la tarea se reportaría como error en vez de
"No aplicable": se vería en el mensaje del lote, no se adivinaría. Es el fallo seguro.


### Ronda 5c: el script no era PowerShell válido (y los tests no podían verlo)

**El bug.** La ronda 5b acertó la semántica y rompió la sintaxis. `BuildGetScript` armaba una plantilla
(raw string de varias líneas) y la aplanaba con un `Replace` de los saltos de línea por espacios. Ese
aplanado eliminó los separadores de sentencia. El script que llegaba a `powershell.exe -Command` era:

```powershell
$ErrorActionPreference='Stop' [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new() try { $t=@(...) if ($t.Count -eq 0) {...} [pscustomobject]@{...} | ConvertTo-Json ... } catch { ... }
```

Cuatro fronteras sin separador: `'Stop' [Console]`, `) if (`, `} [pscustomobject]`, `true} | Convert`.
No es PowerShell válido, así que **cualquier** tarea del catálogo habría fallado con
`ProcessFailedException` — y con el mensaje "no se pudo consultar", no "no existe".

**Por qué ningún test lo vio.** Los 9 tests de la ronda 5b inyectan un `IProcessRunner` falso: prueban
qué hace el código con una salida dada, no que el script sea ejecutable. El script nunca se parseó ni se
ejecutó.

**Causa raíz, archivo:línea.** `src/FragForge.Actions/Tasks/WindowsScheduledTaskStore.cs`,
`BuildGetScript`: construía un script concatenando líneas y luego lo "reflowaba" con `Replace`. Tratar un
script como texto con formato es el error de fondo: los saltos de línea **son** separadores en
PowerShell.

**Qué cambió.** El script se compone como `List<string>` de sentencias y se unen con `string.Join(' ',
…)`. Los separadores son explícitos, uno por sentencia, y no dependen de ningún reformateo posterior:

| Frontera | Separador | Por qué |
|---|---|---|
| `$ErrorActionPreference='Stop'` → `[Console]::…` | `;` | Asignación seguida de sentencia en la misma línea |
| `[Console]::OutputEncoding=…` → `try {` | `;` | Idem; el cuerpo del `try` ya va tras `{` |
| `$t=@(… -ErrorAction Stop)` → `if (…)` | `;` | El `)` de `@(...)` **no** cierra sentencia |
| `if (…) { … exit 3 }` → `[pscustomobject]…` | `;` | El `}` de un bloque no basta en la misma línea |
| `}` → `catch {` / `}` → `else` | **ninguno** | Un `;` ahí rompería la construcción |

Se conserva el contrato completo de 5b: `-ErrorAction Stop`, `try/catch`, `{"exists":false}` **solo** con
`$_.CategoryInfo.Category -eq 'ObjectNotFound'`, cualquier otro fallo ⇒ stderr + `exit 2`, 0 resultados sin
excepción ⇒ stderr + `exit 3`, y el saneado de `Literal()` (dobla comillas simples, rechaza caracteres de
control y el marcador `__TASK`).

**Tests nuevos — `tests/FragForge.Tests/Core/ScheduledTaskScriptParsesTests.cs`:**

| Test | Qué prueba | Línea |
|---|---|---|
| `GeneratedScript_ParsesWithTheRealPowerShellParser` | **Parseo REAL** del script generado con `[System.Management.Automation.Language.Parser]::ParseInput`. Es el test que habría atrapado el bug. | `:57` |
| `EveryGeneratedScript_Parses` | Lo mismo, también con una ruta hostil (`O'Brien`). | `:96` |
| `Script_HasNoLineBreaksAndKeepsTheContract` | Contrato portable: sin `\n`/`\r`, con `-ErrorAction Stop`, sin `SilentlyContinue`, con `ObjectNotFound`. | `:126` |
| `Script_HasExplicitStatementSeparators` | Fija `'Stop';`, `-ErrorAction Stop);`, `exit 3 };`, `} catch {`, y que **no** reaparezca `'Stop' [Console]`. | `:142` |
| `MissingTask_RealPowerShell_ReturnsExistsFalse_WithoutThrowing` | **Ejecución REAL** (solo lectura) con `ProcessRunner` real sobre `\FragForgeNoExiste\Tarea` ⇒ `Exists == false` sin excepción. | `:186` |

`ProcessRequest` **no** tiene variables de entorno (`src/FragForge.Core/Abstractions/IProcessRunner.cs:7-14`:
`FileName`, `Arguments`, `WorkingDirectory`, `StandardInput`, `Timeout`), así que el script va por archivo
temporal y se lee con `Get-Content -Raw`; se borra en `finally`.

**No verificado.** El entorno del agente es Linux, sin `dotnet` y sin PowerShell: **los dos tests reales no
se han ejecutado**. Son la sonda pendiente. Tampoco se ejecutó el test de parseo sobre el script viejo para
verlo fallar (red-first): se comprobó a mano, con una simulación del `Replace`, que producía las cuatro
fronteras rotas.


### Auditoría de identificadores externos (ronda 5b)

Sin compilador, la alternativa fue citar la definición real de **cada** identificador externo que usan
los dos archivos. Todos se localizaron: ninguno quedó sin definición.

| Identificador | Definido en |
|---|---|
| `ScheduledTaskAction` | `src/FragForge.Actions/Tasks/ScheduledTaskAction.cs:6` |
| `ScheduledTaskActionState` | `src/FragForge.Core/Models/ActionState.cs:57` |
| `IScheduledTaskStore` | `src/FragForge.Core/Abstractions/IContracts.cs:82` |
| `IProtectionPolicy` | `src/FragForge.Core/Abstractions/IContracts.cs:66` |
| `ITweakAction` / `ITweakActionFactory` | `src/FragForge.Core/Abstractions/ITweakAction.cs:16` / `:28` |
| `ActionSpec` | `src/FragForge.Core/Models/TweakDefinition.cs:46` |
| `TweakDefinition` | `src/FragForge.Core/Models/TweakDefinition.cs:6` |
| `LocalizedText` | `src/FragForge.Core/Models/LocalizedText.cs:5` |
| `ExpectedImpact` | `src/FragForge.Core/Models/TweakDefinition.cs:53` |
| `TweakState` / `RiskLevel` / `EvidenceLevel` | `src/FragForge.Core/Models/Enums.cs:5` / `:4` / `:3` |
| `FragForgeException` | `src/FragForge.Core/Models/FragForgeException.cs:3` |
| `ActionNotApplicableException` | `src/FragForge.Core/Models/FragForgeException.cs:13` |
| `ProcessFailedException(executable, exitCode, error)` | `src/FragForge.Core/Models/FragForgeException.cs:20` |
| `TweakStatus(5 args)` | `src/FragForge.Core/Models/Results.cs:16` |
| `ScanReport` | `src/FragForge.Core/Models/Results.cs:17` |
| `BatchReport` (`DryRun`, `Success`, `Errors`, `Results`, `SnapshotId`) | `src/FragForge.Core/Models/Results.cs:63` |
| `TweakApplyResult` (`State`, `Message`) | `src/FragForge.Core/Models/Results.cs:51` |
| `ActionExecutionContext` (`OperationId`/`Hardware` **required**) | `src/FragForge.Core/Abstractions/ITweakAction.cs:5` |
| `ProcessRequest` / `ProcessResult` | `src/FragForge.Core/Abstractions/IProcessRunner.cs:7` / `:15` |
| `IProcessRunner` | `src/FragForge.Core/Abstractions/IProcessRunner.cs:3` |
| `ProtectionPolicy.CreateDefault()` | `src/FragForge.Core/Persistence/ProtectionPolicy.cs:13` |
| `Spec.Action(kind, parameters)` | `tests/FragForge.Tests/Fakes/TestSupport.cs:10` |
| `JsonDefaults.Options` | `src/FragForge.Core/Logging/JsonDefaults.cs:6` |
| `EngineTestHarness` / `CreateEngine(hooks, factory)` / `Options(dryRun, force, profile)` | `tests/FragForge.Tests/Core/EngineTestHarness.cs:13` / `:47` / `:59` |
| `TestProfiles.Pn64()` | `tests/FragForge.Tests/Fakes/FakeHardwareProfiler.cs:13` |
| `FakeActionFactory` / `ScriptedActionOptions` / `TempDirectory` | `tests/FragForge.Tests/Fakes/TestSupport.cs:79` / `:40` / `:104` |
| `FakeJournal` / `FakeSystemRestoreService` | `tests/FragForge.Tests/Fakes/FakePlatformServices.cs:85` / `:6` |
| `FakeRegistry` | `tests/FragForge.Tests/Fakes/FakeRegistry.cs:5` |
| `FakeHardwareProfiler` | `tests/FragForge.Tests/Fakes/FakeHardwareProfiler.cs:6` |
| `FileJournal` / `FileSnapshotStore` / `FragForgePaths` / `FileExecutionGate` | `src/FragForge.Core/Persistence/` |
| `ConditionFactory` / `PreflightService` / `TweakCatalog` | `src/FragForge.Core/Conditions/`, `Preflight/`, `src/FragForge.Tweaks/TweakCatalog.cs:11` |
| `NullLogger<T>` | `Microsoft.Extensions.Logging.Abstractions` (paquete) |

Cuatro correcciones que salieron de esta auditoría:

1. **CS0103**: faltaba `using FragForge.Core.Logging;` (resuelto usando `Spec.Action`).
2. **`ActionExecutionContext`** tiene `OperationId` y `Hardware` como `required`: un
   `new ActionExecutionContext()` no compila. Se construye con ambos.
3. **`Assert.ThrowsAny<>`** no se usaba en ningún otro test del repo: se cambió a `Assert.Throws<>`,
   que sí está probado en el proyecto, y coincide con el `ArgumentException` exacto que lanza el código.
4. **CA1416**: `WindowsScheduledTaskStore` está marcada `[SupportedOSPlatform("windows")]` y el test vive
   en `net10.0`. Se anotó la clase de test con la misma marca, como en Capa 0.

### No verificado

- **Sin `dotnet` no se compiló ni se ejecutó nada.** Lo único automático fue tree-sitter (sintaxis),
  que **no es compilar**. La confirmación es la corrida del usuario.
- **No sé si faltan más tareas del catálogo en esa PC.** Solo está medido `Appraiser`. El arreglo
  cubre cualquiera que falte, pero no se enumeraron las 7 tareas `ScheduledTask` del catálogo contra
  el equipo.
- No se ejerció el camino real de `powershell.exe`/`Get-ScheduledTask`: los tests usan dobles.
- La carrera "desaparece entre verify y captura" se prueba con un doble que cambia de respuesta; en
  hardware real no se reprodujo.
- **La categoría `ObjectNotFound` no se midió en Windows real** (ver arriba): la confirma la sonda del
  usuario.
- El script de PowerShell no se ejecutó: los tests validan el texto generado y el contrato con un
  runner falso.

## 10. Lo que NO está hecho

- **Nada compilado, nada ejecutado.** Siete confirmaciones.
- No hay validación contra un Windows 11 25H2 real. Los nombres de servicio y tarea salen de
  conocimiento documentado, no de una lectura en un equipo de prueba. El inventario existe
  precisamente para que la primera pasada real confirme o desmienta.
- `fragforge drift --reapply` no se probó contra un journal real.
- La Capa 2 no tiene UI en WPF: solo CLI. La ventana de Capa 0/1 no se tocó.
- El `manual-revert.md` no se probó abriéndolo en WinRE. Los comandos son los documentados, pero
  nadie los ejecutó en una máquina rota.
