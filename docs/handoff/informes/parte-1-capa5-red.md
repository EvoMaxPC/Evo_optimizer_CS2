# Parte 1 — Capa 5: Red

**Commits** `b5bc22a` (adapter) · `4b0ec58` (tweaks) · `de21287` (diag) · `8d97263` (docs) · `0bd62cc` (fix)
**Rango** `f44ba1d..0bd62cc` — 5 commits, 35 ficheros, +2858 / −70, **+124 tests** (490 → 614)
**Informe de detalle** `../FragForge-Capa5-Informe.md`

---

## Qué pedía

Que los tweaks de red no se aplicaran "al adaptador que se llame así", sino al adaptador **correcto**,
identificado por hardware; un tweak de EEE que no corte la red sin avisar; y diagnóstico de red de solo lectura.

## Qué se encontró al auditar

La ruta de apply identificaba adaptadores **por nombre visible**. Un usuario con dos adaptadores, o que renombre
el suyo ("Ethernet 2" → "Mi red"), podía recibir el tweak en el adaptador equivocado. No era teórico: es
exactamente lo que pasa cuando se cambia una tarjeta.

Y **dos candidatos de tweak chocaban con la denylist**: `*InterruptModeration`, `*FlowControl`, `*RSS`. Se
descartaron en vez de relajar la política.

## Qué se hizo

### 5.1 Identidad de hardware, no nombre

Interfaz nueva `INetworkAdapterResolver` con dos implementaciones: `WindowsNetworkAdapterResolver` y
`UnsupportedNetworkAdapterResolver` (para plataformas no Windows, que devuelve "no soportado" en vez de fallar).

Resolución por **GUID** o por **hardware ID** (`PCI\VEN_8086&DEV_15F3` para el I225-V). El nombre visible quedó
**eliminado de la ruta de apply**. Si no hay identidad resoluble → `null`. Nunca "el primero que parezca".

### 5.2 Propiedades por `RegistryKeyword`

`NetAdapterPropertyAction` lee y escribe por keyword del registro (`*EEE`, etc.), no por nombre humanamente
legible. Se añadió `NetworkAdvancedPropertyReader` que mapea **por posición de columna** con fixtures en es-MX y
en-US — precisamente para no depender de que el sistema esté en un idioma concreto (R-C: nada de etiquetas
localizadas).

La comprobación de que solo se piden propiedades que existen vive en `MSFT_NetAdapter`, y hay test
(`NetAdapterPropertiesTests`).

### 5.3 `network.i225v-eee-off` rehecho

Antes era un tweak genérico de ahorro de energía. Ahora:
- se ata al **hardware ID** del I225-V;
- declara `RestartsNetworkLink: true`, lo que dispara el **aviso de corte de enlace ANTES de aplicar** (no en el
  resultado, donde sería inútil);
- el aviso se pregunta **una vez por todos** los tweaks que reinician el enlace, no una vez por tweak;
- `NetworkChangePreflight` comprueba BitLocker **en modo solo lectura** (`manage-bde -status`) antes de tocar la
  red, porque un reinicio del adaptador en un equipo con BitLocker activo es justo el momento en el que uno quiere
  saber en qué estado está.

### 5.4 Diagnóstico de solo lectura

`fragforge net diagnose` y `fragforge net probe --host H --count N`: mediana, p99 y jitter, con `NA` cuando no hay
medida (**`NA ≠ 0`**, hay test) y sin emitir veredicto. `DefaultRouteProbe` resuelve la ruta por defecto real con
`GetBestInterface`.

## Qué se decidió NO hacer

| Descartado | Motivo |
|---|---|
| `*InterruptModeration`, `*FlowControl`, `*RSS` | Denylist de red. No se debilita. |
| Tweaks de Wi-Fi AX210 | No se enumeraron sus propiedades reales. Sin datos, no hay tweak. |
| Afirmar que el 14 de `NdisPhysicalMedium` es Ethernet en esta NIC | No medido. Se toma del `.inf` del driver, y así está marcado en el código. |

## Qué no se verificó

- **`ValidRegistryValues` reales del I225-V**: no se enumeraron. Si el driver no expone `*EEE`, el tweak es
  *No aplicable* — que es el comportamiento implementado, pero no se observó.
- **Duración del corte** al reiniciar el adaptador: no se midió. El aviso se escribió según lo que documenta el
  driver.
- **BitLocker**: no se ejecutó `manage-bde`. Los fixtures son del formato documentado.
- **Ping**: no se lanzó ni un paquete.
- **`GetBestInterface`** en esta PC: no se ejecutó.
