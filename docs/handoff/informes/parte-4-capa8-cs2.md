# Parte 4 — Capa 8: CS2

**Commit** `4dd2000` — 1 commit, 14 ficheros, +1683, **+25 tests** (490 → 515)
**Informe de detalle** `../FragForge-Capa8-Informe.md`

---

## Qué pedía

Tweaks de prioridad de CPU y de optimizaciones de pantalla completa para CS2, más saber **dónde está instalado**
el juego.

## Qué se encontró al auditar

**Los dos tweaks pedidos ya existían y hacían exactamente lo que se pedía.** No se duplicaron.

| Tweak | Acción | Qué hace |
|---|---|---|
| `cs2.cpu-priority-high` | `IfeoPerfOptions` con `{"imageName":"cs2.exe","priorityClass":3}` | Escribe `CpuPriorityClass=3` en `HKLM\...\Image File Execution Options\cs2.exe\PerfOptions` |
| `cs2.fullscreen-optimizations-off` | `Registry` con `mode: AddToken` | Añade `DISABLEDXMAXIMIZEDWINDOWEDMODE` a la cadena existente de `AppCompatFlags\Layers`, **sin duplicar** ni destruir el resto |

Detalles que ya estaban bien resueltos:
- `IfeoPerfOptionsAction` **prohíbe Realtime (4)** explícitamente. Solo acepta clases 1, 2, 3, 5, 6.
- Solo escribe `CpuPriorityClass`; **no toca `Debugger`**, que está en la denylist.
- `AddToken` conserva el prefijo `~` y los demás flags; Verify comprueba que el token está **y** que la cadena
  empieza por el original; Revert restaura la cadena exacta o **borra el valor** si no existía.
- `HKCU` se resuelve con `TargetUserSid` → `ResolveUserHive` (R-H).

**Conclusión de la auditoría**: lo que faltaba de verdad era otra cosa.

## Qué se construyó

### 8.2 Resolución de `cs2.exe` (lo que faltaba)

`SteamCs2Resolver`: lee el `libraryfolders.vdf` de Steam y el **`appmanifest`** del juego para localizar CS2 en
cualquier biblioteca, no solo en la de por defecto. Todo en `Core`, sin WPF, para poder testearlo.

### 8.6 Inspector de solo lectura

`Cs2Inspector` + `Cs2ConfigParser` + `VdfLike`: lee los `.cfg` del juego y el `localconfig.vdf`, y muestra el
estado **sin escribir nada**.

### 8.7 Detección de proceso

`Cs2Process`: ¿está CS2 en ejecución? Sirve a la Capa 9 para el modo sesión y a la UI para avisar. **No mata ni
prioriza nada.**

## Qué se decidió NO hacer

| Descartado | Motivo |
|---|---|
| Duplicar los dos tweaks de 8.3 y 8.4 | Ya existían y funcionaban. Reescribirlos sin medirlos habría sido peor. |
| 8.5 | Descartado en el enunciado de la capa |

## Qué no se verificó

- **Nada compilado ni ejecutado.**
- **IFEO y AppCompat no se probaron en Windows real**: no se comprobó que `CpuPriorityClass=3` cambie de verdad la
  prioridad al crear el proceso, ni que `DISABLEDXMAXIMIZEDWINDOWEDMODE` altere la presentación. Depende del
  sistema, y aquí no hay sistema.
- **No se ejecutó `fragforge cs2 inspect`** contra una instalación de Steam real. La lógica se validó con fixtures.
- **No se comprobó el `localconfig.vdf` real**: el parser tolerante se escribió para él pero no se probó con un
  archivo completo de verdad.
- **No hay protección contra leer los `.cfg` mientras CS2 está abierto** (podrían estar a medio escribir). Es solo
  lectura, pero el valor leído podría ser transitorio.
