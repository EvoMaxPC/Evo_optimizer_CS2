using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Diagnostics;

public sealed class InspectOnlyAction : ITweakAction
{
    private readonly IReadOnlyList<IDiagnosticProbe> _probes;
    private readonly InspectionParameters _parameters;
    public string Kind => "InspectOnly";
    public InspectOnlyAction(IEnumerable<IDiagnosticProbe> probes, ActionSpec specification)
    {
        _parameters = ActionParameters.Parse<InspectionParameters>(specification);
        var available = probes.ToArray();
        var keys = _parameters.Probes.Count == 0 ? available.Select(p => p.Key).ToArray() : _parameters.Probes;
        var missing = keys.Except(available.Select(p => p.Key), StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0) throw new CatalogValidationException("Unknown diagnostic probes.", missing);
        _probes = available.Where(p => keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var observations = new List<DiagnosticObservation>();
        foreach (var probe in _probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { observations.Add(await probe.ReadAsync(context.Hardware, cancellationToken)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { observations.Add(new DiagnosticObservation(probe.Key, "Unknown", probe.GetType().Name, "Unknown", ex.Message)); }
        }
        return new InspectionActionState { Observations = observations };
    }
    public Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(new VerificationResult(VerificationOutcome.Unknown, "Solo lectura: no hay estado objetivo que verificar."));
    public Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(VerificationResult.Match("InspectOnly no realizó cambios."));
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult("Solo lectura, cero escrituras. Sondear: " + string.Join(", ", _probes.Select(p => p.Key)) + ".");
}
internal sealed record InspectionParameters { public IReadOnlyList<string> Probes { get; init; } = []; }
