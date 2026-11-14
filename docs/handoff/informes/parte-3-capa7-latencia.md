# Parte 3 — Capa 7: Latencia

**Commit** `aebafee` — 1 commit, 3 ficheros, +293 / −10, **+10 tests** (544 → 554)
**Informe de detalle** `../FragForge-Capa7-Informe.md`

---

## Qué pedía

Auditar los tweaks `latency.*` existentes antes de añadir más.

## Qué se encontró al auditar

La capa entera **fue** la auditoría. De los cuatro tweaks, tres estaban bien y **uno estaba mal**.

| Tweak | Riesgo | Evidencia antes → después | Veredicto |
|---|---|---|---|
| `latency.game-mode-on` | Safe | Medium → Medium | **Correcto**, sin cambios |
| `latency.mouse-acceleration-off` | Safe | **High → Low** | **Evidencia corregida** |
| `latency.game-dvr-capture-off` | Moderate | High → High | Correcto; la política HKLM ya estaba incluida |
| `latency.global-timer-resolution` | Moderate | Medium → Medium | Correcto; notas ampliadas |

## El hallazgo: la evidencia de "aceleración de mouse off" estaba inflada

Se declaraba `High`. Al revisarlo no aguanta una lectura crítica: la mejora depende del juego, del motor y del
sensor, y no hay medición propia que la respalde. Se **bajó a `Low`**.

Ese gesto —bajar la evidencia de algo que ya estaba publicado— es más incómodo que añadir un tweak, y por eso se
documenta aquí: es la prueba de que `EvidenceLevel` se usa como dato, no como marketing.

**Y un segundo detalle honesto**: al auditar las curvas de aceleración se encontró un **error de auditoría
propio** en la primera revisión, que se corrigió. Queda escrito en el informe de la capa.

Las **curvas** en sí se verificaron contra el formato real (5 puntos × 8 B, QWORD little-endian, 16.16 fixed-point,
canónicas X = 0/12.7998/25.5996/38.3994/51.1992, ratio 4.375) y son correctas.

## Qué se decidió NO hacer

| Candidato | Motivo |
|---|---|
| Suspensión selectiva USB | **Ya existía** (`power.usb-selective-suspend-off`). No se duplica. |
| MMCSS | Descartado |
| `NetworkThrottlingIndex` | Descartado |
| DWM transparencia/animaciones | Descartado |

## Perfiles

Los cuatro `latency.*` no tienen `profiles` explícito, así que entran por regla de riesgo/evidencia:
`game-mode-on` y `mouse-acceleration-off` (Safe) entran en los cuatro; `game-dvr-capture-off` y
`global-timer-resolution` (Moderate) en Balanced/Competitive/Advanced.

**No se movió ningún conteo**: no se añadieron tweaks `latency.*` y los dos `gpu.*` de la Capa 6 tienen
`profiles: []`.

## Qué no se verificó

- **Nada compilado ni ejecutado.**
- No se midió el efecto real de ningún tweak de latencia. La capa **no cambia** el `EvidenceLevel` de nada salvo
  para **bajarlo**, que es lo contrario de prometer.
- La corrección de `High → Low` es de criterio, no de medición: no había medición que inflara ni que respaldara.
