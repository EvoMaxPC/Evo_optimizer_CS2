## 3i. Catálogo de semillas — tabla resumen

| ID | Cat. | Evidencia | Riesgo | opt-in | Reboot | Acciones | Condiciones | Archivo |
|---|---|---|---|:-:|:-:|---|---|---|
| `cpu.power-throttling-off` | Cpu | Medium | Moderate | no | sí | Registry | windows, hybridCpu | `src/FragForge.Tweaks/Catalog/Cpu/cpu.power-throttling-off.json` |
| `cs2.cpu-priority-high` | Cs2 | Medium | Moderate | no | no | IfeoPerfOptions | windows | `src/FragForge.Tweaks/Catalog/Cs2/cs2.cpu-priority-high.json` |
| `cs2.fullscreen-optimizations-off` | Cs2 | Experimental | Experimental | sí | no | Registry | windows, userHive, cs2 | `src/FragForge.Tweaks/Catalog/Cs2/cs2.fullscreen-optimizations-off.json` |
| `diagnostics.system-inspect` | Diagnostics | High | Safe | no | no | InspectOnly | windows | `src/FragForge.Tweaks/Catalog/Diagnostics/diagnostics.system-inspect.json` |
| `latency.game-dvr-capture-off` | Latency | High | Moderate | no | sí | Registry | windows, userHive | `src/FragForge.Tweaks/Catalog/Latency/latency.game-dvr-capture-off.json` |
| `latency.game-mode-on` | Latency | Medium | Safe | no | no | Registry | windows, userHive | `src/FragForge.Tweaks/Catalog/Latency/latency.game-mode-on.json` |
| `latency.global-timer-resolution` | Latency | Medium | Moderate | no | sí | Registry | windows | `src/FragForge.Tweaks/Catalog/Latency/latency.global-timer-resolution.json` |
| `latency.mouse-acceleration-off` | Latency | High | Safe | no | sí | Registry | windows, userHive | `src/FragForge.Tweaks/Catalog/Latency/latency.mouse-acceleration-off.json` |
| `network.i225v-eee-off` | Network | Medium | Moderate | sí | no | NetAdapterProperty | windows, adapter | `src/FragForge.Tweaks/Catalog/Network/network.i225v-eee-off.json` |
| `power.fragforge-performance` | Power | Medium | Moderate | sí | no | PowerPlan | windows | `src/FragForge.Tweaks/Catalog/Power/power.fragforge-performance.json` |

**Perfiles:** `Safe` = solo riesgo Safe sin `opt-in`; `Balanced` = Safe+Moderate sin experimental ni `opt-in`; `Competitive` = Safe+Moderate (incluye `opt-in`); `Advanced` = todo, y todo tweak Advanced/Experimental exige además `--accept-risk <id>` + `--risk-phrase "ACEPTO EL RIESGO"`.

Los 10 seeds están en `src/FragForge.Tweaks/Catalog/<Categoría>/` y su contenido completo está en `03c-tweaks.md`.
