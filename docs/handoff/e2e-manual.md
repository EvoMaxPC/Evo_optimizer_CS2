# Prueba E2E manual — Capa 11.8

**Esto no se puede automatizar en CI.** Cada paso escribe en el registro, en servicios o en el enrutador de
red del equipo donde se ejecuta. El CI corre tests con dobles; lo de aquí se hace a mano, en una máquina que se
pueda reinstalar.

**Equipo de referencia**: i3-1220P, Intel UHD, Windows 11 25H2 es-MX (build ≥ 22000). Versión 0.1.0.

> **Aviso**: antes de empezar, crea un punto de restauración a mano desde Windows. El programa también puede
> crearlo, pero si algo va mal quieres uno que no dependa de que el programa arranque.

---

## 0. Preparación

```powershell
# Consola elevada (la app y el CLI piden requireAdministrator)
cd <repo>
dotnet build FragForge.sln -c Release
$cli = ".\src\FragForge.Cli\bin\Release\net10.0\fragforge.exe"
```

| # | Comando | Resultado esperado |
|---|---|---|
| 0.1 | `& $cli doctor` | Lista el preflight, el número de tweaks del catálogo y `journal íntegro`. **Exit 0** si el equipo es apto. |
| 0.2 | `& $cli doctor; $LASTEXITCODE` | Imprime `0`. Si imprime `3`, el preflight bloqueó algo: leer el motivo antes de seguir. |

**Si 0.1 falla, PARA.** El resto del guion aplica cambios reales.

---

## 1. Scan

```powershell
& $cli scan
```

**Esperado**
- Hardware: CPU `12th Gen Intel(R) Core(TM) i3-1220P`, GPU Intel UHD, RAM, discos.
- Cada tweak con su estado (`NotApplied` / `Applied` / `NotApplicable`).
- Ningún tweak en `Unknown` sin motivo escrito al lado.
- **No se modifica nada**: `Get-AuthenticodeSignature` del registro no cambia, no se crea snapshot.

```powershell
# Comprobación de que el scan no escribe: no debe aparecer un snapshot nuevo
& $cli snapshot list
```

**Falla si**: el scan tarda más de ~60 s, lanza excepción, o aparece un snapshot que no pediste.

---

## 2. Dry-run de CADA perfil

Este es el paso más importante: el dry-run dice qué se haría **sin hacerlo**.

```powershell
foreach ($p in 'Safe','Balanced','Competitive','Advanced') {
  Write-Host "=== $p ===" -ForegroundColor Cyan
  & $cli apply --profile $p --dry-run
  Write-Host "exit=$LASTEXITCODE"
}
```

**Esperado por perfil**

| Perfil | Qué debe pasar |
|---|---|
| `Safe` | Solo tweaks conservadores. Ningún `RequiresReboot` sin marcarlo. Ningún tweak de red que corte el enlace. **Exit 0**. |
| `Balanced` | Más tweaks que Safe; los de `Risk=Advanced` **no** deben aparecer. **Exit 0**. |
| `Competitive` | Incluye tweaks de CS2. Si hay alguno que reinicie el adaptador de red, tiene que avisarlo. **Exit 0**. |
| `Advanced` | Puede incluir `Risk=Advanced`. Con `--dry-run` **no** debe pedir confirmación de riesgo: la frase solo se exige al aplicar de verdad. **Exit 0**. |

```powershell
# El dry-run tampoco escribe nada:
& $cli snapshot list
```

**Falla si**
- Un perfil cambia el perfil anterior (`--profile Safe` y luego `--profile Advanced` no deben "arrastrar").
- Aparece un snapshot nuevo.
- El dry-run aplica algo: compruébalo abriendo `regedit` en una clave que el dry-run diga que tocaría.

---

## 3. Aplicar UN tweak

Elige **uno** de riesgo bajo. Este es el único paso que modifica el sistema.

```powershell
# 1) Ver qué hay: elige un id con Risk=Safe y RequiresReboot=false
& $cli list --risk Safe

# 2) Vista previa de ese solo tweak
& $cli apply <id-elegido> --dry-run

# 3) Aplicar
& $cli apply <id-elegido>
```

**Esperado**
- El comando dice qué acción hace, crea un snapshot y sale con **exit 0**.
- Si el tweak es `Risk=Advanced`, pide `--accept-risk <id>` y `--risk-phrase`. Si no lo pasas, **no aplica** y
  sale con código distinto de 0. Eso es correcto, no un fallo.

```powershell
# 4) ¿Se aplicó?
& $cli scan | Select-String "<id-elegido>"
# 5) Observa el cambio a mano en regedit / services.msc / Get-NetAdapter
```

---

## 4. Verificar

```powershell
& $cli status
```

**Esperado**
- `Journal: íntegro (N entradas)` — N ≥ 2 (snapshot + apply).
- `snapshots=1` (o el que corresponda).
- El tweak aplicado sale `Applied` en la lista.

**Falla si** el journal sale `DAÑADO`. **No sigas**: con el journal dañado la reversión no es de fiar.

---

## 5. Revertir

```powershell
& $cli revert --last
& $cli status
```

**Esperado**
- `revert --last` restaura el tweak aplicado y sale **exit 0**.
- `status` muestra el tweak otra vez como `NotApplied` (o como estaba antes).
- En `regedit`, el valor vuelve a su estado original.

```powershell
# Segunda comprobación: revertir otra vez no debe romper nada
& $cli revert --last
```

**Esperado**: dice que no hay nada que revertir, y no falla de forma catastrófica.

---

## 6. Modo sesión con CS2 cerrado

```powershell
& $cli session status      # debe decir "Modo sesión : inactivo"
& $cli session start --profile Competitive
& $cli session status
& $cli session stop
& $cli session status
```

**Esperado**
- `start`: crea el marcador y aplica el perfil. **Con CS2 cerrado no debe matar nada ni abrir el juego.**
- `status`: `Modo sesión : esperando CS2` o `activo`, con `Snapshot` y `Operación` rellenados.
- `stop`: `Sesión revertida y marcador eliminado.`
- El último `status`: `inactivo`, sin marcador.

**Prueba explícita de que no mata procesos**: abre el Bloc de notas o el Explorador antes de `start` y comprueba
que sigue abierto con el mismo PID.

```powershell
Get-Process notepad -ErrorAction SilentlyContinue | Select-Object Id, ProcessName
```

**Falla si** desaparece cualquier proceso que no sea de FragForge. R-M lo prohíbe: nunca se mata nada sin
confirmación y journal, y nunca Steam, CS2 ni el anticheat.

---

## 7. Recuperación tras cerrar a la fuerza

Este es el escenario que de verdad rompe las cosas: el usuario mata el programa a mitad.

```powershell
# 1) Arranca una sesión
& $cli session start --profile Competitive

# 2) Con la sesión activa, MATA el proceso a la fuerza desde OTRA consola:
Stop-Process -Name fragforge -Force

# 3) Comprueba que el marcador quedó huérfano
& $cli session status
```

**Esperado (paso 3)**: sale como sesión pendiente/recuperable, con el `Operación` y el `Snapshot` del punto 6, y
**no** como "inactivo" limpio. El marcador es lo que permite recuperar.

```powershell
# 4) Recuperar
& $cli session stop
& $cli session status
```

**Esperado**: revierte lo que la sesión había aplicado, borra el marcador y deja `inactivo`.

```powershell
# 5) Si el paso 4 falla a propósito (por ejemplo, cerrando el programa durante el revert):
#    vuelve a ejecutarlo. Debe ser REINTENTABLE: el marcador se conserva hasta que la reversión
#    se completa de verdad.
& $cli session stop
```

**Falla si**
- El marcador se borra aunque la reversión no se completó (perderías la forma de recuperar).
- Tras matar el proceso queda un tweak aplicado y no hay forma de saberlo (`status` miente).
- El journal sale `DAÑADO` después del corte.

**Prueba destructiva opcional** (solo en una máquina desechable): corta la luz / reinicia a mitad del revert y
comprueba que al volver, `status` sigue ofreciendo la recuperación. Es la prueba que valida que el journal
sobrevive a un corte seco.

---

## 8. La UI (lo que el CLI no cubre)

Abrir `src\FragForge.App\bin\Release\net10.0-windows\FragForge.exe`.

| # | Comprobación | Esperado |
|---|---|---|
| 8.1 | Título de la ventana | `FragForge 0.1.0` (sin hash del commit) |
| 8.2 | Las 8 pestañas abren | Resumen, Tweaks, Diagnóstico/A-B, CS2, Modo sesión, Red, Seguridad, Mantenimiento |
| 8.3 | Cambiar el perfil a `Advanced` | El contador de tweaks sube, no baja |
| 8.4 | Cambiar el idioma a `en-US` | Todos los textos cambian, ninguno queda en español |
| 8.5 | Seleccionar un tweak → **Vista previa** | Aparece el plan; el texto dice que NO se ha modificado nada |
| 8.6 | **Aplicar** ese tweak | Pide confirmación, crea punto de restauración, aplica y verifica |
| 8.7 | Pestaña Mantenimiento → Revertir | Lista la operación; el botón revierte **esa** operación |
| 8.8 | Navegación con Tab | Se puede llegar a todos los botones sin ratón |
| 8.9 | Lector de pantalla (Narrador) | Lee el nombre de los combos de perfil e idioma |

---

## 9. Cierre

```powershell
& $cli doctor
& $cli status
```

**Esperado**: journal íntegro, sin sesión pendiente, sin tweaks aplicados que no quieras conservar.

Si algo cambió de más, **no lo arregles a mano a ciegas**: eso es justo lo que el proyecto intenta evitar.

---

## Qué NO cubre este guion

- **El instalador**: `installer/FragForge.iss` no se ha compilado nunca (no hay Inno Setup en el entorno donde
  se escribió). Probarlo en una máquina virtual: instalar, desinstalar respondiendo **NO** a borrar
  `%ProgramData%`, comprobar que el journal sigue ahí y que los tweaks **no** se revirtieron solos.
- **La firma**: nunca se ejecutó. Sin certificado, SmartScreen avisará. Ver `docs/firma.md`.
- **Rendimiento**: este guion comprueba corrección, no FPS. El proyecto no promete FPS (R-K).
