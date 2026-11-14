# Changelog

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/). El proyecto usa
[Versionado Semántico](https://semver.org/lang/es/).

> **Nota honesta**: el proyecto no se ha ejecutado nunca en un equipo Windows desde este repositorio (no había
> compilador en el entorno de desarrollo). Las entradas de abajo describen lo que el código hace, no lo que se ha
> visto funcionar. Ver `docs/handoff/`.

## [0.1.0] — sin fecha de publicación

Primera versión funcionalmente completa: capas 0 a 11.

### Añadido

- **Motor transaccional**: preflight → condiciones/conflictos → perfil/riesgo → idempotencia → snapshot →
  apply → verify → rollback inverso por tweak → journal.
- **Snapshot y journal**: captura exacta del estado previo (tipo, datos, artefactos `.reg`/`.pow`/`.bcd`),
  journal *append-only* con cadena SHA-256, punto de restauración opcional.
- **Catálogo declarativo JSON** con esquema propio y validación semántica (ids, ciclos, conflictos,
  dependencias, denylist). Textos es-MX y en-US en todos los tweaks.
- **Denylist central** embebida, fusionable con una política de usuario; nunca se debilita.
- **CLI `fragforge`**: `scan`, `list`, `apply` (con `--dry-run`), `revert`, `status`, `snapshot`,
  `export-report`, `doctor`, `session`, `net`, `cs2`, `security`, `maintenance`, `probe`, `mouse-test`,
  `tools verify`.
- **Medición A/B** con PresentMon: ganchos `IExperimentHook`, líneas base `InspectOnly`, informes JSON/HTML.
- **Modo sesión**: aplica un perfil al empezar a jugar y lo revierte al terminar; recuperación tras cierre
  forzado o corte (el marcador se conserva hasta que la reversión se completa de verdad).
- **Postura de seguridad** de solo lectura (TPM, Secure Boot, VBS/HVCI, Defender) que informa sin tocar nada.
- **App WPF** con pestañas por capa (Resumen, Tweaks, Diagnóstico/A-B, CS2, Modo sesión, Red, Seguridad,
  Mantenimiento), vista previa antes/después, y reversión por operación concreta.
- **`FragForge.UI.Core`**: ViewModels en un proyecto `net10.0` sin WPF, con tests. Antes la UI no tenía ni uno.
- **Instalador Inno Setup** (`installer/FragForge.iss`) y scripts `publish.ps1` / `sign.ps1`.
- **CI**: ubuntu (lógica, con `-warnaserror`) + windows-latest (solución completa); el artefacto de *publish*
  solo se genera en tags.
- **Documentación**: `docs/elevacion.md`, `docs/firma.md`, `docs/seguridad.md`, `docs/descartados.md`,
  `docs/manual-revert.md` y `docs/handoff/e2e-manual.md`.

### Seguridad

- Desinstalar **no** revierte tweaks y **no** borra `%ProgramData%\FragForge` sin confirmación explícita.
- Nunca se commitean certificados, contraseñas ni tokens: la firma se configura por parámetro o variable de
  entorno.
- Sin telemetría, sin auto-update, todo offline.
- No se mata ningún proceso sin confirmación y registro; nunca Steam, CS2 ni anticheat.

### Limitaciones conocidas

- **Nada compilado ni ejecutado** en el entorno donde se escribió: build, instalador, firma y UI sin verificar.
- El desinstalador de Inno (`unins000.exe`) se firma aparte, después de compilar el `.iss`.
- Un certificado de firma nuevo **no** quita el aviso de SmartScreen de inmediato (falta reputación).
- Los tweaks declaran su `evidence`; el proyecto no promete FPS.
