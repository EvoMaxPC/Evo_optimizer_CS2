using FragForge.Core.Models;

namespace FragForge.Core.Abstractions;

public interface ITweakCondition
{
    string Type { get; }
    ConditionResult Evaluate(HardwareProfile profile);
}
public interface ITweakConditionFactory
{
    IReadOnlySet<string> SupportedTypes { get; }
    ITweakCondition Create(ConditionSpec specification);
}
public interface IHardwareProfiler
{
    Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
}
public interface IWmiQuery
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default);
}
public interface ITweakCatalog
{
    IReadOnlyList<TweakDefinition> Definitions { get; }
    TweakDefinition Get(string id);
    bool TryGet(string id, out TweakDefinition? definition);
    string GetDefinitionHash(string id);
}
public interface ITweakEngine
{
    Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
    Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
    Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
    Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default);
    Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null);
}
public interface ISnapshotStore
{
    string RootPath { get; }
    string GetArtifactDirectory(string snapshotId);
    Task SaveAsync(SystemSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<SystemSnapshot> GetAsync(string snapshotId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemSnapshot>> ListAsync(CancellationToken cancellationToken = default);
}
public interface IJournal
{
    Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken = default);
    Task<JournalIntegrity> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}
public interface IExecutionGate
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
public interface ISystemRestoreService
{
    Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default);
    Task EnableProtectionAsync(CancellationToken cancellationToken = default);
    Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default);
}
public interface IPreflightService
{
    Task<PreflightReport> CheckAsync(ApplyOptions options, HardwareProfile profile, CancellationToken cancellationToken = default);
}
public interface IProtectionPolicy
{
    void EnsureRegistryAllowed(string key, string? valueName = null);
    void EnsureServiceAllowed(string name);
    void EnsureTaskAllowed(string path);
    void EnsureBcdAllowed(string element);
    void EnsureFileAllowed(string path);
    void EnsurePowerShellAllowed(string script);
    void EnsureNetworkPropertyAllowed(string adapterName, string keyword);
}
public interface IServiceController
{
    Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default);
    Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false);
    Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default);
}
public interface IScheduledTaskStore
{
    Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default);
}
public interface INetworkAdapterController
{
    Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default);
    Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default);
}
public interface IGameLocator
{
    Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default);
}
/// <summary>
/// Resuelve cs2.exe por la vía declarada de Steam: registro → <c>libraryfolders.vdf</c> →
/// <c>appmanifest_730.acf</c> (<c>installdir</c>) → <c>game\bin\win64\cs2.exe</c>.
///
/// Es una interfaz NUEVA y aditiva (R-A): no cambia ninguna firma existente, y <see cref="IGameLocator"/>
/// sigue igual para el motor. La implementación Windows vive en Actions; la interfaz no depende de
/// Windows, así que Core sigue siendo net10.0 puro.
/// </summary>
public interface ICs2Resolver
{
    Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default);
}
public interface IDiagnosticProbe
{
    string Key { get; }
    Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default);
}
public interface IExperimentHook
{
    int Order { get; }
    Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default);
}
public interface IUiLogSink
{
    void Write(string level, string message);
    event EventHandler<string>? MessageWritten;
}
