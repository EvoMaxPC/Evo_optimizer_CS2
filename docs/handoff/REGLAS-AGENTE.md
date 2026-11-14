# Reglas del agente

Valen para todas las rondas. Este archivo no crea reglas nuevas: recoge las ya vigentes.

## Compilador

- Sin compilador: cada identificador externo (tipo, método, propiedad, `using`, parámetro *required*) se
  audita con grep real y se cita archivo:línea en el informe. Nunca de memoria. Antes de usar una API que
  no aparece en otro lugar del repo, busca un uso existente y cópialo.

## Commits

- Un commit por tema. Solo lo pedido: sin funcionalidad ni refactors no pedidos. No uses ni commitees
  `docs/auditoria` ni scripts propios.

## Informes

- Informe en `docs/handoff/FragForge-CapaN-Informe.md` con: qué cambió, tabla de auditoría de
  identificadores, qué NO se verificó (nada fue compilado ni ejecutado).
- Descartados en `docs/descartados.md` con motivo.

## Reglas R

- R-A no cambiar ctor de `TweakEngine` ni firmas de `Abstractions`.
- R-B nuevo `ActionState` → `JsonDerivedType` + test round-trip.
- R-C parsear por GUID/hex/enum/columnas, nunca por etiquetas localizadas (fixtures es-MX y en-US).
- R-D `IProtectionPolicy.Ensure*` antes de `Apply` + test `PolicyRejected`.
- R-E números como texto invariante.
- R-F contadores de rendimiento vía WMI `Win32_PerfFormattedData_*`.
- R-G nada irreversible en el motor.
- R-H HKCU vía `TargetUserSid` + `RegistryPath.ResolveUserHive`.
- R-I dispositivos por hardware ID / `InterfaceGuid` / `NetCfgInstanceId`, nunca por nombre visible.
- R-J tweaks de boot/driver/dispositivo/red: `RecoveryWriter` + preflight BitLocker de solo lectura +
  revert verificable.
- R-K sin promesas de FPS; `EvidenceLevel` honesto.
- R-L `Unknown` antes que adivinar; escribir solo valores enumerados.
- R-M nunca matar procesos sin confirmación y journal; nunca procesos del sistema, anticheat, Steam, cs2 ni
  `lghub*`.
- R-N checklist estático: 0 warnings en Core (`TreatWarningsAsErrors`), enums JSON por nombre, campos
  persistidos solo aditivos, recursos embebidos `<RootNamespace>.<ruta con puntos>`, sin tipos
  Windows-only en clases `net10.0` (o `[SupportedOSPlatform]` en clase y test), sin asserts de tamaño
  exacto del catálogo, URLs estables.

## Denylist

- Nunca se debilita. Si un tweak choca con ella, se descarta y se documenta.
- Protegidos: servicios `ipfsvc`, `jhi_service`, `cplspcon`, `WMIRegistrationService`; todo bajo
  `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management`; valores `Debugger`,
  `GlobalFlag`, `VerifierDlls`, `VerifierFlags`, `MitigationOptions`, `MitigationAuditOptions`,
  `DisableExceptionChainValidation`.
- El test `RealCatalog_PassesTheRealDenylist` debe seguir pasando.

## Hardware y energía

- Intel DTT gobierna PL1/PL2: no tocar límites de potencia.
- Hardware objetivo fijo: i3-1220P, Intel UHD iGPU únicamente, Windows 11 25H2 es-MX (decidir por
  build ≥ 22000, no por `ProductName`).
- No hardcodear 200 Hz / 5 ms.
- Un tweak nuevo = JSON del catálogo (copia la estructura de uno existente, verifícala con grep) + acción +
  test con dobles + `conditions` + verify + revert + títulos es-MX/en-US + `EvidenceLevel` + riesgo. Antes
  de crear un tweak, busca en el catálogo si ya existe uno equivalente y audítalo en vez de duplicarlo.
- Cada candidato necesita: efecto técnico, fuente, cómo se mide. Si solo es folclore, va a
  `docs/descartados.md`, no al catálogo.
