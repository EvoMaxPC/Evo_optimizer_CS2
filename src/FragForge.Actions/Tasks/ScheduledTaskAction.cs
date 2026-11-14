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
        if (!state.Exists) throw new ActionNotApplicableException($"La tarea {_parameters.Path} no existe.");
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
