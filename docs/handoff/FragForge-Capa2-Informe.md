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

## 10. Lo que NO está hecho

- **Nada compilado, nada ejecutado.** Siete confirmaciones.
- No hay validación contra un Windows 11 25H2 real. Los nombres de servicio y tarea salen de
  conocimiento documentado, no de una lectura en un equipo de prueba. El inventario existe
  precisamente para que la primera pasada real confirme o desmienta.
- `fragforge drift --reapply` no se probó contra un journal real.
- La Capa 2 no tiene UI en WPF: solo CLI. La ventana de Capa 0/1 no se tocó.
- El `manual-revert.md` no se probó abriéndolo en WinRE. Los comandos son los documentados, pero
  nadie los ejecutó en una máquina rota.
