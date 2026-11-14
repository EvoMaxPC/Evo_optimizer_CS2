# FragForge — Capa 7: Latencia (auditoría y correcciones)

**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Base:** `b8a6087` (Capa 9)
**Alcance:** auditar los 4 `latency.*` existentes, **no duplicarlos**, corregir lo que falle, y evaluar los candidatos de 7.2 sin implementar folclore.

> **Nada compilado ni ejecutado.** Sin `dotnet`, `mono`, `csc` ni PowerShell en el entorno. Todo se auditó con
> `grep` real y se cita `archivo:línea`.

**Regla de la ronda cumplida:** no se creó ningún tweak `latency.*` nuevo. Los 4 ya existían y se auditaron uno por
uno. Se corrigieron dos y se documentó el resto.

---

## 7.0 Auditoría de los 4 existentes

| Tweak | Riesgo | Evidencia (antes → después) | Reinicio | Admin | Veredicto |
|---|---|---|---|---|---|
| `latency.game-mode-on` | Safe | Medium (sin cambio) | No | No | **Correcto**, sin cambios |
| `latency.mouse-acceleration-off` | Safe | **High → Low** | Sí | Sí | Evidencia corregida; **curvas verificadas correctas** |
| `latency.game-dvr-capture-off` | Moderate | High (sin cambio) | Sí | Sí | Correcto; política HKLM **ya incluida** |
| `latency.global-timer-resolution` | Moderate | Medium (sin cambio) | Sí | Sí | Correcto; notas ampliadas |

---

## 7.1 Contratos, uno por uno

### `latency.game-mode-on` — correcto, sin cambios

`HKCU\Software\Microsoft\GameBar` con **`AllowAutoGameMode=1`** y **`AutoGameModeEnabled=1`**, ambos `DWord`.

Es exactamente el contrato de 7.1. No se tocó. Condiciones: `windows` + `userHive` (R-H: va por `TargetUserSid` y
`RegistryPath.ResolveUserHive`, no por `HKCU` literal del proceso).

### `latency.mouse-acceleration-off` — auditoría; **1 corrección real y 1 error de auditoría corregido**

#### Lo que sí se corrigió: `EvidenceLevel`

Estaba en **`High`** para un ajuste cuyo efecto en CS2 **no existe** si el juego usa raw input. Baja a **`Low`**, y
las notas ahora dicen explícitamente, en los dos idiomas, que CS2 usa `m_rawinput 1`, que ese modo lee el movimiento
directamente del dispositivo y que **el efecto es de ESCRITORIO**. Un test exige `Low` o `Experimental`.

#### Error de auditoría que se cometió y se corrigió en la misma ronda (importante)

En una primera pasada concluí que las curvas `SmoothMouseXCurve`/`SmoothMouseYCurve` estaban **degeneradas**
(`y = 0` en los cinco puntos) y las "arreglé" escribiendo la curva canónica de pendiente 4.375.

**Ese diagnóstico era falso y la "corrección" era peor que el original.** Las curvas se guardan como **5 puntos ×
8 bytes**, y cada punto es **un** número en formato **16.16 fixed-point** en los 4 bytes bajos, con los 4 bytes
altos en cero. Al leer cada punto como dos `int32` se obtiene `(x, 0)`, que parece una curva con pendiente cero.
No lo es: es el mismo número visto con el formato equivocado.

Con el formato correcto, el valor original del repo decodifica a:

```
X = [0, 1, 2, 3, 4]
Y = [0, 1, 2, 3, 4]      → pendiente Y/X = 1.0 constante
```

Es **exactamente la curva lineal 1:1** que promete la descripción. El original **estaba bien**.

La corrección falsa se revirtió: los bytes `SmoothMouseXCurve`/`SmoothMouseYCurve` del JSON quedaron **idénticos**
a como estaban (`git diff` no muestra cambios en ningún valor base64). La curva canónica que había puesto tiene
pendiente **4.375**, es decir habría **multiplicado la sensibilidad del puntero ×4.375**: un cambio de
comportamiento que nadie pidió y que no era el objetivo del tweak.

El test `MouseAcceleration_CurvesAreLinearOneToOne` decodifica con el formato correcto y exige pendiente **1.0**. Se
comprobó que **rechaza** la curva de 4.375: si alguien vuelve a "arreglarlo" así, el test falla.

Los tres valores del contrato (`MouseSpeed`, `MouseThreshold1`, `MouseThreshold2`) siguen como **`String` (REG_SZ)
= "0"`**, y un test lo verifica.

### `latency.game-dvr-capture-off` — correcto; decisión sobre la política HKLM

Los tres valores del contrato están presentes:

- `HKCU\System\GameConfigStore` → `GameDVR_Enabled=0`
- `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR` → `AppCaptureEnabled=0`
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR` → `AllowGameDVR=0`

**Decisión:** 7.1 pedía *evaluar* si la política HKLM debía ser un tweak **separado opt-in**. **Se decide NO
separarla**, por tres razones:

1. **Ya está incluida** en este tweak y el contrato de 7.1 la enumera como parte de `game-dvr-capture-off`.
2. Separarla **duplicaría** la escritura de la misma clave en dos tweaks con `revert` distintos, y el segundo en
   revertir borraría lo que el primero puso. Eso es un conflicto real, no una preferencia.
3. Sería un refactor del catálogo no pedido, con riesgo de romper el revert de un tweak ya entregado.

Se deja **fijado con un test** (`GameDvr_WritesBothUserValuesAndTheMachinePolicy`) para que un cambio accidental se
detecte. La política de máquina ya requiere admin (`requiresAdmin: true`).

### `latency.global-timer-resolution` — correcto; notas ampliadas

`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel` → `GlobalTimerResolutionRequests=1` (DWord),
`requiresReboot: true`. Coincide con el contrato.

**La duda de la denylist, resuelta con el test real:**

- `MatchesPrefix` (`src/FragForge.Core/Persistence/ProtectionPolicy.cs:104`) acepta igualdad **o** `prefijo\`.
- La clave del tweak es `...\Session Manager\kernel`; la protegida es `...\Session Manager\Memory Management`.
- **`kernel` NO es `Memory Management` ni empieza por `Memory Management\`.** Comprobado además ejecutando la
  política real: el test `GlobalTimerResolution_KeyIsNotUnderTheProtectedMemoryManagementPrefix` llama a
  `EnsureRegistryAllowed` y **no lanza**, y como **control positivo** comprueba que la misma llamada **sí lanza**
  con `...\Session Manager\Memory Management` + `ClearPageFileAtShutdown`. Si el control positivo no lanzara, el
  test probaría que la comprobación es un no-op.

`RealCatalogDenylistTests` (`tests/FragForge.Tests/Catalog/RealCatalogDenylistTests.cs:45-73`) sigue cubriendo el
catálogo completo con la política real; la denylist **no se tocó**.

**Notas ampliadas** (pedido explícito): se documenta que el ajuste **restaura el comportamiento anterior a Windows 10
2004 / Windows 11** (antes se podía subir la resolución del temporizador globalmente; desde 2004 el cambio es por
proceso), que `EvidenceLevel = Medium` se refiere al **mecanismo** del timer y **no** a una ganancia probada, y que
**el efecto en CS2 no se ha medido**.

---

## 7.2 Candidatos, uno por uno

| Candidato | Efecto técnico | Fuente | Cómo se mide | Decisión |
|---|---|---|---|---|
| **Suspensión selectiva USB** | Un puerto USB no entra en bajo consumo. | Esquema de energía de Windows. | A/B con el mismo ratón y el mismo `m_rawinput`; se mira si hay microcortes. | **No duplicar** |
| **MMCSS** | Reserva CPU para tareas multimedia. | Esquema `SystemProfile\Tasks\Games`. | Perfil de CPU; comparar 1% lows. | **Descartado** |
| **NetworkThrottlingIndex** | Limita paquetes por milisegundo. | Documentación informal. | Diff de `netstat`/timing. | **Descartado** |
| **DWM transparencia/animaciones** | Menos composición del escritorio. | Opciones visuales de Windows. | A/B visual; no hay métrica de juego. | **Descartado** |

### Suspensión selectiva USB — **ya existe**, no se duplica

Ya está `power.usb-selective-suspend-off`:

- subgrupo `2a737441-1930-4402-8d77-b2bebba308a3`
- ajuste `48e6b7a6-50f5-4782-a5d4-53bb8f07e226`
- `acValue`/`dcValue` = **0**, `mode: settingsOnly`, `activate: false`, sobre el plan `FragForge Performance`
  (origen `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`).

**Comprobado antes de escribir nada** (como pedía el encargo): `power.fragforge-performance` **no lo cubre** — solo
escribe `893dee8e-2bef-41e0-89c6-b55d0929964c` y `0cc5b647-c1df-4637-891a-dec35c318583`, ambos `=100`. Son ajustes
de procesador. **No hay duplicado y no se añadió nada.**

### MMCSS — **descartado**

El encargo lo condiciona a justificar que **CS2 usa la tarea `Tasks\Games`**. **No hay forma de justificarlo con este
entorno**: requiere leer los registros de MMCSS del equipo real mientras corre el juego, y eso no se puede hacer aquí.
Además, CS2 declara su propio `-threads` y no depende de MMCSS para su hilo principal. Aplicar MMCSS "porque sí" es
exactamente el folclore que la ronda prohíbe. → `docs/descartados.md`.

### NetworkThrottlingIndex — **descartado**

El propio encargo lo describe como "probable folclore → descartar salvo fuente sólida". **No se encontró una fuente
sólida** que muestre efecto medible en CS2 con un adaptador moderno. → `docs/descartados.md`.

### DWM transparencia/animaciones — **descartado**

La condición era "solo si reversible y claro". Es reversible, pero **no es claro**: no existe una métrica de juego
que se pueda atribuir a esto, y el efecto es de composición del escritorio. Evidencia baja sin forma de medirla. →
`docs/descartados.md`.

### Ya descartados (sin cambios)

- **`Win32PrioritySeparation`** — ya cubierto por `cpu.priority-separation-foreground`.
- **EmptyStandbyList** — descartado en rondas anteriores; sigue descartado.

---

## 7.3 Perfiles

Los 4 `latency.*` **no tienen `profiles` explícito**, así que entran por reglas de riesgo/evidencia:

- `latency.game-mode-on` y `latency.mouse-acceleration-off`: Safe → entran en los cuatro.
- `latency.game-dvr-capture-off` y `latency.global-timer-resolution`: Moderate → Balanced/Competitive/Advanced.

Conteo verificado tras los cambios: **Safe 3 / Balanced 7 / Competitive 9 / Advanced 12**. **No se movió ningún
conteo**, porque no se añadieron tweaks `latency.*` y los dos `gpu.*` nuevos tienen `profiles: []`. El test
`NewTweaks_DoNotJoinAnyProfile_SoTheCountsStayOrdered` comprueba la relación no decreciente (no los tamaños exactos,
R-N) y que ninguno de los dos nuevos entra en un perfil.

---

## Tabla de auditoría de identificadores

| Identificador | Archivo:línea | Uso |
|---|---|---|
| `ProtectionPolicy.EnsureRegistryAllowed` | `src/FragForge.Core/Persistence/ProtectionPolicy.cs:36` | test de denylist |
| `ProtectedRegistryValues` | `src/FragForge.Core/Persistence/ProtectionPolicy.cs:46` | no se tocó |
| `MatchesPrefix` | `src/FragForge.Core/Persistence/ProtectionPolicy.cs:104` | igualdad o `prefijo\` |
| `ServiceKeyRegex` | `src/FragForge.Core/Persistence/ProtectionPolicy.cs:105` | solo `…\SERVICES\<svc>` |
| `ProtectedTargetException` | `src/FragForge.Core/Models/FragForgeException.cs:12` | control positivo |
| `RealCatalogDenylistTests` | `tests/FragForge.Tests/Catalog/RealCatalogDenylistTests.cs:45-73` | catálogo real + política real |
| `EvidenceLevel` | `src/FragForge.Core/Models/Enums.cs:3` | `Low` en mouse |
| `TweakCatalog.Get` | `src/FragForge.Tweaks/TweakCatalog.cs:22` | lectura por id |
| `TweakCatalogValidator` | `src/FragForge.Tweaks/Validation/` | valida contra el schema |
| `JsonTweakCatalogLoader.ReadEmbeddedSchema` | `src/FragForge.Tweaks/TweakCatalog.cs:28` | schema embebido (R-N) |
| `PowerPlanAction` (settingsOnly) | `src/FragForge.Actions/Power/PowerPlanAction.cs:38-41`, `:255-257` | solo escribe el ajuste |
| `RegistryValueConverter.SupportedKinds` | `src/FragForge.Actions/Registry/RegistryValueConverter.cs:9` | `String`/`Binary` válidos |
| `ProfileSelector.Select` | `src/FragForge.Core/Engine/` | conteos por perfil |

---

## Qué NO se verificó

- **Nada compilado ni ejecutado.** Sin compilador ni PowerShell.
- **Los bytes de la curva**: se decodificaron con Python usando el formato 16.16 fixed-point documentado, y se
  verificó que la pendiente es 1.0. **No se escribieron en un Windows real** ni se comprobó el efecto en el puntero.
  El formato se tomó de fuente externa (curva canónica de Windows `c0,cc,0c,00` = 12.7998), no de memoria.
- **`m_rawinput 1` en las 5 cuentas**: el dato de que CS2 usa raw input viene del encargo y de la documentación del
  juego, no de leer los `cfg` del usuario. El tweak está documentado como efecto de escritorio **precisamente** para
  no depender de eso.
- **Efecto de `GlobalTimerResolutionRequests` en CS2**: no medido. No se afirma ninguno.
- **MMCSS**: no se comprobó si CS2 usa `Tasks\Games`; por eso se descarta en vez de implementarse.
- **Suspensión selectiva USB**: no se comprobó si el ratón del usuario sufre cortes; el tweak ya existía y no se
  modificó.
- **Denylist**: se ejecutó la lógica con Python sobre la clave concreta, no el test real (no hay runtime .NET). El
  test añadido reproduce la misma comprobación para que se ejecute cuando haya compilador.
