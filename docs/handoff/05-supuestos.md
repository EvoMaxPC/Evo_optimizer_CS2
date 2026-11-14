## 5. Supuestos y limitaciones (honestas, verificables)

### 5.1 Qué está verificado y qué no

| Área | Estado | Detalle |
|---|---|---|
| Compilación de los 7 proyectos | **Verificado** (sandbox Linux, SDK 10.0.400) | 0 warnings / 0 errores, incluida la App WPF con `EnableWindowsTargeting`. |
| 103 tests xUnit | **Verificado** | Motor, catálogo, acciones y composición sobre fakes. |
| CLI (`scan`, `apply --dry-run`, `revert` lógico, `doctor`, códigos de salida) | **Verificado** en Linux | En Linux la plataforma se reporta no soportada y todo queda `NotApplicable` (por diseño). |
| Adaptadores reales en Windows (registro, WMI, powercfg, bcdedit, servicios, tareas, NetAdapter, System Restore) | **NO ejecutado** aún | Requieren el PN64. Están aislados tras interfaces y cubiertos por fakes; quedan pendientes de la primera pasada real. |
| `SystemRestoreService.GetProtectionStateAsync` | **Heurística no validada** | No existe API/WMI documentada para el estado de protección *por unidad*. Se usa `Win32_Volume` ↔ `Win32_ShadowStorage`, con *fallback* `Unknown`. Si `Unknown`, el preflight bloquea salvo `--no-restore-point` o `--enable-system-protection`. **Validar en el PN64 el primer día.** |
| `bcdedit /enum {current} /v` para leer `disabledynamictick` | A validar | Si el elemento no aparece, se asume `existed=false` y el revert hará `bcdedit /deletevalue`. Verificar que el parseo funciona con la salida en es-MX. |
| Detección de Atlas | Heurística de baja/media confianza | Organización en registro, carpeta `ProgramData\Atlas`, servicios SysMain/WSearch/Spooler/DiagTrack detenidos. **No** se afirma con certeza: solo se usa para *no pisar* cambios (`AppliedExternally`). |
| Canal de RAM | **Unknown por defecto** | `Win32_PhysicalMemory` describe módulos, no el entrelazado. Dos DIMM no prueban dual-channel. El tweak de canales nunca se aplica sobre `Unknown`. |
| PL1/PL2 | No disponible por WMI | En el PN64 los gobierna DTT/IPF (`WMIRegistrationService`). El profiler reporta `Unknown` con nota; **no se inventan valores**. |

### 5.2 Limitaciones de diseño (deliberadas)

1. **Sin medición.** La Capa 0 no mide FPS ni frametime. `cpu.power-throttling-off`, `latency.global-timer-resolution` y `power.fragforge-performance` están marcados Medium/opt-in precisamente porque su beneficio depende del equipo y puede empeorar temperatura/consumo. Se aplican solo en perfil Competitive/Advanced y con A/B en Capa 1.
2. **`GlobalTimerResolutionRequests`** no es una API documentada como contrato público (sí el mecanismo `timeBeginPeriod`). Por eso evidencia Medium + advertencia de consumo.
3. **IFEO `CpuPriorityClass`** puede no "pegarse" en algunas builds de Win11; el motor lo detecta porque hace `Verify` después de aplicar y, si no coincide, revierte y falla el tweak (no deja estado a medias).
4. **El tweak de fullscreen optimizations** es Experimental: depende de flip model, ventana sin bordes y driver Intel. Requiere perfil Advanced + aceptación por ID + frase literal.
5. **`mouse-acceleration-off` afecta a todo el escritorio** (no solo CS2) y requiere cerrar sesión. CS2 puede usar raw input y no verse afectado; el texto del tweak lo dice explícitamente.
6. **La App WPF** no tiene visor de snapshots ni editor de catálogo (fuera del alcance de Capa 0): permite escanear, seleccionar, dry-run, aplicar, revertir, snapshot manual, doctor, exportar informe y ver logs.
7. **Un solo usuario objetivo.** `HKCU` se resuelve al SID interactivo (`HKU\<SID>`), nunca al hive del proceso elevado. Si el SID no se resuelve, las acciones de usuario fallan en lugar de escribir en el lugar equivocado.
8. **Sin compresión de snapshots.** `snapshot.json` incluye la definición completa + estados; para 10 tweaks es de pocos KB. `FileEdit` limita a 1 MiB por archivo para no inflar los snapshots.
9. **Sin firma de código.** Los binarios no están firmados; SmartScreen puede advertir. No afecta a la lógica pero es requisito para distribución pública.
10. **SHA-256 del journal no es defensa criptográfica fuerte** (no hay HMAC con secreto): detecta edición accidental o torpe, no un atacante con acceso administrativo. Con admin, todo es reconstruible de todos modos; el objetivo es la integridad operativa.

### 5.3 Compatibilidad con Atlas Playbook v0.5.0

* Atlas ya desactivó `SysMain`, `WSearch`, `Spooler`, `DiagTrack`: FragForge **lo detecta** (servicios no corriendo / valores ya presentes) y marca `AppliedExternally` **sin sobrescribir**, salvo `--force` explícito.
* Ningún tweak de la Capa 0 toca los servicios que Atlas gestiona: el catálogo no contiene acciones `Service` en esta capa (la infraestructura existe y está probada, pero sin semillas que luchen contra Atlas).
* Los tweaks con `opt-in` (plan de energía, EEE, fullscreen) quedan **fuera** de Balanced: no se aplican por accidente sobre una configuración ya ajustada.

### 5.4 Riesgos residuales conocidos

| Riesgo | Mitigación implementada |
|---|---|
| Rollback a medias por fallo de disco/permisos | Journal + snapshot en `%ProgramData%`; código de salida 5; guía manual en `docs/manual-revert.md`. |
| Punto de restauración limitado a 24 h | Se pone `SystemRestorePointCreationFrequency=0` temporalmente y se restaura el valor original en `finally`. |
| Protección del sistema desactivada | Preflight bloquea; `--enable-system-protection` la habilita y **verifica** que quedó activa antes de aplicar. |
| Reinicio pendiente / TiWorker activo | Preflight bloquea; `--force` no lo omite. |
| Cancelación a mitad de lote | El tweak en curso revierte; lo ya aplicado permanece y es reversible por snapshot (`--last`). |
| Denylist insuficiente | Fusionable desde `%ProgramData%\FragForge\policy\denylist.json` sin recompilar. |
