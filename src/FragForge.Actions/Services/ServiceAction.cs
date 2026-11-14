using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Services;

public sealed class ServiceAction : ITweakAction
{
    private readonly IServiceController _controller;
    private readonly IProtectionPolicy _policy;
    private readonly ServiceParameters _parameters;
    public string Kind => "Service";
    public ServiceAction(IServiceController controller, IProtectionPolicy policy, ActionSpec specification)
    {
        _controller = controller; _policy = policy; _parameters = ActionParameters.Parse<ServiceParameters>(specification);
        if (string.IsNullOrWhiteSpace(_parameters.Name)) throw new CatalogValidationException("Service name is required.", ["name"]);
        if (_parameters.StartMode is not null and not ("Auto" or "Manual" or "Disabled")) throw new CatalogValidationException("Invalid service start mode.", [_parameters.StartMode]);
        if (_parameters.State is not null and not ("Running" or "Stopped")) throw new CatalogValidationException("Invalid service state.", [_parameters.State]);
        if (_parameters.StartMode is null && _parameters.State is null && _parameters.DelayedAutoStart is null) throw new CatalogValidationException("Service action has no target.", ["empty"]);
        _policy.EnsureServiceAllowed(_parameters.Name);
    }
    public async Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        _policy.EnsureServiceAllowed(_parameters.Name);
        var current = await _controller.GetAsync(_parameters.Name, cancellationToken);
        if (!current.Exists) throw new ActionNotApplicableException($"El servicio {_parameters.Name} no existe.");
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => new ServiceActionState { Service = await _controller.GetAsync(_parameters.Name, cancellationToken) };
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (_parameters.StartMode is not null || _parameters.DelayedAutoStart is not null) await _controller.SetStartModeAsync(_parameters.Name, _parameters.StartMode ?? "Manual", _parameters.DelayedAutoStart, cancellationToken);
        if (_parameters.State is not null) await _controller.SetStateAsync(_parameters.Name, _parameters.State, cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await _controller.GetAsync(_parameters.Name, cancellationToken);
        var ok = (_parameters.StartMode is null || current.StartMode == _parameters.StartMode) && (_parameters.State is null || current.State == _parameters.State) && (_parameters.DelayedAutoStart is null || current.DelayedAutoStart == _parameters.DelayedAutoStart);
        return ok ? VerificationResult.Match($"Servicio {_parameters.Name}: {current.StartMode}/{current.State}.") : VerificationResult.Missing($"Servicio {_parameters.Name} no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (ServiceActionState)before;
        if (!state.Service.Exists) throw new InvalidOperationException("Cannot revert a service that did not exist at capture.");
        _policy.EnsureServiceAllowed(state.Service.Name);
        await _controller.SetStartModeAsync(state.Service.Name, state.Service.StartMode ?? "Manual", _parameters.DelayedAutoStart is null ? null : state.Service.DelayedAutoStart, cancellationToken, !state.Service.DelayedAutoStartExisted);
        await _controller.SetStateAsync(state.Service.Name, state.Service.State ?? "Stopped", cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Servicio restaurado.") : VerificationResult.Missing("El servicio no volvió al estado capturado.");
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"Servicio {_parameters.Name}: inicio={_parameters.StartMode ?? "sin cambio"}, estado={_parameters.State ?? "sin cambio"}, delayed={_parameters.DelayedAutoStart?.ToString() ?? "sin cambio"}. Revert restaura inicio y estado exactos.");
}
internal sealed record ServiceParameters { public required string Name { get; init; } public string? StartMode { get; init; } public string? State { get; init; } public bool? DelayedAutoStart { get; init; } }
