using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Core.Preflight;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using Microsoft.Extensions.Logging.Abstractions;

namespace FragForge.Tests.Core;

public sealed class EngineTestHarness : IDisposable
{
    public FakeRegistry Registry { get; } = new();
    public FakeHardwareProfiler Profiler { get; } = new();
    public FakeSystemRestoreService Restore { get; } = new();
    public FakeJournal? JournalOverride { get; set; }
    public TempDirectory Directory { get; } = new();
    public FragForgePaths Paths { get; }
    public IJournal Journal { get; }
    public ISnapshotStore Snapshots { get; }
    public IProtectionPolicy Policy { get; } = ProtectionPolicy.CreateDefault();
    public FakeActionFactory Factory { get; }
    public TweakCatalog Catalog { get; private set; } = new([]);
    public string UserSid => TestProfiles.Pn64().InteractiveUserSid!;

    public EngineTestHarness(Dictionary<string, ScriptedActionOptions>? scripts = null, bool inMemoryJournal = false)
    {
        if (inMemoryJournal) JournalOverride = new FakeJournal();
        Paths = new FragForgePaths(Directory.Path);
        Paths.EnsureCreated();
        Journal = (IJournal?)JournalOverride ?? new FileJournal(Paths);
        Snapshots = new FileSnapshotStore(Paths);
        Factory = new FakeActionFactory(Registry, Policy, scripts);
    }
    public EngineTestHarness WithCatalog(params TweakDefinition[] definitions)
    {
        Catalog = new TweakCatalog(definitions);
        return this;
    }
    public EngineTestHarness WithProfile(HardwareProfile profile) { Profiler.Profile = profile; return this; }
    public TweakEngine CreateEngine(IEnumerable<IExperimentHook>? hooks = null) => new(
        Catalog,
        Profiler,
        Factory,
        new ConditionFactory(),
        Snapshots,
        Journal,
        Restore,
        new PreflightService(Restore, Catalog),
        new FileExecutionGate(Paths),
        hooks ?? [],
        NullLogger<TweakEngine>.Instance);
    public ApplyOptions Options(bool dryRun = false, bool force = false, TweakProfile profile = TweakProfile.Advanced) => new() { DryRun = dryRun, Force = force, CreateRestorePoint = true, Profile = profile, TargetUserSid = UserSid };
    public void Dispose() => Directory.Dispose();
}
