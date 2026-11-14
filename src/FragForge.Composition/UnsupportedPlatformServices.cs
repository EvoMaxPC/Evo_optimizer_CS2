using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Composition;

public sealed class UnsupportedWmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
}
public sealed class UnsupportedServiceController : IServiceController
{
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(new ServiceCapture { Name = name, Exists = false });
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false) => throw new PlatformNotSupportedException();
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedScheduledTaskStore : IScheduledTaskStore
{
    public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false });
    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedNetworkAdapterController : INetworkAdapterController
{
    public Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default) => Task.FromResult(new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false });
    public Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedSystemRestoreService : ISystemRestoreService
{
    public Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(FeatureState.NotPresent);
    public Task EnableProtectionAsync(CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedGameLocator : IGameLocator
{
    public Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default) => Task.FromResult(new GameInfo());
}
