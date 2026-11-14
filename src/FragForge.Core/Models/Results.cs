namespace FragForge.Core.Models;

public sealed record VerificationResult(VerificationOutcome Outcome, string Message)
{
    public static VerificationResult Match(string message = "El estado coincide.") => new(VerificationOutcome.Matches, message);
    public static VerificationResult Missing(string message) => new(VerificationOutcome.DoesNotMatch, message);
}

public sealed record ConditionResult(bool IsMet, bool IsKnown, string Reason)
{
    public static ConditionResult Met(string reason) => new(true, true, reason);
    public static ConditionResult Unmet(string reason) => new(false, true, reason);
    public static ConditionResult Unknown(string reason) => new(false, false, reason);
}

public sealed record TweakStatus(string Id, TweakState State, string Reason, IReadOnlyList<VerificationResult> ActionResults, IReadOnlyList<DiagnosticObservation> Observations);
public sealed record ScanReport(HardwareProfile Hardware, IReadOnlyList<TweakStatus> Tweaks, DateTimeOffset CreatedAt);
public sealed record EngineProgress(string Stage, string? TweakId, double Fraction, string Message);
public sealed record ActionProgress(string Stage, double Fraction, string Message);

public sealed record ApplyOptions
{
    public bool DryRun { get; init; }
    public bool Force { get; init; }
    public bool CreateRestorePoint { get; init; } = true;
    public bool EnableSystemProtection { get; init; }
    public TweakProfile Profile { get; init; } = TweakProfile.Safe;
    public IReadOnlySet<string> RiskAcknowledgements { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public string? AdvancedRiskPhrase { get; init; }
    public string? Cs2ExecutableOverride { get; init; }
    public string? TargetUserSid { get; init; }
    public bool AllowPowerShellActions { get; init; }
}

public sealed record RevertOptions
{
    public bool Force { get; init; }
    public string? TargetUserSid { get; init; }
    public string? Cs2ExecutableOverride { get; init; }
}

public sealed record RevertRequest
{
    public IReadOnlyList<string> Ids { get; init; } = [];
    public string? SnapshotId { get; init; }
    public bool Last { get; init; }
    public RevertOptions Options { get; init; } = new();
}

public sealed record DryRunChange(string TweakId, int ActionIndex, string Kind, string Description);
public sealed record TweakApplyResult
{
    public required string Id { get; init; }
    public required TweakState State { get; init; }
    public required string Message { get; init; }
    public bool Changed { get; init; }
    public bool RolledBack { get; init; }
    public bool RollbackFailed { get; init; }
    public IReadOnlyList<DryRunChange> PlannedChanges { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record BatchReport
{
    public required string OperationId { get; init; }
    public string? SnapshotId { get; init; }
    public bool DryRun { get; init; }
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public bool HasCriticalFailure { get; init; }
    public long? RestorePointSequence { get; init; }
    public IReadOnlyList<TweakApplyResult> Results { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PreflightCheck(string Id, PreflightSeverity Severity, string Message, string? Remediation = null);
public sealed record PreflightReport(bool CanApply, IReadOnlyList<PreflightCheck> Checks)
{
    public static PreflightReport Passed(IReadOnlyList<PreflightCheck> checks) => new(!checks.Any(c => c.Severity == PreflightSeverity.Blocking), checks);
}
public sealed record RestorePointResult(long? Sequence, string Message);
public sealed record ExperimentContext(string OperationId, string? SnapshotId, ExperimentPhase Phase, TweakDefinition Tweak, HardwareProfile Hardware, DateTimeOffset Timestamp, string? ArtifactDirectory = null);
public sealed record ExperimentArtifact(string Name, string Path, string ContentType);
public sealed record BenchmarkMeasurement(string Metric, double Value, string Unit, IReadOnlyDictionary<string, string> Metadata);
public sealed record BenchmarkSession(string Id, string Label, IReadOnlyList<BenchmarkMeasurement> Measurements, IReadOnlyList<ExperimentArtifact> Artifacts);
