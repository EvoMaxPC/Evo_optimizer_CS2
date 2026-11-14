using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Tasks;

public sealed class ScheduledTaskAction(IScheduledTaskStore store, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly ScheduledTaskParameters _parameters = ActionParameters.Parse<ScheduledTaskParameters>(specification);
    public string Kind => "ScheduledTask";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureTaskAllowed(_parameters.Path);
        if (!_parameters.Path.StartsWith('\\') || _parameters.Path.Contains("..")) throw new CatalogValidationException("Scheduled task path must be absolute and safe.", [_parameters.Path]);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureTaskAllowed(_parameters.Path);
        var state = await store.GetAsync(_parameters.Path, cancellationToken);
        // Objeto inexistente = NO APLICABLE, nunca error ni "aplicado" (R-L: no inventar).
        //
        // COMO se decide "no existe" (estado real, ronda 5c): el script de
        // WindowsScheduledTaskStore.BuildGetScript usa Get-ScheduledTask -ErrorAction Stop dentro de un
        // try/catch y emite {"exists":false} SOLO si la excepcion tiene
        // $_.CategoryInfo.Category -eq 'ObjectNotFound'. La categoria es un valor de enumeracion de
        // PowerShell, no texto: un Windows en es-MX, en-US o el que sea da el mismo resultado.
        //
        // Cualquier OTRA falla (acceso denegado, Programador de tareas caido, RPC, timeout, o exito con
        // 0 resultados sin excepcion) NO produce exists:false: el script escribe por stderr y sale con
        // codigo distinto de 0, asi que WindowsScheduledTaskStore.RunAsync lanza ProcessFailedException
        // y sube como error real. No se llega a este if.
        //
        // Es decir: este if solo se ejecuta cuando el store pudo AFIRMAR que la tarea no existe.
        if (!state.Exists) throw new ActionNotApplicableException($"La tarea no existe en este equipo: {_parameters.Path}");
        return state;
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) { policy.EnsureTaskAllowed(_parameters.Path); await store.SetEnabledAsync(_parameters.Path, _parameters.Enabled, cancellationToken); }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await store.GetAsync(_parameters.Path, cancellationToken);
        return current.Exists && current.Enabled == _parameters.Enabled ? VerificationResult.Match($"Tarea {_parameters.Path} enabled={current.Enabled}.") : VerificationResult.Missing("La tarea programada no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) { var state = (ScheduledTaskActionState)before; policy.EnsureTaskAllowed(state.Path); await store.SetEnabledAsync(state.Path, state.Enabled, cancellationToken); }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Tarea restaurada.") : VerificationResult.Missing("La tarea no volvió al estado capturado.");
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"Tarea {_parameters.Path}: Enabled={_parameters.Enabled}; no se elimina ni se reescribe su XML.");
}
internal sealed record ScheduledTaskParameters { public required string Path { get; init; } public bool Enabled { get; init; } }
