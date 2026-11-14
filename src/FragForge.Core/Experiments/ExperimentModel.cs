using FragForge.Core.Models;

namespace FragForge.Core.Experiments;

public enum ExperimentMode { Interleaved, Blocked }
public enum ArmKind { Baseline, Candidate }

/// <summary>Una corrida individual. <see cref="IsValid"/> es false si la captura fue parcial, el proceso murió o faltan frames.</summary>
public sealed record RunResult
{
    public required string RunId { get; init; }
    public required ArmKind Arm { get; init; }
    public required int Index { get; init; }
    public RunMetrics? Metrics { get; init; }
    public bool IsValid { get; init; }
    public string? InvalidReason { get; init; }
    /// <summary>
    /// Validez de la captura (Valid/Suspect/Invalid). Campo ADITIVO: un JSON previo sin él se lee como
    /// Valid sin motivos, que es exactamente el comportamiento anterior (R-B).
    /// </summary>
    public CaptureValidityReport Validity { get; init; } = CaptureValidityReport.Ok();
    public double DurationSeconds { get; init; }
    public string? CsvPath { get; init; }
    public string? SensorCsvPath { get; init; }
    public string? PresentModePrimary => Metrics?.PresentModePrimary;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record ExperimentArm
{
    public required ArmKind Kind { get; init; }
    public required IReadOnlyList<RunResult> Runs { get; init; }
    /// <summary>
    /// Un brazo es válido si tiene corridas y ninguna es inválida. <c>Suspect</c> NO invalida el brazo:
    /// se informa en el comparador, pero no bloquea el veredicto.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Runs.Count > 0 && Runs.All(r => r.IsValid && r.Validity.IsUsable);

    /// <summary>Corridas sospechosas del brazo, para mostrarlas en el comparador A/B.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RunResult> SuspectRuns => Runs.Where(r => r.Validity.IsSuspect).ToArray();

    /// <summary>Vector de una métrica sobre las corridas VÁLIDAS. Las inválidas no aportan ceros.</summary>
    public double[] Metric(Func<RunMetrics, double?> selector) => Runs.Where(r => r.IsValid && r.Metrics is not null)
        .Select(r => selector(r.Metrics!))
        .Where(v => v is not null && !double.IsNaN(v.Value))
        .Select(v => v!.Value)
        .ToArray();
}

/// <summary>
/// Entorno de medición. Si difiere entre brazos el experimento es INVÁLIDO: no se compara lo que no es comparable.
/// </summary>
public sealed record ExperimentEnvironment
{
    public string OsBuild { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string CpuModel { get; init; } = "";
    public string GpuName { get; init; } = "";
    public string PowerPlanGuid { get; init; } = "";
    public string PowerPlanName { get; init; } = "";
    public int? RefreshRateHz { get; init; }
    public int? MonitorWidth { get; init; }
    public int? MonitorHeight { get; init; }
    public bool PendingReboot { get; init; }
    public bool ServicingActive { get; init; }
    public double? BackgroundCpuPercent { get; init; }
    public IReadOnlyList<BackgroundProcessInfo> TopBackgroundProcesses { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Campos que invalidan la comparación si cambian entre brazos.</summary>
    public IReadOnlyList<string> ComparabilityKeys() =>
    [
        $"build={OsBuild}",
        $"plan={PowerPlanGuid}",
        $"refresh={RefreshRateHz}",
        $"resolution={MonitorWidth}x{MonitorHeight}",
        $"pendingReboot={PendingReboot}",
        $"servicing={ServicingActive}"
    ];
}

/// <summary>Proceso de fondo relevante. Solo nombre y PID: nunca línea de comandos ni ruta completa.</summary>
public sealed record BackgroundProcessInfo(string Name, int ProcessId, double? CpuPercent);

public sealed record ExperimentPlan
{
    public int SchemaVersion { get; init; } = 1;
    public required string SessionId { get; init; }
    public required IReadOnlyList<string> TweakIds { get; init; }
    public required ExperimentMode Mode { get; init; }
    public required int RunsPerArm { get; init; }
    public required double DurationSeconds { get; init; }
    public required double WarmupSeconds { get; init; }
    public required int Seed { get; init; }
    public bool IsAaCalibration { get; init; }
    public int? CooldownSeconds { get; init; }
    public int RequiredReboots { get; init; }
    public bool RequiresReboot => RequiredReboots > 0;
    public string? Label { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Orden de las corridas. Intercalado ABAB con el arranque aleatorizado por semilla.
    /// El diseño en bloque es posible pero se marca como confundido con la deriva térmica.
    /// </summary>
    public IReadOnlyList<ArmKind> BuildSchedule()
    {
        var random = new Random(Seed);
        var schedule = new List<ArmKind>(RunsPerArm * 2);
        if (Mode == ExperimentMode.Interleaved)
        {
            var startsWithCandidate = random.Next(2) == 1;
            for (var i = 0; i < RunsPerArm; i++)
            {
                schedule.Add(startsWithCandidate ? ArmKind.Candidate : ArmKind.Baseline);
                schedule.Add(startsWithCandidate ? ArmKind.Baseline : ArmKind.Candidate);
            }
        }
        else
        {
            var order = random.Next(2) == 1 ? new[] { ArmKind.Baseline, ArmKind.Candidate } : [ArmKind.Candidate, ArmKind.Baseline];
            foreach (var arm in order) for (var i = 0; i < RunsPerArm; i++) schedule.Add(arm);
        }
        return schedule;
    }
}

/// <summary>Estado persistido del experimento. Permite reanudar tras un reinicio obligatorio.</summary>
public sealed record ExperimentState
{
    public int SchemaVersion { get; init; } = 1;
    public required ExperimentPlan Plan { get; init; }
    public required IReadOnlyList<RunResult> Runs { get; init; }
    public bool BaselineApplied { get; init; }
    public bool CandidateApplied { get; init; }
    public bool Completed { get; init; }
    public bool Invalidated { get; init; }
    public string? InvalidReason { get; init; }
    public ExperimentEnvironment? Environment { get; init; }
    public ExperimentVerdict? Verdict { get; init; }
    public IReadOnlyList<string> Log { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int NextRunIndex => Runs.Count;
    public bool NeedsResume => !Completed && !Invalidated;
    public ExperimentArm Arm(ArmKind kind) => new() { Kind = kind, Runs = Runs.Where(r => r.Arm == kind).ToArray() };
}

public sealed record ExperimentSummary(string SessionId, string Label, ExperimentVerdictKind? Verdict, VerdictConfidence? Confidence, DateTimeOffset UpdatedAt, int Runs, bool Completed, bool Invalidated);

public sealed record ExperimentArtifactFile(string Name, string RelativePath, string ContentType, long SizeBytes);

/// <summary>Evidencia local por tweak. NO edita el catálogo embebido: es una sugerencia para el desarrollador.</summary>
public sealed record LocalEvidence
{
    public int SchemaVersion { get; init; } = 1;
    public required string TweakId { get; init; }
    public required ExperimentVerdictKind Verdict { get; init; }
    public required VerdictConfidence Confidence { get; init; }
    public double? P1LowChangePct { get; init; }
    public double? AvgFpsChangePct { get; init; }
    public string? ConfidenceInterval { get; init; }
    public required string SessionId { get; init; }
    public string WindowsBuild { get; init; } = "";
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? SuggestedEvidenceLevel { get; init; }
    public string? Note { get; init; }
}

public sealed record LocalEvidenceDocument
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<LocalEvidence> Entries { get; init; } = [];
}

/// <summary>Hechos medidos en esta máquina (facts.json). Los consume el profiler como evidencia, nunca como verdad absoluta.</summary>
public sealed record MeasuredFact
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public required string Confidence { get; init; }
    public required string Method { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Note { get; init; }
}

public sealed record FactsDocument
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<MeasuredFact> Facts { get; init; } = [];
    public MeasuredFact? Find(string key) => Facts.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
