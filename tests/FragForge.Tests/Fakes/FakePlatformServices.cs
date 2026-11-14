using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public sealed class FakeSystemRestoreService : ISystemRestoreService
{
    public FeatureState Protection { get; set; } = FeatureState.Enabled;
    public int CreateCalls { get; private set; }
    public int EnableCalls { get; private set; }
    public bool FailCreate { get; set; }
    public Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Protection);
    public Task EnableProtectionAsync(CancellationToken cancellationToken = default) { EnableCalls++; Protection = FeatureState.Enabled; return Task.CompletedTask; }
    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        CreateCalls++;
        if (FailCreate) throw new InvalidOperationException("Restore point failed (fake).");
        return Task.FromResult(new RestorePointResult(42, "Punto creado (fake)."));
    }
}
public sealed class FakeServiceController : IServiceController
{
    public Dictionary<string, ServiceCapture> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, string StartMode, bool? Delayed, bool RestoreAbsentDelayed)> StartModeCalls { get; } = [];
    public List<(string Name, string State)> StateCalls { get; } = [];
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false });
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false)
    {
        StartModeCalls.Add((name, startMode, delayedAutoStart, restoreAbsentDelayed));
        var current = Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false };
        Services[name] = restoreAbsentDelayed
            ? current with { Exists = true, StartMode = startMode, DelayedAutoStart = false, DelayedAutoStartExisted = false }
            : current with { Exists = true, StartMode = startMode, DelayedAutoStart = delayedAutoStart ?? current.DelayedAutoStart ?? false, DelayedAutoStartExisted = true };
        return Task.CompletedTask;
    }
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default)
    {
        StateCalls.Add((name, state));
        var current = Services.TryGetValue(name, out var service) ? service : new ServiceCapture { Name = name, Exists = false };
        Services[name] = current with { Exists = true, State = state };
        return Task.CompletedTask;
    }
}
public sealed class FakeScheduledTaskStore : IScheduledTaskStore
{
    public Dictionary<string, ScheduledTaskActionState> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, bool Enabled)> Calls { get; } = [];
    public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Tasks.TryGetValue(path, out var task) ? task : new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false });
    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
    {
        Calls.Add((path, enabled));
        var current = Tasks.TryGetValue(path, out var task) ? task : new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false };
        Tasks[path] = current with { Exists = true, Enabled = enabled };
        return Task.CompletedTask;
    }
}
public sealed class FakeNetworkAdapterController : INetworkAdapterController
{
    public Dictionary<(string Adapter, string Keyword), NetworkPropertyCapture> Properties { get; } = new();
    public List<(string Adapter, string Keyword, IReadOnlyList<string> Value)> Calls { get; } = [];
    public Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default) => Task.FromResult(Properties.TryGetValue((adapterName, keyword), out var capture) ? capture : new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false });
    public Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default)
    {
        Calls.Add((adapterName, keyword, value));
        var current = Properties.TryGetValue((adapterName, keyword), out var capture) ? capture : new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false };
        Properties[(adapterName, keyword)] = current with { Exists = true, RegistryValue = value };
        return Task.CompletedTask;
    }
}
public sealed class FakeGameLocator : IGameLocator
{
    public GameInfo Info { get; set; } = TestProfiles.Pn64().Games;
    public Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default) => Task.FromResult(Info);
}
public sealed class FakeWmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
}
public sealed class FakeConditionFactoryProbe : ITweakConditionFactory
{
    private readonly FragForge.Core.Conditions.ConditionFactory _inner = new();
    public IReadOnlySet<string> SupportedTypes => _inner.SupportedTypes;
    public ITweakCondition Create(ConditionSpec specification) => _inner.Create(specification);
}
public sealed class FakeJournal : IJournal
{
    public List<JournalEntry> Entries { get; } = [];
    public bool Corrupt { get; set; }
    public Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry with { Hash = entry.Hash.Length == 0 ? Guid.NewGuid().ToString("N") : entry.Hash }); return Task.CompletedTask; }
    public Task<IReadOnlyList<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JournalEntry>>(Entries.ToArray());
    public Task<JournalIntegrity> VerifyIntegrityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Corrupt ? new JournalIntegrity(false, Entries.Count, "Cadena rota (fake).") : new JournalIntegrity(true, Entries.Count, null));
}
public sealed class FakeClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
}
