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

3. **Comportamiento no verificado.** Qué hace exactamente la opción cuando hay una sesión con el mismo
   nombre, y si afecta a sesiones de otros nombres, no se observó en la PC objetivo. R-L prohíbe actuar
   sobre lo no verificado.

**Para reabrirlo:** hacerlo con un nombre de sesión fijo y documentado, cambiar primero el test de higiene
(de forma consciente y explicada), probar en la PC objetivo qué ocurre con una sesión ajena activa, y
exigir confirmación interactiva al usuario. Mientras tanto, la captura falla de forma limpia si hay una
sesión con el mismo nombre, que es el comportamiento seguro.
