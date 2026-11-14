# FragForge — Capa 0 (fundaciones)

Optimizador **reversible** para Windows 11 orientado a Counter-Strike 2 (FPS, estabilidad de frametime, latencia de entrada y de red). Esta es la **Capa 0**: no promete milagros, sino un motor de cambios *transaccional* con captura exacta del estado previo, verificación, reversión automática y una bitácora a prueba de manipulación.

> Estado actual: solución completa y compilable (Core, Actions, Tweaks, Composition, Cli, App, Tests). La capa de medición A/B pertenece a la Capa 1, pero el motor ya expone los ganchos.

## 1. Decisiones de arquitectura

| Decisión | Por qué |
|---|---|
| **.NET 10 LTS** (`net10.0`; App `net10.0-windows`) | Soporte hasta nov-2028. .NET 8 entra en EOL en nov-2026. |
| **WPF + CommunityToolkit.Mvvm 8.4.1** | WPF viene con Windows (sin runtime extra); el toolkit evita *boilerplate* sin acoplar el motor a la UI. |
| **Microsoft.Extensions.Hosting/DI/Logging + Serilog** | Composición explícita, *fakes* fáciles en tests, log estructurado a archivo rotativo y consola. |
| **Motores y acciones detrás de interfaces** | `IRegistry`, `IProcessRunner`, `IServiceController`, `IExecutionGate`… permiten probar el 100% del motor en Linux con *fakes*. |
| **Catálogo declarativo JSON con esquema propio** | Un tweak = un archivo con textos es-MX/en-US, evidencia, riesgo, condiciones y acciones. Validado por esquema **y** por semántica (IDs, ciclos, conflictos, riesgo). |
| **Snapshot + journal append-only con cadena SHA-256** | Revertir exige prueba del estado previo. El journal detecta ediciones manuales. |
| **Separación captura/restauración vs. catálogo** | Si un tweak desaparece del catálogo, el snapshot sigue pudiendo revertirse porque guarda la definición completa y sus estados. |
| **Denylist central embebida + fusión de política de usuario** | Seguridad por defecto *y* extensibilidad consciente desde `%ProgramData%\FragForge\policy\denylist.json`. |
| **Todo offline** | Sin descargas ni scripts remotos; PowerShell local solo con bandera explícita, hash de scripts y doble confirmación. |

### Flujo de una aplicación
```
preflight → dependencias/conflictos → condiciones → perfil/riesgo → ¿ya aplicado? (idempotencia)
   → snapshot (captura exacta + .reg + .pow/.bcd, punto de restauración)
   → por acción: Apply → Verify   (si Verify falla: rollback inverso del tweak y se detiene el lote)
   → journal (éxito/fallo por tweak)
```

## 2. Árbol de carpetas

```text
Evo_optimizer_CS2/
├─ FragForge.sln
├─ Directory.Build.props / Directory.Packages.props / global.json / NuGet.config
├─ .github/workflows/ci.yml
├─ docs/
│  └─ manual-revert.md
├─ src/
│  ├─ FragForge.Core/                 # motor puro, sin dependencias de Windows
│  │  ├─ Abstractions/               # IRegistry, IProcessRunner, ITweakAction, contratos…
│  │  ├─ Conditions/                 # ConditionFactory (windows, hybridCpu, gpuVendor, cs2…)
│  │  ├─ Engine/                     # TweakEngine, DependencyResolver, ProfileSelector…
│  │  ├─ Localization/               # Localizer, UiLogSink
│  │  ├─ Logging/                    # JsonDefaults, UiLoggerProvider
│  │  ├─ Models/                     # enums, resultados, snapshot/journal, HardwareProfile
│  │  ├─ Persistence/                # FragForgePaths, FileSnapshotStore, FileJournal, política…
│  │  ├─ Policy/denylist.json        # denylist embebida
│  │  └─ Preflight/
│  ├─ FragForge.Actions/             # implementaciones reales (Windows) + fakes hostiles
│  │  ├─ Bcd/  Diagnostics/  Files/  Network/  Platform/  Power/  Registry/  Scripting/  Services/  Tasks/
│  │  └─ DependencyInjection/
│  ├─ FragForge.Tweaks/              # catálogo JSON embebido, esquema, validador, i18n
│  │  ├─ Catalog/{Cpu,Power,Network,Gpu,Latency,Cs2,Debloat,Memory,Diagnostics}/
│  │  ├─ Localization/{es-MX,en-US}.json
│  │  ├─ Schema/tweak.schema.json
│  │  └─ Validation/
│  ├─ FragForge.Composition/         # CompositionRoot + ReportService (HTML/JSON)
│  ├─ FragForge.Cli/                 # fragforge.exe
│  └─ FragForge.App/                 # FragForge.exe (WPF MVVM, tema oscuro)
└─ tests/FragForge.Tests/            # xUnit 2.9.3: fakes de registro/proceso/servicio/tarea/red
```

## 3. Compilar, probar y publicar

```powershell
# Requisitos: .NET SDK 10.0.400+ y Windows 11 para ejecutar (compilar la App requiere Windows).
dotnet restore FragForge.sln
dotnet build   FragForge.sln -c Release
dotnet test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release

# Cli autocontenido de un solo archivo (win-x64)
dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 --self-contained true -o publish/cli

# App WPF autocontenida (manifiesto requireAdministrator)
dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 --self-contained true -o publish/app
```

En un entorno sin acceso a nuget.org, usa el modo offline (carpeta de paquetes local):

```powershell
dotnet restore FragForge.sln --configfile .\NuGet.offline.config
```

## 4. Uso del CLI

```text
fragforge scan   [--json] [--culture es-MX|en-US]
fragforge list   [--category Cs2] [--risk Moderate] [--json]
fragforge apply  <ids…|--profile Safe|Balanced|Competitive|Advanced> [--dry-run] [--force]
                 [--no-restore-point] [--enable-system-protection] [--cs2-path RUTA]
                 [--accept-risk ID] [--risk-phrase "ACEPTO EL RIESGO"] [--allow-powershell]
fragforge revert <ids…|--snapshot ID|--last> [--force]
fragforge status [--json]
fragforge snapshot list|create|show [ID] [--label TEXTO] [--no-restore-point]
fragforge export-report --out reporte.html|reporte.json
fragforge doctor [--json]
```

Códigos de salida: `0` éxito · `1` fallo · `2` uso · `3` preflight/catálogo · `4` cancelación · `5` rollback crítico.

Ejemplos:

```powershell
fragforge scan
fragforge apply latency.mouse-acceleration-off latency.game-dvr-capture-off --dry-run
fragforge apply --profile Competitive
fragforge revert --last
fragforge export-report --out "$env:USERPROFILE\Desktop\fragforge.html"
```

## 5. Cómo escribir un tweak

Un archivo JSON por tweak en `src/FragForge.Tweaks/Catalog/<Categoría>/`. Se embebe en el ensamblado y se valida al arrancar.

```jsonc
{
  // Identificador estable, minúsculas con puntos. NUNCA lo cambies tras publicarlo:
  // el journal y los snapshots lo referencian.
  "id": "cs2.example-tweak",
  "category": "Cs2",                       // Cpu|Power|Network|Gpu|Latency|Cs2|Debloat|Memory|Diagnostics
  "title":       { "es-MX": "Título visible", "en-US": "Visible title" },
  "description": { "es-MX": "Qué hace exactamente.", "en-US": "What it does exactly." },
  "evidence": "Medium",                    // High|Medium|Low|Experimental — sin placebo
  "risk": "Moderate",                      // Safe|Moderate|Advanced|Experimental
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [                          // BLOQUEAN si no se cumplen; Unknown ≠ aplicable
    { "type": "windows" },
    { "type": "hybridCpu", "parameters": { "expected": true } }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          { "key": "HKLM\\SYSTEM\\CurrentControlSet\\Services\\SysMain", "name": "Start",
            "kind": "DWord", "value": 4, "mode": "Set" }
          // mode "AddToken" añade un token a un valor REG_SZ existente sin destruir el resto.
        ]
      }
    }
  ],
  "tags": ["opt-in"],                      // "opt-in" lo excluye de Balanced
  "conflictsWith": ["other.tweak"],
  "dependsOn": [],
  "expectedImpact": { "fps": "Bajo/variable", "1%lows": "Posible", "inputLatency": "None" },
  "references": ["https://learn.microsoft.com/…"],   // obligatorio: sin fuente, no hay tweak
  "notes":          { "es-MX": "Honestidad sobre límites.", "en-US": "Honesty about limits." },
  "whenNotToApply": { "es-MX": "Cuándo NO usarlo.",          "en-US": "When NOT to apply." }
}
```

Reglas duras del validador:

1. `id` único, minúsculas, `^[a-z0-9][a-z0-9.-]{2,80}$`.
2. `title`/`description` **en ambos idiomas**.
3. Al menos una `reference` con URL.
4. Acciones `PowerShell` o `Bcdedit` ⇒ `risk` debe ser `Advanced` o `Experimental`.
5. Toda acción mutante exige `requiresAdmin: true`.
6. Sin autociclos (`dependsOn`/`conflictsWith` que se apunten a sí mismos) y sin dependencias/conflictos inexistentes.
7. Cada destino debe pasar la **denylist** en el momento de construir la acción (falla al cargar el catálogo, no al aplicar).

## 6. Seguridad y política

- **Nunca** por defecto: Defender, Firewall, Windows Update, Secure Boot/TPM, mitigaciones de CPU, VBS/HVCI.
- Los tweaks `Advanced`/`Experimental` requieren perfil *Advanced*, `--accept-risk ID` y la frase literal `ACEPTO EL RIESGO`.
- `--force` **solo** omite idempotencia y detección de deriva. No omite denylist, condiciones ni preflight.
- Servicios intocables (entre otros): `WinDefend`, `mpssvc`, `BFE`, `RpcSs`, `DcomLaunch`, `PlugPlay`, `Power`, `EventLog`, `CryptSvc`, `TrustedInstaller`, `ipfsvc`, `jhi_service`, `cplspcon`, `WMIRegistrationService`.
- `FileEdit` rechaza `.exe/.dll/.sys` y rutas de `%SystemRoot%` (solo configuración de texto del usuario/juego).
- `PowerShell` está deshabilitado por defecto, bloquea tokens de red/ejecución dinámica y guarda SHA-256 de los cuatro scripts.

## 7. Reversión manual de emergencia

Ver [`docs/manual-revert.md`](docs/manual-revert.md). Resumen:

1. `%ProgramData%\FragForge\snapshots\<id>\snapshot.json` contiene el estado exacto (tipo y datos) de cada valor, más `artifacts\registry-before.reg`, `*.pow` y `*.bcd`.
2. `fragforge revert --snapshot <id>` es la vía normal.
3. Si el CLI no arranca: importa `artifacts\registry-before.reg` (doble clic / `reg import`), `powercfg /setactive <GUID previo>` usando el snapshot, `bcdedit /import artifacts\bcd-*.bcd` para BCD y restaura los archivos de `artifacts\` con sus rutas y fechas.

## 8. Capa 0 vs Capa 1

La Capa 0 **no mide**; deja ganchos para medir: `IExperimentHook` (`BeforeApply` fatal, `AfterApply`/`AfterRevert` con advertencia), `InspectOnly` como línea base de diagnóstico y `export-report` con JSON/HTML listos para PresentMon, frametimes, 1% lows, DPC/ISR y temperaturas.
