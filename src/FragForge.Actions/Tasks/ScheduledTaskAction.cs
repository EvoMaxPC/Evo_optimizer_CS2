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
        // "No existe" lo decide WindowsScheduledTaskStore.GetAsync por DATO ESTRUCTURADO, no por texto:
        // devuelve {"exists":false} cuando Get-ScheduledTask no encuentra la tarea. La deteccion no pasa
        // por ningun mensaje localizado, asi que un Windows en es-MX, en-US o el que sea da el mismo
        // resultado.
        //
        // Un fallo real (acceso denegado, Task Scheduler caido, timeout) NO llega aqui: el store lanza
        // ProcessFailedException, que sube como error. Esta distincion es la clave de la ronda.
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
