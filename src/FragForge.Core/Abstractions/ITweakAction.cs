using FragForge.Core.Models;

namespace FragForge.Core.Abstractions;

public sealed record ActionExecutionContext
{
    public required string OperationId { get; init; }
    public required HardwareProfile Hardware { get; init; }
    public string? ArtifactDirectory { get; init; }
    public string? TargetUserSid { get; init; }
    public bool AllowPowerShellActions { get; init; }
    public IProgress<ActionProgress>? Progress { get; init; }
    public void Report(string stage, double fraction, string message) => Progress?.Report(new ActionProgress(stage, fraction, message));
}

public interface ITweakAction
{
    string Kind { get; }
    Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
}

public interface ITweakActionFactory
{
    IReadOnlySet<string> SupportedKinds { get; }
    ITweakAction Create(ActionSpec specification);
}
