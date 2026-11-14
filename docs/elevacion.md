# Elevación: `requireAdministrator` (Capa 11.4)

**Decisión: la app y el CLI se manifiestan como `requireAdministrator`.** No hay modo `asInvoker` con
elevación bajo demanda. Este documento justifica la decisión con lo que el código realmente hace.

---

## 1. Lo que ya estaba

Los dos manifiestos ya pedían elevación antes de esta ronda:

- `src/FragForge.App/app.manifest`: `requestedExecutionLevel level="requireAdministrator"`.
- `src/FragForge.Cli/app.manifest`: `requestedExecutionLevel level="requireAdministrator"`.

O sea que la decisión no es nueva: lo que faltaba era **justificarla por escrito** y comprobar que no es
una comodidad sino una necesidad.

---

## 2. Qué necesita admin de verdad

Todo verificado con archivo:línea, no de memoria.

| Operación | Dónde | Por qué exige elevación |
|---|---|---|
| Escribir `HKLM` | 7 valores en el catálogo (`grep -c HKLM src/FragForge.Tweaks/Catalog/`) | `HKLM` no es escribible por un usuario estándar |
| Servicios (`Service`) | `src/FragForge.Actions/Services/ServiceAction.cs` | `ChangeServiceConfig` requiere `SERVICE_CHANGE_CONFIG` |
| Tareas programadas | `src/FragForge.Actions/Tasks/ScheduledTaskAction.cs` | Las tareas bajo `\Microsoft\Windows\` son de SYSTEM |
| BCD | `src/FragForge.Actions/Bcd/BcdeditAction.cs` | `bcdedit` sin elevación devuelve "acceso denegado" |
| Propiedades de adaptador | `src/FragForge.Actions/Network/NetAdapterPropertyAction.cs` | `Set-NetAdapterAdvancedProperty` reinicia el dispositivo |
| Punto de restauración | `SystemRestoreService` (`srclient.dll`) | Requiere admin para crearlo |
| TPM | `src/FragForge.Actions/Platform/HardwareProfiler.cs:28` (`root\CIMV2\Security\MicrosoftTpm`) | El espacio de nombres Security no se abre sin privilegios |
| PresentMon | `src/FragForge.Actions/Platform/ProcessRunner.cs:13-16` | Ver §3 |

El preflight ya lo comprueba y lo bloquea: `src/FragForge.Core/Preflight/PreflightService.cs:29` marca
`admin` como `Blocking` si `IsAdministrator` es false, y `--force` no lo omite.

---

## 3. Las dos alternativas y por qué se descartaron

### `asInvoker` + elevar solo al aplicar

La idea: la app arranca sin UAC, el usuario navega y lee, y al pulsar Aplicar se relanza elevada.

**Problema 1 — la lectura tampoco funciona sin admin.** La postura de seguridad lee
`Win32_Tpm` en `root\CIMV2\Security\MicrosoftTpm` (`HardwareProfiler.cs:28`). Sin elevación esa consulta
falla y el TPM sale `Unknown`… siempre. La Capa 10 se diseñó justamente para no adivinar, así que una app
"AsInvoker" mostraría una postura permanentemente incompleta en el equipo de destino.

**Problema 2 — el proceso de aplicación tendría que ser otro.** Aplicar no es una llamada: es un motor con
snapshot, journal y revert, con estado en memoria (`TweakEngine`). Relanzar "solo la parte que escribe"
obligaría a serializar el plan, pasarlo a un segundo proceso y confiar en que ese proceso haga exactamente
lo mismo. Es una superficie de fallo nueva a cambio de evitar un UAC.

**Problema 3 — el desajuste de rutas.** El proceso elevado no comparte el perfil del usuario sin elevar;
`%ProgramData%\FragForge` sí, pero `HKCU` no. Y `HKCU` es donde viven varios tweaks (`latency.*`,
`cs2.*`) que se resuelven con `TargetUserSid` (R-H). Un segundo proceso tendría que reconstruir ese contexto.

### `highestAvailable`

Es un punto intermedio que solo funciona bien en cuentas de administrador, y en una cuenta estándar se
comporta como `asInvoker` (falla igual). No resuelve nada y confunde: el usuario no sabe en qué modo está.

---

## 4. Coste aceptado

- **UAC al arrancar.** Es el precio de que la lectura sea completa y de que la aplicación sea un solo
  proceso.
- **La app no es usable por un usuario sin permisos de administrador**, salvo el CLI en modo lectura.
  Es coherente con lo que es: una herramienta que escribe en `HKLM` y en servicios.

## 5. Lo que NO cambia

- El preflight **sigue comprobando** `IsAdministrator`. Si alguien ejecuta el binario en un contexto donde el
  manifiesto no se aplicó (un servicio, un contenedor, una tarea sin privilegios), el motor no asume nada:
  bloquea con el motivo.
- Los tests y el `dotnet test` corren **sin** elevación: no aplican tweaks, usan dobles.
- La lectura de hardware que no requiere elevación (CPU, RAM, GPU, discos) funciona igual en ambos modos.

## 6. Cómo revertir esta decisión

Si en el futuro se quisiera `asInvoker`: hay que (a) aceptar que la postura de seguridad saldrá incompleta,
(b) partir el motor en dos procesos con un contrato serializado, y (c) resolver `TargetUserSid` en el
proceso elevado. No es un cambio de manifiesto; es un rediseño. Por eso se decide una vez y se documenta.
