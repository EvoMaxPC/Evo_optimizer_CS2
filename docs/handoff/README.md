# Paquete de entrega FragForge — Capa 0

Documentación para entrega/revisión por otra sesión de IA (Claude) o por una persona.

| Archivo | Para qué |
|---|---|
| **`00-prompt-claude.md`** | **Empieza aquí.** Briefing compacto (~110 líneas) para pegar al inicio de una sesión nueva: hardware fijo, invariantes, decisiones ya tomadas, estado de verificación, pendientes y comandos. |
| **`FragForge-Capa0-Informe.md`** | Informe completo en un solo archivo: briefing + arquitectura + árbol + **los 122 archivos con su código íntegro** + comandos + supuestos/limitaciones + contrato Capa 1. |
| `01-arquitectura.md` | Decisiones y flujo transaccional (fragmento). |
| `02-arbol.md` | Árbol de carpetas y métricas por proyecto. |
| `03a-core.md` … `03i-catalogo.md` | Inventario de código por proyecto (fragmentos del informe). |
| `04-comandos.md` | Restore/build/test/publish y primer uso en el PN64. |
| `05-supuestos.md` | Qué está verificado, qué no, riesgos residuales. |
| `06-capa1.md` | Contrato de la Capa 1: hooks, PresentMon, promoción de evidencia. |

## Verificación reproducible

```bash
dotnet build FragForge.sln -c Release     # 0 warnings / 0 errores
dotnet test tests/FragForge.Tests/FragForge.Tests.csproj -c Release   # 103 pasan
```

## Orden de lectura recomendado para Claude

1. `00-prompt-claude.md` (contexto e invariantes).
2. Sección **1** del informe (arquitectura y reglas del motor).
3. `03a-core.md` → `Engine/TweakEngine.cs`, `Persistence/FileJournal.cs`, `Persistence/ProtectionPolicy.cs`.
4. `03b-actions.md` → `Registry/RegistryAction.cs`, `Power/PowerPlanAction.cs`, `Bcd/BcdeditAction.cs`.
5. `03c-tweaks.md` → catálogo + esquema + validador.
6. `05-supuestos.md` y `06-capa1.md` antes de proponer cambios.
