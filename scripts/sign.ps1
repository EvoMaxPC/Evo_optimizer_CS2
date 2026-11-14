<#
.SYNOPSIS
    Firma los ejecutables publicados con signtool.

.DESCRIPTION
    Capa 11.6. El certificado NUNCA esta en el repositorio: se pasa por parametro o por variable de
    entorno. Si no hay certificado, el script NO falla por defecto: avisa de que Windows SmartScreen
    mostrara "editor desconocido" y sigue. Con -RequireSignature si falla.

    PROHIBIDO commitear certificados (.pfx), contrasenas o tokens. Este script no los escribe en disco ni
    en el log.

.PARAMETER Path
    Carpeta con los .exe a firmar (la que produce scripts/publish.ps1).

.PARAMETER CertificatePath
    Ruta al .pfx. Tambien se puede usar la variable de entorno FRAGFORGE_SIGN_CERT.

.PARAMETER CertificateThumbprint
    Huella del certificado instalado en el almacen. Alternativa al .pfx. Variable: FRAGFORGE_SIGN_THUMBPRINT.

.PARAMETER Password
    Contrasena del .pfx. Variable: FRAGFORGE_SIGN_PASSWORD. Si se pasa por linea de comandos queda en el
    historial de PowerShell: preferir la variable de entorno.

.PARAMETER TimestampUrl
    Servidor de timestamp RFC 3161. Variable: FRAGFORGE_SIGN_TIMESTAMP. Sin timestamp, la firma caduca
    cuando caduque el certificado y el binario deja de ser valido retroactivamente.

.EXAMPLE
    $env:FRAGFORGE_SIGN_CERT = 'D:\certs\fragforge.pfx'
    $env:FRAGFORGE_SIGN_PASSWORD = '...'
    ./scripts/sign.ps1 -Path artifacts/publish
#>
[CmdletBinding()]
param(
    [string] $Path = 'artifacts/publish',
    [string] $CertificatePath = $env:FRAGFORGE_SIGN_CERT,
    [string] $CertificateThumbprint = $env:FRAGFORGE_SIGN_THUMBPRINT,
    [string] $Password = $env:FRAGFORGE_SIGN_PASSWORD,
    [string] $TimestampUrl = $(if ($env:FRAGFORGE_SIGN_TIMESTAMP) { $env:FRAGFORGE_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }),
    [switch] $RequireSignature
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $hasCert = $CertificatePath -or $CertificateThumbprint
    if (-not $hasCert) {
        $message = @'
No hay certificado configurado: NO se firma nada.

  Sin firma, Windows SmartScreen mostrara "Windows protegio tu PC" / editor desconocido
  la primera vez que alguien ejecute el instalador o la app. No es un fallo del codigo:
  es lo que pasa con cualquier binario sin reputacion.

  Para firmar:
    $env:FRAGFORGE_SIGN_CERT     = 'ruta\al\certificado.pfx'
    $env:FRAGFORGE_SIGN_PASSWORD = 'contraseña'
    ./scripts/sign.ps1

  Ver docs/firma.md.
'@
        if ($RequireSignature) { throw $message }
        Write-Warning $message
        return
    }

    if ($CertificatePath -and -not (Test-Path $CertificatePath)) {
        # Se dice la RUTA, nunca la contrasena ni el contenido.
        throw "No existe el certificado indicado: $CertificatePath"
    }

    # signtool viene con el Windows SDK. Se busca en el PATH y, si no esta, en las rutas tipicas.
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source
    if (-not $signtool) {
        $candidates = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending
        $signtool = $candidates | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw 'No se encontro signtool.exe. Instala el Windows SDK.' }

    $targets = Get-ChildItem -Path $Path -Filter '*.exe' -Recurse -ErrorAction SilentlyContinue
    if (-not $targets) { throw "No hay .exe que firmar en '$Path'. Ejecuta antes scripts/publish.ps1." }

    foreach ($exe in $targets) {
        Write-Host "==> firmando $($exe.FullName)" -ForegroundColor Cyan
        # NO llamar a esta variable $args: es una variable automatica de PowerShell y asignarla rompe el
        # scope de la funcion. Se llama $signArgs a proposito.
        $signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/v')
        if ($CertificateThumbprint) {
            $signArgs += @('/sha1', $CertificateThumbprint)
        } else {
            $signArgs += @('/f', $CertificatePath)
            if ($Password) { $signArgs += @('/p', $Password) }
        }
        $signArgs += $exe.FullName

        & $signtool @signArgs
        if ($LASTEXITCODE -ne 0) { throw "signtool fallo en $($exe.Name) (exit $LASTEXITCODE)." }

        # Verificar despues de firmar: una firma invalida es peor que no tener firma, porque el usuario
        # confia en ella.
        & $signtool verify /pa /v $exe.FullName
        if ($LASTEXITCODE -ne 0) { throw "La firma de $($exe.Name) no verifica." }
    }

    Write-Host ''
    Write-Host 'Firmado y verificado.' -ForegroundColor Green
}
finally {
    Pop-Location
}
