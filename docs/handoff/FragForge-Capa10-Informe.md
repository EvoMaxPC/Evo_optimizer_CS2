# FragForge — Capa 10: Seguridad y anticheat

**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Principio rector:** FragForge no inyecta, no abre handles a `cs2.exe`, no modifica archivos del juego, no
toca Steam ni procesos de anticheat. Ese contrato está **escrito** (`docs/seguridad.md`) y **probado**
(`tests/FragForge.Tests/Security/`).

**Compilación: DESCONOCIDO.** No hay `dotnet` en este entorno. Nada se compiló ni se ejecutó. La verificación
es estática: tree-sitter sobre cada `.cs` tocado, más comprobaciones empíricas con Python de las fixtures y
del JSON del catálogo. Esto se detalla en "Qué NO se verificó".

---

## Commits

| Hash | Asunto |
|---|---|
| `2e24434` | `feat(capa10-posture)` — postura de solo lectura, `security status`, panel, `anticheatNote` |
| `d53c80e` | `fix(capa10-resilience)` — catálogo externo malo no tumba la app |
| `500b5f9` | `feat(capa10-opt-in)` — 10.3 descartado con motivo, `DefinitionSha256` verificado, `docs/seguridad.md` |
| (este) | `docs` — este informe |

---

## 10.1 Postura de seguridad de solo lectura

`SecurityPosture` + `ISecurityPostureService` + `WindowsSecurityPostureService`, expuesto por
`fragforge security status` y por un panel en la App.

| Componente | Fuente | Parseo |
|---|---|---|
| VBS | `Win32_DeviceGuard.VirtualizationBasedSecurityStatus` (`root\Microsoft\Windows\DeviceGuard`) | 0 → Disabled, 2 → Enabled, resto → Unknown |
| HVCI | `SecurityServicesRunning` contiene **2**; si no, escenario de registro | Por valor de enum, no por texto |
| Secure Boot | Registro `UEFISecureBootEnabled`; si falta, `Confirm-SecureBootUEFI` | `true`/`false` literales del cmdlet |
| TPM | `Win32_Tpm` (`Security\MicrosoftTpm`) | `IsEnabled_InitialValue` |
| BitLocker | `manage-bde -status` (reutiliza el preflight de Capa 5) | Solo estado |
| Lista de drivers vulnerables | `CI\Config\VulnerableDriverBlocklistEnable` | DWORD 1/0, ausente → Unknown |
| Driver de terceros (PawnIO) | `Services\PawnIO\ImagePath` | `bool?`: null ≠ false |

**`Unknown` es de primera clase.** No se lee → `Unknown`, nunca `Disabled`. Casos concretos con test:

- Secure Boot en BIOS heredado: la consulta falla → `Unknown` (no `Disabled`).
- Valor de registro ausente → `Unknown` (el valor por defecto depende de la versión de Windows).
- Driver que no se pudo comprobar → `null` (no `false`).
- WMI que lanza → VBS y HVCI en `Unknown` con la observación del motivo.

**BitLocker**: se mapea solo `EncryptedProtected` → `Enabled` y `NotEncrypted` → `Disabled`. Un volumen
cifrado con la protección **suspendida** no está protegido pero tampoco está en claro: es `Unknown`.

**PawnIO se reporta como DATO.** No se afirma que ningún anticheat lo bloquee ni lo permita. El contrato
remite al proveedor.

### Panel en la App

`MainViewModel.SecurityPostureService` (inyectado por propiedad, mismo patrón que `Sessions`, para no tocar
el constructor), `SecuritySummary`, `SecurityContract` y un botón que relanza la lectura. Si el servicio no
está (tests, diseñador), el panel dice "no disponible" y no rompe.

---

## 10.2 Aviso de compatibilidad por tweak

Campo ADITIVO y opcional `anticheatNote: LocalizedText?` en `TweakDefinition`, declarado en
`tweak.schema.json` con `es-MX` y `en-US` obligatorios. Se muestra en una columna del `DataGrid`; cuando
falta, `AnticheatNote.Resolve` devuelve el **contrato común**, no una celda vacía.

La salvedad concreta se añadió al tweak que la necesita: `cs2.cpu-priority-high`, que escribe
**IFEO `PerfOptions`** sobre el nombre de imagen `cs2.exe`. Escribe en el registro del sistema y se aplica
antes de que el juego arranque; no abre el proceso. El aviso lo dice literalmente y remite al proveedor.

**Prohibido en el catálogo, con test**: "seguro con", "compatible con", "safe with", "allowed by"
(`AnticheatNoteTests.NoRealTweakClaimsAnticheatCompatibility`, sobre el catálogo real).

---

## 10.3 Tweak opt-in de alto riesgo — **DESCARTADO**, con tres motivos

El enunciado permitía descartarlo si no se podía garantizar el revert verificable. No se puede, y además hay
un bloqueo de política:

1. **Denylist.** `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard` está en `registryKeyPrefixes` y la clave
   del tweak cuelga de ese prefijo. Implementarlo exigiría quitarlo, y eso abriría **todo** DeviceGuard (VBS
   incluido), no solo HVCI. La denylist no se debilita.
2. **R-J / revert verificable.** El estado real de HVCI solo cambia tras reiniciar. `VerifyRevertAsync` podría
   confirmar el valor de registro, no la protección efectiva.
3. **Coste/beneficio.** Exige reinicio y debilita la atestación del equipo. Ser opt-in no lo hace buena idea.

Está desarrollado en `docs/descartados.md` con qué haría falta para reabrirlo. **Lo que sí se entrega** es la
lectura del estado en `security status`: el usuario que quiera cambiarlo lo hace a mano, sabiendo qué hace.

No se tocó Secure Boot ni TPM en ningún punto de esta ronda.

---

## 10.4 Resiliencia del catálogo externo

**Hueco real encontrado y cerrado.** `LoadDocuments` concatenaba embebidos y externos, acumulaba errores y
lanzaba: un solo JSON inválido en `%ProgramData%\FragForge\catalog` dejaba al usuario **sin aplicación** y se
perdía también el catálogo del binario.

Ahora hay dos rutas deliberadamente distintas:

| Ruta | Para | Comportamiento |
|---|---|---|
| `LoadDocuments` | Embebidos, tests de esquema | **Estricta**: lanza. Un fallo del binario debe verse |
| `LoadDocumentsResilient` | Catálogo del usuario | **Resiliente**: la entrada mala se descarta con motivo, el resto carga |

Rechazos cubiertos, todos con test:

- **Debugger** (IFEO) → `ProtectedTargetException`, motivo con el destino.
- **Servicio protegido** (`ipfsvc`) → rechazado.
- **Prefijo Memory Management** → rechazado.
- **JSON inválido** → rechazado por nombre de archivo, sin id.
- **ID duplicado de uno embebido** → rechazado, y **la primera definición gana**: un externo no puede
  reemplazar un tweak auditado. Si pudiera, bastaría con copiar el id para sustituir el contenido.
- **Depende de algo rechazado** → se cae también, iterando hasta que el conjunto se estabiliza, para no dejar
  dependencias colgando.

El rechazo por denylist ocurre **al cargar**, no al aplicar: el usuario ve el motivo en el arranque.
`TweakActionFactory` sigue siendo la última línea; esto adelanta el aviso, no relaja la política.

**Comprobación empírica del catálogo real**: se replicó la comprobación de política en Python sobre los 32
JSON del catálogo con la denylist real → **0 conflictos** (ningún servicio protegido, ningún prefijo
protegido, ningún valor protegido, ninguna imagen crítica). El catálogo embebido pasa la política.

El validador se descompuso en tres métodos aditivos (`ValidateSingle`, `ValidatePolicy`,
`ValidateReferences`); `ValidateCatalog` quedó intacto para no romper los tests de tiers y denylist que ya
existían.

---

## 10.5 Integridad

| Pieza | Estado antes | Qué se hizo |
|---|---|---|
| Journal | Ya detectaba manipulación | Se **cita** el test existente y se añade el del límite |
| Snapshots (`DefinitionSha256`) | **Se escribía y nadie lo comprobaba** | **Cerrado**: se verifica en el revert |
| RecoveryWriter | Ya verificaba SHA-256 por archivo | Se **cita** el test existente |
| Allowlist de herramientas | Ya verificaba hash antes de ejecutar | Se **cita** el test existente |

**El hueco de los snapshots** es el hallazgo de esta capa: `TweakEngine:463` guardaba `DefinitionSha256` y no
había ninguna lectura de ese campo fuera de las fixtures. El revert saca los parámetros de la definición
guardada en el snapshot, así que editar `snapshot.json` a mano permitía revertir a lo que dijera el archivo.
Ahora `VerifySnapshotDefinitions` lo comprueba antes de tocar nada y el revert falla con
`snapshot_definition_tampered`.

Se comprobó que la serialización usada al verificar es **idéntica** a la usada al calcular
(`JsonDefaults.Create(false)` en ambos sitios); si no lo fuera, el revert fallaría siempre.

Un snapshot **sin** hash (histórico) no se rechaza: se acepta. Romper la recuperación de datos antiguos
sería justo lo contrario de lo que hace falta.

**El journal NO es "firmado"**. Es tamper-evident (cadena SHA-256), no autenticado: sin clave, quien edite
una línea y recalcule la cadena obtiene un journal "válido". En vez de presentarlo como firma, se documenta el
límite y se escribe un test que demuestra la forja (`TheJournal_IsTamperEvident_ButNotAuthenticated`). En el
informe de la entrega anterior se llamó "firmado"; **esa palabra era incorrecta y queda corregida aquí.**

---

## 10.6 Privilegios

Un test por ruta:

| Ruta | Sin admin | Test |
|---|---|---|
| Leer perfil / postura | Sí | `Reading_DoesNotDependOnAdmin` |
| Aplicar | **No**: preflight `admin` = Blocking + remediación | `Apply_WithoutAdmin_IsBlockedWithAnActionableMessage` |
| Aplicar (con admin) | Sí | `Apply_WithAdmin_IsNotBlockedByPrivileges` |
| Dry-run | **Sí**: no escribe nada | `DryRun_WithoutAdmin_IsAllowed` |
| Plataforma no soportada | Su propio Blocking | `AnUnsupportedPlatform_ReportsItsOwnBlockingCheck` |

El bloqueo es del **preflight**, no de cada acción: el usuario ve el motivo antes de que se toque nada.
`--force` no omite el preflight de admin.

---

## 10.7 `docs/seguridad.md`

208 líneas con: el principio literal, la lista cerrada de lo que la app NO hace, **seis amenazas** con su
mitigación o su límite explícito (incluida la que no se puede cerrar: un atacante con escritura en disco),
la tabla de privilegios, la tabla de integridad y lo que queda pendiente.

Al escribirlo se detectó que una fila afirmaba que `KillProcess` bloquea procesos del juego. **No existe
ninguna acción de matar procesos en el código**: no hay `KillProcess` ni `OpenProcess`. La fila se corrigió
para decir la verdad (no hay con qué matarlos) en vez de atribuir una protección inexistente.

---

## Tabla de auditoría de identificadores

| Identificador | Archivo:línea | Uso |
|---|---|---|
| `ISecurityPostureService` (nueva) | `src/FragForge.Core/Abstractions/IContracts.cs:208` | 10.1, solo lectura |
| `SecurityPosture` (nuevo) | `src/FragForge.Core/Models/SecurityPosture.cs:17` | modelo |
| `SecurityPosture.Hvci` | `.../SecurityPosture.cs:23` | HVCI |
| `SecurityPosture.ThirdPartyKernelDriver` | `.../SecurityPosture.cs:53` | `bool?` |
| `SecurityPosture.AnticheatContract` | `.../SecurityPosture.cs:62` | contrato literal |
| `WindowsSecurityPostureService` | `src/FragForge.Actions/Security/WindowsSecurityPostureService.cs:26` | implementación |
| `DeviceGuardScope` | `.../WindowsSecurityPostureService.cs:33` | `root\Microsoft\Windows\DeviceGuard` |
| `HvciScenarioKey` | `.../WindowsSecurityPostureService.cs:37` | escenario de HVCI |
| `CiConfigKey` | `.../WindowsSecurityPostureService.cs:40` | lista de drivers vulnerables |
| `HvciServiceValue` | `.../WindowsSecurityPostureService.cs:46` | 2 = HVCI |
| `ReadAsync` | `.../WindowsSecurityPostureService.cs:48` | lectura completa |
| `ReadSecureBootAsync` | `.../WindowsSecurityPostureService.cs:172` | registro → cmdlet |
| `DetectThirdPartyDriverAsync` | `.../WindowsSecurityPostureService.cs:232` | PawnIO por nombre de servicio |
| `WmiValues` (internal → **public**) | `src/FragForge.Actions/Platform/HardwareProfiler.cs:11` | reutilizar parseo |
| `TpmScope` / `TpmClass` | `src/FragForge.Actions/Platform/HardwareProfiler.cs:24-26` | TPM |
| `BitLockerState` | `src/FragForge.Core/Preflight/NetworkChangePreflight.cs:128` | solo estado |
| `NetworkChangePreflight` | `src/FragForge.Composition/CompositionRoot.cs:103` | reutilizado |
| `SecurityCommands.RunAsync` | `src/FragForge.Cli/SecurityCommands.cs:20` | `security status` |
| `security` (verbo) | `src/FragForge.Cli/Program.cs` | dispatch |
| `SecurityPostureService` (VM) | `src/FragForge.App/ViewModels/MainViewModel.cs:118` | inyección por propiedad |
| `SecuritySummary` | `src/FragForge.App/ViewModels/MainViewModel.cs:124` | panel |
| `ReadSecurityAsync` | `src/FragForge.App/ViewModels/MainViewModel.cs:143` | comando |
| `TweakItemViewModel.AnticheatNote` | `src/FragForge.App/ViewModels/TweakItemViewModel.cs:24` | columna |
| `ISecurityPostureService` (DI) | `src/FragForge.Actions/DependencyInjection/ServiceCollectionExtensions.cs:42` | fábrica con `IsWindows` |
| `TweakDefinition.AnticheatNote` | `src/FragForge.Core/Models/TweakDefinition.cs:67` | campo aditivo |
| `anticheatNote` (esquema) | `src/FragForge.Tweaks/Schema/tweak.schema.json` | validación |
| `AnticheatNote.Resolve` | `src/FragForge.Core/Models/AnticheatNote.cs:24` | fallback |
| `LoadEmbeddedAsync` | `src/FragForge.Tweaks/TweakCatalog.cs:50` | embebidos + externos por separado |
| `LoadDocuments` (estricta) | `.../TweakCatalog.cs:86` | embebidos/tests |
| `LoadDocumentsResilient` | `.../TweakCatalog.cs:122` | **10.4** |
| `CatalogRejection` | `.../TweakCatalog.cs:194` | motivo visible |
| `CatalogLoadReport` | `.../TweakCatalog.cs:197` | reporte |
| `CatalogLoadReport` en `TweakCatalog` | `.../TweakCatalog.cs:30` | propiedad |
| `ValidateCatalog` (intacto) | `src/FragForge.Tweaks/Validation/TweakCatalogValidator.cs:20` | sin cambios |
| `ValidateSingle` | `.../TweakCatalogValidator.cs:78` | aditivo |
| `ValidatePolicy` | `.../TweakCatalogValidator.cs:121` | denylist al cargar |
| `ValidateReferences` | `.../TweakCatalogValidator.cs:137` | dependencias |
| `VerifySnapshotDefinitions` | `src/FragForge.Core/Engine/TweakEngine.cs:652` | **10.5** |
| llamada en revert | `src/FragForge.Core/Engine/TweakEngine.cs:296` | antes de tocar nada |
| `snapshot_definition_tampered` | `src/FragForge.Core/Engine/TweakEngine.cs:659` | código de error |
| `TweakCatalog.GetDefinitionHash` | `src/FragForge.Tweaks/TweakCatalog.cs:20` | origen del hash |
| `JsonDefaults.Create` | `src/FragForge.Core/Logging/JsonDefaults.cs:9` | serialización idéntica |
| `denylist.registryKeyPrefixes` | `src/FragForge.Core/Policy/denylist.json` | **bloquea 10.3** |
| `Admin = Blocking` | `src/FragForge.Core/Preflight/PreflightService.cs:29` | 10.6 |
| `ToolVerifier` | `src/FragForge.Actions/Measurement/ToolManifest.cs` | hash antes de ejecutar |
| `RejectedIds`/log | `src/FragForge.Composition/CompositionRoot.cs` | rechazos al log |

---

## Tests nuevos

| Archivo | Qué cubre |
|---|---|
| `tests/FragForge.Tests/Security/SecurityPostureTests.cs` | VBS por valor, HVCI en ejecución/configurado, Secure Boot heredado, blocklist, PawnIO, **leer no escribe** |
| `tests/FragForge.Tests/Security/AnticheatNoteTests.cs` | fallback, culturas, campo opcional, round-trip, **sin afirmaciones de compatibilidad** |
| `tests/FragForge.Tests/Security/ExternalCatalogResilienceTests.cs` | catálogo malicioso completo, duplicado que no sobrescribe, JSON roto, dependencia en cascada, estricta sigue estricta |
| `tests/FragForge.Tests/Security/IntegrityTests.cs` | journal detecta línea, journal **no autentica**, snapshot manipulado bloquea revert, revert normal funciona, recovery index |
| `tests/FragForge.Tests/Security/PrivilegeDegradationTests.cs` | una prueba por ruta de privilegio |

---

## Qué NO se verificó

- **Nada compilado ni ejecutado.** Sin `dotnet`. El árbol de tests nuevos no se ha visto correr. Los conteos
  de tests que aparecen arriba son de lectura del código, no de ejecución.
- **Valores reales de DeviceGuard en esta PC**: no se ejecutó la consulta. No se sabe si
  `SecurityServicesRunning` trae el 2, ni si el escenario de HVCI existe en el registro de este equipo. El
  parseo está probado con dobles.
- **Comportamiento de anticheats reales**: **no se probó ninguno**. No se afirma nada sobre VAC, FACEIT ni
  ningún otro. El texto del contrato es deliberadamente una remisión al proveedor.
- **`Confirm-SecureBootUEFI` en este equipo**: no se ejecutó. El camino de BIOS heredado está probado con un
  runner falso que devuelve `unknown`.
- **PawnIO**: no se comprobó si la clave del servicio existe realmente en esta PC; se afirma que está
  instalado según el comentario de `SensorSampler`, no por una lectura ejecutada.
- **`manage-bde`**: no se ejecutó; el mapeo a `FeatureState` está probado con dobles del preflight.
- **RecoveryWriter en el flujo real**: el motor **todavía no lo invoca** al aplicar. Es un hueco preexistente
  que esta ronda documenta pero no cierra (cerrarlo exige cambiar el flujo de `TweakEngine`, fuera de 10.5).
- **Firma del journal**: no existe y no se simuló. Lo que hay es tamper-evidence, con test que demuestra su
  límite.
- **`ILogger<CatalogLoadReport>` en el arranque**: se escribe por log, pero no se ejecutó la app para ver la
  línea.

## Decisiones que conviene revisar

1. **10.3 descartado por denylist**, no por falta de tiempo. Si la intención del proyecto era tener ese tweak,
   hay que decidir antes si DeviceGuard sale de la denylist — y este informe recomienda que no.
2. **`WmiValues` pasó de `internal` a `public`** para reutilizar el parseo en vez de duplicarlo. Es una
   ampliación de superficie pública en `FragForge.Actions`, reversible si molesta.
3. **El catálogo externo ya no puede provocar un fallo de arranque**, ni siquiera por un error del propio
   binario: los embebidos se validan aparte de forma estricta antes. Si esa validación falla, la app no
   arranca, que es lo correcto para un bug de entrega.
