using System.Text.Json;
using FragForge.Core.Experiments;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Experiments;

public sealed class ExperimentPlanTests
{
    private static ExperimentPlan Plan(int runs = 3, ExperimentMode mode = ExperimentMode.Interleaved, int seed = 42) => new()
    {
        SessionId = "session-1",
        TweakIds = ["tweak.one"],
        Mode = mode,
        RunsPerArm = runs,
        DurationSeconds = 120,
        WarmupSeconds = 10,
        Seed = seed
    };

    [Fact]
    public void InterleavedSchedule_AlternatesAndHasEqualArms()
    {
        var schedule = Plan(runs: 4).BuildSchedule();
        Assert.Equal(8, schedule.Count);
        Assert.Equal(4, schedule.Count(arm => arm == ArmKind.Baseline));
        Assert.Equal(4, schedule.Count(arm => arm == ArmKind.Candidate));
        for (var index = 1; index < schedule.Count; index++) Assert.NotEqual(schedule[index - 1], schedule[index]);
    }

    [Fact]
    public void Schedule_IsDeterministicForSameSeed()
    {
        Assert.Equal(Plan(seed: 7).BuildSchedule(), Plan(seed: 7).BuildSchedule());
    }

    [Fact]
    public void BlockedMode_GroupsArmsTogether_AndWarnsThroughPlan()
    {
        var schedule = Plan(runs: 3, mode: ExperimentMode.Blocked).BuildSchedule();
        // xUnit2013: el tamaño 1 se comprueba con Assert.Single, que ademas dice CUAL fue el unico
        // elemento cuando falla (con Assert.Equal solo se veia "1 != 2").
        Assert.Single(schedule.Take(3).Distinct());
        Assert.Single(schedule.Skip(3).Distinct());
        Assert.NotEqual(schedule[0], schedule[3]);
    }

    [Fact]
    public void RequiredReboots_ComesFromTheCatalog_NotFromAGuess()
    {
        var plan = Plan();
        Assert.Equal(0, plan.RequiredReboots);
        Assert.False(plan.RequiresReboot);
    }

    [Fact]
    public void Arm_Metric_IgnoresInvalidRuns()
    {
        var arm = new ExperimentArm
        {
            Kind = ArmKind.Baseline,
            Runs =
            [
                new RunResult { RunId = "a", Arm = ArmKind.Baseline, Index = 0, IsValid = true, Metrics = new RunMetrics { P1Low = 100 } },
                new RunResult { RunId = "b", Arm = ArmKind.Baseline, Index = 1, IsValid = false, Metrics = new RunMetrics { P1Low = 0 } }
            ]
        };
        Assert.Equal([100.0], arm.Metric(metrics => metrics.P1Low));
        Assert.False(arm.IsValid);
    }
}

public sealed class ExperimentStoreTests
{
    private static ExperimentPlan Plan(string sessionId = "abc-1") => new()
    {
        SessionId = sessionId,
        TweakIds = ["tweak.one"],
        Mode = ExperimentMode.Interleaved,
        RunsPerArm = 2,
        DurationSeconds = 60,
        WarmupSeconds = 5,
        Seed = 1,
        Label = "prueba"
    };

    [Fact]
    public async Task SaveAndLoad_RoundTripsThroughStrictJson()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new ExperimentStore(paths);
        await store.SaveStateAsync(new ExperimentState { Plan = Plan(), Runs = [] });
        var loaded = await store.LoadStateAsync("abc-1");
        Assert.NotNull(loaded);
        Assert.Equal("abc-1", loaded!.Plan.SessionId);
        Assert.Equal(2, loaded.Plan.RunsPerArm);
        Assert.True(File.Exists(store.GetStatePath("abc-1")));
        Assert.True(Directory.Exists(store.GetRunsDirectory("abc-1")));
    }

    [Fact]
    public async Task LoadMissing_ReturnsNull()
    {
        using var directory = new TempDirectory();
        var store = new ExperimentStore(new FragForgePaths(directory.Path));
        Assert.Null(await store.LoadStateAsync("nope"));
    }

    [Fact]
    public void InvalidSessionId_IsRejected()
    {
        using var directory = new TempDirectory();
        var store = new ExperimentStore(new FragForgePaths(directory.Path));
        Assert.Throws<ArgumentException>(() => store.GetSessionDirectory("../../etc"));
        Assert.Throws<ArgumentException>(() => store.GetSessionDirectory("con espacio"));
        Assert.Throws<ArgumentException>(() => store.GetSessionDirectory(new string('a', 81)));
        Assert.Throws<ArgumentException>(() => store.GetSessionDirectory(""));
    }

    [Fact]
    public async Task UnknownJsonMember_IsRejectedByDisallowPolicy()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new ExperimentStore(paths);
        Directory.CreateDirectory(store.GetSessionDirectory("strict"));
        await File.WriteAllTextAsync(store.GetStatePath("strict"), """
            {
              "schemaVersion": 1,
              "plan": { "schemaVersion": 1, "sessionId": "strict", "tweakIds": [], "mode": "Interleaved", "runsPerArm": 1, "durationSeconds": 60, "warmupSeconds": 5, "seed": 1 },
              "runs": [],
              "campoDesconocido": true
            }
            """);
        await Assert.ThrowsAsync<JsonException>(() => store.LoadStateAsync("strict"));
    }

    [Fact]
    public async Task List_SkipsInvalidDirectoriesAndReturnsSummaries()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new ExperimentStore(paths);
        Directory.CreateDirectory(Path.Combine(paths.Experiments, "no valido"));
        await store.SaveStateAsync(new ExperimentState { Plan = Plan("ok-1"), Runs = [] });
        var items = await store.ListAsync();
        Assert.Single(items);
        Assert.Equal("prueba", items[0].Label);
        Assert.Equal("ok-1", items[0].SessionId);
    }

    [Fact]
    public async Task Atomically_WrittenFile_LeavesNoTempFilesBehind()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new ExperimentStore(paths);
        await store.SaveStateAsync(new ExperimentState { Plan = Plan(), Runs = [] });
        var files = Directory.GetFiles(store.GetSessionDirectory("abc-1"));
        Assert.Single(files);
        Assert.EndsWith("session.json", files[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListArtifacts_ReportsRelativePathsWithContentTypes()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new ExperimentStore(paths);
        await store.SaveStateAsync(new ExperimentState { Plan = Plan(), Runs = [] });
        await File.WriteAllTextAsync(Path.Combine(store.GetRunsDirectory("abc-1"), "run-01.csv"), "FrameTime\n5\n");
        var artifacts = await store.ListArtifactsAsync("abc-1");
        Assert.Contains(artifacts, artifact => artifact.RelativePath == "runs/run-01.csv" && artifact.ContentType == "text/csv");
        Assert.Contains(artifacts, artifact => artifact.Name == "session.json" && artifact.ContentType == "application/json");
    }
}

public sealed class FactStoreTests
{
    [Fact]
    public async Task WriteThenRead_ReplacesByKey_CaseInsensitively()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new FileFactStore(paths);
        await store.WriteAsync(new MeasuredFact { Key = "memoryBandwidth", Value = "10 GB/s", Confidence = "Medium", Method = "test" });
        await store.WriteAsync(new MeasuredFact { Key = "MemoryBandwidth", Value = "12 GB/s", Confidence = "High", Method = "test" });
        var document = await store.ReadAsync();
        var fact = Assert.Single(document.Facts);
        Assert.Equal("12 GB/s", fact.Value);
        Assert.Equal("High", fact.Confidence);
    }

    [Fact]
    public async Task CorruptFactsFile_DegradesToEmptyInsteadOfThrowing()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.Facts, "{not json");
        Assert.Empty((await new FileFactStore(paths).ReadAsync()).Facts);
    }

    [Fact]
    public async Task FactsAreSortedByKey_ForStableDiffs()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var store = new FileFactStore(paths);
        await store.WriteAsync(new MeasuredFact { Key = "z", Value = "1", Confidence = "High", Method = "test" });
        await store.WriteAsync(new MeasuredFact { Key = "a", Value = "2", Confidence = "High", Method = "test" });
        Assert.Equal(["a", "z"], (await store.ReadAsync()).Facts.Select(fact => fact.Key));
    }
}
