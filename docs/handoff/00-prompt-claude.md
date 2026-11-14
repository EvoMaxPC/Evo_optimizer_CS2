# Briefing compacto para otra sesión de Claude (copiar/pegar)

> Pega este bloque completo al inicio de una sesión nueva. El informe extenso está en
> `docs/handoff/FragForge-Capa0-Informe.md` (todos los archivos con su código completo).

---

## Contexto

Proyecto **FragForge**: optimizador profesional y **reversible** para Windows 11 orientado a Counter-Strike 2
(FPS, estabilidad de frametime, latencia de entrada). Repo: `EvoMaxPC/Evo_optimizer_CS2`,
rama de trabajo `arena/c1a5f702-evo-optimizer-cs2` (base `main@41af03d`, entrega `a2c5429`).

**La Capa 0 está terminada y verificada**: solución .NET 10 de 7 proyectos, compila con
0 warnings / 0 errores y **103 tests xUnit pasan**. La Capa 1 (medición A/B) está diseñada pero **no implementada**.

## Hardware objetivo (FIJO, no asumir otro)

ASUS MiniPC PN64, BIOS AMI 3.12.00 UEFI · Intel i3-1220P (2P + 8E, 12 hilos, 28 W, híbrido con Thread Director) ·
solo iGPU Intel UHD (sin NVIDIA/AMD) · 16 GB RAM (verificar dual-channel) · SSD Kingston SA400S37480G SATA ·
Intel I225-V + Wi-Fi 6E AX210 · TPM 2.0 · SecureBoot/VBS/HVCI: **detectar, nunca asumir** ·
Windows 11 Pro build 26200 (25H2), es-MX, UTC-3, usuario `ZuKA` · Atlas Playbook v0.5.0 ya aplicado.

## Estructura

```
src/FragForge.Core/         motor puro, sin Windows: Models, Abstractions, Engine, Conditions,
                            Persistence (snapshot/journal/gate/denylist), Preflight, Localization
src/FragForge.Actions/      adaptadores Windows: Registry, IFEO, Service, ScheduledTask, PowerPlan,
                            Bcdedit, NetAdapterProperty, FileEdit, PowerShell, InspectOnly,
                            Platform (WMI, CPU topology, System Restore, HardwareProfiler)
src/FragForge.Tweaks/       catálogo JSON embebido (10 seeds), JSON Schema propio, validador semántico, i18n
src/FragForge.Composition/  CompositionRoot (DI + Serilog), ReportService (JSON/HTML)
src/FragForge.Cli/          fragforge.exe: scan, list, apply, revert, status, snapshot, export-report, doctor
src/FragForge.App/          WPF MVVM (CommunityToolkit.Mvvm 8.4.1), tema oscuro
tests/FragForge.Tests/      103 tests sobre fakes (sin WMI/registro reales)
```

## Invariantes que NO se pueden romper

1. **Reversibilidad exacta**: cada `ITweakAction` captura `ActionState` y restaura el estado previo,
   incluido *"el valor no existía"* ⇒ `DeleteValue` (+ limpieza de ancestros vacíos).
2. **Transacción por tweak**: `Apply → Verify`; si `Verify` falla ⇒ rollback inverso de las acciones
   intentadas ⇒ se detiene el lote ⇒ exit code 5 si el rollback falla.
3. **Journal append-only con cadena SHA-256**; `revert` exige `VerifyIntegrityAsync` antes de tocar nada.
4. **`--force` solo** omite idempotencia y detección de deriva. **Nunca** omite denylist, condiciones,
   perfil de riesgo ni preflight (reboot pendiente, TiWorker/TrustedInstaller, espacio, System Protection).
5. **`Unknown` ≠ `Disabled`**: condiciones tri-estado; si no hay evidencia, el tweak no aplica.
6. **Nunca por defecto**: Defender, Firewall, Windows Update, Secure Boot/TPM, mitigaciones de CPU, VBS/HVCI.
   Servicios intocables: `WinDefend, mpssvc, BFE, RpcSs, DcomLaunch, PlugPlay, Power, EventLog, CryptSvc,
   TrustedInstaller, ipfsvc, jhi_service, cplspcon, WMIRegistrationService`.
7. **Anti-cheat**: sin inyección, sin leer/escribir memoria de `cs2.exe`, sin drivers propios.
8. Tweaks Advanced/Experimental ⇒ perfil `Advanced` + `--accept-risk <id>` + frase literal **`ACEPTO EL RIESGO`**.
9. `HKCU` se resuelve al SID interactivo (`HKU\<SID>`), nunca al hive del proceso elevado.
10. **Cero placeholders**: todo archivo compila como está; `TreatWarningsAsErrors` activo en Core.

## Decisiones ya tomadas (no re-litigar)

* .NET 10 LTS (no 8: EOL nov-2026) · WPF + CommunityToolkit.Mvvm · M.E.Hosting/DI + Serilog ·
  System.Text.Json (`$actionState` como discriminador) · xUnit 2.9.3 · versiones centralizadas en
  `Directory.Packages.props` · `%ProgramData%\FragForge\` como raíz de datos
  (`snapshots/`, `journal/journal.ndjson`, `logs/`, `reports/`, `policy/denylist.json`, `.fragforge.lock`).
* Catálogo declarativo JSON validado con esquema **y** semántica al arrancar (si falla, no se ejecuta nada).
* Punto de restauración: se pone `SystemRestorePointCreationFrequency=0` temporalmente y se restaura en `finally`.
* Publicación: `-r win-x64 --self-contained true -p:PublishSingleFile=true`; `RuntimeIdentifier` solo se
  aplica al pasar `-r` (así `dotnet build` de Core/Actions/Tweaks/Composition/Tests funciona en Linux).

## Estado exacto de verificación

| Qué | Estado |
|---|---|
| `dotnet build FragForge.sln -c Release` (7 proyectos, incl. WPF) | ✅ 0 warnings / 0 errores |
| `dotnet test tests/FragForge.Tests` | ✅ 103/103 |
| CLI `scan`, `apply --dry-run`, `doctor`, códigos 0/2 | ✅ en Linux (plataforma no soportada ⇒ `NotApplicable`, por diseño) |
| Adaptadores Windows reales (registro/WMI/powercfg/bcdedit/servicios/tareas/red/System Restore) | ❌ **no ejecutados aún** — primera pasada pendiente en el PN64 |
| `SystemRestoreService.GetProtectionStateAsync` | ⚠️ heurística no validada (`Win32_Volume` ↔ `Win32_ShadowStorage`); fallback `Unknown` |
| Parseo de `bcdedit /enum {current} /v` en es-MX | ⚠️ por validar |

## Pendiente inmediato (orden sugerido)

1. **Validar en el PN64**: `fragforge doctor` → `scan` → `apply --profile Safe --dry-run` →
   aplicar 1 tweak → `status` → `revert --last` y comparar. Reportar cualquier desviación en WMI/parseo.
2. Validar `GetProtectionStateAsync` con protección activada y desactivada; si falla, dejar `Unknown` y
   exigir `--enable-system-protection` o `--no-restore-point`.
3. Capa 1: implementar `IExperimentHook` con PresentMon (ETW, no inyección) + persistencia
   `%ProgramData%\FragForge\experiments\<id>\` (`session.json` + CSV) y promoción de `EvidenceLevel`
   solo con A/B reproducible (≥ 3 × 120 s, medianas, misma demo/resolución/plan de energía).
4. Firmar los binarios (SmartScreen) y adjuntar `docs/manual-revert.md` al instalador.

## Comandos

```powershell
dotnet restore FragForge.sln
dotnet build   FragForge.sln -c Release
dotnet test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release
dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\cli
dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\app
```

CLI: `fragforge scan|list|apply|revert|status|snapshot|export-report|doctor` — exit codes
`0` éxito, `1` fallo, `2` uso, `3` preflight/catálogo, `4` cancelación, `5` rollback crítico.

## Documentos de referencia

| Archivo | Contenido |
|---|---|
| `docs/handoff/FragForge-Capa0-Informe.md` | Informe completo: arquitectura, árbol, **122 archivos con su código íntegro**, comandos, supuestos, contrato Capa 1 |
| `README.md` | Arquitectura, build/publish, cómo escribir un tweak (ejemplo JSON comentado) |
| `docs/manual-revert.md` | Reversión manual de emergencia por `$actionState` (.reg, .pow, .bcd, archivos) |
| `src/FragForge.Tweaks/Schema/tweak.schema.json` | Contrato exacto de un tweak |
| `src/FragForge.Core/Policy/denylist.json` | Política de seguridad central |
