## 4. Comandos exactos de restore/build/test/publish

### 4.1 En tu PC (Windows 11, red normal)

```powershell
# Requisitos: .NET SDK 10.0.400 o superior; Visual Studio 2022 17.14+ (opcional, solo para la App)
git clone https://github.com/EvoMaxPC/Evo_optimizer_CS2.git
cd Evo_optimizer_CS2
git checkout arena/c1a5f702-evo-optimizer-cs2

dotnet restore FragForge.sln
dotnet build   FragForge.sln -c Release          # debe dar 0 warnings / 0 errores
dotnet test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release
```

### 4.2 Publicar los ejecutables (win-x64, self-contained, un solo archivo)

```powershell
dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:PublishTrimmed=false -o publish\cli

dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:PublishTrimmed=false -o publish\app
```

> **Nota importante:** `RuntimeIdentifier`, `SelfContained` y `PublishSingleFile` **solo** se aplican cuando se pasa `-r win-x64` (están condicionados en el `.csproj`). Así `dotnet build` funciona también en Linux/macOS para Core/Actions/Tweaks/Composition/Tests. La App WPF solo compila en Windows (o con `EnableWindowsTargeting` + *targeting pack* del SDK).

Ambos binarios llevan `app.manifest` con `requestedExecutionLevel level="requireAdministrator"`, DPI awareness *per-monitor v2* y `supportedOS` Windows 10/11.

### 4.3 Ejecución

```powershell
# CLI
.\publish\cli\fragforge.exe doctor
.\publish\cli\fragforge.exe scan
.\publish\cli\fragforge.exe apply latency.mouse-acceleration-off --dry-run
.\publish\cli\fragforge.exe apply --profile Balanced
.\publish\cli\fragforge.exe status
.\publish\cli\fragforge.exe revert --last
.\publish\cli\fragforge.exe export-report --out "$env:USERPROFILE\Desktop\fragforge.html"

# App
.\publish\app\FragForge.exe
```

Códigos de salida: `0` éxito · `1` fallo · `2` uso · `3` preflight/catálogo · `4` cancelación · `5` rollback crítico.

### 4.4 Comandos usados en el sandbox (solo referencia, Linux sin red a nuget.org)

```bash
export DOTNET_ROOT=/home/user/.fragforge-tools/dotnet
D=$DOTNET_ROOT/dotnet
$D restore FragForge.sln --configfile /home/user/.fragforge-tools/probe/nuget.config
$D build   FragForge.sln -c Release --no-restore
$D test    tests/FragForge.Tests/FragForge.Tests.csproj -c Release --no-build
$D src/FragForge.Cli/bin/Release/net10.0/fragforge.dll scan --data-root /tmp/ff
```

**Salida real del sandbox (verificado):**

```text
FragForge.Core        -> …/FragForge.Core.dll
FragForge.Actions     -> …/FragForge.Actions.dll
FragForge.Tweaks      -> …/FragForge.Tweaks.dll
FragForge.Composition -> …/FragForge.Composition.dll
FragForge.Tests       -> …/FragForge.Tests.dll
FragForge.App         -> …/FragForge.dll
FragForge.Cli         -> …/fragforge.dll
Build succeeded.  0 Warning(s)  0 Error(s)

Passed! - Failed: 0, Passed: 103, Skipped: 0, Total: 103
```

### 4.5 Primer uso recomendado en el PN64

```powershell
fragforge doctor                       # ¿preflight apto? ¿System Protection activa?
fragforge scan                         # perfil de hardware + estado de los 10 tweaks
fragforge apply --profile Safe --dry-run
fragforge apply latency.mouse-acceleration-off      # 1 tweak, con punto de restauración
fragforge status
fragforge revert --last                 # verifica vuelta exacta al estado previo
```
