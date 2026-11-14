<#
.SYNOPSIS
    Publica FragForge (app WPF y CLI) self-contained win-x64, listo para el instalador.

.DESCRIPTION
    Capa 11.5. No hace nada que no sea `dotnet publish`: no firma, no empaqueta y no toca el sistema.
    La firma va DESPUES, con scripts/sign.ps1 sobre los .exe que salen de aqui.

.PARAMETER Configuration
    Release por defecto. No hay motivo para publicar Debug.

.PARAMETER OutputRoot
    Carpeta raiz de salida. Por defecto artifacts/publish.

.PARAMETER SkipTests
    No ejecuta los tests. Solo para no repetirlos cuando ya se corrieron en CI.

.EXAMPLE
    ./scripts/publish.ps1
    ./scripts/publish.ps1 -Configuration Release -OutputRoot D:\build\fragforge
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputRoot = 'artifacts/publish',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime  = 'win-x64'

# El runtime tiene que ser Windows: publicar win-x64 desde Linux produce un binario que no se puede
# ejecutar aqui y el instalador espera un arbol de Windows.
if (-not $IsWindows -and $env:OS -ne 'Windows_NT') {
    throw 'Este script publica win-x64 y debe ejecutarse en Windows.'
}

Push-Location $repoRoot
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'No se encontro `dotnet` en el PATH.'
    }

    Write-Host "==> Version" -ForegroundColor Cyan
    # La version sale del Directory.Build.props: una sola fuente de verdad para assembly, instalador y
    # nombre del artefacto. Si se cambia alli, todo lo demas la sigue.
    [xml] $props = Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props')
    $version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $version) { throw 'No se pudo leer <Version> de Directory.Build.props.' }
    Write-Host "    $version"

    if (-not $SkipTests) {
        Write-Host '==> Tests' -ForegroundColor Cyan
        dotnet test (Join-Path $repoRoot 'FragForge.sln') -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "Los tests fallaron (exit $LASTEXITCODE)." }
    }

    foreach ($project in @(
        @{ Path = 'src/FragForge.App/FragForge.App.csproj'; Out = 'app' },
        @{ Path = 'src/FragForge.Cli/FragForge.Cli.csproj'; Out = 'cli' }
    )) {
        $out = Join-Path (Join-Path $OutputRoot $project.Out) $runtime
        if (Test-Path $out) { Remove-Item -Recurse -Force $out }
        Write-Host "==> publish $($project.Path) -> $out" -ForegroundColor Cyan
        dotnet publish (Join-Path $repoRoot $project.Path) `
            -c $Configuration `
            -r $runtime `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:DebugType=embedded `
            -o $out
        if ($LASTEXITCODE -ne 0) { throw "publish fallo en $($project.Path) (exit $LASTEXITCODE)." }
    }

    # Un .exe que no existe rompe el instalador mas tarde y de forma menos clara: se comprueba aqui.
    foreach ($exe in @(
        (Join-Path $OutputRoot "app/$runtime/FragForge.exe"),
        (Join-Path $OutputRoot "cli/$runtime/fragforge.exe")
    )) {
        if (-not (Test-Path $exe)) { throw "Falta el ejecutable esperado: $exe" }
        $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
        Write-Host "    OK $exe ($size MB)"
    }

    Write-Host ''
    Write-Host "Listo. Siguiente paso: ./scripts/sign.ps1 -Path $OutputRoot" -ForegroundColor Green
    Write-Host 'Recuerda: sin certificado, SmartScreen avisara. Ver docs/firma.md.'
}
finally {
    Pop-Location
}
