# Parte 5 — Capa 9: Modo sesión

**Commit** `b8a6087` — 1 commit, 17 ficheros, +1190 / −3, **+15 tests** (515 → 530)
**Informe de detalle** `../FragForge-Capa9-Informe.md`

---

## Qué pedía

Un modo "sesión": aplicar un perfil al empezar a jugar y revertirlo al terminar. Y sobrevivir a que maten el
proceso o se corte la luz.

## Lo primero: el criterio, después el código

Criterio para que un tweak entre en el modo sesión: efecto **inmediato** (sin reinicio), **totalmente
reversible**, y que no deje el equipo en un estado que necesite reiniciar para arreglarse.

De 32 tweaks, calificaron **3**:

| Tweak | ¿Califica? | Motivo |
|---|---|---|
| `power.fragforge-performance` | **Sí** | Plan de energía: inmediato, sin reinicio, reversible |
| `cpu.epp-performance` | **Sí** | EPP del procesador: inmediato, reversible |
| `cpu.core-parking-off` | **Sí** | Core parking: inmediato, reversible |

Los otros 29 quedaron fuera **con motivo escrito**. Los casos interesantes:
- `cs2.cpu-priority-high`: IFEO solo aplica **al crear el proceso**; si CS2 ya corre no surte efecto, y no se puede
  revertir "al cerrar el juego" de forma limpia.
- `latency.*` y `cpu.power-throttling-off`: `requiresReboot: true`.
- `debloat.*`: no son de rendimiento en partida y tocan servicios del sistema.

## El modelo: `sessionEligible` (aditivo)

Campo **opcional** en `TweakDefinition`, `false` por defecto. Se añadió también al **esquema**, que tenía
`additionalProperties: false`: **sin este cambio el catálogo no habría cargado**. Ese detalle se detectó antes de
romper nada.

**Segunda barrera en código**: `GameSessionService.EligibleIds` filtra además `!d.RequiresReboot`. Si un JSON se
marcara por error `sessionEligible: true` en un tweak que necesita reinicio, **queda fuera igualmente**. Con test
(`EligibleIds_TakesOnlySessionEligibleTweaksWithoutReboot`).

## `GameSessionService`

Vive en **Core**, sin ninguna referencia a WPF. `StartAsync(profile)` aplica los elegibles **sin punto de
restauración** y escribe el marcador en `%ProgramData%\FragForge\session.json`.

El marcador es la pieza clave: guarda operación, snapshot, hora, perfil e ids. Si el proceso muere, **el marcador
sobrevive** y es lo que permite recuperar.

## Recuperación (9.3)

Al arrancar, si hay marcador huérfano, `status` lo reporta como sesión pendiente/recuperable **en vez de mostrarla
inactiva**. Ese es el punto: que la app no mienta sobre el estado del sistema.

Si el revert **no** se completa, el marcador **se conserva** para reintentar. Borrarlo sería perder la única forma
de saber qué quedó aplicado.

## CLI y UI

- `fragforge session start|stop|status`. **No hay verbo de vigilancia**: el vigilante tiene que sobrevivir al
  comando, y un proceso de consola esperando dos horas no es la forma. El CLI deja la sesión preparada; la
  vigilancia continua es de la App.
- En la UI, un `ToggleButton` con el estado visible. Se deja **sin** `IsChecked` enlazado a propósito: enlazarlo al
  enum necesitaría un converter que no existe en el repo, y añadirlo sin poder compilarlo habría sido peor.
- Textos nuevos en es-MX **y** en-US.

## Prohibiciones que se respetan (9.5)

No se mata ni se prioriza ningún proceso. No se abre `cs2.exe`. No se toca Steam.

## Qué no se verificó

- **Nada compilado ni ejecutado.** Los 11 tests de sesión están escritos, no pasados.
- **No se probó contra CS2 real**: ni que el polling detecte el proceso, ni el comportamiento al cerrar el juego,
  ni qué pasa si CS2 se reinicia.
- **No se verificó la caída real de la app**: la recuperación se prueba con un marcador en disco y un servicio
  nuevo, **no matando el proceso de verdad**.
- No se comprobó que revertir por `snapshotId` funcione contra un snapshot real de una sesión real.
- **No se ejecutó la UI.**
- No se verificó qué pasa si `session.json` existe pero el snapshot fue borrado a mano.
- **No se midió ningún efecto de rendimiento** de los tres tweaks elegibles (R-K).
