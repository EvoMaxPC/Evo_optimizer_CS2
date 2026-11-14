# Parte 2 — Capa 6: iGPU Intel y memoria

**Commit** `12bfa82` — 1 commit, 6 ficheros, +643 / −2, **+14 tests** (530 → 544)
**Informe de detalle** `../FragForge-Capa6-Informe.md`

---

## Qué pedía

Diagnóstico de la GPU integrada (iGPU) y tweaks opt-in de evidencia baja, sin prometer nada.

## Qué se encontró al auditar

Faltaban tres datos que el usuario necesita para decidir: **cuánta memoria comparte la iGPU**, **si la RAM está
en single o dual channel** (esto sí afecta de verdad al rendimiento de una iGPU) y **si HAGS está activado**.

## Qué se hizo

### 6.1 Diagnóstico de solo lectura

- **Memoria compartida**: lo que faltaba. Se lee y se muestra tal cual.
- **Canales de RAM**: el diagnóstico dice **"probable, no confirmable"** y **nunca** "confirmado". Un solo módulo
  → single-channel; módulos distintos → no se sugiere nada. Es el ejemplo más claro de R-L en el proyecto: es
  mejor decir "probable" que afirmar algo que no se puede comprobar.
- **HAGS**: estado leído, no inventado.

### 6.2 Contadores de la iGPU por WMI (R-F)

`GpuPerformanceProbe` consulta por **nombre de clase WMI**, nunca por texto localizado. Si la clase no existe o
devuelve 0 filas, el resultado es `Unknown` **con un aviso que nombra la clase consultada** — así el usuario sabe
qué se intentó y por qué no hay dato.

Un detalle de test interesante: el doble compartido de `IWmiQuery` siempre devuelve `[]`. Aquí hacía falta uno
propio capaz de devolver filas o lanzar, así que se escribió uno específico en el test en vez de complicar el
doble común.

### 6.3 Tweaks (todos opt-in, evidencia baja)

| Tweak | Estado | Nota |
|---|---|---|
| `gpu.hags-toggle` | Implementado | Opt-in explícito |
| `gpu.intel-graphics-power-plan-max` | Implementado, **no verificado** | Así marcado en el JSON |
| `DirectXUserGlobalSettings` / `SwapEffectUpgradeEnable` | **NO implementado** | Ver descartados |

Los dos nuevos llevan `profiles: []`, es decir, **no entran en ningún perfil automáticamente**. Solo por id
explícito. El test `NewTweaks_DoNotJoinAnyProfile_SoTheCountsStayOrdered` comprueba la relación no decreciente
entre perfiles (no los tamaños exactos, R-N) y que ninguno de los dos entra solo.

### 6.4 Recomendación (solo texto)

Se escribe una recomendación en texto; **no se escribe nada** en el sistema.

## Qué se decidió NO hacer

| Descartado | Motivo |
|---|---|
| `DirectXUserGlobalSettings` / `SwapEffectUpgradeEnable` | Sin evidencia suficiente para un cambio global de DirectX |
| Más tweaks de iGPU | Evidencia baja; añadir más sería relleno |
| Afirmar dual-channel sin poder confirmarlo | Precisamente lo que R-L prohíbe |

## Qué no se verificó

- **Nada compilado ni ejecutado.**
- Los **valores reales** de memoria compartida y canales en el equipo de referencia: el parseo está probado con
  dobles, no con la máquina.
- Que `gpu.intel-graphics-power-plan-max` tenga efecto: **marcado no verificado en el propio catálogo**.
