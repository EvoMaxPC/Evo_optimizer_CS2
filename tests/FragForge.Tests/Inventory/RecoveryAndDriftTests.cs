using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Drift;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Core.Recovery;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Inventory;

public sealed class RecoveryWriterTests
{
    private static SystemSnapshot Snapshot(string id = "snap-1") => new()
    {
        Id = id,
        Label = "prueba",
        HardwareBuild = "26200",
        CreatedAt = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero),
        Tweaks =
        [
            new TweakSnapshot
            {
                TweakId = "debloat.service.diagtrack-disabled",
                DefinitionSha256 = "abc",
                Definition = new FakeCatalog("debloat.service.diagtrack-disabled").Get("debloat.service.diagtrack-disabled"),
                Actions =
                [
                    new CapturedAction(0, "Service", new ServiceActionState
                    {
                        Service = new ServiceCapture { Name = "DiagTrack", Exists = true, StartMode = "Auto", State = "Running", DelayedAutoStart = false }
                    }),
                    new CapturedAction(1, "Registry", new RegistryActionState
                    {
                        Values =
                        [
                            new RegistryValueCapture { Key = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", Name = "AllowTelemetry", KeyExisted = true, ValueExisted = true, ValueKind = "DWord", IntegerData = 1 },
                            new RegistryValueCapture { Key = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", Name = "Nuevo", KeyExisted = false, ValueExisted = false }
                        ]
                    })
                ]
            },
            new TweakSnapshot
            {
                TweakId = "debloat.task.ceip-consolidator",
                DefinitionSha256 = "def",
                Definition = new FakeCatalog("debloat.task.ceip-consolidator").Get("debloat.task.ceip-consolidator"),
                Actions =
                [
                    new CapturedAction(0, "ScheduledTask", new ScheduledTaskActionState
                    {
                        Path = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                        Exists = true,
                        Enabled = true,
                        XmlDefinition = "<Task><Settings/></Task>"
                    })
                ]
            }
        ]
    };

    [Fact]
    public async Task WritesEveryRecoveryArtefact_WithIndexAndHashes()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var writer = new RecoveryWriter(paths);
        var result = await writer.WriteAsync(Snapshot(), "op-1");

        Assert.True(File.Exists(Path.Combine(result.Directory, "manual-revert.md")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "registry-original.reg")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "services-original.csv")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "tasks-original.xml")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "recovery-index.json")));
        Assert.Equal(5, result.Index.Files.Count);
        Assert.All(result.Index.Files, file => Assert.Equal(64, file.Sha256.Length));
        Assert.Equal("26200", result.Index.OsBuild);
    }

    [Fact]
    public async Task IndexHashesMatchTheActualFiles()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var writer = new RecoveryWriter(paths);
        var result = await writer.WriteAsync(Snapshot(), "op-1");
        Assert.Empty(await writer.VerifyAsync(result.Directory));
    }

    [Fact]
    public async Task VerifyDetectsATamperedFile()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var writer = new RecoveryWriter(paths);
        var result = await writer.WriteAsync(Snapshot(), "op-1");
        await File.AppendAllTextAsync(Path.Combine(result.Directory, "manual-revert.md"), "\nmanipulado");
        var problems = await writer.VerifyAsync(result.Directory);
        Assert.Contains(problems, problem => problem.Contains("manual-revert.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyReportsMissingFile()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var writer = new RecoveryWriter(paths);
        var result = await writer.WriteAsync(Snapshot(), "op-1");
        File.Delete(Path.Combine(result.Directory, "services-original.csv"));
        Assert.Contains(await writer.VerifyAsync(result.Directory), problem => problem.Contains("Falta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualIsBilingualAndHasTheOfflineControlSetPath()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var result = await new RecoveryWriter(paths).WriteAsync(Snapshot(), "op-1");
        var manual = await File.ReadAllTextAsync(Path.Combine(result.Directory, "manual-revert.md"));
        Assert.Contains("## es-MX", manual, StringComparison.Ordinal);
        Assert.Contains("## en-US", manual, StringComparison.Ordinal);
        Assert.Contains("ControlSet001", manual, StringComparison.Ordinal);
        Assert.Contains("reg load HKLM\\OFFLINE", manual, StringComparison.Ordinal);
        Assert.Contains("snap-1", manual, StringComparison.Ordinal);
        Assert.Contains("WinRE", manual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceCsvCarriesTheOriginalModeAndTheEquivalentInstruction()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var result = await new RecoveryWriter(paths).WriteAsync(Snapshot(), "op-1");
        var csv = await File.ReadAllLinesAsync(Path.Combine(result.Directory, "services-original.csv"));
        Assert.StartsWith("servicio;", csv[0], StringComparison.Ordinal);
        Assert.Contains("DiagTrack;Auto;Running;False;", csv[1], StringComparison.Ordinal);
        Assert.Contains("start= auto", csv[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryBackup_EncodesAbsentValueAsDeletion_NotKeyDeletion()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var result = await new RecoveryWriter(paths).WriteAsync(Snapshot(), "op-1");
        var reg = await File.ReadAllTextAsync(Path.Combine(result.Directory, "registry-original.reg"));
        Assert.Contains("dword:00000001", reg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AllowTelemetry", reg, StringComparison.Ordinal);
        Assert.DoesNotContain("[-HKEY", reg, StringComparison.Ordinal);
        Assert.StartsWith("Windows Registry Editor", reg.TrimStart('\uFEFF'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskXml_IsWellFormedAndKeepsTheOriginalDefinition()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var result = await new RecoveryWriter(paths).WriteAsync(Snapshot(), "op-1");
        var xml = await File.ReadAllTextAsync(Path.Combine(result.Directory, "tasks-original.xml"));
        var document = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("tareas", document.Root!.Name.LocalName);
        Assert.Contains("<Task><Settings/></Task>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameTimestamp_ReusesTheFolder_WithoutLosingTheIndex()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var writer = new RecoveryWriter(paths);
        var first = await writer.WriteAsync(Snapshot("a"), "op");
        var second = await writer.WriteAsync(Snapshot("b"), "op");
        Assert.Equal(first.Directory, second.Directory);
        var index = JsonSerializer.Deserialize<RecoveryIndex>(await File.ReadAllTextAsync(Path.Combine(second.Directory, "recovery-index.json")), JsonDefaults.Options);
        Assert.Equal("b", index!.SnapshotId);
    }

    [Fact]
    public async Task DifferentDays_GetDifferentFolders()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var writer = new RecoveryWriter(paths);
        var first = await writer.WriteAsync(Snapshot("a"), "op");
        var second = await writer.WriteAsync(Snapshot("b") with { CreatedAt = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero) }, "op");
        Assert.NotEqual(first.Directory, second.Directory);
        Assert.True(Directory.Exists(first.Directory));
        Assert.True(Directory.Exists(second.Directory));
    }

    [Fact]
    public async Task CopyTo_ProducesAnIdenticalVerifiableCopy()
    {
        using var directory = new TempDirectory();
        using var usb = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var writer = new RecoveryWriter(paths);
        var result = await writer.WriteAsync(Snapshot(), "op-1");
        var target = await writer.CopyToAsync(result.Directory, usb.Path);
        Assert.Empty(await writer.VerifyAsync(target));
    }

    [Fact]
    public async Task WritesOnlyUnderTheDataDirectory()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var result = await new RecoveryWriter(paths).WriteAsync(Snapshot(), "op-1");
        Assert.StartsWith(Path.GetFullPath(paths.Root), Path.GetFullPath(result.Directory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPowerArtifact_IsANoteNotACrash()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        var snapshot = Snapshot() with { Artifacts = ["artifacts/power-x.pow"] };
        var result = await new RecoveryWriter(paths).WriteAsync(snapshot, "op-1");
        Assert.Contains(result.Index.Notes, note => note.Contains("power-x.pow", StringComparison.Ordinal));
    }
}

public sealed class DriftServiceTests
{
    private sealed class FakeSnapshots : ISnapshotStore
    {
        public List<SystemSnapshot> Items { get; } = [];
        public string RootPath => "memory";
        public string GetArtifactDirectory(string snapshotId) => "memory/" + snapshotId;
        public Task SaveAsync(SystemSnapshot snapshot, CancellationToken cancellationToken = default) { Items.Add(snapshot); return Task.CompletedTask; }
        public Task<SystemSnapshot> GetAsync(string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult(Items.Single(s => s.Id == snapshotId));
        public Task<IReadOnlyList<SystemSnapshot>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SystemSnapshot>>(Items.ToArray());
    }

    private sealed class FakeEngine : ITweakEngine
    {
        public Dictionary<string, TweakState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(new TweakStatus(id, States.TryGetValue(id, out var state) ? state : TweakState.NotApplied, "fake", [], []));
        public Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
    }

    private static JournalEntry Entry(string tweak, JournalEventType type, DateTimeOffset at, string? snapshotId = null) => new()
    {
        EntryId = Guid.NewGuid().ToString("N"),
        OperationId = "op",
        EventType = type,
        TweakId = tweak,
        SnapshotId = snapshotId,
        Timestamp = at,
        PreviousHash = "0",
        Hash = "hash-" + Guid.NewGuid().ToString("N")
    };

    private static DriftService Build(FakeJournal journal, FakeSnapshots snapshots, FakeEngine engine, string build = "26200") =>
        new(journal, snapshots, engine, new FakeCatalog("tweak.a", "tweak.b"), new FakeHardwareProfiler
        {
            Profile = TestProfiles.Pn64() with { Os = TestProfiles.Pn64().Os with { Build = int.Parse(build), Version = "10.0" } }
        });

    [Fact]
    public async Task NothingApplied_NoDrift()
    {
        var report = await Build(new FakeJournal(), new FakeSnapshots(), new FakeEngine()).DetectAsync();
        Assert.False(report.HasDrift);
        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task AppliedAndStillApplied_NoDrift()
    {
        var journal = new FakeJournal();
        journal.Entries.Add(Entry("tweak.a", JournalEventType.TweakApplied, DateTimeOffset.UtcNow.AddHours(-1)));
        var engine = new FakeEngine();
        engine.States["tweak.a"] = TweakState.Applied;
        var report = await Build(journal, new FakeSnapshots(), engine).DetectAsync();
        Assert.False(report.HasDrift);
    }

    [Fact]
    public async Task AppliedButNowNotApplied_IsLostDrift()
    {
        var journal = new FakeJournal();
        journal.Entries.Add(Entry("tweak.a", JournalEventType.TweakApplied, DateTimeOffset.UtcNow.AddHours(-1)));
        var engine = new FakeEngine();
        engine.States["tweak.a"] = TweakState.NotApplied;
        var report = await Build(journal, new FakeSnapshots(), engine).DetectAsync();
        Assert.True(report.HasDrift);
        Assert.Equal(DriftKind.Lost, Assert.Single(report.Items).Kind);
    }

    [Fact]
    public async Task RevertedButNowApplied_IsReappearedDrift()
    {
        var journal = new FakeJournal();
        journal.Entries.Add(Entry("tweak.a", JournalEventType.TweakApplied, DateTimeOffset.UtcNow.AddHours(-2)));
        journal.Entries.Add(Entry("tweak.a", JournalEventType.TweakRolledBack, DateTimeOffset.UtcNow.AddHours(-1)));
        var engine = new FakeEngine();
        engine.States["tweak.a"] = TweakState.AppliedExternally;
        var report = await Build(journal, new FakeSnapshots(), engine).DetectAsync();
        Assert.Equal(DriftKind.Reappeared, Assert.Single(report.Items).Kind);
    }

    [Fact]
    public async Task BuildChanged_IsBlamedOnTheFeatureUpdate()
    {
        var journal = new FakeJournal();
        journal.Entries.Add(Entry("tweak.a", JournalEventType.TweakApplied, DateTimeOffset.UtcNow.AddHours(-1), "snap-1"));
        var snapshots = new FakeSnapshots();
        snapshots.Items.Add(new SystemSnapshot { Id = "snap-1", HardwareBuild = "26100", Tweaks = [] });
        var engine = new FakeEngine();
        engine.States["tweak.a"] = TweakState.NotApplied;
        var report = await Build(journal, snapshots, engine, build: "26200").DetectAsync();
        Assert.Contains("26100", report.Items[0].Explanation, StringComparison.Ordinal);
        Assert.Contains("26200", report.Items[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetGone_IsExplainedAsNotApplicable()
    {
        var journal = new FakeJournal();
        journal.Entries.Add(Entry("tweak.a", JournalEventType.TweakApplied, DateTimeOffset.UtcNow.AddHours(-1)));
        var engine = new FakeEngine();
        engine.States["tweak.a"] = TweakState.NotApplicable;
        var report = await Build(journal, new FakeSnapshots(), engine).DetectAsync();
        Assert.Contains("ya no existe en este equipo", report.Items[0].Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TweakNoLongerInCatalog_IsAWarningNotDrift()
    {
        var journal = new FakeJournal();
        journal.Entries.Add(Entry("tweak.eliminado", JournalEventType.TweakApplied, DateTimeOffset.UtcNow));
        var report = await Build(journal, new FakeSnapshots(), new FakeEngine()).DetectAsync();
        Assert.False(report.HasDrift);
        Assert.Contains(report.Warnings, warning => warning.Contains("tweak.eliminado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OldSnapshotsWithoutHardwareBuild_StillLoad()
    {
        var json = """{"schemaVersion":1,"id":"snap-1","tweaks":[]}""";
        var snapshot = JsonSerializer.Deserialize<SystemSnapshot>(json, JsonDefaults.Options);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.HardwareBuild);
    }

    [Fact]
    public async Task StateReadFailure_IsUnknownNotLost()
    {
        var journal = new FakeJournal();
        journal.Entries.Add(Entry("tweak.a", JournalEventType.TweakApplied, DateTimeOffset.UtcNow));
        var report = await new DriftService(journal, new FakeSnapshots(), new ThrowingEngine(), new FakeCatalog("tweak.a"), new FakeHardwareProfiler()).DetectAsync();
        Assert.Equal(DriftKind.Unknown, Assert.Single(report.Items).Kind);
        Assert.False(report.HasDrift);
    }

    private sealed class ThrowingEngine : ITweakEngine
    {
        public Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("WMI caído");
        public Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
    }
}
