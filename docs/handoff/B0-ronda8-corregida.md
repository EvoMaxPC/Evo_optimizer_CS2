# B0 — Tabla de los 25 fallos de la ronda 8, corregida

Base: `73dd3c2` (fin de ronda 8, 801/801). Evidencia: `git log`, `git show` y `git log -S` sobre el repo.
**Sin compilador en este entorno: los hashes son ciertos, que el test pase tras cada commit no está verificado por mí.**

## Por qué esta tabla sustituye a la anterior

En el informe de la ronda 9 dije que `CatalogTests:375`, `RealCatalogDenylistTests:101` y
`TierValidatorIntegrationTests:74` los arregló `4084fd1` + `2cd290c`. **Es falso, y lo es de una forma
que se puede demostrar:**

| Commit | Qué hizo de verdad en `TweakCatalogValidator.cs` |
|---|---|
| `4084fd1` | En **`ValidateSingle`** añadió `catch (ProtectedTargetException ex) { errors.Add(...); }`. |
| `2cd290c` | **Borró esa línea** y volvió a `catch (Exception ex) when (ex is not ProtectedTargetException)`. |
| `73dd3c2` | **Restauró** el `catch (ProtectedTargetException)` y añadió el `catch` final. |

Comprobado con `git show 2cd290c` → una línea borrada:
`-                catch (ProtectedTargetException ex) { errors.Add($"{definition.Id}: {ex.Message}"); }`

Por tanto los 6 tests que dependen de que `ProtectedTargetException` **no escape** de `ValidateSingle`
no podían estar verdes entre `2cd290c` y `73dd3c2`. `2cd290c` no arregló esos 3: los **rompió** (el mensaje
de `73dd3c2` lo dice literalmente: «Arregla los 6 fallos que quedaban. Un solo defecto, mío, de 2cd290c»).

Yo atribuí a `2cd290c` el mérito de `73dd3c2` porque leí el rango de commits, no el estado del código en
cada uno. `git log -S` habría mostrado el borrado; no lo usé. Eso es lo que falló.

## Commit FINAL que dejó verde cada test

| # | Test | Commit que lo deja verde | Lado | Causa en una línea |
|---|---|---|---|---|
| 1 | `NetworkProbeTests.Median_HandlesEvenAndOddCounts` (par 1,2,3,4) | `680ac3b` | test | Esperado 2; el comentario de al lado ya decía 3 |
| 2 | `NetworkProbeTests.Median_HandlesEvenAndOddCounts` (par 1..6) | `680ac3b` | test | Esperado 3; el comentario ya decía 4 |
| 3 | `NetworkProbeTests.Percentile_UsesNearestRank` (p99) | `680ac3b` | test | Esperado 100; nearest-rank con n=100 da la 99.ª |
| 4 | `Cs2ResolutionTests` barras finales | `a3fa685` | producto | El doc promete quitar barras finales; `GetFullPath` no las quita |
| 5 | `Cs2ResolutionTests` resolución ×5 | `84ebf47` | test | Sondeaba `ProgramFilesX86\Steam` real: en un PC con Steam salían instalaciones de más |
| 6 | `MemoryChannelDescriptionTests:82` | `f81dc71` | producto | La rama de 0 módulos era la única que no decía «no el entrelazado» |
| 7 | `Cs2InspectorTests.DoesNotModify` | `e802ddc` | test | El doble creaba `cs2.exe` dentro de `InspectAsync` |
| 8 | `Cs2InspectorTests:57` (`mat_vsync`) | `4e13e04` | test | Esperaba `"1"`; el fixture dice `"0"` |
| 9 | `CatalogTests.APolicyRejectedAction_FailsTheWholeCatalogLoad…` | **`73dd3c2`** | producto | `2cd290c` hizo que `ProtectedTargetException` escapara de `ValidateSingle` |
| 10 | `RealCatalogDenylistTests.ValidatorWithTiers_RejectsT0Service…` | **`73dd3c2`** | producto | Ídem: sin retorno de `ValidateSingle`, no se llega a `ValidatePolicy` |
| 11 | `TierValidatorIntegrationTests.CoreService_FailsValidation` | **`73dd3c2`** | producto | Ídem |
| 12 | `ExternalCatalogResilienceTests.ThePolicyRejection_ExplainsWhy` | **`73dd3c2`** | producto | Ídem |
| 13 | `ExternalCatalogResilienceTests.AMaliciousExternalCatalog_…` | **`73dd3c2`** | producto | Ídem |
| 14 | `ExternalCatalogResilienceTests.AnEntryThatDependsOnARejectedOne_…` | **`73dd3c2`** | producto | Ídem |
| 15 | `ExternalCatalogResilienceTests` (id embebido reemplazado) | `f07ba8f` | producto | La primera definición no ganaba: un externo reemplazaba un embebido |
| 16 | `ExternalCatalogResilienceTests` (duplicado x2) | `2cd290c` | producto | `ValidateCatalog` repetía a mano las reglas de `ValidateSingle` |
| 17 | `IntegrityTests:113` `StillWorks` | `9ae0a3b` | test | El fake guardaba `_applied` en la instancia y el motor recrea la acción |
| 18 | `IntegrityTests:93` `Rejects…` | `badb50e` | test | Buscaba `"kind":"Fake"` y el serializador escribe `"kind": "Fake"` |
| 19 | `GameSessionServiceTests:88` | `9ae0a3b` | test | Mismo fake, mismo drift |
| 20 | `GameSessionServiceTests:107` | `9ae0a3b` | test | Ídem |
| 21 | `GameSessionServiceTests:163` | `9ae0a3b` | test | Ídem |
| 22 | `MainViewModelTests:149` | `104c51e` | test | Asignaba `"en-US"` que ya era el valor actual: no notifica |
| 23 | `SecurityPostureTests` (driver) | `f8284e3` | producto | 3 lecturas de registro sin `try` tumbaban la postura entera |
| 24 | `CatalogTests` (xUnit2000, argumentos al revés) | `9d046ab` | test | `Assert.Equal(actual, esperado)` invertido |
| 25 | `TweakCatalogValidator` doc-comment huérfano | `725237d` | docs | El `<summary>` se quedó sin miembro al reordenar `f07ba8f` |

> Las filas 1-8, 15-25 coinciden con lo que ya dije y no cambian. **Las filas 9-14 son las que corrijo: el
> commit final es `73dd3c2`, no `4084fd1`/`2cd290c`.** Y la fila 25 (`725237d`) no es un test sino la
> recolocación de un doc-comment: estaba fuera de la lista original y la añado para que cuadre con los
> 16 commits de la ronda 8.

## Conteo

16 commits en la ronda 8 (`4084fd1` … `73dd3c2`), 25 fallos. La ronda se cerró con 801/801 según el
volcado del usuario; ese dato **no lo he verificado yo** (no hay compilador aquí).
