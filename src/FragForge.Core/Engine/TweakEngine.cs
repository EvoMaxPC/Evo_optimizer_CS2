using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Core.Preflight;
using Microsoft.Extensions.Logging;

namespace FragForge.Core.Engine;

public sealed class TweakEngine(
    ITweakCatalog catalog,
    IHardwareProfiler profiler,
    ITweakActionFactory actionFactory,
    ITweakConditionFactory conditionFactory,
    ISnapshotStore snapshots,
    IJournal journal,
    ISystemRestoreService restore,
    IPreflightService preflight,
    IExecutionGate gate,
    IEnumerable<IExperimentHook> hooks,
    ILogger<TweakEngine> logger) : ITweakEngine
{
    private readonly IReadOnlyList<IExperimentHook> _hooks = hooks.OrderBy(h => h.Order).ToArray();

    public async Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        progress?.Report(new EngineProgress("hardware", null, 0, "Detectando hardware y estado del sistema."));
        var hardware = await profiler.ProfileAsync(cancellationToken, progress);
        var ownership = await ReadOwnershipAsync(cancellationToken);
        var statuses = new List<TweakStatus>();
        for (var i = 0; i < catalog.Definitions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = catalog.Definitions[i];
            progress?.Report(new EngineProgress("scan", definition.Id, (double)(i + 1) / catalog.Definitions.Count, definition.Title.EsMx));
            statuses.Add(await EvaluateAsync(definition, hardware, ownership, cancellationToken));
        }
        return new ScanReport(hardware, statuses, DateTimeOffset.UtcNow);
    }

    public async Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default)
    {
        var definition = catalog.Get(id);
        var hardware = await profiler.ProfileAsync(cancellationToken);
        var ownership = await ReadOwnershipAsync(cancellationToken);
        return await EvaluateAsync(definition, hardware, ownership, cancellationToken);
    }

    public async Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var hardware = await profiler.ProfileAsync(cancellationToken, progress);
        var options = new ApplyOptions { CreateRestorePoint = createRestorePoint, Profile = TweakProfile.Advanced };
        var preflightReport = await preflight.CheckAsync(options, hardware, cancellationToken);
        if (!preflightReport.CanApply) throw new PreflightFailedException("El preflight impide crear el snapshot con punto de restauración.", preflightReport);
        var plans = new List<ActionPlan>();
        foreach (var definition in catalog.Definitions.Where(d => !d.ReadOnly))
        {
            var plan = await TryBuildPlanAsync(definition, hardware, null, operationId, cancellationToken);
            if (plan is not null) plans.Add(plan);
        }
        var snapshot = await CaptureAndPersistAsync(operationId, label, hardware, plans, createRestorePoint, cancellationToken, progress);
        logger.LogInformation("Snapshot {SnapshotId} creado con {TweakCount} tweaks capturados.", snapshot.Id, snapshot.Tweaks.Count);
        return snapshot;
    }

    public async Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var results = new List<TweakApplyResult>();
        var errors = new List<string>();
        var warnings = new List<string>();
        string? snapshotId = null;
        long? restoreSequence = null;
        var cancelled = false;
        var critical = false;
        var failed = false;
        var preflightBlocked = false;
        await AppendAsync(operationId, JournalEventType.OperationStarted, null, null, options.DryRun ? "Apply dry-run" : "Apply", cancellationToken);
        try
        {
            var ordered = DependencyResolver.Resolve(ids, catalog);
            var hardware = ApplyOverrides(await profiler.ProfileAsync(cancellationToken, progress), options.TargetUserSid, options.Cs2ExecutableOverride);
            var plans = new List<ActionPlan>();
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (definition.DependsOn.Any(skipped.Contains))
                {
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, TweakState.NotApplicable, "Una dependencia no está aplicada o no es aplicable."));
                    continue;
                }
                var condition = EvaluateConditions(definition, hardware);
                if (!condition.IsMet)
                {
                    // Mismo criterio que en el scan: condición desconocida != condición incumplida.
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, condition.IsKnown ? TweakState.NotApplicable : TweakState.Unknown, condition.Reason));
                    continue;
                }
                if (!options.DryRun) ProfileSelector.EnsureRiskAccepted(definition, options);
                var plan = await TryBuildPlanAsync(definition, hardware, options, operationId, cancellationToken);
                if (plan is null)
                {
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, TweakState.NotApplicable, "El destino de la acción no está disponible en este equipo."));
                    continue;
                }
                plans.Add(plan);
            }
            if (options.DryRun)
            {
                foreach (var plan in plans)
                {
                    var changes = new List<DryRunChange>();
                    for (var i = 0; i < plan.Actions.Count; i++) changes.Add(new(plan.Definition.Id, i, plan.Actions[i].Kind, await plan.Actions[i].DryRunAsync(plan.Context, cancellationToken)));
                    var current = await VerifyPlanAsync(plan, cancellationToken);
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = current.State, Message = "Simulación sin cambios. " + current.Reason, PlannedChanges = changes });
                }
                // F2: la vista previa NO lanza, pero TAMPOCO puede ocultar que el apply va a fallar. Se
                // consulta el preflight en modo informativo y los Blocking se vuelcan como advertencias:
                // el usuario ve el motivo ANTES de intentar aplicar. No se lanza PreflightFailedException
                // aquí a propósito: un dry-run no cambia nada, así que no hay nada que impedir.
                var previewPreflight = await preflight.CheckAsync(
                    options with { SelectedIds = plans.Select(p => p.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) },
                    hardware,
                    cancellationToken);
                // Se aceptan Blocking Y Warning: el preflight degrada a Warning lo que en un apply real
                // bloquea (preflight.CheckAsync, rama DryRun), porque aqui no hay nada que impedir. Filtrar
                // solo por Blocking hacia que el aviso no saliera NUNCA (fallo C3 de la ronda de fallos 1).
                warnings.AddRange(previewPreflight.Checks
                    .Where(c => c.Severity is PreflightSeverity.Blocking or PreflightSeverity.Warning)
                    .Select(c => "NO SE PODRÁ APLICAR — " + c.Message));
                return await FinishAsync(new BatchReport { OperationId = operationId, DryRun = true, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            if (plans.Count == 0)
            {
                warnings.Add("No hay tweaks aplicables; no se creó snapshot ni punto de restauración.");
                return await FinishAsync(new BatchReport { OperationId = operationId, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            // Capa 5.3 (R-J): si algo de lo que se va a aplicar reinicia el enlace, el aviso va ANTES de tocar
            // nada, no después. Es lo que la UI y el CLI muestran al usuario: la conexión se corta unos segundos.
            var linkRestarts = plans.Where(p => p.Definition.RestartsNetworkLink && !p.Definition.ReadOnly).Select(p => p.Definition.Id).ToArray();
            if (linkRestarts.Length > 0)
                warnings.Add("AVISO: " + string.Join(", ", linkRestarts) + " reinicia el adaptador de red; la conexión se cortará unos segundos. No lo apliques durante una partida.");
            var needsRestore = options.CreateRestorePoint && plans.Any(p => !p.Definition.ReadOnly);
            // R-J: el preflight necesita saber QUE se aplica, no solo que hay catalogo. Se pasan los planes ya
            // resueltos (con dependencias y descartes por condicion hechos), no los ids que llego el llamador.
            var effectiveOptions = options with
            {
                CreateRestorePoint = needsRestore,
                SelectedIds = plans.Select(p => p.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            };
            var preflightReport = await preflight.CheckAsync(effectiveOptions, hardware, cancellationToken);
            warnings.AddRange(preflightReport.Checks.Where(c => c.Severity == PreflightSeverity.Warning).Select(c => c.Message));
            if (!preflightReport.CanApply) throw new PreflightFailedException("El preflight bloqueó la aplicación: " + string.Join(" ", preflightReport.Checks.Where(c => c.Severity == PreflightSeverity.Blocking).Select(c => c.Message + (c.Id == PreflightService.RestoreUnavailableId && c.Remediation is not null ? " " + c.Remediation : ""))), preflightReport);
            var acceptedWithoutRestore = options.AcceptWithoutSystemRestore && preflightReport.Checks.Any(c => c.Id == PreflightService.RestoreUnavailableId && c.Severity == PreflightSeverity.Warning);
            if (acceptedWithoutRestore) needsRestore = false;
            var ownership = await ReadOwnershipAsync(cancellationToken);
            var applicable = new List<ActionPlan>();
            foreach (var plan in plans)
            {
                var state = await VerifyPlanAsync(plan, cancellationToken);
                // El objeto de la accion no existe en este equipo: NO APLICABLE, como en el scan.
                // No es un error y no se aplica. Antes esta excepcion escapaba hasta el catch general
                // de ApplyAsync y tumbaba el lote entero (bug medido: la tarea Appraiser, eliminada por
                // Atlas Playbook).
                if (state.State == TweakState.NotApplicable)
                {
                    skipped.Add(plan.Definition.Id);
                    results.Add(Skipped(plan.Definition.Id, TweakState.NotApplicable, state.Reason));
                    continue;
                }
                if (state.State is TweakState.Applied or TweakState.AppliedExternally && !options.Force)
                {
                    var owned = ownership.Contains(plan.Definition.Id);
                    results.Add(Skipped(plan.Definition.Id, owned ? TweakState.Applied : TweakState.AppliedExternally, owned ? "Ya está aplicado por FragForge; idempotencia." : "Ya coincide con el objetivo, posiblemente por Atlas/usuario. No se sobrescribe sin Force."));
                    continue;
                }
                applicable.Add(plan);
            }
            if (applicable.Count == 0)
            {
                // El aviso debe decir la verdad: si lo que paso es que el objeto no existe, no es
                // "ya coinciden".
                warnings.Add(results.Any(r => r.State == TweakState.NotApplicable)
                    ? "Nada que aplicar: algún tweak no aplica en este equipo (su objetivo no existe)."
                    : "Todos los tweaks seleccionados ya coinciden; no hubo cambios.");
                return await FinishAsync(new BatchReport { OperationId = operationId, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            if (needsRestore && options.EnableSystemProtection)
            {
                var protection = await restore.GetProtectionStateAsync(cancellationToken);
                if (protection == FeatureState.Disabled)
                {
                    await restore.EnableProtectionAsync(cancellationToken);
                    protection = await restore.GetProtectionStateAsync(cancellationToken);
                    if (protection != FeatureState.Enabled) throw new PreflightFailedException("No se pudo confirmar la protección del sistema tras habilitarla; no se aplicará ningún cambio.", preflightReport);
                    warnings.Add("Protección del sistema habilitada explícitamente para esta operación.");
                }
                else if (protection != FeatureState.Enabled) throw new PreflightFailedException("La protección del sistema no pudo confirmarse; usa --no-restore-point si aceptas solo snapshot y journal.", preflightReport);
            }
            // Si el objetivo desaparecio ENTRE la sonda de TryBuildPlanAsync y esta captura, el tweak
            // queda fuera del snapshot: se reporta como no aplicable y no se aplica.
            var vanished = new List<string>();
            var snapshot = await CaptureAndPersistAsync(operationId, "apply: " + string.Join(", ", applicable.Select(p => p.Definition.Id)), hardware, applicable, needsRestore, cancellationToken, progress, vanished);
            snapshotId = snapshot.Id;
            restoreSequence = snapshot.RestorePointSequence;
            foreach (var id in vanished)
            {
                results.Add(Skipped(id, TweakState.NotApplicable, "El objetivo de la acción ya no existe en este equipo."));
            }
            applicable.RemoveAll(p => vanished.Contains(p.Definition.Id, StringComparer.OrdinalIgnoreCase));
            foreach (var plan in applicable)
            {
                plan.States.Clear();
                var captured = snapshot.Tweaks.FirstOrDefault(t => t.TweakId == plan.Definition.Id);
                if (captured is null) continue;
                foreach (var action in captured.Actions)
                    plan.States.Add(action.State);
            }
            foreach (var plan in applicable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunHooksAsync(ExperimentPhase.BeforeApply, operationId, snapshotId, plan, hardware, snapshots.GetArtifactDirectory(snapshotId), cancellationToken, warnings, failOnError: true);
            }
            foreach (var plan in applicable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempted = new List<(ITweakAction Action, ActionState Before)>();
                var planWarnings = new List<string>();
                try
                {
                    await AppendAsync(operationId, JournalEventType.TweakApplyStarted, plan.Definition.Id, snapshotId, null, cancellationToken);
                    for (var i = 0; i < plan.Actions.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var action = plan.Actions[i];
                        var before = plan.States[i];
                        attempted.Add((action, before));
                        progress?.Report(new EngineProgress("apply", plan.Definition.Id, (double)i / plan.Actions.Count, $"Aplicando {action.Kind}."));
                        await action.ApplyAsync(before, plan.Context, cancellationToken);
                        var verification = await action.VerifyAsync(before, plan.Context, cancellationToken);
                        if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException($"{action.Kind}: {verification.Message}");
                        action.ReportReady(plan.Context, "verify", 1, verification.Message);
                    }
                    await AppendAsync(operationId, JournalEventType.TweakApplied, plan.Definition.Id, snapshotId, null, cancellationToken);
                    await RunHooksAsync(ExperimentPhase.AfterApply, operationId, snapshotId, plan, hardware, snapshots.GetArtifactDirectory(snapshotId), cancellationToken, planWarnings, failOnError: false);
                    if (plan.Definition.RequiresReboot) planWarnings.Add("Requiere reinicio para que todos los efectos sean activos.");
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = plan.Definition.ReadOnly ? TweakState.Inspected : TweakState.Applied, Message = plan.Definition.ReadOnly ? "Inspección completada sin cambios." : "Aplicado y verificado.", Changed = !plan.Definition.ReadOnly, Warnings = planWarnings });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed = true;
                    cancelled |= ex is OperationCanceledException;
                    errors.Add($"{plan.Definition.Id}: {ex.Message}");
                    logger.LogError(ex, "Fallo al aplicar {TweakId}; iniciando rollback del tweak.", plan.Definition.Id);
                    var rollbackErrors = await RollbackAsync(operationId, snapshotId, plan.Definition.Id, attempted, plan.Context);
                    critical |= rollbackErrors.Count > 0;
                    errors.AddRange(rollbackErrors);
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = rollbackErrors.Count > 0 ? TweakState.Unknown : TweakState.NotApplied, Message = ex.Message, RolledBack = rollbackErrors.Count == 0, RollbackFailed = rollbackErrors.Count > 0, Warnings = planWarnings });
                    break;
                }
            }
            if (results.Any(r => r.Changed)) warnings.Add("Se recomienda reiniciar antes de medir A/B si algún tweak lo requiere; no se reinicia automáticamente.");
        }
        catch (OperationCanceledException ex)
        {
            cancelled = true;
            failed = true;
            errors.Add("Operación cancelada: " + ex.Message);
            await AppendSafeAsync(operationId, JournalEventType.OperationFailed, null, snapshotId, ex.Message);
        }
        catch (PreflightFailedException ex)
        {
            preflightBlocked = true;
            failed = true;
            errors.Add(ex.Message);
            await AppendSafeAsync(operationId, JournalEventType.OperationFailed, null, snapshotId, ex.Message);
        }
        catch (Exception ex)
        {
            failed = true;
            errors.Add(ex.Message);
            logger.LogError(ex, "La operación {OperationId} falló antes de completar.", operationId);
            await AppendSafeAsync(operationId, JournalEventType.OperationFailed, null, snapshotId, ex.Message);
        }
        var success = !failed && !cancelled && !critical && results.All(r => !r.RollbackFailed);
        // DryRun refleja lo PEDIDO, no solo el camino feliz: si la operacion falla antes de llegar al
        // return del dry-run (linea ~123), este reporte decia DryRun=false aunque se hubiera pedido
        // dry-run, y parecia que el dry-run se habia ignorado. Un dry-run fallido sigue siendo un
        // dry-run: Success=false es lo que dice que fallo.
        return await FinishAsync(new BatchReport { OperationId = operationId, SnapshotId = snapshotId, DryRun = options.DryRun, Success = success, Cancelled = cancelled, HasCriticalFailure = critical, PreflightBlocked = preflightBlocked, RestorePointSequence = restoreSequence, Results = results, Errors = errors, Warnings = warnings }, success, CancellationToken.None);
    }

    public async Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var results = new List<TweakApplyResult>();
        var errors = new List<string>();
        var warnings = new List<string>();
        var critical = false;
        await AppendAsync(operationId, JournalEventType.OperationStarted, null, null, "Revert", cancellationToken);
        try
        {
            var modes = (request.Ids.Count > 0 ? 1 : 0) + (request.SnapshotId is not null ? 1 : 0) + (request.Last ? 1 : 0);
            if (modes != 1) throw new FragForgeException("revert_selection", "Usa exactamente uno de: IDs, --snapshot o --last.");
            var integrity = await journal.VerifyIntegrityAsync(cancellationToken);
            if (!integrity.IsValid) throw new FragForgeException("journal_integrity", "El journal no es íntegro: " + integrity.Error);
            var entries = await journal.ReadAllAsync(cancellationToken);
            var selections = SelectRevertSnapshots(request, entries);
            var currentHardware = await profiler.ProfileAsync(cancellationToken, progress);
            foreach (var selection in selections)
            {
                var snapshot = await snapshots.GetAsync(selection.SnapshotId, cancellationToken);
                // Capa 10.5: el snapshot guarda DefinitionSha256 de cada tweak al capturarlo. Antes se
                // ESCRIBÍA y no se comprobaba nunca, así que editar snapshot.json a mano permitía revertir a lo
                // que dijera el archivo en vez de a lo que se aplicó. Ahora se verifica antes de tocar nada.
                VerifySnapshotDefinitions(snapshot);
                var snapshotEntries = entries.Where(e => e.SnapshotId == snapshot.Id && e.EventType == JournalEventType.TweakApplied).ToArray();
                var previouslyReverted = RevertedIds(entries, snapshot.Id);
                if (snapshotEntries.Length > 0 && snapshotEntries.All(e => previouslyReverted.Contains(e.TweakId!)) && !request.Options.Force) throw new FragForgeException("snapshot_already_reverted", $"El snapshot {snapshot.Id} ya fue revertido. Force permite restaurarlo de nuevo, con riesgo de pisar cambios posteriores.");
                var selectedTweaks = selection.Ids.Count > 0 ? snapshot.Tweaks.Where(t => selection.Ids.Contains(t.TweakId, StringComparer.OrdinalIgnoreCase)).ToArray()
                    : request.Last ? snapshot.Tweaks.Where(t => snapshotEntries.Any(e => e.TweakId == t.TweakId) && (request.Options.Force || !previouslyReverted.Contains(t.TweakId))).ToArray()
                    : snapshot.Tweaks.Where(t => request.Options.Force || !previouslyReverted.Contains(t.TweakId)).ToArray();
                if (selection.Ids.Count > 0 && selectedTweaks.Length != selection.Ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new FragForgeException("revert_selection", "Algún ID no pertenece al snapshot seleccionado.");
                var hardware = currentHardware with { Games = snapshot.Hardware?.Games ?? currentHardware.Games };
                if (request.Options.Cs2ExecutableOverride is not null) hardware = ApplyOverrides(hardware, null, request.Options.Cs2ExecutableOverride);
                if (request.Options.TargetUserSid is not null) hardware = ApplyOverrides(hardware, request.Options.TargetUserSid, null);
                var plans = new List<RevertPlan>();
                await AppendAsync(operationId, JournalEventType.RevertStarted, null, snapshot.Id, null, cancellationToken);
                foreach (var tweak in selectedTweaks.Reverse())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var context = CreateContext(operationId, hardware, snapshots.GetArtifactDirectory(snapshot.Id), request.Options.TargetUserSid ?? snapshot.Hardware?.InteractiveUserSid, true);
                    var actions = new List<(ITweakAction Action, ActionState Before)>();
                    foreach (var captured in tweak.Actions.AsEnumerable().Reverse())
                    {
                        var action = actionFactory.Create(tweak.Definition.Actions[captured.Index]);
                        await action.PreflightAsync(context, cancellationToken);
                        var drift = await action.VerifyAsync(captured.State, context, cancellationToken);
                        if (drift.Outcome != VerificationOutcome.Matches && !request.Options.Force && !tweak.Definition.ReadOnly) throw new FragForgeException("revert_drift", $"'{tweak.TweakId}' cambió desde la aplicación ({drift.Message}). Revisa el estado o usa --force para restaurar exactamente el snapshot.");
                        actions.Add((action, captured.State));
                    }
                    plans.Add(new RevertPlan(tweak, context, actions));
                }
                var snapshotFailed = false;
                foreach (var plan in plans)
                {
                    var localErrors = new List<string>();
                    foreach (var (action, before) in plan.Actions)
                    {
                        try
                        {
                            progress?.Report(new EngineProgress("revert", plan.Tweak.TweakId, 0.5, $"Restaurando {action.Kind}."));
                            await action.RevertAsync(before, plan.Context, CancellationToken.None);
                            var verification = await action.VerifyRevertAsync(before, plan.Context, CancellationToken.None);
                            if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException(verification.Message);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            localErrors.Add($"{plan.Tweak.TweakId}/{action.Kind}: {ex.Message}");
                            logger.LogError(ex, "Fallo al revertir {TweakId}.", plan.Tweak.TweakId);
                        }
                    }
                    errors.AddRange(localErrors);
                    critical |= localErrors.Count > 0;
                    snapshotFailed |= localErrors.Count > 0;
                    results.Add(new TweakApplyResult { Id = plan.Tweak.TweakId, State = localErrors.Count > 0 ? TweakState.Unknown : TweakState.NotApplied, Message = localErrors.Count > 0 ? "Reversión incompleta; revisa el journal y el snapshot." : "Estado previo restaurado y verificado.", Changed = localErrors.Count == 0, RollbackFailed = localErrors.Count > 0 });
                    await AppendAsync(operationId, localErrors.Count > 0 ? JournalEventType.RevertFailed : JournalEventType.RevertCompleted, plan.Tweak.TweakId, snapshot.Id, string.Join(" ", localErrors), CancellationToken.None);
                    if (localErrors.Count == 0)
                    {
                        var actionPlan = new ActionPlan(plan.Tweak.Definition, plan.Actions.Select(a => a.Action).ToList(), plan.Tweak.Actions.Select(a => a.State).ToList(), plan.Context, "");
                        await RunHooksAsync(ExperimentPhase.AfterRevert, operationId, snapshot.Id, actionPlan, hardware, snapshots.GetArtifactDirectory(snapshot.Id), CancellationToken.None, warnings, false);
                    }
                }
                var updatedEntries = await journal.ReadAllAsync(CancellationToken.None);
                var appliedIds = snapshotEntries.Where(e => e.TweakId is not null).Select(e => e.TweakId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var revertedIds = RevertedIds(updatedEntries, snapshot.Id);
                if (!snapshotFailed && (appliedIds.Length == 0 || appliedIds.All(revertedIds.Contains))) await AppendAsync(operationId, JournalEventType.SnapshotReverted, null, snapshot.Id, null, CancellationToken.None);
            }
        }
        catch (OperationCanceledException ex) { errors.Add("Reversión cancelada antes de iniciar el siguiente bloque: " + ex.Message); }
        catch (Exception ex) { errors.Add(ex.Message); logger.LogError(ex, "Falló la reversión {OperationId}.", operationId); }
        var success = errors.Count == 0 && !critical && results.All(r => !r.RollbackFailed);
        await AppendSafeAsync(operationId, success ? JournalEventType.OperationCompleted : JournalEventType.OperationFailed, null, request.SnapshotId, success ? "Reversión completada." : string.Join(" ", errors));
        return new BatchReport { OperationId = operationId, SnapshotId = request.SnapshotId, Success = success, HasCriticalFailure = critical, Results = results, Errors = errors, Warnings = warnings };
    }

    private async Task<TweakStatus> EvaluateAsync(TweakDefinition definition, HardwareProfile hardware, IReadOnlySet<string> owned, CancellationToken cancellationToken)
    {
        var condition = EvaluateConditions(definition, hardware);
        if (!condition.IsMet)
        {
            // R-L: una condición DESCONOCIDA no es lo mismo que una condición incumplida. Se
            // distingue en el estado para no afirmar "No aplicable" sobre algo que no se pudo medir
            // (hotfix Capa 0: con "arquitectura desconocida" todo el catálogo salía No aplicable).
            var state = condition.IsKnown ? TweakState.NotApplicable : TweakState.Unknown;
            return new(definition.Id, state, condition.Reason, [], []);
        }
        try
        {
            var context = CreateContext("scan", hardware, null, hardware.InteractiveUserSid, false);
            var actions = definition.Actions.Select(actionFactory.Create).ToArray();
            foreach (var action in actions) await action.PreflightAsync(context, cancellationToken);
            var states = new List<ActionState>();
            var verifications = new List<VerificationResult>();
            foreach (var action in actions)
            {
                var before = await action.CaptureAsync(context, cancellationToken);
                states.Add(before);
                verifications.Add(await action.VerifyAsync(before, context, cancellationToken));
            }
            var observations = states.OfType<InspectionActionState>().SelectMany(s => s.Observations).ToArray();
            if (definition.ReadOnly) return new(definition.Id, TweakState.Inspected, "Solo lectura; no hay cambios que aplicar.", verifications, observations);
            var state = Classify(verifications, owned.Contains(definition.Id));
            var reason = string.Join(" ", verifications.Select(v => v.Message));
            return new(definition.Id, state, reason, verifications, observations);
        }
        catch (ActionNotApplicableException ex) { return new(definition.Id, TweakState.NotApplicable, ex.Message, [], []); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(definition.Id, TweakState.Unknown, ex.Message, [], []); }
    }

    private static TweakState Classify(IReadOnlyList<VerificationResult> results, bool owned)
    {
        if (results.Count == 0 || results.Any(r => r.Outcome == VerificationOutcome.Unknown)) return TweakState.Unknown;
        var matches = results.Count(r => r.Outcome == VerificationOutcome.Matches);
        if (matches == results.Count) return owned ? TweakState.Applied : TweakState.AppliedExternally;
        if (matches == 0 && results.All(r => r.Outcome == VerificationOutcome.DoesNotMatch)) return TweakState.NotApplied;
        return TweakState.PartiallyApplied;
    }

    private ConditionResult EvaluateConditions(TweakDefinition definition, HardwareProfile hardware)
    {
        foreach (var specification in definition.Conditions)
        {
            var result = conditionFactory.Create(specification).Evaluate(hardware);
            if (!result.IsKnown) return ConditionResult.Unknown(result.Reason);
            if (!result.IsMet) return result;
        }
        return ConditionResult.Met(definition.Conditions.Count == 0 ? "Sin condiciones restrictivas." : "Todas las condiciones se cumplen.");
    }

    private async Task<ActionPlan?> TryBuildPlanAsync(TweakDefinition definition, HardwareProfile hardware, ApplyOptions? options, string operationId, CancellationToken cancellationToken)
    {
        var context = CreateContext(operationId, hardware, null, options?.TargetUserSid ?? hardware.InteractiveUserSid, options?.AllowPowerShellActions ?? false);
        var actions = new List<ITweakAction>();
        try
        {
            foreach (var specification in definition.Actions)
            {
                var action = actionFactory.Create(specification);
                await action.PreflightAsync(context, cancellationToken);
                actions.Add(action);
            }
        }
        catch (ActionNotApplicableException) { return null; }
        catch (OperationCanceledException) { throw; }
        return new ActionPlan(definition, actions, [], context, EvaluateConditions(definition, hardware).Reason);
    }

    private async Task<SystemSnapshot> CaptureAndPersistAsync(string operationId, string label, HardwareProfile hardware, IReadOnlyList<ActionPlan> plans, bool createRestorePoint, CancellationToken cancellationToken, IProgress<EngineProgress>? progress, List<string>? unavailable = null)
    {
        var snapshotId = NewId();
        var artifactDirectory = snapshots.GetArtifactDirectory(snapshotId);
        Directory.CreateDirectory(artifactDirectory);
        var captured = new List<TweakSnapshot>();
        var registryStates = new List<RegistryActionState>();
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = plan.Context with { OperationId = operationId, ArtifactDirectory = artifactDirectory, Progress = new ProgressAdapter(progress, plan.Definition.Id) };
            plan.Context = context;
            var actions = new List<CapturedAction>();
            try
            {
                for (var i = 0; i < plan.Actions.Count; i++)
                {
                    var state = await plan.Actions[i].CaptureAsync(context, cancellationToken);
                    actions.Add(new CapturedAction(i, plan.Actions[i].Kind, state));
                    if (state is RegistryActionState registry) registryStates.Add(registry);
                }
            }
            catch (ActionNotApplicableException)
            {
                // El objetivo desaparecio entre la sonda de TryBuildPlanAsync y esta captura: no se
                // inventa un snapshot para algo que no existe, ni se rompe la operacion. El tweak queda
                // fuera del snapshot, no se aplica y se reporta como no aplicable.
                unavailable?.Add(plan.Definition.Id);
                continue;
            }
            captured.Add(new TweakSnapshot { TweakId = plan.Definition.Id, DefinitionSha256 = catalog.GetDefinitionHash(plan.Definition.Id), Definition = plan.Definition, Actions = actions });
            await AppendAsync(operationId, JournalEventType.TweakCaptureCompleted, plan.Definition.Id, snapshotId, null, cancellationToken);
        }
        if (registryStates.Count > 0) await RegistryBackupExporter.WriteAsync(Path.Combine(artifactDirectory, "registry-before.reg"), registryStates, cancellationToken);
        long? sequence = null;
        if (createRestorePoint && plans.Any(p => !p.Definition.ReadOnly))
        {
            progress?.Report(new EngineProgress("restore-point", null, 0.1, "Creando punto de restauración."));
            var restoreResult = await restore.CreateAsync("FragForge " + operationId[..8], cancellationToken);
            sequence = restoreResult.Sequence;
            await AppendAsync(operationId, JournalEventType.RestorePointCreated, null, snapshotId, restoreResult.Message, cancellationToken);
        }
        var snapshot = new SystemSnapshot
        {
            Id = snapshotId,
            Label = label,
            Hardware = hardware,
            HardwareBuild = hardware.Os.Build.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RestorePointSequence = sequence,
            Tweaks = captured
        };
        await snapshots.SaveAsync(snapshot, cancellationToken);
        await AppendAsync(operationId, JournalEventType.SnapshotCreated, null, snapshotId, label, cancellationToken);
        return snapshot;
    }

    private async Task<IReadOnlyList<string>> RollbackAsync(string operationId, string snapshotId, string tweakId, IReadOnlyList<(ITweakAction Action, ActionState Before)> attempted, ActionExecutionContext context)
    {
        var errors = new List<string>();
        await AppendSafeAsync(operationId, JournalEventType.TweakRollbackStarted, tweakId, snapshotId, null);
        foreach (var (action, before) in attempted.AsEnumerable().Reverse())
        {
            try
            {
                await action.RevertAsync(before, context, CancellationToken.None);
                var verification = await action.VerifyRevertAsync(before, context, CancellationToken.None);
                if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException(verification.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add($"{tweakId}/{action.Kind}: {ex.Message}"); }
        }
        await AppendSafeAsync(operationId, errors.Count == 0 ? JournalEventType.TweakRolledBack : JournalEventType.TweakRollbackFailed, tweakId, snapshotId, string.Join(" ", errors));
        return errors;
    }

    private async Task<TweakStatus> VerifyPlanAsync(ActionPlan plan, CancellationToken cancellationToken)
    {
        var states = new List<ActionState>();
        var results = new List<VerificationResult>();
        try
        {
            foreach (var action in plan.Actions)
            {
                var before = await action.CaptureAsync(plan.Context, cancellationToken);
                states.Add(before);
                results.Add(await action.VerifyAsync(before, plan.Context, cancellationToken));
            }
        }
        catch (ActionNotApplicableException ex)
        {
            // El objeto de la accion NO existe en este equipo (tarea programada, servicio, plan de
            // energia, adaptador). Es un HECHO, no un fallo: el tweak es NO APLICABLE.
            //
            // Este es el punto por el que se escapaba el error medido en la PC objetivo: ApplyAsync
            // llama a VerifyPlanAsync ANTES de capturar el snapshot, asi que aqui se decide sin pagar
            // una captura extra (hay acciones cuyo CaptureAsync ejecuta un script de PowerShell).
            //
            // Solo se traga ActionNotApplicableException. Un fallo REAL (acceso denegado, servicio de
            // Task Scheduler caido, timeout) es otra excepcion y sube: sigue siendo error.
            //
            // No se toca plan.States: el plan no llega a la captura del snapshot.
            return new TweakStatus(plan.Definition.Id, TweakState.NotApplicable, ex.Message, [], []);
        }
        plan.States.Clear();
        plan.States.AddRange(states);
        return new(plan.Definition.Id, Classify(results, false), string.Join(" ", results.Select(r => r.Message)), results, states.OfType<InspectionActionState>().SelectMany(s => s.Observations).ToArray());
    }

    private async Task RunHooksAsync(ExperimentPhase phase, string operationId, string snapshotId, ActionPlan plan, HardwareProfile hardware, string artifactDirectory, CancellationToken cancellationToken, List<string> warnings, bool failOnError)
    {
        foreach (var hook in _hooks)
        {
            try
            {
                var artifacts = await hook.RunAsync(new ExperimentContext(operationId, snapshotId, phase, plan.Definition, hardware, DateTimeOffset.UtcNow, artifactDirectory), cancellationToken);
                await AppendAsync(operationId, JournalEventType.HookCompleted, plan.Definition.Id, snapshotId, $"{phase}: {hook.GetType().Name}; artifacts={artifacts.Count}", cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (failOnError) throw new FragForgeException("experiment_hook_failed", $"El gancho {phase} falló antes de aplicar: {ex.Message}", ex);
                warnings.Add($"Gancho {phase}: {ex.Message}");
            }
        }
    }

    private ActionExecutionContext CreateContext(string operationId, HardwareProfile hardware, string? artifactDirectory, string? userSid, bool allowPowerShell) => new()
    {
        OperationId = operationId, Hardware = hardware, ArtifactDirectory = artifactDirectory, TargetUserSid = userSid, AllowPowerShellActions = allowPowerShell
    };

    private static HardwareProfile ApplyOverrides(HardwareProfile profile, string? userSid, string? cs2Path)
    {
        if (userSid is not null && !System.Text.RegularExpressions.Regex.IsMatch(userSid, @"^S-1-\d+(?:-\d+)+$")) throw new FragForgeException("invalid_sid", "SID de usuario inválido.");
        if (cs2Path is not null)
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cs2Path));
            if (!Path.GetFileName(full).Equals("cs2.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new FragForgeException("invalid_cs2_path", "La ruta manual debe apuntar a un cs2.exe existente.");
            profile = profile with { Games = new GameInfo { Cs2ExecutablePath = full, Cs2DetectionSource = "Manual", Cs2Candidates = [full] } };
        }
        if (userSid is not null) profile = profile with { InteractiveUserSid = userSid };
        return profile;
    }

    private async Task<HashSet<string>> ReadOwnershipAsync(CancellationToken cancellationToken)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var entries = await journal.ReadAllAsync(cancellationToken);
            var revertedSnapshots = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.EventType == JournalEventType.TweakApplied && entry.TweakId is not null && entry.SnapshotId is not null && !revertedSnapshots.Contains(entry.SnapshotId)) owned.Add(entry.TweakId);
                if (entry.EventType is JournalEventType.TweakRolledBack or JournalEventType.RevertCompleted && entry.TweakId is not null) owned.Remove(entry.TweakId);
            }
            foreach (var entry in entries.Where(e => e.EventType == JournalEventType.SnapshotReverted))
                foreach (var applied in entries.Where(e => e.SnapshotId == entry.SnapshotId && e.EventType == JournalEventType.TweakApplied && e.TweakId is not null)) owned.Remove(applied.TweakId!);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { logger.LogWarning(ex, "No se pudo leer el journal; el estado coincidente se marcará externo."); }
        return owned;
    }

    private static IReadOnlyList<(string SnapshotId, IReadOnlyList<string> Ids)> SelectRevertSnapshots(RevertRequest request, IReadOnlyList<JournalEntry> entries)
    {
        if (request.SnapshotId is not null) return [(request.SnapshotId, [])];
        var reverted = entries.Where(e => e.EventType == JournalEventType.RevertCompleted && e.TweakId is not null).Select(e => e.TweakId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshotReverted = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = entries.Where(e => e.EventType == JournalEventType.TweakApplied && e.TweakId is not null && e.SnapshotId is not null && !snapshotReverted.Contains(e.SnapshotId)).ToArray();
        if (request.Last)
        {
            var last = applied.LastOrDefault(e => request.Options.Force || !reverted.Contains(e.TweakId!));
            if (last is null) throw new FragForgeException("no_snapshot", "No hay un snapshot aplicado pendiente de revertir.");
            return [(last.SnapshotId!, [])];
        }
        var result = new List<(string, IReadOnlyList<string>)>();
        foreach (var id in request.Ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = applied.LastOrDefault(e => e.TweakId!.Equals(id, StringComparison.OrdinalIgnoreCase) && (request.Options.Force || !reverted.Contains(id)));
            if (entry is null) throw new FragForgeException("no_snapshot", $"No hay una aplicación propia reversible de '{id}'. No se deshacen cambios externos/Atlas sin un snapshot explícito.");
            result.Add((entry.SnapshotId!, [id]));
        }
        return result.GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, (IReadOnlyList<string>)g.SelectMany(x => x.Item2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
    }

    private static HashSet<string> RevertedIds(IReadOnlyList<JournalEntry> entries, string snapshotId)
    {
        var ids = entries.Where(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.RevertCompleted && e.TweakId is not null).Select(e => e.TweakId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entries.Any(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.SnapshotReverted))
            foreach (var entry in entries.Where(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.TweakApplied && e.TweakId is not null)) ids.Add(entry.TweakId!);
        return ids;
    }

    private async Task<BatchReport> FinishAsync(BatchReport report, bool success, CancellationToken cancellationToken)
    {
        await AppendSafeAsync(report.OperationId, success ? JournalEventType.OperationCompleted : JournalEventType.OperationFailed, null, report.SnapshotId, success ? "Operación completada." : string.Join(" ", report.Errors));
        return report;
    }
    /// <summary>
    /// Verifica que la definición guardada en cada tweak del snapshot siga coincidiendo con el hash que se
    /// calculó al capturarlo (Capa 10.5).
    ///
    /// <para>
    /// El snapshot guarda la definición COMPLETA para poder revertir aunque el catálogo haya cambiado después.
    /// Esa comodidad es también un riesgo: quien pueda editar <c>snapshot.json</c> puede cambiar los parámetros
    /// de la acción que se va a revertir. El hash no autentica (cualquiera que edite el archivo puede recalcular
    /// el SHA-256 a juego), pero sí convierte una edición accidental o a ciegas en un fallo ruidoso en vez de en
    /// una reversión silenciosa a un estado que nunca se aplicó.
    /// </para>
    ///
    /// <para>
    /// Un snapshot sin hash (versiones antiguas) NO se rechaza: se acepta y se avisa. Rechazarlo rompería la
    /// recuperación de datos históricos, que es justo cuando más falta hace.
    /// </para>
    /// </summary>
    private static void VerifySnapshotDefinitions(SystemSnapshot snapshot)
    {
        foreach (var tweak in snapshot.Tweaks)
        {
            if (string.IsNullOrWhiteSpace(tweak.DefinitionSha256)) continue;
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tweak.Definition, JsonDefaults.Create(false))))).ToLowerInvariant();
            if (!string.Equals(actual, tweak.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new FragForgeException("snapshot_definition_tampered",
                    $"El snapshot {snapshot.Id} no es íntegro: la definición de '{tweak.TweakId}' no coincide con su DefinitionSha256 (esperado {tweak.DefinitionSha256}, obtenido {actual}). No se revierte con una definición manipulada.");
        }
    }

    private Task AppendAsync(string operationId, JournalEventType type, string? tweakId, string? snapshotId, string? message, CancellationToken cancellationToken) => journal.AppendAsync(new JournalEntry { EntryId = NewId(), OperationId = operationId, EventType = type, TweakId = tweakId, SnapshotId = snapshotId, Message = message }, cancellationToken);
    private async Task AppendSafeAsync(string operationId, JournalEventType type, string? tweakId, string? snapshotId, string? message)
    {
        try { await AppendAsync(operationId, type, tweakId, snapshotId, message, CancellationToken.None); }
        catch (Exception ex) { logger.LogCritical(ex, "No se pudo escribir el journal. No continúes aplicando cambios."); throw new RollbackException("El journal no pudo persistirse; revisa disco/permisos antes de continuar.", ex); }
    }
    private static TweakApplyResult Skipped(string id, TweakState state, string message) => new() { Id = id, State = state, Message = message };
    private static string NewId() => Guid.NewGuid().ToString("D");
    private sealed class ActionPlan(TweakDefinition definition, IReadOnlyList<ITweakAction> actions, List<ActionState> states, ActionExecutionContext context, string conditionReason)
    {
        public TweakDefinition Definition { get; } = definition;
        public IReadOnlyList<ITweakAction> Actions { get; } = actions;
        public List<ActionState> States { get; } = states;
        public ActionExecutionContext Context { get; set; } = context;
        public string ConditionReason { get; } = conditionReason;
    }
    private sealed record RevertPlan(TweakSnapshot Tweak, ActionExecutionContext Context, IReadOnlyList<(ITweakAction Action, ActionState Before)> Actions);
    private sealed class ProgressAdapter(IProgress<EngineProgress>? outer, string tweakId) : IProgress<ActionProgress>
    {
        public void Report(ActionProgress value) => outer?.Report(new EngineProgress(value.Stage, tweakId, value.Fraction, value.Message));
    }
}

internal static class ActionProgressExtensions
{
    public static void ReportReady(this ITweakAction action, ActionExecutionContext context, string stage, double fraction, string message) => context.Report(stage, fraction, message);
}
