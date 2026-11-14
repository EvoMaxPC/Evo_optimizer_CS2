# requiresAdmin / requiresReboot — revisión de C4

## Lo que dice el catálogo hoy

| Tweak | Clave | `requiresAdmin` | `requiresReboot` |
|---|---|---|---|
| `latency.mouse-acceleration-off` | `HKCU\Control Panel\Mouse` | `true` | **`true`** |
| `latency.game-mode-on` | `HKCU\Software\Microsoft\GameBar` | (por defecto `true`) | `false` |
| `cs2.fullscreen-optimizations-off` | `HKCU\...` | (por defecto `true`) | `false` |
| `latency.game-dvr-capture-off` | `HKCU\...` | (por defecto `true`) | `true` |

## requiresAdmin = true

**Correcto aunque la clave sea HKCU**, y por dos razones independientes:

1. `TweakDefinition.cs:15` da a `RequiresAdmin` el valor por defecto `true`, y hay un test del validador
   que exige que toda acción mutante lo lleve (`TweakCatalogValidator.cs:73`:
   `if (!definition.ReadOnly && !definition.RequiresAdmin) → "una acción mutante requiere Admin"`).
   Ponerlo en `false` solo porque la clave es del usuario haría fallar la validación.
2. El motor aplica **como otro usuario** cuando se le pasa `TargetUserSid` (`ApplyOptions.TargetUserSid`,
   `Results.cs:31`): escribir en la rama de OTRO usuario sí requiere elevación. El flag describe la peor
   de las rutas, no la más cómoda.

No se cambia.

## requiresReboot = true — SOSPECHA, sin cambiar

**Lo que se puede afirmar:** no he encontrado ninguna fuente en el repo que justifique que un cambio en
`HKCU\Control Panel\Mouse` necesite reiniciar Windows. `TweakEngine.cs:232` solo lo usa para añadir el
aviso «Requiere reinicio para que todos los efectos sean activos», y `GameSessionService.cs:58` lo usa
para dejar el tweak fuera de las sesiones de juego. Es decir: el flag **no bloquea nada**, solo avisa y
excluye de sesión.

**Lo que hace sospechar:** el propio catálogo no es coherente consigo mismo. Cuatro tweaks escriben en
HKCU y dos declaran `requiresReboot: true` (`mouse-acceleration-off`, `game-dvr-capture-off`) y dos
`false` (`game-mode-on`, `cs2.fullscreen-optimizations-off`). Misma clase de cambio, valores distintos.
Al menos uno de los cuatro está mal, y no hay documento en el repo que decida cuál.

**Por qué NO lo cambio en esta ronda:** cambiarlo altera comportamiento observable (el aviso al aplicar
y la elegibilidad para sesión de juego) y la ronda 9 prohíbe funcionalidad nueva. Además, el sentido
correcto no es evidente: para `game-dvr-capture-off` (política de Game Bar) sí hay motivos para pensar
que Windows no la relee hasta reiniciar; para el ratón, `SPI_SETMOUSE` sí se aplica en caliente, pero
estos valores del registro los lee el panel de control y el efecto sobre el puntero del escritorio es
inmediato en la práctica. Eso es **conocimiento externo no verificado en este entorno**, así que no es
base suficiente para tocar el catálogo.

**Qué haría falta para cerrarlo:** medirlo en el equipo real (cambiar los tres valores con la app,
mover el ratón y comprobar si la aceleración desaparece sin reiniciar) y dejar el resultado en
`docs/handoff/`. Es la ronda 10.

## Conclusión

- `requiresAdmin = true`: **CONFIRMADO correcto** por contrato y por la ruta `TargetUserSid`.
- `requiresReboot = true`: **SOSPECHA**. No se toca el JSON; queda anotado y con un test que fija la
  membresía en perfiles para que un cambio futuro de la regla no pase inadvertido.
