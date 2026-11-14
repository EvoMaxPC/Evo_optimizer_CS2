# Descartados de la Capa 3 (y por qué)

Registro de las ideas que se evaluaron para la Capa 3 de energía y **no** se implementaron. Está aquí
para que nadie las vuelva a proponer sin leer antes el motivo. Cada entrada dice qué se descartó, con
qué evidencia y qué haría falta para reabrirlo.

> Criterio general: nada entra si no se puede revertir, si no se puede verificar con una lectura previa,
> o si promete algo que no se puede medir en este equipo. El presupuesto térmico es de 28 W y el
> gobierno de PL1/PL2 es de Intel DTT: no se toca ni se asume.

---

## 1. `powercfg /h off` (desactivar la hibernación)

**Descartado.** La hibernación ya está deshabilitada en los equipos objetivo. Escribirlo otra vez no
aporta nada y, si algún día estuviera activa, apagarla elimina la suspensión híbrida y el archivo
`hiberfil.sys` deja de existir: es un cambio de configuración del sistema que no forma parte del
objetivo de consistencia de frametime.

**Motivo en una línea:** no hay nada que ganar; el estado ya es el deseado.

**Para reabrirlo:** haría falta un caso real donde la hibernación esté activa *y* se demuestre un
efecto medible sobre el frametime. No se conoce ninguno.

---

## 2. Overlay de modo de energía (superponer un esquema sobre el activo)

**Descartado.** Se propuso escribir un "modo de energía" por encima del esquema activo. **Ese comando
no existe:** `powercfg /?` no lo documenta. Implementar algo que la herramienta oficial no expone
obligaría a tocar el registro sin verificación y sin reversión clara.

**Motivo en una línea:** la operación no existe en `powercfg`; inventarla sería escribir a ciegas.

**Para reabrirlo:** solo si Microsoft documenta la operación y se puede leer el estado previo por GUID.

---

## 3. `IDLEDISABLE=1` (desactivar la reducción de estado inactivo del procesador)

**Descartado.** Impide que los núcleos entren en estados de bajo consumo. Con un presupuesto de 28 W y
gobierno de DTT, eso **sube la temperatura de reposo** y deja menos margen térmico para el impulso
sostenido: el efecto neto puede ser peor que el estado actual, y no hay forma de medirlo aquí sin A/B
largo.

**Motivo en una línea:** gasta margen térmico en reposo a cambio de un beneficio no demostrado.

**Para reabrirlo:** A/B con telemetría de temperatura y de frametime en este mismo equipo. Sin eso, no.

---

## 4. Escribir `PERFBOOSTMODE` (modo de impulso del procesador)

**Descartado.** En la PN64 el ajuste ya vale **2** (impulso habilitado), que es el estado que se
quiere. Además, de los valores que el sistema enumera para este ajuste, los que este proyecto no
quiere escribir son justamente los peores:

- `5` y `6` son modos **agresivos**: en un equipo de 28 W con DTT suben la temperatura y recortan el
  impulso sostenido. Escribirlos sería empeorar el objetivo declarado.
- `0` deshabilita el impulso: es un cambio con efecto claro y no deseado.

Por eso `power.boost-mode` queda como **sonda de solo lectura**: informa si el valor está en 0, 5 o 6,
y no escribe nada.

**Motivo en una línea:** el valor actual ya es el bueno, y los alternativos son perjudiciales en este
equipo. Se observa, no se escribe.

**Para reabrirlo:** solo si un equipo concreto llega con un valor perjudicial y existe una razón
documentada para corregirlo por hardware específico (no por regla general).

---

## 5. `powercfg /powerthrottling` por aplicación (EcoQoS por proceso)

**Descartado para esta pasada.** `powercfg /powerthrottling` permite aplicar o quitar la limitación de
energía por proceso (EcoQoS) a ejecutables concretos, incluido `cs2.exe`. Funcionalmente encaja, pero
requiere un **Kind de acción nuevo** (no es `Registry` ni `PowerPlan`, y su estado capturable es una
lista de procesos con su modo, no un valor de registro ni un índice de energía) y, con ello, una
entrada nueva en el contrato de `ActionState` y su serialización.

El Bloque 3 de esta capa cerró justamente el contrato de `PowerPlanActionState`. Añadir otro Kind en la
misma pasada sin el ciclo completo de pruebas (captura, reversión, ida y vuelta de JSON, DryRun) sería
exactamente el tipo de cambio apresurado que el proyecto evita.

**Motivo en una línea:** no es un valor de energía sino una lista de procesos: necesita Kind propio y
pasa a una pasada posterior.

**Para reabrirlo (criterios de aceptación):**
1. Kind nuevo (p. ej. `PowerThrottling`) con su `ActionState` registrado en `JsonDerivedType` y probado
   de ida y vuelta.
2. Captura de la lista previa por ejecutable, y reversión que restaure solo esos ejecutables.
3. `Ensure*` de `IProtectionPolicy` que aplique, y prueba de `PolicyRejected`.
4. DryRun que muestre la lista de procesos objetivo sin ejecutar.
5. Verificación posterior a la aplicación (leer y comparar), igual que `PowerPlanAction`.
6. Texto bilingüe sin promesa de FPS y con A/B obligatorio.

---

## 6. `cpu.priority-separation-foreground` — NO descartado, **bloqueado por verificación**

**No se implementó en esta pasada.** Se diseñó completo (Evidencia `Low`, riesgo `Experimental`,
perfiles `[]`, A/B obligatorio, efecto pequeño esperado, texto bilingüe con `whenNotToApply`), pero el
valor objetivo propuesto era `0x26` y **no se pudo verificar el significado de sus bits**:

```
0x26 = 0b10_0110
  bits[1:0] = 10  → ¿quantum variable?
  bits[3:2] = 01  → ¿quantum corto?
  bits[5:4] = 10  → ¿reparto 3:1?
```

Esa lectura es una hipótesis, no un hecho documentado en una fuente que pueda citarse. Como el valor
controla el reparto de CPU entre primer plano y fondo, escribir el número equivocado no es cosmético:
cambia el comportamiento del planificador en todo el sistema.

**Qué se decidió:** no publicar un tweak cuyo propio texto diga "no aplicar hasta confirmar el valor".
Un tweak así se puede aplicar por id (el opt-in no impide la aplicación por id) y escribiría un valor
sin verificar. Es mejor que no exista a que exista mal etiquetado.

**Para reabrirlo:** confirmar el significado de los bits con una fuente primaria, fijar el valor,
añadirlo al catálogo con `evidence: Low`, `profiles: []`, A/B obligatorio y el texto de efecto pequeño.
Al añadirlo, hay que volver a incluir su id en la tabla `ProfileMembership` del test del catálogo.

---

## Cómo se usa este archivo

Si un tweak aparece aquí, **no** debe añadirse al catálogo sin cumplir la sección "para reabrirlo". El
test `ProfileMembership_MatchesTheDeclaredTable_ForEveryCatalogId` obliga a declarar perfiles para todo
id del catálogo; este documento obliga a justificar por qué algunos ids no existen.

---

## `--stop_existing_session` de PresentMon (rechazado en la ronda de Capa 1, contrato 2.6.0)

El contrato de PresentMon 2.6.0 de esa ronda pedía añadir `--stop_existing_session` al comando de captura.
**No se implementó.** Tres motivos, en orden de peso:

1. **Choca con R-M.** La opción mata sesiones ETW existentes. FragForge no mata nada ajeno sin
   confirmación explícita, y una sesión ETW puede pertenecer a LatencyMon, a un profiler del usuario o a
   Intel PresentMon Service. El test `PresentMonIsNeverInvokedWithStopExistingSession`
   (`SourceHygieneAndAnalysisTests`) lo prohíbe de forma deliberada para cualquier `.cs` de `src/`.

2. **No tendría efecto útil con el diseño actual.** La opción opera sobre el nombre de sesión. FragForge
   genera un nombre único por corrida (`FragForge-<runId>`, `PresentMonFrameCapture.SessionName`), así que
   **no existe una sesión previa con ese nombre** que detener. Sería un no-op que solo añade riesgo.

3. ~~**Comportamiento no verificado.**~~ **CORREGIDO por la adenda 2: el flag SÍ está verificado.** Se
   ejecutó `--session_name FragForge --stop_existing_session` en la PC objetivo: exit 0 y sin procesos
   residuales. Lo que sigue **sin** verificarse es si afecta a sesiones de OTROS nombres; solo se probó
   con el nombre propio. Los motivos 1 y 2 se mantienen intactos, y son suficientes por sí solos.

**Para reabrirlo:** hacerlo con un nombre de sesión fijo y documentado, cambiar primero el test de higiene
(de forma consciente y explicada), probar en la PC objetivo qué ocurre con una sesión ajena activa, y
exigir confirmación interactiva al usuario. Mientras tanto, la captura falla de forma limpia si hay una
sesión con el mismo nombre, que es el comportamiento seguro.

---

## Hotfix Capa 0 — lo que NO se implementó y por qué

### Añadir `FeatureState.Unknown` (pedido en la Etapa 2)

**No se hizo: no hacía falta.** `FeatureState` ya es `{ Unknown, Enabled, Disabled, NotPresent }`
(`src/FragForge.Core/Models/Enums.cs:8`), con `Unknown = 0`, que es además el valor por defecto.
Agregar un miembro habría cambiado el contrato JSON sin necesidad y habría roto el round-trip de los
documentos existentes en un caso donde el enum ya cubría el requisito. El bug real no era el enum,
sino la conversión "excepción → lista vacía → NotPresent", que sí se corrigió.

### Cambiar el namespace de `MSFT_NetAdapter`

**No se cambió: ya era el correcto.** `root\StandardCimv2` estaba bien desde el principio
(`HardwareProfiler.cs:91`). El aviso "Consulta no válida" no venía del scope sino de la consulta.
Cambiarlo a otro namespace habría roto una parte que funciona. Lo que sí se corrigió fue la consulta:
se piden las propiedades en dos pasos para que una propiedad inexistente no tumbe las nueve.

### Test de UI de los combos (Etapa 4)

**No se puede, y no se inventó.** `FragForge.App` apunta a `net10.0-windows` con `UseWPF`, mientras
que `FragForge.Tests` apunta a `net10.0`. El proyecto de tests **no puede** referenciar la App sin
retargetear toda la suite, cosa que excede el alcance de este hotfix. Lo que sí quedó testeado es la
lógica pura de selección (`CultureDefaults.PickInitialCulture`), que es donde estaba el fallo de
"el valor por defecto no existe en la lista". Que WPF pinte la selección no se verificó aquí: eso lo
hace el usuario con `dotnet run`.

### Estilo oscuro completo de los controles

**No se hizo: fuera de alcance.** El hotfix pedía arreglar el encabezado de la tabla, y eso se hizo.
El mismo problema de chrome claro afecta a `ComboBox`, `CheckBox` y `ScrollBar` (no tienen plantilla
propia). Rediseñar el tema completo no forma parte de "cambio de estilo mínimo", así que queda
anotado como deuda visible en vez de meterse de contrabando en este cambio.

---

## Capa 8 — CS2

### Preferencia de GPU para cs2.exe (`UserGpuPreferences`) — 8.5

**Descartado. No se implementa.** La preferencia de GPU por aplicación se guarda en
`HKCU\Software\Microsoft\DirectX\UserGpuPreferences` y solo tiene efecto cuando hay **más de un**
adaptador entre los que elegir.

El hardware objetivo de este proyecto es fijo: **i3-1220P con Intel UHD como única iGPU**. No hay
segunda GPU, así que la clave no puede cambiar qué procesador gráfico usa el juego: no hay nada que
elegir. Añadirla al catálogo habría sido un tweak que no hace nada, con el coste de un valor más en el
registro del usuario y una reversión más que mantener.

Si en el futuro se apunta a equipos con GPU dedicada habría que reabrirlo, y entonces habría que medirlo
antes de prometer nada (R-K).

### Cuadrado de la resolución: usar `SteamGameLocator` en vez de un resolvedor nuevo

**Se descartó reescribir el existente.** `SteamGameLocator`
(`src/FragForge.Actions/Files/SteamGameLocator.cs:8`) compone la ruta con la carpeta **fija**
`steamapps\common\Counter-Strike Global Offensive` y no lee `appmanifest_730.acf`. Se podría haber
cambiado, pero lo consume el perfil de hardware (`HardwareProfiler.cs:165`) y por tanto el motor y la
detección de condiciones `cs2`. Cambiarlo habría sido tocar algo que funciona para resolver un caso
(instalación con carpeta renombrada) sin poder medirlo. Se añadió `SteamCs2Resolver` aparte y se dejó el
locator intacto.

---

## Capa 6 y Capa 7 — Latencia y iGPU

### MMCSS (`SystemProfile\Tasks\Games`)

**Descartado.** La condición de la ronda era justificar que CS2 use la tarea `Tasks\Games` de MMCSS. **No se
pudo justificar**: haría falta leer el estado de MMCSS en el equipo real mientras corre el juego, y este entorno
no tiene Windows. Además CS2 gestiona sus propios hilos (`-threads`) y no depende de MMCSS para su hilo
principal. Aplicarlo "porque hay un tweak famoso que lo hace" es folclore, y la ronda lo prohíbe sin fuente.

Se reabriría si alguien midiera el reparto de CPU de `cs2.exe` con y sin la tarea `Games` en la misma escena.

### NetworkThrottlingIndex

**Descartado.** El propio encargo lo describía como "probable folclore → descartar salvo fuente sólida". No se
encontró una fuente sólida con efecto medible en un adaptador moderno: el ajuste limita paquetes por milisegundo
y el tráfico de CS2 es de decenas por segundo. No hay forma de medirlo aquí, y sin medición no entra.

### DWM: transparencias y animaciones

**Descartado.** La condición era "solo si reversible y claro". Es reversible, pero **no es claro**: el efecto es de
composición del escritorio y no existe una métrica de juego que se le pueda atribuir. Evidencia baja sin forma de
medirla → fuera. El estado de DWM y de MPO ya se **lee** (no se toca) en
`src/FragForge.Actions/Diagnostics/DiagnosticProbes.cs:31`.

### MPO (`OverlayTestMode`)

**Descartado como tweak.** Corrige parpadeos y artefactos de composición conocidos; **no aporta FPS** ni mejora la
latencia. Ya existe como sonda de solo lectura que informa `DisabledByOverride` cuando vale `5`
(`src/FragForge.Actions/Diagnostics/DiagnosticProbes.cs:31`). Escribirlo habría sido cambiar un problema visual por
otro sin poder medir nada.

### Claves de clase del driver de pantalla `{4d36e968-c1e0-4863-9cf1-...}`

**Descartado.** Tocar claves bajo la clase del adaptador de pantalla es una operación de driver. R-J exige
`RecoveryWriter`, preflight de BitLocker en solo lectura y un revert verificable; además, un valor equivocado deja
la pantalla en negro sin acceso. No hay forma de verificarlo en este entorno, y el beneficio no está medido.

### Forzar la resolución o la frecuencia de refresco

**Descartado.** La pantalla y el refresco **se leen, no se fuerzan**. Forzarlos puede dejar el monitor en negro y
el encargo prohíbe hardcodear 200 Hz / 5 ms. El perfil ya lee `CurrentHorizontalResolution`,
`CurrentVerticalResolution`, `CurrentRefreshRate` (`src/FragForge.Actions/Platform/HardwareProfiler.cs:101`).

### `DirectXUserGlobalSettings` con `SwapEffectUpgradeEnable=1;`

**Descartado por un defecto de la acción existente, no por falta de fundamento.** El valor de DirectX es una
cadena **separada por `;`**, pero `AddToken` une y separa tokens **por espacio**:
escribe con `original + " " + token` (`src/FragForge.Actions/Registry/RegistryAction.cs:75`) y comprueba con
`value.Split(' ')` (`src/FragForge.Actions/Registry/RegistryAction.cs:143`).

Con `;` la cadena resultante sería inválida y el `verify` no la reconocería. Las dos salidas posibles eran:
(1) escribir la cadena completa como `String`, lo que **pisa** las demás preferencias del usuario —prohibido por la
propia ronda—, o (2) cambiar `RegistryAction` para aceptar un separador, que es un refactor fuera del alcance.
Se descarta hasta que se haga (2) de forma explícita, con su test de fusión y su revert exacto.

### Separar `AllowGameDVR=0` en un tweak propio

**Descartado.** La política de máquina ya está **dentro** de `latency.game-dvr-capture-off` y el contrato de la
ronda la enumera ahí. Separarla crearía dos tweaks que escriben la misma clave con `revert` distintos: al revertir
uno se borraría lo que puso el otro. Habría sido un conflicto real. Se dejó como está y se fijó con un test.

### Crear un tweak nuevo de suspensión selectiva USB

**No se añadió: ya existía.** (No es un descarte técnico, es una decisión de no duplicar.) `power.usb-selective-suspend-off` ya escribe los GUID exactos del encargo
(`2a737441-1930-4402-8d77-b2bebba308a3` / `48e6b7a6-50f5-4782-a5d4-53bb8f07e226` = 0, CA y CC) y
`power.fragforge-performance` **no** los cubre (solo escribe `893dee8e-…` y `0cc5b647-…` = 100). Se revisó antes
de escribir nada y no se añadió un segundo tweak.

---

## Capa 5 — Red

### `*InterruptModeration` off en el I225-V — **descartado: choca con la denylist**

**No se implementó.** No es por falta de fundamento técnico: desactivar la moderación de interrupciones hace
que el adaptador interrumpa por paquete, lo que puede reducir la latencia de interrupción. Pero
`*InterruptModeration` figura en `networkPropertyKeywords` de `denylist.json`, y **la denylist no se debilita**
(REGLA). La acción lo rechaza con `ProtectedTargetException` y hay un test que lo fija keyword por keyword.

Por qué está protegida tiene sentido: la moderación de interrupciones afecta al consumo y al calor de todo el
equipo, y el proyecto decidió que no se toca desde el catálogo.

### `*FlowControl` off — **descartado: choca con la denylist**

Mismo caso: `*FlowControl` está en `networkPropertyKeywords`. Además, la evidencia es baja incluso sin la
denylist: en una red doméstica sin congestión el control de flujo apenas actúa, y en un enlace saturado quitarlo
puede aumentar la pérdida. No hay fuente que demuestre mejora en CS2.

### RSS y RSC (`*RSS`, `*RscIPv4`, `*RscIPv6`) — **descartados: denylist**

Las cuatro keywords están protegidas. En particular **Receive Segment Coalescing** afecta a la latencia de TCP y
CS2 va por UDP (Steam Datagram Relay): aunque se pudiera escribir, no ayudaría al juego. El propio encargo lo
reconoce ("no ayuda a UDP").

### `Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing` — **descartado**

Es un ajuste **global del sistema**, no del adaptador: afecta a todas las conexiones del equipo, no solo al
juego, y no es reversible por adaptador con el snapshot actual. Además solo interviene en TCP; CS2 usa UDP. No
se implementa.

### LSO (`*LsoV2IPv4` / `*LsoV2IPv6`) — **descartado**

Offload de segmentación en **transmisión TCP**. No está en la denylist, pero no toca UDP ni el camino de
recepción del juego, y no hay fuente con efecto medible en CS2. Folclore para este caso de uso.

### TcpAckFrequency / TCPNoDelay por interfaz — **descartado**

Los dos son ajustes de TCP. CS2 usa **UDP** (Steam Datagram Relay), así que no pueden afectar a su tráfico. Su
efecto, si existe, es sobre otras aplicaciones TCP del equipo.

### NetworkThrottlingIndex — **descartado**

Ver la sección de Capa 7: folclore sin fuente sólida. Se descarta otra vez aquí por ser un candidato de red.

### Cambios de DNS — **descartado**

El DNS resuelve nombres; no interviene en la latencia de un flujo UDP ya establecido contra un relay. Cambiar
los servidores DNS no es un ajuste de latencia de juego y puede empeorar la privacidad y el rendimiento de
resolución. Fuera del alcance.

### QoS DSCP — **descartado**

Marcar paquetes con DSCP solo sirve si el **router** respeta esas marcas y aplica colas. Depende de un
dispositivo que FragForge no controla ni puede verificar, y en una red doméstica el tráfico suele recibir
best-effort. Sin forma de medirlo desde el equipo, no entra.

### Wi-Fi AX210: ahorro de energía y roaming — **descartado (no se implementó)**

El encargo lo condiciona a que el adaptador de la ruta por defecto **sea Wi-Fi**. En esta PC la ruta de juego es
Ethernet (I225-V); el AX210 está como alternativa. Además, los nombres de esas propiedades varían por driver y
habría que enumerarlas en el equipo real, cosa que no se puede hacer aquí. Se deja **solo la recomendación en
texto**: para jugar, Ethernet; el Wi-Fi queda como respaldo.

### Forzar el tipo de medio o la velocidad del enlace — **descartado**

La velocidad y el estado **se leen, no se fuerzan**. Fijar una velocidad en un adaptador 2.5G puede dejar el
enlace sin negociar y cortar la conexión de forma persistente.

### "Adaptador por defecto = el único activo con puerta de enlace" — **descartado**

Fue el primer criterio del diagnóstico de 5.5 y **está mal en esta PC**. Con el I225-V y la AX210 activos a la
vez, ambos pueden tener gateway configurada, así que "el único con gateway" no encuentra ninguno y devuelve
`null` para siempre. La puerta de enlace solo dice que el adaptador **puede** enrutar; no dice cuál eligió el
stack para un destino concreto.

Lo correcto es preguntárselo al stack: **`GetBestInterface`** (`iphlpapi.dll`) devuelve el índice de la interfaz
que Windows usaría para alcanzar una dirección IPv4. Se consulta contra `192.0.2.1` (TEST-NET-1, reservado para
documentación): **no se envía ni un paquete**, solo se pide la decisión de enrutado. Implementado en
`src/FragForge.Actions/Measurement/DefaultRouteProbe.cs`.

`NetworkInterface.GetAllNetworkInterfaces()` se sigue usando para el índice IPv4 de cada adaptador
(`GetIPv4Properties().Index`), pero **no** para inferir la ruta.

---

# Descartados de la Capa 10 (seguridad y anticheat)

## 10.3 — Tweak opt-in "Integridad de memoria off" (HVCI)

**Descartado.** El enunciado lo condicionaba a "solo si lo justificas" y a que el revert fuera verificable.

**El bloqueo, en una línea:** `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard` está en
`denylist.json:registryKeyPrefixes`, y el destino del tweak
(`...\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity`) cuelga de ese prefijo. Las reglas de la casa
dicen que la denylist **nunca se debilita**. Implementarlo obligaría a quitar el prefijo, y eso no abriría
solo HVCI: abriría todo DeviceGuard, incluidos VBS y todos sus escenarios. Es exactamente el tipo de
"pequeña excepción" que convierte una política en un adorno.

**Segundo motivo, independiente:** R-J pide revert **verificable**. El estado real de HVCI solo cambia tras
reiniciar, así que `VerifyRevertAsync` no puede confirmarlo en el momento: en el mejor caso verifica el valor
de registro, no la protección efectiva. Un tweak de seguridad que no se puede verificar después de aplicarlo
no cumple R-J.

**Tercer motivo:** exige reinicio, rompe la atestación de dispositivos y debilita la base de seguridad del
sistema. Que sea opt-in no lo hace buena idea.

**Lo que sí se entrega:** `fragforge security status` informa del estado de HVCI por WMI
(`SecurityServicesRunning`, valor 2) y por el escenario de registro, ambos de SOLO LECTURA. El usuario que
quiera desactivarlo lo hace a mano, sabiendo lo que hace, fuera de FragForge.

**Para reabrirlo** haría falta: (a) una decisión explícita de sacar DeviceGuard de la denylist —que este
proyecto no contempla—, y (b) un mecanismo de verificación post-reinicio que hoy no existe.

## 10.3-bis — Tweak de "debilitar Secure Boot / TPM"

**Descartado de entrada.** No se evalúa siquiera: `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot` está en
la denylist y no existe acción de firmware. Tocar Secure Boot o el TPM no es un tweak de rendimiento, es
bajar la postura de seguridad del equipo.

## Journal "firmado" con clave

**Descartado en esta ronda.** El journal es **tamper-evident** (cadena SHA-256 encadenada por
`PreviousHash`), no **autenticado**: no hay clave, así que quien pueda escribir el archivo puede recalcular
la cadena. Firmarlo de verdad exigiría gestión de claves (dónde vive, quién la rota, qué pasa si se pierde,
qué pasa en un equipo sin TPM disponible) y ese diseño no está hecho. Prometer "journal firmado" sin clave
sería mentir sobre la garantía. Se documenta el límite en `docs/seguridad.md` §3-D y §8, con un test que
demuestra la forja (`IntegrityTests.TheJournal_IsTamperEvident_ButNotAuthenticated`).

## Afirmar compatibilidad con VAC/FACEIT en los avisos

**Descartado.** No hay fuente que lo respalde y los requisitos cambian sin aviso. Los textos dicen lo que la
app hace y remiten al proveedor. Hay un test sobre el catálogo real que prohíbe "seguro con",
"compatible con", "safe with" y "allowed by" en `anticheatNote`.

---

# Capa 11 — UI, instalador, firma y E2E

## Auto-update

**Descartado.** El enunciado lo prohíbe y además no tendría sentido: un actualizador automático es una
descarga remota, y el proyecto entero se apoya en "todo offline, nada se descarga". Se documenta aquí para que
nadie lo proponga como mejora: el `.iss` lleva `DisableAutoUpdating=yes` a propósito.

## Telemetría

**Descartada**, también por enunciado y por coherencia. No hay ningún punto del código que envíe nada a
ninguna parte. Lo único que sale del equipo es lo que el usuario exporta a mano con `export-report`.

## `asInvoker` con elevación bajo demanda

**Descartado.** Analizado en `docs/elevacion.md`. El motivo corto: la postura de seguridad lee `Win32_Tpm` en
`root\CIMV2\Security\MicrosoftTpm` (`HardwareProfiler.cs:28`), que exige elevación; sin ella el TPM saldría
`Unknown` siempre. Además aplicar no es "una llamada" sino un motor con snapshot y journal, así que partir el
proceso obligaría a serializar el plan y a resolver `TargetUserSid` en el segundo proceso. Coste alto para
evitar un UAC.

## `highestAvailable`

**Descartado.** En una cuenta estándar se comporta como `asInvoker` (falla igual) y en una de administrador
funciona igual que `requireAdministrator`. No aporta nada y hace que el usuario no sepa en qué modo está.

## Firmar en CI

**Descartado.** Necesitaría el certificado y su contraseña como secretos de GitHub. El enunciado lo prohíbe de
forma explícita ("PROHIBIDO commitear certificados, contraseñas o tokens") y un certificado de firma de código
en un CI público es un objetivo de ataque. La firma es un paso local y documentado en `docs/firma.md`.

## `-warnaserror` global en el job de Windows

**Descartado por ahora.** La solución incluye App (WPF) y CLI, que nunca se compilaron en el entorno donde se
escribió la Capa 11. Poner el flag global convertiría un aviso preexistente de WPF en un build rojo que no
tiene nada que ver con el código nuevo. Core sí rompe con warnings (`Directory.Build.props`), y el job de Linux
usa `-warnaserror` sobre la cadena completa que compila. El siguiente paso, cuando se haya compilado una vez en
Windows, es extenderlo.

## Pestañas con la lógica dentro

**Descartado.** Las pestañas de la Capa 11.3 solo enlazan a comandos y colecciones que ya existían en
`MainViewModel`. Meter lógica nueva en el XAML habría dejado esa lógica sin tests y fuera de `FragForge.UI.Core`,
que es exactamente el problema que la Capa 11.2 acaba de resolver.

## Sustituir el desinstalador de Inno por uno propio

**Descartado.** El riesgo del desinstalador no era que Inno lo generara, sino qué hace al ejecutarse. Se
resuelve en `CurUninstallStepChanged` (dos confirmaciones explícitas, en `usUninstall`, antes de borrar nada) y
no reescribiendo el instalador.
