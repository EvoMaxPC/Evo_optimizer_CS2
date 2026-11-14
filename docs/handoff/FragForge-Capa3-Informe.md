# FragForge — Capa 3: energía, planes y mantenimiento

**Fecha:** 2026-11-13
**Rama:** `arena/c1a5f702-evo-optimizer-cs2`
**Base:** `4dfb236` (pasada 1 de la Capa 3 cerrada)
**Estado:** código y pruebas **escritos y commiteados**; **NADA compilado ni ejecutado** (ver §0).

---

## 0. Advertencia previa

Este entorno sigue sin SDK de .NET: no hay `dotnet`, no hay paquete disponible y los hosts de
distribución están bloqueados (`api.nuget.org`, `builds.dotnet.microsoft.com`,
`dotnetcli.azureedge.net`, `release-assets.githubusercontent.com`, además de
`learn.microsoft.com`).

De esta capa hay **diseño, código y pruebas**, pero **cero evidencia de ejecución**. Todo lo que
sigue describe *lo que el código hace*, no *lo que se observó al correrlo*. Donde digo "verificado"
quiero decir "verificado estáticamente o simulado en Python", nunca "compilado".

Lo que sí se pudo comprobar, y se hizo:

- Los **5 JSON nuevos** pasan el schema real del catálogo (validación propia contra
  `$defs`, `required`, `enum`, `additionalProperties: false`), igual que los 30 archivos del catálogo.
- La tabla `ProfileMembership` coincide **exactamente** con los ids del catálogo (30 = 30, sin
  diferencias en ningún sentido).
- **Ningún ajuste de energía tiene dos dueños**: 8 ajustes declarados, 0 solapamientos.
- El parser `PowerCfgQueryParser` reproducido en Python sobre el fixture de la PN64 devuelve
  AC=5 para PERFEPP, AC=0 para CPMINCORES1 y AC=2 para PERFBOOSTMODE, que son los valores reales.
- `defrag <volumen> /L` y `DISM /Online /Cleanup-Image /StartComponentCleanup` están confirmados
  contra documentación; `/ResetBase` está prohibido y hay un test que lo impide.

---

## 1. Qué se hizo en la pasada 2

### 1.1 Tests con los fixtures reales de la PN64

`tests/FragForge.Tests/Actions/PowerCfgQueryParserTests.cs` gana el fixture `Pn64Output` (salida
real de `/qh` en es-MX) y tres pruebas:

| Test | Qué fija |
| --- | --- |
| `UsbSetting_ParsesWithoutAnAliasLine` | El bloque de USB no trae línea "Alias de GUID": el bloque se identifica por GUID, no por alias. |
| `CoreParkingVariant1_ParsesAsARange` | CPMINCORES1 (`…584`) se lee como rango 0..100 inc 1, con AC=0 y DC=0. |
| `TwoBlocksOfTheSameSubgroup_AreDistinguishedByTheirSettingGuid` | Dos ajustes del mismo subgrupo no se confunden: la clave es (subgrupo, ajuste). |

### 1.2 Cuatro tweaks nuevos

| Id | Perfiles | Evidencia | Riesgo | Ajuste |
| --- | --- | --- | --- | --- |
| `power.pcie-aspm-off` | `[]` | Low | Moderate | ASPM `ee12f906…` → AC 0 |
| `power.usb-selective-suspend-off` | `[]` | Low | Moderate | USB `48e6b7a6…` → AC 0 |
| `cpu.epp-performance` | `[Advanced]` | Low | Moderate | PERFEPP `…63/…64/…65` → AC 0 |
| `cpu.core-parking-off` | `[Advanced]` | Low | Moderate | CPMINCORES1 `…584` → AC 100 |

Todos: Kind `PowerPlan`, `settingsOnly`, `dependsOn: ["power.fragforge-performance"]`, solo AC,
`profiles` explícito, texto es-MX/en-US con `whenNotToApply`, sin promesa de FPS y Evidence honesta.

### 1.3 Un quinto tweak que **no** se publicó

`cpu.priority-separation-foreground` no está en el catálogo. Motivo en §3.

### 1.4 `skipUnenumeratedSettings`: el problema que apareció al aplicar los datos reales

La PN64 enumera **las tres** variantes de PERFEPP (verificado con powercfg /qh: sufijos 63 y 64 con AC 5, sufijo 65 con AC 0; una versién anterior de este texto decía "una sola" y era un error). El tweak declara tres (`…63/…64/…65`) porque
no se sabe cuál corresponde a qué. Con el Preflight tal como estaba, cualquier ajuste no enumerado
hacía **fallar el tweak entero**: el resultado habría sido "no aplica" en el equipo de referencia.

Se añadió un parámetro explícito al `PowerPlanAction`:

```jsonc
"skipUnenumeratedSettings": true
```

- **Con `true`**: las variantes que el sistema no enumera se omiten; se escriben las que sí. Si
  **ninguna** está enumerada, el tweak es `NotApplicable` (la bandera no autoriza a no escribir nada).
- **Con `false` (por defecto)**: comportamiento anterior, un ajuste ausente hace fallar el tweak
  entero. Así no se escribe "casi todo" en silencio.

La misma regla se aplica en `Preflight`, en la revalidación de `Apply` (antes de cada
`/setacvalueindex`) y en `Verify` (no se exige una variante que no se escribió). Tres pruebas nuevas
cubren los tres casos.

### 1.5 Cuatro sondas de solo lectura

`src/FragForge.Actions/Diagnostics/PowerQueryProbe.cs`. No son entradas de catálogo y aparecen en
`probe`/`doctor`:

| Clave | Qué lee | Nota |
| --- | --- | --- |
| `power.active-scheme` | `powercfg /getactivescheme`, **por GUID** | Si el perfil discrepa, se informa; no se elige uno. |
| `power.processor-settings` | `/qh`, AC + rango/lista por ajuste | Dice qué variantes **no** están enumeradas. |
| `power.boost-mode` | `/qh`, PERFBOOSTMODE | Finding si vale 0, 5 o 6. Solo lectura: no lo escribe. |
| `power.battery` | `FormFactor.HasBattery` (WMI) | **`unknown` ≠ `absent`**: son estados distintos. |

`powercfg` nunca se resuelve por `PATH`: se usa `Platform.ProcessRunner.SystemExecutable`. Si falla,
la confianza es `Unknown` y la nota lo explica; no se inventa un valor.

### 1.6 `fragforge maintenance`

`src/FragForge.Cli/MaintenanceCommands.cs`. Lista **cerrada** de verbos, argumentos **fijos**:

| Verbo | Comando | Nota |
| --- | --- | --- |
| `retrim` | `defrag.exe <unidad del sistema> /L` | Sintaxis confirmada: `/L` = retrim. |
| `component-cleanup` | `Dism.exe /Online /Cleanup-Image /StartComponentCleanup` | **`/ResetBase` prohibido**: haría las actualizaciones permanentes. |

- Exige `--confirm <verbo>`; sin coincidencia exacta no ejecuta y devuelve código 2.
- Se bloquea si `cs2.exe` corre o si hay modo sesión; si no se puede comprobar, **tampoco ejecuta**.
- Journal con comando exacto y código de salida.
- `Ensure*` que aplica: `EnsureFileAllowed` sobre la unidad del sistema para `retrim`. Para
  `component-cleanup` el almacén de componentes no está expuesto en `IProtectionPolicy`: se documenta
  y **no se simula** una comprobación que no existe.
- `--dry-run` imprime el comando sin ejecutar.
- Cinco pruebas fijan la lista cerrada, la ausencia de `/ResetBase` y el rechazo de verbos ajenos.

### 1.7 B3: textos de FPS

Se revisaron los 31 archivos. Los `debloat.*` ya decían "sin efecto medible esperado en FPS" y se
dejaron como estaban. Se normalizaron los 5 que prometían algo vago:

| Id | Antes | Ahora |
| --- | --- | --- |
| `power.fragforge-performance` | `Variable` | Texto exacto de B3 |
| `latency.game-dvr-capture-off` | `Variable; puede reducir…` | Texto exacto + matiz |
| `latency.game-mode-on` | `Variable, generalmente pequeño` | Texto exacto + "efecto pequeño" |
| `cpu.power-throttling-off` | `Bajo/variable` | "Efecto bajo y variable; no cuantificado. A/B" |
| `cs2.cpu-priority-high` | `Moderado/variable` | "No cuantificado… A/B" |

Ningún test asertaba `"Variable"` (comprobado con grep).

### 1.8 `docs/descartados.md`

Seis entradas con motivo y criterio de reapertura: `powercfg /h off`, overlay de modo de energía,
`IDLEDISABLE=1`, escribir `PERFBOOSTMODE`, `powercfg /powerthrottling` por aplicación, y el sexto
apartado sobre `priority-separation` (bloqueado, no descartado).

---

## 2. Tabla id → perfiles (catálogo completo, 30 ids)

| Perfil | Ids |
| --- | --- |
| **Safe** (3) | `diagnostics.system-inspect`, `latency.game-mode-on`, `latency.mouse-acceleration-off` |
| **Balanced** (+4 → 7) | `cpu.power-throttling-off`, `cs2.cpu-priority-high`, `latency.game-dvr-capture-off`, `latency.global-timer-resolution` |
| **Competitive** (+2 → 9) | `network.i225v-eee-off`, `power.fragforge-performance` |
| **Advanced** (+3 → 12) | `cs2.fullscreen-optimizations-off`, `cpu.core-parking-off`, `cpu.epp-performance` |
| **Ninguno** (`""`, 16) | `power.pcie-aspm-off`, `power.usb-selective-suspend-off` y los 14 `debloat.*` restantes (16 en total) |

> Los 16 `debloat.*` no pertenecen a ninguno de los cuatro perfiles. Los conteos por perfil no son
> excluyentes entre sí (Balanced incluye a Safe, etc.); lo que se cuenta aquí es la pertenencia
> declarada de cada id.

---

## 3. Lo que **no** se hizo, y por qué

### `cpu.priority-separation-foreground`: bloqueado por falta de verificación

Se diseñó entero, pero el valor propuesto era `0x26` y **no se pudo confirmar el significado de sus
bits** (`0b10_0110`). Publicarlo con el valor puesto habría significado escribir en el planificador
del sistema un número sin verificar; publicarlo con un texto que dijera "no aplicar" habría dejado un
tweak aplicable por id que escribe ese mismo número. Se decidió **no publicarlo** y dejarlo
documentado en `docs/descartados.md` §6 con su criterio de reapertura.

---

## 4. Qué queda pendiente

1. **Compilar y ejecutar** toda la capa. Es la deuda principal y sigue abierta.
2. **Confirmar el valor y los bits** de `Win32PrioritySeparation` y, si procede, publicar el quinto tweak.
3. **Verificar el `Ensure*` de `component-cleanup`**: hoy no existe comprobación porque el almacén de
   componentes no está modelado en `IProtectionPolicy`.
4. **`/ResetBase`**: la prohibición está puesta y probada; si algún día se quisiera exponer, tendría que
   ser con confirmación de riesgo explícita y nunca dentro de `component-cleanup`.
5. **Clase P-core/E-core**: la PN64 no la expone y **ningún texto la afirma**. No se debe asumir.
