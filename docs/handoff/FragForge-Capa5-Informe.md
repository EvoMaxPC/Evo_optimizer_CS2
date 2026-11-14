# FragForge — Capa 5: Red (adaptador por hardware, EEE y diagnóstico)

**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Base:** `f44ba1d` (Capa 6/7)
**Hardware medido:** Intel I225-V 2.5G (`{D72A2249-535C-4478-9FDD-9B3A439F6395}`) y Wi-Fi Intel AX210
(`{0F4DC9CC-3733-4C8F-96FD-92AAB123BD6F}`). **CS2 usa UDP (Steam Datagram Relay): los ajustes de TCP no mejoran
su red.**

> **Nada compilado ni ejecutado.** Sin `dotnet`, `mono`, `csc` ni PowerShell en el entorno. Todo se auditó con
> `grep` real y se cita `archivo:línea`.

---

## 5.1 Decisión X3 — `INetworkAdapterResolver` (interfaz nueva)

`INetworkAdapterController` **no se tocó** (R-A). Se añadió una interfaz nueva en
`src/FragForge.Core/Abstractions/IContracts.cs` (al final, aditiva):

| Tipo | Qué hace |
|---|---|
| `INetworkAdapterResolver` | `ListAsync()` y `ResolveAsync(selector)` por identidad de hardware. |
| `NetworkAdapterIdentity` | `InterfaceGuid`, `NetCfgInstanceId`, `HardwareId` (`PnPDeviceID`), `NdisPhysicalMedium`, `LinkSpeedBitsPerSecond`, `Kind`, `State`, `IsDefaultRoute`. |
| `NetworkAdapterSelector` | `InterfaceGuid`, `NetCfgInstanceId`, `HardwareIdContains`, y `HasHardwareIdentity`. |

Implementación `src/FragForge.Actions/Network/WindowsNetworkAdapterResolver.cs`:

- `MSFT_NetAdapter` en `root\StandardCimv2`, reutilizando
  `WindowsHardwareProfiler.NetAdapterProperties` (`src/FragForge.Actions/Platform/HardwareProfiler.cs:260-273`)
  **más cuatro propiedades** (`PnPDeviceID`, `InterfaceIndex`, `MaxSpeed`, `MediaConnectState`), todas presentes
  en las 103 propiedades reales medidas en `NetAdapterPropertiesTests.cs`.
- `NetCfgInstanceId` se lee de
  `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\<NNNN>`, recorriendo solo
  subclaves de cuatro dígitos.
- **Un adaptador sin `InterfaceGuid` no entra en la lista**: sin identidad no hay ruta de apply.
- `ResolveAsync` devuelve `null` si no hay **una** coincidencia única; con dos candidatos no elige.

### La identificación por nombre visible quedó eliminada de la ruta de apply

`NetAdapterPropertyAction` aceptaba `adapterName` con el token `{AdapterDescription:I225-V}` y resolvía con
`Description.Contains(...)` sobre el perfil. Ese parámetro **ya no existe**: el record
`NetworkParameters` solo tiene `InterfaceGuid` y `HardwareIdContains`. Si ninguno está presente, es
`CatalogValidationException`; no hay ninguna vía que caiga al nombre.

---

## 5.2 Propiedades por `RegistryKeyword`

`src/FragForge.Actions/Network/NetworkAdvancedPropertyReader.cs`.

- Lee con `Get-NetAdapterAdvancedProperty -InterfaceGuid <guid> -RegistryKeyword <kw>`.
- La salida se parsea como **objeto JSON** (`ConvertTo-Json`), nunca por texto de consola.
- **Mapeo `RegistryValue` ↔ `DisplayValue` por POSICIÓN** entre `ValidRegistryValues` y `ValidDisplayValues`: es
  la única forma de traducir sin leer etiquetas localizadas (R-C). Hay tests que dan la misma propiedad con
  textos en es-MX y en en-US y comprueban que el comportamiento es idéntico.
- `WriteAsync` **se niega a escribir** un valor que no esté en `ValidRegistryValues` (R-L): lanza
  `ActionNotApplicableException` antes de tocar el proceso.
- Propiedad inexistente → `Exists = false` → `ActionNotApplicableException` en el preflight. El tweak es
  **No aplicable**.

---

## 5.3 `network.i225v-eee-off` rehecho

| Antes | Ahora |
|---|---|
| `conditions: adapter{descriptionContains: I225-V}` | `adapter{hardwareIdContains: DEV_15F3}` |
| `adapterName: "{AdapterDescription:I225-V}"` | `hardwareIdContains: DEV_15F3` |
| `evidence: Medium` | `evidence: Low` (sin medición) |
| Sin aviso de corte | `restartsNetworkLink: true` + aviso en motor, UI y CLI |

`DEV_15F3` está verificado como el device ID del I225-V (`PCI\VEN_8086&DEV_15F3`), no de memoria.

**R-J — BitLocker y aviso de corte:**

- `src/FragForge.Core/Preflight/NetworkChangePreflight.cs`: consulta `manage-bde -status` y **el único argumento
  que pasa es `-status`**. Hay un test que falla si aparece `-off`, `-on`, `-suspend`, `-resume` o `-protectors`.
- Se activa desde `PreflightService` solo si el catálogo tiene algún tweak con `RestartsNetworkLink`.
- **Ningún estado de BitLocker bloquea**: informa. Con cifrado activo sale un `Warning` que pide guardar la clave
  de recuperación.
- El aviso del corte lo emite el motor (`TweakEngine`) **antes** de aplicar, y la UI lo pregunta en su propio
  diálogo antes de continuar.
- **Modo sesión:** `GameSessionService.EligibleIds` filtra además `!d.RestartsNetworkLink`. Un tweak de red no
  entra en una sesión **aunque alguien lo marque elegible por error**. Hay test.

---

## 5.4 Candidatos, uno por uno

| Candidato | Efecto técnico | Decisión |
|---|---|---|
| `*InterruptModeration` off | Interrupción por paquete en vez de por lote. | **Denylist** → descartado |
| `*FlowControl` off | Sin tramas PAUSE. | **Denylist** → descartado |
| RSC (`Set-NetOffloadGlobalSetting`) | Solo TCP; CS2 es UDP. | Descartado |
| LSO (`*LsoV2IPv4/6`) | Segmentación TCP en envío. | Descartado |
| Wi-Fi AX210 power/roaming | Solo si la ruta por defecto es Wi-Fi. | Descartado; queda recomendación en texto |
| TcpAckFrequency / TCPNoDelay | TCP. CS2 es UDP. | Descartado |
| NetworkThrottlingIndex | Folclore sin fuente. | Descartado |
| DNS | No es latencia de flujo. | Descartado |
| QoS DSCP | Depende del router. | Descartado |

### Lo importante: dos candidatos chocaban con la denylist

`denylist.json` protege en `networkPropertyKeywords`: `*DeviceSleepOnDisconnect`, `*WakeOnMagicPacket`,
`*WakeOnPattern`, `*NetworkAddress`, **`*FlowControl`**, `*RSS`, **`*RscIPv4`**, **`*RscIPv6`**,
**`*InterruptModeration`**.

Implementé los dos tweaks y los **eliminé al comprobarlo**: habrían hecho fallar `RealCatalog_PassesTheRealDenylist`
y **la denylist no se debilita**. `*EEE` **no** está en la lista, que es por lo que 5.3 sí se puede implementar. Hay
un test que fija el rechazo keyword por keyword y otro que confirma que `*EEE` pasa.

---

## 5.5 Diagnóstico (solo lectura)

`src/FragForge.Actions/Measurement/NetworkProbe.cs`.

- Adaptador por la ruta por defecto: se cruzan las identidades del resolutor con `NetworkInterface` para ver cuál
  tiene puerta de enlace. **Si hay más de un candidato, no se elige**: `Adapter = null` y nota explicando.
- Tipo de medio por `NdisPhysicalMedium` con el mapeo existente **14 = Ethernet / 9 = Wi-Fi**, que se muestra
  junto al valor crudo y con la etiqueta **NO VERIFICADO en esta NIC**.
- Velocidad de enlace real (`Speed`, cayendo a `MaxSpeed` solo si falta).
- EEE actual: el `net diagnose` **no** lo escribe; se remite a `fragforge probe`.

**Ping:** `NetworkProbe.MeasureAsync(host, count)` con `System.Net.NetworkInformation.Ping`.
- **El host lo elige el usuario**; sin `--host` el comando devuelve error de uso. No hay hosts por defecto.
- Mediana, p99 (rango más cercano), jitter = desviación media absoluta respecto a la mediana, mín/máx y pérdida.
- **Con cero respuestas: `HasSamples = false` y mediana/p99/jitter/mín/máx en `null`. La pérdida es 100%.** NA no
  es 0.
- **Sin veredicto**: hay un test que comprueba que el resultado no tiene propiedades `Verdict`, `Rating` ni `Score`.

CLI: `fragforge net probe --host <h> --count <n>` y `fragforge net diagnose`, con lista cerrada de verbos como
`cs2`.

---

## 5.6 Snapshot y `RollbackFailed`

- `CaptureAsync` guarda **todas** las propiedades que se van a tocar, con su valor previo, antes de escribir
  nada.
- El `revert` reescribe por el **GUID guardado en el snapshot**, no por el del parámetro: si el adaptador cambia
  de índice o de nombre, el revert sigue apuntando al mismo dispositivo.
- Si al revertir el valor ya no está enumerado o el adaptador desapareció, la acción **lanza** y el motor marca
  `RollbackFailed`. No se traga el error. Hay test.

---

## Tabla de auditoría de identificadores

| Identificador | Archivo:línea | Uso |
|---|---|---|
| `INetworkAdapterController` | `src/FragForge.Core/Abstractions/IContracts.cs:87-91` | **no se cambió** (R-A) |
| `INetworkAdapterResolver` (nueva) | `src/FragForge.Core/Abstractions/IContracts.cs:140` | 5.1 |
| `NetworkAdapterIdentity` (nuevo) | `src/FragForge.Core/Abstractions/IContracts.cs:159` | identidad de hardware |
| `NetworkAdapterSelector` (nuevo) | `src/FragForge.Core/Abstractions/IContracts.cs:186` | selector |
| `IWmiQuery.QueryAsync` | `src/FragForge.Core/Abstractions/IContracts.cs:19-22` | firma del doble |
| `IRegistry.*` | `src/FragForge.Core/Abstractions/IRegistry.cs:3-12` | NetCfgInstanceId |
| `WindowsHardwareProfiler.NetAdapterProperties` | `src/FragForge.Actions/Platform/HardwareProfiler.cs:260-273` | reutilizada |
| `WindowsHardwareProfiler.MapPhysicalMedium` | `src/FragForge.Actions/Platform/HardwareProfiler.cs:287-292` | 14/9 |
| `NetAdapterScope` / `NetAdapterClass` | `src/FragForge.Actions/Platform/HardwareProfiler.cs:32-33` | `root\StandardCimv2` |
| `RealMsftNetAdapterProperties` (103) | `tests/FragForge.Tests/Core/NetAdapterPropertiesTests.cs` | fixture medido |
| `NetworkAdapterInfo` | `src/FragForge.Core/Models/HardwareProfile.cs:101-111` | campos añadidos aditivos |
| `MemoryModuleInfo` (patrón) | `src/FragForge.Core/Models/HardwareProfile.cs:64-74` | estilo de record |
| `AdapterParameters` | `src/FragForge.Core/Conditions/ConditionFactory.cs:48` | extendido |
| `AdapterCondition` | `src/FragForge.Core/Conditions/ConditionFactory.cs:108-121` | evalúa hardware ID |
| `TweakActionFactory` ctor | `src/FragForge.Actions/DependencyInjection/TweakActionFactory.cs:15` | `IWmiQuery? wmi` opcional |
| `TweakActionFactory.Create` | `.../TweakActionFactory.cs:23-41` | construye resolutor |
| `PreflightService` ctor | `src/FragForge.Core/Preflight/PreflightService.cs:6` | `NetworkChangePreflight?` opcional |
| `PreflightCheck` | `src/FragForge.Core/Models/Results.cs:77` | severidad |
| `PreflightReport.Passed` | `src/FragForge.Core/Models/Results.cs:80` | no bloquea |
| `TweakDefinition.SessionEligible` | `src/FragForge.Core/Models/TweakDefinition.cs:53` | patrón del campo nuevo |
| `TweakDefinition.RestartsNetworkLink` | `src/FragForge.Core/Models/TweakDefinition.cs:54-63` | **nuevo, aditivo** |
| `ProfileSelector.Select` | `src/FragForge.Core/Engine/ProfileSelector.cs:34` | conteos |
| `GameSessionService.EligibleIds` | `src/FragForge.Core/Sessions/GameSessionService.cs:54-59` | filtro de red |
| `TweakEngine` rollback | `src/FragForge.Core/Engine/TweakEngine.cs:237`, `:335` | `RollbackFailed` ya existía |
| `ProcessRequest` / `ProcessResult` | `src/FragForge.Core/Abstractions/IProcessRunner.cs:7-18` | PowerShell y manage-bde |
| `ActionNotApplicableException` | `src/FragForge.Core/Models/FragForgeException.cs:13` | No aplicable |
| `ProtectedTargetException` | `src/FragForge.Core/Models/FragForgeException.cs:12` | denylist de red |
| `EnsureNetworkPropertyAllowed` | `src/FragForge.Core/Persistence/ProtectionPolicy.cs:87-93` | R-D |
| `denylist.networkPropertyKeywords` | `src/FragForge.Core/Persistence/denylist.json` | 9 keywords |
| `CommandLineParser.Flags/ValueOptions` | `src/FragForge.Cli/CommandLine.cs:14-15` | `--host`, `--count` |
| `ExitCodes` | `src/FragForge.Cli/CommandLine.cs:34-42` | `Usage`, `MeasurementInvalid` |
| `ParsedArgs` | `src/FragForge.Cli/CommandLine.cs:3-11` | `Value`/`Has` |
| `FakeProcessRunner` | `tests/FragForge.Tests/Fakes/FakeProcessRunner.cs:5-16` | doble |
| `FakeRegistry` | `tests/FragForge.Tests/Fakes/FakeRegistry.cs:5-42` | doble |
| `Tweak.Create` | `tests/FragForge.Tests/Fakes/TestSupport.cs:18` | `with { RestartsNetworkLink }` |

**Sobre `InternalsVisibleTo`:** sigue sin existir en el repo. Por eso `NormalizeGuid` se declaró `public`
(el perfil y el resolutor lo comparten) y `NetworkAdvancedPropertyReader` es `public` **no sealed** con
`ReadAsync`/`WriteAsync` `virtual`, que es lo que permite a los tests de la acción sustituirlo sin PowerShell.

---

## Tests añadidos

| Archivo | Qué fija |
|---|---|
| `tests/FragForge.Tests/Network/NetworkAdapterResolverTests.cs` | resolución por GUID y por hardware ID; funciona con nombre y descripción renombrados; sin identidad → null; GUID normalizado; NetCfgInstanceId |
| `tests/FragForge.Tests/Network/NetworkAdvancedPropertyReaderTests.cs` | mapeo por posición con fixtures es-MX y en-US; no escribe valores no enumerados; solo `-InterfaceGuid`, nunca `-Name` |
| `tests/FragForge.Tests/Network/NetworkProbeTests.cs` | mediana/p99/jitter; **NA≠0**; sin host no mide; sin veredicto; mapeo 14/9 |
| `tests/FragForge.Tests/Network/BitLockerPreflightTests.cs` | **solo `-status`**; sin cifrado ≠ desconocido; nunca bloquea |
| `ActionTests.NetworkPropertyActionTests` | captura completa antes de escribir; R-L; denylist keyword por keyword; revert que falla lanza |
| `EngineTests` | aviso de corte antes de aplicar; sin aviso si no reinicia |
| `GameSessionServiceTests` | un tweak que reinicia red no entra en sesión |
| `NetAdapterPropertiesTests` | el resolutor solo pide propiedades que existen |

---

## Qué NO se verificó

- **Nada compilado ni ejecutado.** Sin compilador ni PowerShell.
- **`ValidRegistryValues` reales del I225-V**: no se enumeraron. No se sabe si el driver expone `*EEE`, cuántos
  valores acepta, ni si "0" es "desactivado". **Si no lo expone, el tweak es No aplicable**, que es el
  comportamiento implementado.
- **Comportamiento del reinicio del adaptador**: no se comprobó cuánto dura el corte ni si el enlace vuelve solo.
  El aviso se implementó asumiendo lo que documenta el driver, no una medición.
- **Mapeo `NdisPhysicalMedium`**: el 14 = Ethernet / 9 = Wi-Fi **no se midió en estas NICs**. La fuente del driver
  del I225-V confirma `*PhysicalMediaType = 14` en su `.inf`, pero el valor que devuelve `MSFT_NetAdapter` en
  esta PC no se leyó. Marcado NO VERIFICADO en el código y en el CLI.
- **`NetCfgInstanceId`**: no se comprobó que coincida con `InterfaceGuid` en esta máquina. El código tolera que
  no esté (queda `null`) y la resolución por GUID sigue funcionando.
- **BitLocker**: no se ejecutó `manage-bde`; los fixtures son del formato documentado, no de esta PC.
- **Ping**: no se lanzó ni un paquete. La estadística se validó con dobles y con cálculos en Python.
- **Wi-Fi AX210**: no se enumeraron sus propiedades reales (ahorro de energía, roaming). Por eso queda como
  recomendación de texto y no como tweak.
