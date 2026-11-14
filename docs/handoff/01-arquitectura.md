# FragForge — Capa 0 · Informe de entrega completo

**Rama:** `arena/c1a5f702-evo-optimizer-cs2` (base `main@41af03d`, entrega `a2c5429`)
**Fecha:** 2026-11-13 · **Destino:** MiniPC ASUS PN64 (i3-1220P, UHD, 16 GB, SA400, I225-V + AX210, Win11 Pro 26200 es-MX)
**Estado verificado:** solución de 7 proyectos compila **0 warnings / 0 errores**; **103 tests xUnit verdes**; CLI smoke-tested.

Repositorio: todos los archivos listados abajo existen en el árbol de trabajo y están versionados (122 archivos versionados).

## Índice
1. [Arquitectura y decisiones](#1-arquitectura-y-decisiones)
2. [Árbol de carpetas](#2-árbol-de-carpetas)
3. [Inventario de archivos (contenido completo)](#3-inventario-de-archivos)
4. [Comandos exactos](#4-comandos-exactos-de-restore-build-test-publish)
5. [Supuestos y limitaciones](#5-supuestos-y-limitaciones)
6. [Contrato para la Capa 1](#6-contrato-para-la-capa-1)

---

## 1. Arquitectura y decisiones

FragForge es un **motor de cambios transaccional** para Windows 11 orientado a CS2. La Capa 0 no promete FPS: garantiza que **todo cambio es reversible al estado exacto**, incluida la *no existencia* previa de un valor, y que cada tweak declara evidencia, riesgo y cuándo **no** aplicarlo.

### Flujo de una operación

```
preflight (plataforma/build/admin/reboot/TiWorker/espacio/System Protection)
  → resolución de dependencias y conflictos (topológico + detección de ciclos)
  → condiciones (tri-estado: Met / Unmet / Unknown; Unknown ≠ aplicable)
  → perfil de riesgo (Safe / Balanced / Competitive / Advanced)
  → idempotencia (Applied / AppliedExternally / Force)
  → snapshot: captura exacta por acción + artifacts (.reg, .pow, .bcd)
  → punto de restauración (SystemRestorePointCreationFrequency temporal = 0, restaurado en finally)
  → por tweak: por acción Apply → Verify
        Verify falla ⇒ rollback inverso de las acciones intentadas ⇒ se detiene el lote
  → journal append-only encadenado por SHA-256 (OperationStarted … TweakApplied … SnapshotReverted)
```

### Decisiones y su justificación

| Decisión | Justificación |
|---|---|
| **.NET 10 LTS** (`net10.0`; App `net10.0-windows`) | Soporte hasta nov-2028; .NET 8 expira nov-2026. SDK 10.0.400 fijado en `global.json` con `rollForward: latestFeature`. |
| **WPF + CommunityToolkit.Mvvm 8.4.1** | WPF está incluido en Windows (cero runtime extra). El toolkit solo genera `INotifyPropertyChanged`/`ICommand`; el motor no conoce la UI. La App **no** requiere admin para *mirar* (aunque el manifiesto pide elevación por coherencia con el motor real). |
| **Microsoft.Extensions.Hosting + DI + Serilog 4.3** | Composición explícita, `ILogger<T>` inyectable (tests con `NullLogger`), log estructurado rotativo (`logs/fragforge-<fecha>.log`, 14 días, 10 MB) + consola en CLI. |
| **Puertos/adaptadores para todo efecto** | `IRegistry`, `IProcessRunner`, `IServiceController`, `IScheduledTaskStore`, `INetworkAdapterController`, `ISystemRestoreService`, `IWmiQuery`, `ICpuTopologyProbe`, `IExecutionGate`, `IJournal`, `ISnapshotStore`. Resultado: **el 100 % de la lógica del motor se prueba en Linux con fakes**; solo los adaptadores necesitan Windows. |
| **Catálogo declarativo JSON + doble validación** | Un tweak = un archivo embebido. Validación **de esquema** (JSON Schema 2020-12 propio, sin dependencias) **y semántica** (IDs únicos, ciclos, conflictos inexistentes, localización obligatoria, referencias obligatorias, `PowerShell`/`Bcdedit` ⇒ riesgo Advanced/Experimental, denylist evaluada al construir la acción). El catálogo se valida al arrancar: si algo está mal, la aplicación no ejecuta nada. |
| **Captura/restauración separada del catálogo** | El `SystemSnapshot` guarda la `TweakDefinition` completa y el `ActionState` de cada acción. Si el tweak desaparece o cambia en una versión futura, el snapshot sigue siendo autosuficiente para revertir (se compara `DefinitionSha256`). |
| **Journal append-only con cadena SHA-256** | Cada entrada incluye `PreviousHash`/`Hash`. `revert` exige `VerifyIntegrityAsync` antes de tocar nada: una edición manual del journal bloquea la reversión en lugar de ejecutar sobre datos corruptos. |
| **Denylist central embebida + fusión desde `%ProgramData%`** | Seguridad por defecto (Defender, firewall, Update, DeviceGuard, servicios críticos, BCD) y ampliación consciente por el usuario sin recompilar. |
| **Todo offline** | Sin descargas ni scripts remotos. `PowerShellAction` está deshabilitada por defecto, exige `--allow-powershell`, perfil Advanced, aceptación por ID + frase literal, bloquea tokens de red/ejecución dinámica y persiste el SHA-256 de los 4 scripts. |
| **Anti-cheat seguro por diseño** | Nunca se inyecta código, nunca se lee/escribe memoria de `cs2.exe`, no hay drivers propios. Solo registro, servicios, tareas, energía, BCD (lista blanca), red, archivos de configuración de texto y (opcional) PowerShell local. |
| **Estados tri-estado en condiciones y seguridad** | `VBS/HVCI/SecureBoot/TPM/HAGS/MPO` y el canal de RAM se reportan `Unknown` cuando la evidencia es insuficiente. **`Unknown` nunca se interpreta como `Disabled`** ni habilita un tweak. |

### Semántica del motor (contratos que no se pueden relajar)

* `--force` **solo** omite idempotencia y detección de deriva. **Nunca** omite denylist, condiciones, perfil de riesgo ni preflight.
* Un `Verify` fallido ⇒ `RevertAsync` en orden inverso **de las acciones ya intentadas** de ese tweak, `TweakRolledBack`/`TweakRollbackFailed` en journal y **parada del lote**. Rollback fallido ⇒ código de salida **5** y mensaje crítico.
* Cancelación: `CancellationToken` en toda la superficie; el rollback y la escritura final del journal usan `CancellationToken.None` para no dejar estado a medias.
* Revertir exige **exactamente uno** de: IDs, `--snapshot` o `--last`.
* Sin snapshot, **no se deshacen** cambios externos/Atlas: `revert <id>` falla con explicación explícita.
* Si un tweak ya coincide y **no** fue aplicado por FragForge ⇒ `AppliedExternally` y **no se sobrescribe** (compatibilidad con Atlas Playbook v0.5.0).
