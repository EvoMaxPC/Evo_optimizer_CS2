# FragForge

Optimizador **reversible** para Windows 11 orientado a Counter-Strike 2 (estabilidad de frametime, latencia de
entrada y de red). No promete milagros: es un motor de cambios *transaccional* con captura exacta del estado
previo, verificación, reversión y una bitácora a prueba de manipulación.

> **Estado: v0.1.0, funcionalmente completa (capas 0–11).** La solución compila en el CI, pero **nada de esto se
> ha ejecutado en un equipo real con Windows**: el entorno donde se escribió no tiene compilador. El instalador y
> la firma nunca se han ejecutado. Antes de usar esto en un equipo que te importe, lee
> [`docs/handoff/e2e-manual.md`](docs/handoff/e2e-manual.md) y hazlo en una máquina que puedas reinstalar.

## Qué hace y qué no

| Sí | No |
|---|---|
| Cambios declarativos con snapshot previo y verificación posterior | No promete FPS: cada tweak declara `evidence` honesta |
| Reversión por snapshot, por tweak o por operación | No toca Defender, Firewall, Update, Secure Boot/TPM, VBS/HVCI |
| Journal append-only con cadena SHA-256 | No descarga nada ni envía telemetría |
| es-MX y en-US de serie | No mata procesos sin confirmación (ni Steam, ni CS2, ni anticheat) |

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
├─ .github/workflows/ci.yml          # ubuntu (logica) + windows-latest (solucion y publish en tags)
├─ docs/
│  ├─ elevacion.md                   # por que requireAdministrator (capa 11.4)
│  ├─ firma.md                       # signtool, variables de entorno, SmartScreen (capa 11.6)
│  ├─ seguridad.md                   # postura de seguridad y limites
│  ├─ descartados.md                 # lo que se decidio NO hacer y por que (todas las capas)
│  ├─ manual-revert.md               # reversion de emergencia sin el programa
│  └─ handoff/                       # informes por capa + e2e-manual.md
├─ installer/FragForge.iss           # Inno Setup (capa 11.5)
├─ scripts/publish.ps1               # publish self-contained win-x64
├─ scripts/sign.ps1                  # firma con signtool (certificado por entorno)
├─ src/
│  ├─ FragForge.Core/                # motor puro, net10.0, sin dependencias de Windows
│  ├─ FragForge.Actions/             # acciones concretas (registro, servicios, BCD, red, ...)
│  ├─ FragForge.Tweaks/              # catalogo JSON embebido + validador + localizacion
│  ├─ FragForge.Composition/         # CompositionRoot (DI) y CompositionOverrides para tests
│  ├─ FragForge.UI.Core/             # ViewModels, net10.0 SIN WPF: testeable (capa 11.2)
│  ├─ FragForge.App/                 # WPF: vistas, binding y dialogos. net10.0-windows
│  └─ FragForge.Cli/                 # fragforge.exe
└─ tests/FragForge.Tests/            # xUnit; los tests Windows llevan Trait Category=Windows
```

## 3. Compilar, probar, empaquetar y firmar

Requisitos: .NET SDK 10.0.400+ y **Windows** para compilar la App (WPF). El motor, la lógica y los tests
también compilan en Linux.

```powershell
dotnet restore FragForge.sln
dotnet build   FragForge.sln -c Release
dotnet test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release
```

En un entorno sin acceso a nuget.org, usa el modo offline:

```powershell
dotnet restore FragForge.sln --configfile .\NuGet.offline.config
```

Publicar e instalar:

```powershell
./scripts/publish.ps1                 # app + CLI self-contained win-x64 en artifacts/publish
./scripts/sign.ps1 -Path artifacts/publish   # opcional; sin certificado avisa de SmartScreen
iscc /DMyAppVersion=0.1.0 installer\FragForge.iss
```

La versión vive en **un solo sitio**: `Directory.Build.props` (`<Version>`). El assembly, el instalador y el
nombre del artefacto la leen de ahí.

Sin certificado, Windows **SmartScreen** avisará la primera vez. Eso no es un fallo: ver
[`docs/firma.md`](docs/firma.md) y [`docs/elevacion.md`](docs/elevacion.md).

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
fragforge match [--duration SEGUNDOS] [--guardar-log]
```

`match` mide CS2 en vivo con PresentMon, sin overlay ni inyección. Requiere Windows y administrador;
F9 marca un segmento, F10 muestra una vista parcial y F11 o Ctrl+C termina con resumen. Sin duración
espera hasta terminar manualmente; sin `--guardar-log` no guarda el resumen. No verificado en vivo.

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

## 8. Estado por capas

| Capa | Contenido | Estado |
|---|---|---|
| 0 | Motor transaccional, journal, snapshot, denylist | Cerrada |
| 1 | Medición A/B con PresentMon (`probe`, `mouse-test`, `tools verify`) | Cerrada |
| 2–4 | Perfiles, riesgo, rollback, idempotencia, deriva | Cerrada |
| 5 | Red: enlace, TCP, DPC | Cerrada |
| 6–7 | Latencia de entrada e iGPU | Cerrada |
| 8 | CS2: resolución, prioridad, configuración | Cerrada |
| 9 | Modo sesión y recuperación tras corte | Cerrada |
| 10 | Seguridad: postura, preflight, journal tamper-evident | Cerrada |
| 11 | UI testeable, instalador, firma, CI y E2E | Cerrada |

**Lo que no está verificado**: la compilación, el instalador, la firma y la UI **nunca se han ejecutado** en un
equipo Windows desde este repositorio. Los informes de cada capa en `docs/handoff/` lo dicen capa por capa, y
[`docs/handoff/FragForge-Capa11-Informe.md`](docs/handoff/FragForge-Capa11-Informe.md) lo resume.

Reversión de emergencia sin el programa: [`docs/manual-revert.md`](docs/manual-revert.md).
