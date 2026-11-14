# Parte 6 — Capa 10: Seguridad

**Commits** `2e24434` (postura) · `d53c80e` (resiliencia) · `500b5f9` (opt-in + integridad) · `07f7b9d` (informe)
**Rango** `0bd62cc..500b5f9` — 3 commits de código, 27 ficheros, +1853 / −7, **+37 tests** (614 → 651)
**Informe de detalle** `../FragForge-Capa10-Informe.md`

---

## Qué pedía

Postura de seguridad de solo lectura, aviso de compatibilidad por tweak, un tweak opt-in de alto riesgo,
resiliencia del catálogo, integridad del journal y privilegios.

## 10.1 Postura de seguridad (solo lectura)

`SecurityPosture` + `ISecurityPostureService` + `WindowsSecurityPostureService`, expuesto por
`fragforge security status` y por un panel en la App.

| Dato | Fuente | Cómo se parsea |
|---|---|---|
| VBS | `Win32_DeviceGuard.VirtualizationBasedSecurityStatus` | 0 → Disabled, 2 → Enabled, resto → **Unknown** |
| HVCI | `SecurityServicesRunning` contiene 2; si no, registro | **Por valor de enum, no por texto** |
| Secure Boot | `UEFISecureBootEnabled`; si falta, `Confirm-SecureBootUEFI` | `true`/`false` literales |
| TPM | `Win32_Tpm` (`Security\MicrosoftTpm`) | `IsEnabled_InitialValue` |
| BitLocker | `manage-bde -status` (reutiliza el preflight de Capa 5) | Solo estado |
| Blocklist de drivers | `CI\Config\VulnerableDriverBlocklistEnable` | DWORD 1/0, ausente → Unknown |
| Driver de terceros (PawnIO) | `Services\PawnIO\ImagePath` | `bool?`: **`null` ≠ `false`** |

**`Unknown` es de primera clase.** No se lee → `Unknown`, nunca `Disabled`. Con test, caso por caso: Secure Boot en
BIOS heredado, valor de registro ausente, WMI que lanza, driver no comprobable.

**BitLocker**: solo `EncryptedProtected` → Enabled y `NotEncrypted` → Disabled. Cifrado con protección
**suspendida** → `Unknown`: no está protegido, pero tampoco en claro.

**PawnIO se reporta como DATO.** No se afirma que ningún anticheat lo bloquee ni lo permita.

## 10.2 Aviso de compatibilidad por tweak

`TweakDefinition.anticheatNote` + `AnticheatNote` en Core. El aviso **remite al proveedor** ("revisa los
requisitos del proveedor") en vez de afirmar nada sobre VAC o FACEIT, porque no hay fuente que lo respalde.

## 10.3 El tweak estrella: **DESCARTADO**, con tres motivos

HVCI off (integridad de memoria). Se descartó **por denylist**, no por falta de tiempo. El informe recomienda
explícitamente **que `DeviceGuard` siga en la denylist**.

Es la decisión más importante de la capa: el proyecto prefiere no tener un tweak antes que debilitar la política
de seguridad que protege al usuario de sí mismo.

## 10.4 Resiliencia del catálogo externo

Un catálogo externo malo **ya no puede tumbar la app**. Los embebidos se validan aparte de forma estricta antes;
si esa validación falla, la app no arranca (correcto: es un bug de entrega). Pero un archivo del usuario, mal
formado, se reporta y se ignora.

## 10.5 Integridad — el hallazgo técnico de la capa

| Pieza | Estado antes | Qué se hizo |
|---|---|---|
| Journal | Ya detectaba manipulación | Se cita el test existente y se añade el del límite |
| Snapshots (`DefinitionSha256`) | **Se escribía y nadie lo comprobaba** | **Cerrado** |
| RecoveryWriter | Ya verificaba SHA-256 por archivo | Se cita el test |
| Allowlist de herramientas | Ya verificaba hash antes de ejecutar | Se cita el test |

**El hueco**: `TweakEngine` guardaba `DefinitionSha256` y no había **ninguna lectura** de ese campo fuera de las
fixtures. El revert saca los parámetros de la definición guardada en el snapshot, así que editar `snapshot.json` a
mano permitía revertir a lo que dijera el archivo. Ahora `VerifySnapshotDefinitions` lo comprueba **antes de tocar
nada** y el revert falla con `snapshot_definition_tampered`.

Se comprobó que la serialización usada al verificar es **idéntica** a la usada al calcular (`JsonDefaults.Create(false)`
en ambos sitios); si no, el revert fallaría siempre. Un snapshot **sin** hash (histórico) se acepta: romper la
recuperación de datos antiguos sería lo contrario de lo que hace falta.

**Y una corrección de vocabulario**: el journal **no es "firmado"**. Es *tamper-evident* (cadena SHA-256), no
autenticado: quien edite una línea y recalcule la cadena obtiene un journal "válido". En la entrega anterior se
llamó "firmado"; **esa palabra era incorrecta**. Hay un test que demuestra la forja
(`TheJournal_IsTamperEvident_ButNotAuthenticated`) en vez de esconder el límite.

## 10.6 Privilegios

Un test por ruta: leer (sí, sin admin), aplicar (no, preflight Blocking + remediación), dry-run (sí, no escribe),
plataforma no soportada (su propio Blocking), y aplicar con admin (no bloqueado). El bloqueo es del **preflight**,
así que el usuario ve el motivo **antes** de que se toque nada. `--force` **no** omite el preflight de admin.

## 10.7 `docs/seguridad.md`

208 líneas: principio literal, lista cerrada de lo que la app NO hace, **seis amenazas** con mitigación o límite
explícito — incluida la que **no** se puede cerrar: un atacante con escritura en disco.

## Qué no se verificó

- **Nada compilado ni ejecutado.** Los conteos de tests son de lectura del código.
- **Valores reales de DeviceGuard** en el equipo: no se ejecutó la consulta.
- **Ningún anticheat real fue probado.** No se afirma nada sobre VAC ni FACEIT.
- **`Confirm-SecureBootUEFI`** en este equipo: no se ejecutó.
- **PawnIO**: se afirma que está instalado según el comentario de `SensorSampler`, no por una lectura ejecutada.
- **`manage-bde`**: no se ejecutó.
- **`RecoveryWriter` en el flujo real**: el motor **todavía no lo invoca** al aplicar. Es un hueco preexistente que
  esta ronda documenta y **no** cierra (cerrarlo exige cambiar `TweakEngine`, fuera del alcance de 10.5).
- **Firma del journal**: no existe y no se simuló.

## Decisiones que conviene revisar

1. **10.3 descartado por denylist.** Si la intención era tener ese tweak, hay que decidir antes si `DeviceGuard`
   sale de la denylist. El informe recomienda **que no**.
2. **`WmiValues` pasó de `internal` a `public`** para reutilizar el parseo en vez de duplicarlo. Amplía superficie
   pública de `FragForge.Actions`; reversible si molesta.
3. El catálogo externo ya no puede provocar un fallo de arranque, ni siquiera por un error del propio binario, porque
   los embebidos se validan aparte de forma estricta.
