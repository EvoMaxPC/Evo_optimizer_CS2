using System.Net;
using FragForge.Actions.Measurement;
using FragForge.Core.Experiments;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Experiments;

public sealed class ReportRendererTests
{
    private static ExperimentState State(string label = "sin <script>alert('x')</script>") => new()
    {
        Plan = new ExperimentPlan
        {
            SessionId = "report-1",
            TweakIds = ["tweak.one"],
            Mode = ExperimentMode.Interleaved,
            RunsPerArm = 1,
            DurationSeconds = 60,
            WarmupSeconds = 5,
            Seed = 1,
            Label = label
        },
        Environment = new ExperimentEnvironment { OsBuild = "26200", RefreshRateHz = 200, MonitorWidth = 1920, MonitorHeight = 1080 },
        Runs =
        [
            new RunResult { RunId = "a", Arm = ArmKind.Baseline, Index = 0, IsValid = true, Metrics = new RunMetrics { AvgFps = 190, P1Low = 100, FrameTimeP50 = 5.2, FrameTimeP99 = 10, JitterMs = 4.8 } },
            new RunResult { RunId = "b", Arm = ArmKind.Candidate, Index = 1, IsValid = true, Metrics = new RunMetrics { AvgFps = 195, P1Low = 120, FrameTimeP50 = 5.1, FrameTimeP99 = 8, JitterMs = 2.9 } }
        ],
        Verdict = new ExperimentVerdict(ExperimentVerdictKind.Improvement, VerdictConfidence.Indicative, ["p1Low mejora 20 %"], null, VerdictConfidence.Indicative, null, null, null)
    };

    [Fact]
    public void Html_EncodesEveryUserSuppliedString()
    {
        var html = ExperimentReportRenderer.Render(State());
        Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_IsSelfContained_NoScriptsNoCdnNoLinks()
    {
        var html = ExperimentReportRenderer.Render(State());
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<svg", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_MarksFrameBudgetAsReferenceOnly()
    {
        var html = ExperimentReportRenderer.Render(State());
        Assert.Contains("5.00", html, StringComparison.Ordinal);
        Assert.Contains("referencia", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_ShowsBannerWhenNotConfirmed()
    {
        Assert.Contains("no confirmado", ExperimentReportRenderer.Render(State()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_ConfirmedVerdict_HasNoWarnBanner()
    {
        var state = State() with { Verdict = new ExperimentVerdict(ExperimentVerdictKind.Improvement, VerdictConfidence.Confirmed, [], null, null, null, null, null) };
        Assert.DoesNotContain("no confirmado", ExperimentReportRenderer.Render(state), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_InvalidatedExperiment_ShowsReason()
    {
        var state = State() with { Invalidated = true, InvalidReason = "reinicio pendiente", Verdict = null };
        Assert.Contains("reinicio pendiente", ExperimentReportRenderer.Render(state), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_NeverEmitsRawAmpersandsFromUserText()
    {
        var html = ExperimentReportRenderer.Render(State("A & B < C > D \"quoted\""));
        Assert.Contains(WebUtility.HtmlEncode("A & B < C > D \"quoted\""), html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_RendersWithoutMetrics_WhenThereAreNoRuns()
    {
        var state = State() with { Runs = [], Verdict = null };
        var html = ExperimentReportRenderer.Render(state);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
    }
}

public sealed class LocalEvidenceTests
{
    private static ExperimentState State(string sessionId, string tweakId, ExperimentVerdictKind kind, VerdictConfidence confidence) => new()
    {
        Plan = new ExperimentPlan
        {
            SessionId = sessionId,
            TweakIds = [tweakId],
            Mode = ExperimentMode.Interleaved,
            RunsPerArm = 5,
            DurationSeconds = 60,
            WarmupSeconds = 5,
            Seed = 1
        },
        Environment = new ExperimentEnvironment { OsBuild = "26200" },
        Runs = [],
        Verdict = new ExperimentVerdict(kind, confidence, [], new EffectEstimate(
            12.5,
            1.0,
            -3.0,
            new BootstrapResult(1.2, 0.4, 2.0, 2000, 1, false),
            new BootstrapResult(0.1, -0.2, 0.4, 2000, 1, false),
            new PermutationResult(0.01, 20, 1.2, true),
            0.7,
            5,
            5), null, null, null, null)
    };

    [Fact]
    public async Task ImprovementConfirmed_SuggestsHighEvidenceWithBuildAndDate()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new LocalEvidenceStore(paths);
        await store.RecordAsync(State("ev-1", "latency.game-mode-on", ExperimentVerdictKind.Improvement, VerdictConfidence.Confirmed));
        var record = Assert.Single(await store.ForTweakAsync("latency.game-mode-on"));
        Assert.Equal("High", record.SuggestedEvidenceLevel);
        Assert.Equal("26200", record.WindowsBuild);
        Assert.Equal("ev-1", record.SessionId);
        Assert.Contains("no se modifica", record.Note ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Regression_SuggestsExperimental_NotPromotion()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new LocalEvidenceStore(paths);
        await store.RecordAsync(State("ev-2", "tweak.x", ExperimentVerdictKind.Regression, VerdictConfidence.Confirmed));
        Assert.Equal("Experimental", Assert.Single(await store.ForTweakAsync("tweak.x")).SuggestedEvidenceLevel);
    }

    [Fact]
    public async Task ConfirmedRequiresBothHighVerdictAndConfidence()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new LocalEvidenceStore(paths);
        await store.RecordAsync(State("ev-3", "tweak.y", ExperimentVerdictKind.Improvement, VerdictConfidence.Indicative));
        Assert.Equal("Medium", Assert.Single(await store.ForTweakAsync("tweak.y")).SuggestedEvidenceLevel);
    }

    [Fact]
    public async Task InconclusiveOrUnknown_DoesNotPromoteAnything()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new LocalEvidenceStore(paths);
        await store.RecordAsync(State("ev-4", "tweak.z", ExperimentVerdictKind.Inconclusive, VerdictConfidence.Indicative));
        Assert.Null(Assert.Single(await store.ForTweakAsync("tweak.z")).SuggestedEvidenceLevel);
    }

    [Fact]
    public async Task SameSessionIsRewritten_NotDuplicated()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new LocalEvidenceStore(paths);
        await store.RecordAsync(State("ev-5", "tweak.w", ExperimentVerdictKind.Improvement, VerdictConfidence.Confirmed));
        await store.RecordAsync(State("ev-5", "tweak.w", ExperimentVerdictKind.Regression, VerdictConfidence.Confirmed));
        Assert.Equal(ExperimentVerdictKind.Regression, Assert.Single(await store.ForTweakAsync("tweak.w")).Verdict);
    }
}
