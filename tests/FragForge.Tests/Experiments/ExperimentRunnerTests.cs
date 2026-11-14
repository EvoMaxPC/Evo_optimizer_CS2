using FragForge.Core.Abstractions;
using FragForge.Core.Experiments;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FragForge.Tests.Experiments;

public sealed class ExperimentRunnerTests
{
    /// <summary>Captura falsa con la forma que produce la calculadora: peor 1 % a 1000/p1Low ms.</summary>
    private sealed class FakeCapture(double p1Low, int frames = 1200) : IFrameCapture
    {
        public string Name => "Fake";
        public bool Available { get; set; } = true;
        public bool Partial { get; set; }
        public int Calls { get; private set; }

        public Task<CaptureAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Available ? new CaptureAvailability(true, "fake") : CaptureAvailability.Unsupported("fake no disponible"));

        public Task<FrameSeries> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var worstCount = Math.Max(1, frames / 100);
            var worst = 1000.0 / p1Low;
            var rest = Enumerable.Repeat(5.0, frames - worstCount);
            var series = new FrameSeries
            {
                FrameTimesMs = rest.Concat(Enumerable.Repeat(worst, worstCount)).ToArray(),
                PresentModes = Enumerable.Repeat("Independent Flip", frames).ToArray(),
                IsPartial = Partial
            };
            return Task.FromResult(series);
        }

        public FrameSeries ParseCsv(string path) => new();
    }

    private sealed class FakeSensors : ISensorSampler
    {
        public Task<SensorAvailability> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(["cpu"], [], "fake"));
        public Task<SensorSeries> SampleAsync(SensorSamplingRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorSeries());
    }

    private sealed class FakeEnvironment(IReadOnlyList<string>? problems = null) : IExperimentEnvironmentProbe
    {
        public ExperimentEnvironment Environment { get; set; } = new()
        {
            OsBuild = "26200",
            RefreshRateHz = 200,
            MonitorWidth = 1920,
            MonitorHeight = 1080,
            PowerPlanGuid = "plan-1"
        };

        public Task<ExperimentEnvironment> CaptureAsync(HardwareProfile hardware, CancellationToken cancellationToken = default) =>
            Task.FromResult(Environment);

        public IReadOnlyList<string> Validate(ExperimentEnvironment environment) => problems ?? [];
    }

    private sealed class FakeCooldown : ICooldownController
    {
        public int Calls { get; private set; }
        public Task WaitAsync(TimeSpan duration, Func<CancellationToken, Task<double?>> readBaseFrequencyMhz, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Motor falso: no escribe en disco, cuenta llamadas y puede fingir fallos.</summary>
    private sealed class RecordingEngine : ITweakEngine
    {
        public List<string> Calls { get; } = [];
        public bool FailApply { get; set; }
        public bool FailVerification { get; set; }

        public Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) =>
            throw new NotSupportedException();

        public Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
        {
            Calls.Add("apply");
            return Task.FromResult(FailApply
                ? new BatchReport { OperationId = "op", Success = false, Errors = ["no se pudo aplicar"] }
                : new BatchReport { OperationId = "op", Success = true, Results = [new TweakApplyResult { Id = "tweak.one", State = TweakState.Applied, Message = "ok" }] });
        }

        public Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
        {
            Calls.Add("revert");
            return Task.FromResult(new BatchReport { OperationId = "op", Success = true, Results = [new TweakApplyResult { Id = "tweak.one", State = TweakState.NotApplied, Message = "ok" }] });
        }

        public Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TweakStatus(id, FailVerification ? TweakState.PartiallyApplied : (Calls.Count > 0 && Calls[^1] == "apply" ? TweakState.Applied : TweakState.NotApplied), "fake", [], []));

        public Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) =>
            throw new NotSupportedException();
    }

    private sealed record Harness(ExperimentRunner Runner, RecordingEngine Engine, FakeCapture Capture, FakeCooldown Cooldown, ExperimentStore Store);

    private static Harness Build(TempDirectory directory, IExperimentEnvironmentProbe? environment = null, FakeCapture? capture = null)
    {
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new ExperimentStore(paths);
        var engine = new RecordingEngine();
        var fakeCapture = capture ?? new FakeCapture(100);
        var cooldown = new FakeCooldown();
        var catalog = new FakeCatalog("tweak.one");
        var runner = new ExperimentRunner(engine, catalog, new FakeHardwareProfiler(), environment ?? new FakeEnvironment(), fakeCapture, new FakeSensors(), store, cooldown, NullLogger<ExperimentRunner>.Instance);
        return new Harness(runner, engine, fakeCapture, cooldown, store);
    }

    private static ExperimentRequest Request(string sessionId = "test-session", int runs = 2) => new()
    {
        SessionId = sessionId,
        TweakIds = ["tweak.one"],
        RunsPerArm = runs,
        DurationSeconds = 30,
        WarmupSeconds = 0,
        Seed = 1,
        CooldownSeconds = 1
    };

    [Fact]
    public async Task FullRun_AlternatesApplyAndRevert_AndCompletes()
    {
        using var directory = new TempDirectory();
        var harness = Build(directory);
        var result = await harness.Runner.StartAsync(Request(runs: 2));
        Assert.Equal(ExperimentRunOutcome.Completed, result.Outcome);
        Assert.Equal(4, harness.Capture.Calls);
        Assert.Equal(2, harness.Engine.Calls.Count(c => c == "apply"));
        Assert.Equal(2, harness.Engine.Calls.Count(c => c == "revert"));
        var state = await harness.Store.LoadStateAsync("test-session");
        Assert.NotNull(state);
        Assert.True(state!.Completed);
        Assert.Equal(4, state.Runs.Count);
        Assert.NotNull(state.Verdict);
    }

    [Fact]
    public void RunnerNeverTakesIExecutionGate_ByConstruction()
    {
        // El contrato de seguridad de la fase 0.5: el runner no recibe el gate, así que no puede retenerlo.
        var parameters = Assert.Single(typeof(ExperimentRunner).GetConstructors()).GetParameters();
        Assert.DoesNotContain(typeof(IExecutionGate), parameters.Select(p => p.ParameterType));
    }

    [Fact]
    public async Task FailedApply_InvalidatesTheExperiment()
    {
        using var directory = new TempDirectory();
        var harness = Build(directory);
        harness.Engine.FailApply = true;
        var result = await harness.Runner.StartAsync(Request(runs: 1));
        Assert.Equal(ExperimentRunOutcome.Invalid, result.Outcome);
        Assert.True(result.State.Invalidated);
        // Con runs: 1 hay 2 corridas; con Seed = 1 el brazo base va primero, asi que se mide ese brazo y el
        // apply del candidato falla DESPUES. El invariante es que no se captura nada tras el fallo.
        Assert.Equal(result.State.Runs.Count, harness.Capture.Calls);
        Assert.DoesNotContain(result.State.Runs, r => r.Arm == ArmKind.Candidate);
    }

    [Fact]
    public async Task UnverifiedTweakState_InvalidatesTheExperiment()
    {
        // Aplicar "funcionó" pero GetStateAsync no coincide ⇒ se invalida en vez de medir un estado falso.
        using var directory = new TempDirectory();
        var harness = Build(directory);
        harness.Engine.FailVerification = true;
        var result = await harness.Runner.StartAsync(Request(runs: 1));
        Assert.Equal(ExperimentRunOutcome.Invalid, result.Outcome);
        Assert.Contains("Estado inesperado", result.State.InvalidReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnvironmentProblems_AbortBeforeAnyCapture()
    {
        using var directory = new TempDirectory();
        var harness = Build(directory, new FakeEnvironment(["reinicio pendiente"]));
        var result = await harness.Runner.StartAsync(Request(runs: 1));
        Assert.Equal(ExperimentRunOutcome.Invalid, result.Outcome);
        Assert.Equal(0, harness.Capture.Calls);
        Assert.Contains("reinicio pendiente", result.State.InvalidReason ?? string.Empty);
    }

    [Fact]
    public async Task EnvironmentChangeBetweenArms_InvalidatesTheResume()
    {
        using var directory = new TempDirectory();
        var environment = new FakeEnvironment();
        var harness = Build(directory, environment);
        // Simula un experimento a medias: el runner ya guardó el entorno al empezar.
        var plan = new ExperimentPlan
        {
            SessionId = "env-1", TweakIds = ["tweak.one"], Mode = ExperimentMode.Interleaved,
            RunsPerArm = 2, DurationSeconds = 30, WarmupSeconds = 0, Seed = 1, CooldownSeconds = 1
        };
        await harness.Store.SaveStateAsync(new ExperimentState { Plan = plan, Runs = [], Environment = environment.Environment });
        environment.Environment = environment.Environment with { RefreshRateHz = 60 };
        var result = await harness.Runner.ResumeAsync("env-1", Request("env-1"));
        Assert.Equal(ExperimentRunOutcome.Invalid, result.Outcome);
        Assert.Contains("entorno cambió", result.State.InvalidReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PartialCapture_IsMarkedInvalidRun_AndVerdictIsInconclusive()
    {
        using var directory = new TempDirectory();
        var harness = Build(directory, capture: new FakeCapture(100) { Partial = true });
        var result = await harness.Runner.StartAsync(Request(runs: 1));
        Assert.Equal(ExperimentRunOutcome.Completed, result.Outcome);
        Assert.All(result.State.Runs, run => Assert.False(run.IsValid));
        Assert.Equal(ExperimentVerdictKind.Inconclusive, result.State.Verdict!.Kind);
    }

    [Fact]
    public async Task UnavailableCapture_ProducesInvalidRuns_InsteadOfZeros()
    {
        using var directory = new TempDirectory();
        var capture = new FakeCapture(100) { Available = false };
        var harness = Build(directory, capture: capture);
        var result = await harness.Runner.StartAsync(Request(runs: 1));
        Assert.All(result.State.Runs, run => Assert.Null(run.Metrics));
        Assert.All(result.State.Runs, run => Assert.False(run.IsValid));
        Assert.Equal(0, capture.Calls);
    }

    [Fact]
    public async Task ResumeAfterCompletion_IsIdempotent()
    {
        using var directory = new TempDirectory();
        var harness = Build(directory);
        await harness.Runner.StartAsync(Request(runs: 1));
        var calls = harness.Capture.Calls;
        var resumed = await harness.Runner.ResumeAsync("test-session", Request(runs: 1));
        Assert.Equal(ExperimentRunOutcome.Completed, resumed.Outcome);
        Assert.Equal(calls, harness.Capture.Calls);
    }

    [Fact]
    public async Task ResumeAfterInterruption_ContinuesFromPersistedRunCount()
    {
        using var directory = new TempDirectory();
        var harness = Build(directory);
        var environment = ((FakeEnvironment)new FakeEnvironment()).Environment;
        var plan = new ExperimentPlan
        {
            SessionId = "resume-1", TweakIds = ["tweak.one"], Mode = ExperimentMode.Interleaved,
            RunsPerArm = 2, DurationSeconds = 30, WarmupSeconds = 0, Seed = 1, CooldownSeconds = 1
        };
        await harness.Store.SaveStateAsync(new ExperimentState
        {
            Plan = plan,
            Environment = environment,
            Runs = [new RunResult { RunId = "resume-1-run01", Arm = ArmKind.Baseline, Index = 0, IsValid = true, Metrics = new RunMetrics { P1Low = 100, AvgFps = 200, JitterMs = 1, FrameTimeP50 = 5 } }]
        });
        var result = await harness.Runner.ResumeAsync("resume-1", Request("resume-1"));
        Assert.Equal(ExperimentRunOutcome.Completed, result.Outcome);
        Assert.Equal(4, result.State.Runs.Count);
    }

    [Fact]
    public async Task CooldownIsAppliedBetweenRuns_NotBeforeTheFirst()
    {
        using var directory = new TempDirectory();
        var harness = Build(directory);
        await harness.Runner.StartAsync(Request(runs: 2));
        Assert.Equal(3, harness.Cooldown.Calls);
    }

    [Fact]
    public async Task InvalidSessionId_IsRejectedBeforeAnyMeasurement()
    {
        using var directory = new TempDirectory();
        var harness = Build(directory);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Runner.StartAsync(Request("../escape")));
        Assert.Equal(0, harness.Capture.Calls);
    }
}
