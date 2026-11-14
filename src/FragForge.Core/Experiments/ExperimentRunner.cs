using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using Microsoft.Extensions.Logging;

namespace FragForge.Core.Experiments;

/// <summary>Dependencias de entorno que el runner necesita y que se inyectan para poder simularlas en tests.</summary>
public interface IExperimentEnvironmentProbe
{
    Task<ExperimentEnvironment> CaptureAsync(HardwareProfile hardware, CancellationToken cancellationToken = default);
    IReadOnlyList<string> Validate(ExperimentEnvironment environment);
}

/// <summary>Espera entre corridas. En producción duerme; en tests avanza un reloj falso.</summary>
public interface ICooldownController
{
    Task WaitAsync(TimeSpan duration, Func<CancellationToken, Task<double?>> readBaseFrequencyMhz, CancellationToken cancellationToken = default);
}

/// <summary>
/// Espera de enfriamiento real. Consulta la frecuencia base mientras espera para forzar una lectura del
/// sensor y que el driver refresque su ventana; el valor no se usa para decidir nada, solo se sondea.
/// </summary>
public sealed class TimeCooldownController : ICooldownController
{
    public async Task WaitAsync(TimeSpan duration, Func<CancellationToken, Task<double?>> readBaseFrequencyMhz, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await readBaseFrequencyMhz(cancellationToken);
        }
    }
}

public sealed record ExperimentRequest
{
    public required string SessionId { get; init; }
    public required IReadOnlyList<string> TweakIds { get; init; }
    public int RunsPerArm { get; init; } = 3;
    public double DurationSeconds { get; init; } = 120;
    public double WarmupSeconds { get; init; } = 10;
    public int Seed { get; init; } = 20260101;
    public ExperimentMode Mode { get; init; } = ExperimentMode.Interleaved;
    public bool AaCalibration { get; init; }
    public int CooldownSeconds { get; init; } = 30;
    public string? Label { get; init; }
    public string? Cs2ExecutableOverride { get; init; }
    public string? TargetUserSid { get; init; }
    public bool AllowPowerShellActions { get; init; }
    public TweakProfile Profile { get; init; } = TweakProfile.Advanced;
}

public enum ExperimentRunOutcome { Completed, Invalid, NeedsResume, Aborted }

public sealed record ExperimentResult(ExperimentRunOutcome Outcome, ExperimentState State, string Message)
{
    public bool Success => Outcome is ExperimentRunOutcome.Completed or ExperimentRunOutcome.NeedsResume;
}

/// <summary>
/// Orquestador A/B.
///
/// INVARIANTE CRÍTICO: el runner NUNCA mantiene el <see cref="IExecutionGate"/> del motor durante una medición.
/// Llama a <see cref="ITweakEngine.ApplyAsync"/> y <see cref="ITweakEngine.RevertAsync"/>, cada uno toma y suelta
/// el gate por su cuenta, y mide ENTRE llamadas. Así una medición larga no bloquea al motor y el motor nunca
/// queda bloqueado por un proceso de captura que puede tardar minutos.
/// </summary>
public sealed class ExperimentRunner(
    ITweakEngine engine,
    ITweakCatalog catalog,
    IHardwareProfiler profiler,
    IExperimentEnvironmentProbe environment,
    IFrameCapture capture,
    ISensorSampler sensors,
    ExperimentStore store,
    ICooldownController cooldown,
    ILogger<ExperimentRunner> logger)
{
    public async Task<ExperimentResult> StartAsync(ExperimentRequest request, CancellationToken cancellationToken = default)
    {
        var plan = new ExperimentPlan
        {
            SessionId = ExperimentStore.ValidateId(request.SessionId),
            TweakIds = request.TweakIds,
            Mode = request.Mode,
            RunsPerArm = Math.Clamp(request.RunsPerArm, 1, 20),
            DurationSeconds = Math.Clamp(request.DurationSeconds, 10, 1800),
            WarmupSeconds = Math.Clamp(request.WarmupSeconds, 0, 120),
            Seed = request.Seed,
            IsAaCalibration = request.AaCalibration,
            CooldownSeconds = request.CooldownSeconds,
            Label = request.Label,
            RequiredReboots = request.TweakIds.Sum(id => catalog.TryGet(id, out var definition) && definition!.RequiresReboot ? 1 : 0)
        };
        var state = new ExperimentState { Plan = plan, Runs = [], Log = [$"{DateTimeOffset.UtcNow:O} inicio"] };
        await store.SaveStateAsync(state, cancellationToken);
        return await ExecuteAsync(state, request, cancellationToken);
    }

    public async Task<ExperimentResult> ResumeAsync(string sessionId, ExperimentRequest request, CancellationToken cancellationToken = default)
    {
        var state = await store.LoadStateAsync(sessionId, cancellationToken) ?? throw new FragForgeException("experiment_not_found", $"No existe el experimento '{sessionId}'.");
        if (state.Completed) return new ExperimentResult(ExperimentRunOutcome.Completed, state, "El experimento ya está completado.");
        if (state.Invalidated) return new ExperimentResult(ExperimentRunOutcome.Invalid, state, state.InvalidReason ?? "Experimento invalidado.");
        return await ExecuteAsync(state, request, cancellationToken);
    }

    private async Task<ExperimentResult> ExecuteAsync(ExperimentState state, ExperimentRequest request, CancellationToken cancellationToken)
    {
        var hardware = await profiler.ProfileAsync(cancellationToken);
        var current = await environment.CaptureAsync(hardware, cancellationToken);
        var problems = environment.Validate(current);
        if (problems.Count > 0)
        {
            var invalid = state with { Invalidated = true, InvalidReason = string.Join(" ", problems), Environment = current, Log = [.. state.Log, $"{DateTimeOffset.UtcNow:O} precheck falló: {string.Join("; ", problems)}"] };
            await store.SaveStateAsync(invalid, cancellationToken);
            return new ExperimentResult(ExperimentRunOutcome.Invalid, invalid, "Precheck de medición fallido: " + string.Join(" ", problems));
        }

        if (state.Environment is not null && !state.Environment.ComparabilityKeys().SequenceEqual(current.ComparabilityKeys()))
        {
            var invalid = state with { Invalidated = true, InvalidReason = "El entorno cambió respecto al inicio del experimento.", Environment = current };
            await store.SaveStateAsync(invalid, cancellationToken);
            return new ExperimentResult(ExperimentRunOutcome.Invalid, invalid, "El entorno cambió: las corridas no son comparables.");
        }

        var schedule = state.Plan.BuildSchedule();
        var runs = state.Runs.ToList();
        state = state with { Environment = state.Environment ?? current };

        for (var index = runs.Count; index < schedule.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arm = schedule[index];

            // Garantiza el estado del sistema ANTES de medir. Verifica TweakState; si no coincide, invalida.
            var ensure = await EnsureArmStateAsync(arm, request, cancellationToken);
            if (ensure is not null)
            {
                var invalid = state with { Runs = runs, Invalidated = true, InvalidReason = ensure, Log = [.. state.Log, $"{DateTimeOffset.UtcNow:O} {ensure}"] };
                await store.SaveStateAsync(invalid, cancellationToken);
                return new ExperimentResult(ExperimentRunOutcome.Invalid, invalid, ensure);
            }
            state = arm == ArmKind.Candidate ? state with { CandidateApplied = true } : state with { BaselineApplied = true };

            if (runs.Count > 0) await cooldown.WaitAsync(TimeSpan.FromSeconds(state.Plan.CooldownSeconds ?? 30), _ => Task.FromResult<double?>(null), cancellationToken);

            var run = await RunOnceAsync(state, arm, index, cancellationToken);
            runs.Add(run);
            state = state with { Runs = runs, Log = [.. state.Log, $"{DateTimeOffset.UtcNow:O} corrida {index + 1}/{schedule.Count} brazo={arm} válida={run.IsValid}"] };
            await store.SaveStateAsync(state, cancellationToken);
        }

        var verdict = VerdictCalculator.Evaluate(state.Arm(ArmKind.Baseline), state.Arm(ArmKind.Candidate), NoiseFloor.ConservativeDefault);
        var final = state with { Runs = runs, Verdict = verdict, Completed = true, Log = [.. state.Log, $"{DateTimeOffset.UtcNow:O} veredicto {verdict.Kind} ({verdict.Confidence})"] };
        await store.SaveStateAsync(final, cancellationToken);
        return new ExperimentResult(ExperimentRunOutcome.Completed, final, $"Veredicto: {verdict.Kind} ({verdict.Confidence}).");
    }

    /// <summary>
    /// Deja el sistema en el estado del brazo. Devuelve un mensaje si NO se pudo verificar, o null si está listo.
    /// Aplica: brazo candidato ⇒ ApplyAsync(ids). Brazo base ⇒ RevertAsync(ids). Nunca retiene el gate.
    /// </summary>
    private async Task<string?> EnsureArmStateAsync(ArmKind arm, ExperimentRequest request, CancellationToken cancellationToken)
    {
        var options = new ApplyOptions
        {
            Profile = request.Profile,
            CreateRestorePoint = true,
            Cs2ExecutableOverride = request.Cs2ExecutableOverride,
            TargetUserSid = request.TargetUserSid,
            AllowPowerShellActions = request.AllowPowerShellActions
        };
        BatchReport report;
        if (arm == ArmKind.Candidate)
        {
            report = await engine.ApplyAsync(request.TweakIds, options, cancellationToken);
            var acceptable = report.Results.Count > 0 && report.Results.All(r => r.State is TweakState.Applied or TweakState.AppliedExternally or TweakState.Inspected);
            if (!report.Success && !acceptable)
                return "No se pudo aplicar el tweak para el brazo candidato: " + string.Join(" ", report.Errors);
        }
        else
        {
            var revert = new RevertRequest { Ids = request.TweakIds, Options = new RevertOptions { Force = false, Cs2ExecutableOverride = request.Cs2ExecutableOverride, TargetUserSid = request.TargetUserSid } };
            report = await engine.RevertAsync(revert, cancellationToken);
            // Revertir un tweak que nunca se aplicó es un no-op legítimo en la primera corrida del brazo base.
            if (!report.Success && report.Errors.Any(e => !e.Contains("no está aplicado", StringComparison.OrdinalIgnoreCase)))
                logger.LogWarning("Revert del brazo base devolvió errores: {Errors}", string.Join(" ", report.Errors));
        }

        foreach (var id in request.TweakIds)
        {
            var status = await engine.GetStateAsync(id, cancellationToken);
            var expected = arm == ArmKind.Candidate;
            var matches = expected
                ? status.State is TweakState.Applied or TweakState.AppliedExternally or TweakState.Inspected
                : status.State is TweakState.NotApplied or TweakState.NotApplicable or TweakState.Inspected;
            if (!matches) return $"Estado inesperado tras preparar el brazo {arm} para '{id}': {status.State}. Se invalida la medición.";
        }
        return null;
    }

    private async Task<RunResult> RunOnceAsync(ExperimentState state, ArmKind arm, int index, CancellationToken cancellationToken)
    {
        var runId = $"{state.Plan.SessionId}-run{index + 1:00}";
        var runsDirectory = store.GetRunsDirectory(state.Plan.SessionId);
        Directory.CreateDirectory(runsDirectory);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var availability = await capture.CheckAvailabilityAsync(cancellationToken);
            if (!availability.IsAvailable)
                return new RunResult { RunId = runId, Arm = arm, Index = index, IsValid = false, InvalidReason = availability.Reason, StartedAt = started };

            var captureRequest = new CaptureRequest
            {
                ProcessName = "cs2",
                OutputDirectory = runsDirectory,
                RunId = runId,
                Duration = TimeSpan.FromSeconds(state.Plan.DurationSeconds)
            };
            var series = await capture.CaptureAsync(captureRequest, cancellationToken);
            var sensorSeries = await sensors.SampleAsync(new SensorSamplingRequest { OutputDirectory = runsDirectory, RunId = runId, Duration = TimeSpan.FromSeconds(state.Plan.DurationSeconds) }, cancellationToken);

            if (series.IsPartial) return new RunResult { RunId = runId, Arm = arm, Index = index, IsValid = false, InvalidReason = "Captura parcial (cancelada).", StartedAt = started, FinishedAt = DateTimeOffset.UtcNow };
            if (series.FrameCount < 300) return new RunResult { RunId = runId, Arm = arm, Index = index, IsValid = false, InvalidReason = $"Solo {series.FrameCount} frames; mínimo 300.", StartedAt = started, FinishedAt = DateTimeOffset.UtcNow };

            var metrics = FrameMetricsCalculator.Compute(series, state.Plan.WarmupSeconds);
            var validity = CaptureValidityGate.Evaluate(series, TimeSpan.FromSeconds(state.Plan.DurationSeconds), state.Environment?.RefreshRateHz);
            return new RunResult
            {
                RunId = runId,
                Arm = arm,
                Index = index,
                Metrics = metrics,
                Validity = validity,
                IsValid = metrics.AvgFps is not null && validity.IsUsable,
                InvalidReason = metrics.AvgFps is null ? "Sin frametimes utilizables." : validity.IsUsable ? null : string.Join("; ", validity.Reasons),
                DurationSeconds = state.Plan.DurationSeconds,
                CsvPath = Path.Combine("runs", $"{runId}.csv"),
                SensorCsvPath = sensorSeries.CsvPath is null ? null : Path.Combine("runs", Path.GetFileName(sensorSeries.CsvPath)),
                StartedAt = started,
                FinishedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string> { ["frames"] = series.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture), ["presentMode"] = metrics.PresentModePrimary ?? "Unknown" }
            };
        }
        catch (OperationCanceledException)
        {
            return new RunResult { RunId = runId, Arm = arm, Index = index, IsValid = false, InvalidReason = "Cancelado por el usuario.", StartedAt = started, FinishedAt = DateTimeOffset.UtcNow };
        }
    }
}
