# Reversión manual de emergencia (FragForge Capa 0)

Usa este documento solo si `fragforge revert` no está disponible. **No** edites el journal ni borres snapshots: son tu evidencia.

## 1. Localiza el snapshot

```powershell
Get-ChildItem "$env:ProgramData\FragForge\snapshots" | Sort-Object LastWriteTime -Descending | Select-Object -First 5
Get-Content "$env:ProgramData\FragForge\snapshots\<ID>\snapshot.json" | Select-String -Pattern '"tweakId"' -Context 0,1
```

Cada entrada `actions[]` guarda `$actionState` y los datos exactos:

| `$actionState` | Qué restaurar a mano |
|---|---|
| `registry` | `values[]` con `key`, `name`, `valueExisted`, `valueKind`, `stringData`/`integerData`/`binaryBase64`/`multiStringData` y `missingAncestors` |
| `service` | `startMode`, `state`, `delayedAutoStart`, `delayedAutoStartExisted` |
| `scheduledTask` | `enabled` |
| `powerPlan` | `previousActiveGuid`, `planExisted`, `settings[]` (AC/DC) |
| `bcd` | `element`, `existed`, `previousValue` |
| `network` | `properties[]` con `registryValue` |
| `file` | `path`, `existed`, `contentBase64`, `lastWriteTimeUtc` |
| `script` | `capturedJson` (el script de reversión recibe ese JSON por stdin) |
| `inspection` | nada: solo lectura |

## 2. Registro

```powershell
# Atajo: importa el .reg generado antes de aplicar (solo valores capturados)
reg import "$env:ProgramData\FragForge\snapshots\<ID>\artifacts\registry-before.reg"

# O manualmente, respetando el tipo exacto:
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling" /v PowerThrottlingOff /t REG_DWORD /d 0 /f
reg delete "HKCU\Software\Microsoft\GameBar" /v AutoGameModeEnabled /f    # si valueExisted=false
```

Si `valueExisted` es `false`, **elimina** el valor; si `missingAncestors` lista claves, elimínalas solo si están vacías.

## 3. Servicios

```powershell
Set-Service -Name SysMain -StartupType Automatic
sc.exe config SysMain start= auto
Get-Service SysMain | Start-Service
```

Para `delayedAutoStart`, compara con `delayedAutoStartExisted`; si era `false`, borra `DelayedAutostart` (`reg delete "HKLM\SYSTEM\CurrentControlSet\Services\<Servicio>" /v DelayedAutostart /f`).

## 4. Energía

```powershell
powercfg /setactive <previousActiveGuid>          # del snapshot (S-1-...)
powercfg /setacvalueindex <guid> <subgrupo> <ajuste> <acIndex>
powercfg /setdcvalueindex <guid> <subgrupo> <ajuste> <dcIndex>
powercfg /delete <guidDelPlanCreado>              # solo si planExisted era false
```

## 5. BCD

```powershell
bcdedit /enum "{current}" /v
bcdedit /import "$env:ProgramData\FragForge\snapshots\<ID>\artifacts\bcd-disabledynamictick.bcd"
bcdedit /deletevalue "{current}" disabledynamictick     # si existed=false
bcdedit /set "{current}" disabledynamictick No          # si previousValue=No
```

## 6. Red

```powershell
Set-NetAdapterAdvancedProperty -Name "Ethernet" -RegistryKeyword "*EEE" -RegistryValue 1
```

## 7. Archivos

Decodifica `contentBase64` y escribe los bytes; repón `lastWriteTimeUtc`. Si `existed=false`, elimina el archivo y los directorios vacíos que FragForge creó.

## 8. Punto de restauración

`RestorePointSequence` en el snapshot corresponde al punto creado. En Windows 11: *Crear un punto de restauración* → *Restauración del sistema* → elige el punto con la fecha indicada. Requiere Protección del sistema activada.

## 9. Verificación final

```powershell
fragforge status
fragforge scan
```

Ambos deben mostrar `journal íntegro` y los tweaks revertidos en `NotApplied` (o `AppliedExternally` si el sistema ya coincidía por otra herramienta, p. ej. Atlas).
