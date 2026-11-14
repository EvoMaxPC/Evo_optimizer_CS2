# Firma de código (Capa 11.6)

Sin certificado, Windows **SmartScreen** avisará al usuario la primera vez que ejecute el instalador o la app
("Windows protegió tu PC" / editor desconocido). Eso no es un fallo del código: es lo que le pasa a cualquier
binario sin reputación. Este documento explica cómo firmar y qué pasa si no se firma.

---

## 1. Regla que no se rompe

**No se commitea ningún certificado, contraseña ni token.** Ni `.pfx`, ni `.p12`, ni `.snk`, ni un `.env` con
credenciales. El script de firma los recibe **por parámetro o por variable de entorno**, y nunca los escribe en
el log.

## 2. Cómo firmar

```powershell
# 1. Publicar (esto no firma nada)
./scripts/publish.ps1

# 2. Configurar el certificado POR VARIABLE DE ENTORNO (no en el repo, no en el script)
$env:FRAGFORGE_SIGN_CERT      = 'D:\certs\fragforge.pfx'
$env:FRAGFORGE_SIGN_PASSWORD  = '...'

# 3. Firmar y verificar
./scripts/sign.ps1 -Path artifacts/publish
```

Alternativa con certificado ya instalado en el almacén:

```powershell
$env:FRAGFORGE_SIGN_THUMBPRINT = 'A1B2C3...'
./scripts/sign.ps1
```

| Variable | Para qué | Obligatoria |
|---|---|---|
| `FRAGFORGE_SIGN_CERT` | Ruta al `.pfx` | Una de las dos |
| `FRAGFORGE_SIGN_THUMBPRINT` | Huella del certificado en el almacén | Una de las dos |
| `FRAGFORGE_SIGN_PASSWORD` | Contraseña del `.pfx` | Solo si el `.pfx` la tiene |
| `FRAGFORGE_SIGN_TIMESTAMP` | Servidor de timestamp | No (por defecto `timestamp.digicert.com`) |

**Timestamp obligatorio en la práctica**: sin él, la firma caduca cuando caduca el certificado y los binarios
que ya están instalados dejan de validar retroactivamente. El script lo pone siempre.

## 3. Qué hace el script

1. Busca `signtool.exe` en el PATH y, si no está, en `%ProgramFiles(x86)%\Windows Kits\10\bin`.
2. Firma cada `.exe` con SHA-256 y timestamp RFC 3161.
3. **Verifica** cada firma (`signtool verify /pa`). Una firma inválida es peor que no tener firma, porque el
   usuario confía en ella.
4. Si no hay certificado configurado: **avisa y sigue sin firmar** (no rompe un build local). Con
   `-RequireSignature` falla en su lugar.

## 4. El desinstalador de Inno Setup

Inno genera su propio `unins000.exe` al compilar el instalador. `scripts/sign.ps1` firma los `.exe` que
encuentre bajo `-Path`, así que **hay que ejecutarlo también sobre `artifacts/installer`** después de compilar
el `.iss`, o firmar el uninstaller aparte. Si no se firma, SmartScreen también lo avisará al desinstalar.

## 5. Qué NO arregla la firma

- **No quita SmartScreen de golpe.** Un certificado nuevo no tiene reputación: los primeros usuarios siguen
  viendo el aviso hasta que Microsoft acumule historial de descargas. Un certificado EV lo evita desde el
  primer día, pero cuesta bastante más.
- **No protege de un binario modificado.** La firma dice "esto lo publicó FragForge"; no dice "esto es
  seguro". La app pide elevación, así que el usuario tiene que poder comprobar el editor en la ventana de UAC.
- **No sustituye al journal.** Revertir cambios depende del journal en `%ProgramData%\FragForge`, no de la firma.

## 6. Comprobación manual

```powershell
Get-AuthenticodeSignature .\artifacts\publish\app\win-x64\FragForge.exe | Format-List
# Status debe ser 'Valid' y SignerCertificate el certificado esperado.
```

## 7. Estado en esta ronda

**La firma no se ha ejecutado nunca.** En este entorno no hay PowerShell ni signtool ni certificado. Los
scripts están escritos y revisados a mano, pero **sin probar**. Ver el informe de la Capa 11.
