using FragForge.Core.Experiments;
using Xunit;

namespace FragForge.Tests.Experiments;

public sealed class VerdictTests
{
    private static RunResult MakeRun(ArmKind arm, int index, double p1Low, double avgFps, double jitter) => new()
    {
        RunId = $"{arm}-{index}",
        Arm = arm,
        Index = index,
        IsValid = true,
        Metrics = new RunMetrics { P1Low = p1Low, AvgFps = avgFps, JitterMs = jitter, FrameTimeP50 = 1000.0 / avgFps }
    };

    private static ExperimentArm Arm(ArmKind kind, params (double P1, double Fps, double Jitter)[] values) => new()
    {
        Kind = kind,
        Runs = values.Select((v, i) => MakeRun(kind, i, v.P1, v.Fps, v.Jitter)).ToArray()
    };

    private static readonly NoiseFloor Estimated = new(3.0, 1.5, 3.0, 5, true);
    private static readonly NoiseFloor Unknown = NoiseFloor.Unknown;

    [Fact]
    public void ClearP1LowImprovement_IsClassifiedAsImprovement()
    {
        var baseline = Arm(ArmKind.Baseline, (100, 200, 1.0), (101, 199, 1.1), (99, 201, 0.9), (100.5, 200, 1.0), (100.2, 200.5, 1.05));
        var candidate = Arm(ArmKind.Candidate, (120, 200, 1.0), (121, 199, 1.1), (119, 201, 0.9), (120.5, 200, 1.0), (120.2, 200.5, 1.05));
        var verdict = VerdictCalculator.Evaluate(baseline, candidate, Estimated);
        Assert.Equal(ExperimentVerdictKind.Improvement, verdict.Kind);
        Assert.NotNull(verdict.Effect);
        Assert.True(verdict.Effect!.P1LowChangePct > 15);
    }

    [Fact]
    public void ClearP1LowRegression_IsClassifiedAsRegression()
    {
        var baseline = Arm(ArmKind.Baseline, (120, 200, 1.0), (121, 199, 1.1), (119, 201, 0.9), (120.5, 200, 1.0), (120.2, 200.5, 1.05));
        var candidate = Arm(ArmKind.Candidate, (100, 200, 1.0), (101, 199, 1.1), (99, 201, 0.9), (100.5, 200, 1.0), (100.2, 200.5, 1.05));
        Assert.Equal(ExperimentVerdictKind.Regression, VerdictCalculator.Evaluate(baseline, candidate, Estimated).Kind);
    }

    [Fact]
    public void JitterRegression_Wins_overAvgFpsImprovement()
    {
        // avgFps sube un 20 % pero el jitter empeora un 40 %: debe primar la regresión.
        var baseline = Arm(ArmKind.Baseline, (100, 150, 1.0), (101, 151, 1.0), (99, 149, 1.0), (100, 150, 1.0), (100, 150, 1.0));
        var candidate = Arm(ArmKind.Candidate, (100, 180, 1.5), (101, 181, 1.5), (99, 179, 1.5), (100, 180, 1.5), (100, 180, 1.5));
        Assert.Equal(ExperimentVerdictKind.Regression, VerdictCalculator.Evaluate(baseline, candidate, Estimated).Kind);
    }

    [Fact]
    public void EverythingWithinTheFloor_IsNoDifference()
    {
        var baseline = Arm(ArmKind.Baseline, (100.0, 200, 1.00), (100.1, 200.1, 1.01), (99.9, 199.9, 0.99), (100.05, 200.05, 1.0), (100.02, 200.02, 1.005));
        var candidate = Arm(ArmKind.Candidate, (100.2, 200.2, 1.01), (100.3, 200.3, 1.02), (100.1, 200.1, 1.0), (100.25, 200.25, 1.01), (100.22, 200.22, 1.015));
        Assert.Equal(ExperimentVerdictKind.NoDifference, VerdictCalculator.Evaluate(baseline, candidate, Estimated).Kind);
    }

    [Fact]
    public void EquivalenceWithOnlyTwoRunsPerArm_IsNotClaimed()
    {
        // El efecto es minúsculo, pero con 2 vs 2 no se puede afirmar equivalencia: sería vender aire.
        var baseline = Arm(ArmKind.Baseline, (100.0, 200, 1.0), (100.1, 200.1, 1.0));
        var candidate = Arm(ArmKind.Candidate, (100.2, 200.2, 1.0), (100.3, 200.3, 1.0));
        var verdict = VerdictCalculator.Evaluate(baseline, candidate, Estimated);
        Assert.Equal(ExperimentVerdictKind.Inconclusive, verdict.Kind);
        Assert.Contains(verdict.Reasons, reason => reason.Contains("equivalencia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidRun_ForcesInconclusive()
    {
        var baseline = new ExperimentArm { Kind = ArmKind.Baseline, Runs = [MakeRun(ArmKind.Baseline, 0, 100, 200, 1)] };
        var candidate = new ExperimentArm
        {
            Kind = ArmKind.Candidate,
            Runs = [MakeRun(ArmKind.Candidate, 0, 130, 200, 1), new RunResult { RunId = "bad", Arm = ArmKind.Candidate, Index = 1, IsValid = false, InvalidReason = "parcial" }]
        };
        Assert.Equal(ExperimentVerdictKind.Inconclusive, VerdictCalculator.Evaluate(baseline, candidate, Estimated).Kind);
    }

    [Fact]
    public void FewRuns_CapConfidenceAtIndicative()
    {
        var baseline = Arm(ArmKind.Baseline, (100, 200, 1), (101, 199, 1), (99, 201, 1));
        var candidate = Arm(ArmKind.Candidate, (140, 200, 1), (141, 199, 1), (139, 201, 1));
        var verdict = VerdictCalculator.Evaluate(baseline, candidate, new NoiseFloor(3, 1.5, 3, 3, true));
        Assert.Equal(ExperimentVerdictKind.Improvement, verdict.Kind);
        Assert.Equal(VerdictConfidence.Indicative, verdict.Confidence);
    }

    [Fact]
    public void WithoutAaCalibration_ConfidenceNeverConfirmed_AndUsesConservativeFloor()
    {
        var baseline = Arm(ArmKind.Baseline, (100, 200, 1), (101, 199, 1), (99, 201, 1), (100, 200, 1), (100, 200, 1));
        var candidate = Arm(ArmKind.Candidate, (103, 200, 1), (104, 199, 1), (102, 201, 1), (103, 200, 1), (103, 200, 1));
        var verdict = VerdictCalculator.Evaluate(baseline, candidate, Unknown);
        // 3 % de mejora no supera el piso conservador de 5 %.
        Assert.NotEqual(ExperimentVerdictKind.Improvement, verdict.Kind);
        Assert.Contains(verdict.Reasons, r => r.Contains("conservador", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImprovementWithWorseAvgFps_IsInconclusiveNotImprovement()
    {
        // p1Low mejora 20 % pero avgFps cae 10 %: intercambio, no mejora limpia.
        var baseline = Arm(ArmKind.Baseline, (100, 200, 1.0), (101, 200, 1.0), (99, 200, 1.0), (100, 200, 1.0), (100, 200, 1.0));
        var candidate = Arm(ArmKind.Candidate, (120, 180, 1.0), (121, 180, 1.0), (119, 180, 1.0), (120, 180, 1.0), (120, 180, 1.0));
        Assert.Equal(ExperimentVerdictKind.Inconclusive, VerdictCalculator.Evaluate(baseline, candidate, Estimated).Kind);
    }

    [Fact]
    public void PercentChange_IsSymmetricAndNullSafe()
    {
        Assert.Equal(20.0, VerdictCalculator.PercentChange(120, 100)!.Value, 6);
        Assert.Equal(-20.0, VerdictCalculator.PercentChange(80, 100)!.Value, 6);
        Assert.Null(VerdictCalculator.PercentChange(null, 100));
        Assert.Null(VerdictCalculator.PercentChange(1, 0));
    }
}
