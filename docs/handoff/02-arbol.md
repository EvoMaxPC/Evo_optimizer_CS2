## 2. Árbol de carpetas

```text
Evo_optimizer_CS2/
├─ FragForge.sln                      # 7 proyectos (GUIDs …0001–0007)
├─ Directory.Build.props              # net10.0, nullable, analyzers, TreatWarningsAsErrors solo Core
├─ Directory.Packages.props           # versiones centralizadas
├─ global.json                        # SDK 10.0.400 (rollForward latestFeature)
├─ NuGet.config / NuGet.offline.config
├─ README.md                          # arquitectura, build/publish, autoría de tweaks
├─ docs/
│  ├─ manual-revert.md                # reversión manual de emergencia por $actionState
│  └─ handoff/                        # este informe (generado)
├─ .github/workflows/ci.yml           # jobs linux (Core+Tests) y windows (full + publish)
├─ src/
│  ├─ FragForge.Core/
│  │  ├─ Abstractions/
│  │  ├─ Conditions/
│  │  ├─ Engine/
│  │  ├─ Localization/
│  │  ├─ Logging/
│  │  ├─ Models/
│  │  ├─ Persistence/
│  │  ├─ Policy/
│  │  ├─ Preflight/
│  ├─ FragForge.Actions/
│  │  ├─ Bcd/
│  │  ├─ DependencyInjection/
│  │  ├─ Diagnostics/
│  │  ├─ Files/
│  │  ├─ Network/
│  │  ├─ Platform/
│  │  ├─ Power/
│  │  ├─ Registry/
│  │  ├─ Scripting/
│  │  ├─ Services/
│  │  ├─ Tasks/
│  ├─ FragForge.Tweaks/
│  │  ├─ Catalog/
│  │  ├─ Localization/
│  │  ├─ Schema/
│  │  ├─ Validation/
│  ├─ FragForge.Composition/
│  ├─ FragForge.Cli/
│  ├─ FragForge.App/
│  │  ├─ Services/
│  │  ├─ ViewModels/
└─ tests/FragForge.Tests/             # xUnit 2.9.3: 12 archivos, 103 tests
   ├─ Actions/
   ├─ Catalog/
   ├─ Core/
   ├─ Fakes/
   ├─ Integration/
```

### Recuento por proyecto (solo código)

| Proyecto | Archivos .cs | Líneas |
|---|---:|---:|
| `src/FragForge.Core` | 31 | 2113 |
| `src/FragForge.Actions` | 28 | 1809 |
| `src/FragForge.Tweaks` | 4 | 211 |
| `src/FragForge.Composition` | 4 | 207 |
| `src/FragForge.Cli` | 3 | 275 |
| `src/FragForge.App` | 5 | 332 |
| `tests/FragForge.Tests` | 12 | 1668 |

Catálogo: 10 tweaks JSON + esquema + 2 archivos de localización. Total de archivos versionados: 122.

### Archivos más grandes (complejidad concentrada)

```text
src/FragForge.Core/Engine/TweakEngine.cs                                576 líneas
tests/FragForge.Tests/Actions/ActionTests.cs                            429 líneas
tests/FragForge.Tests/Core/EngineTests.cs                               303 líneas
src/FragForge.Actions/Platform/HardwareProfiler.cs                      210 líneas
src/FragForge.App/ViewModels/MainViewModel.cs                           207 líneas
src/FragForge.Actions/Power/PowerPlanAction.cs                          176 líneas
src/FragForge.Core/Conditions/ConditionFactory.cs                       175 líneas
src/FragForge.Core/Models/HardwareProfile.cs                            171 líneas
tests/FragForge.Tests/Core/RegistryActionTests.cs                       170 líneas
tests/FragForge.Tests/Catalog/CatalogTests.cs                           163 líneas
src/FragForge.Cli/Program.cs                                            160 líneas
src/FragForge.Actions/Registry/RegistryAction.cs                        144 líneas
src/FragForge.Core/Models/ActionState.cs                                124 líneas
src/FragForge.Core/Persistence/ProtectionPolicy.cs                      122 líneas
tests/FragForge.Tests/Fakes/TestSupport.cs                              116 líneas
```

---
