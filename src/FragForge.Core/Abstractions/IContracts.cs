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

/// <summary>
/// Resuelve un adaptador de red por su IDENTIDAD DE HARDWARE (R-I), nunca por el nombre visible.
///
/// Es una interfaz NUEVA y aditiva: <see cref="INetworkAdapterController"/> no se toca (R-A). El
/// controlador existente recibe un nombre de adaptador y escribe propiedades; este resolutor solo
/// IDENTIFICA, y es lo que usa la ruta de apply para no depender de un texto que cambia con el idioma
/// y con el usuario que renombra la conexión.
///
/// Las tres identidades pedidas por la ronda, en orden de preferencia:
/// <list type="number">
/// <item><c>InterfaceGuid</c> — el GUID de la interfaz, tal como lo expone <c>MSFT_NetAdapter</c>.</item>
/// <item><c>NetCfgInstanceId</c> — el GUID que la clase de red guarda en el registro
/// (<c>…\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\&lt;NNNN&gt;\NetCfgInstanceId</c>).</item>
/// <item><c>HardwareId</c> — el <c>PnPDeviceID</c>, para exigir un modelo concreto (I225-V, AX210).</item>
/// </list>
/// </summary>
public interface INetworkAdapterResolver
{
    /// <summary>
    /// Adaptadores físicos con su identidad de hardware completa. Es la fuente de la ruta de apply y del
    /// diagnóstico. Si la consulta falla, devuelve lista vacía: no se inventan identidades (R-L).
    /// </summary>
    Task<IReadOnlyList<NetworkAdapterIdentity>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resuelve UN adaptador. Devuelve <c>null</c> cuando no hay coincidencia exacta o hay más de una, que
    /// es un resultado legítimo: quien llama decide si eso es "no aplicable".
    /// </summary>
    Task<NetworkAdapterIdentity?> ResolveAsync(NetworkAdapterSelector selector, CancellationToken cancellationToken = default);
}

/// <summary>
/// Identidad de un adaptador de red. Todos los campos son de hardware o del sistema: ninguno es el nombre
/// visible que el usuario puede renombrar.
/// </summary>
public sealed record NetworkAdapterIdentity
{
    /// <summary>Nombre de la conexión (<c>Ethernet</c>, <c>Wi-Fi</c>…). SOLO para mostrar y para pasar al controlador que ya lo exige; nunca para identificar.</summary>
    public string Name { get; init; } = "Unknown";
    /// <summary>Descripción del controlador. Puede cambiar entre versiones de driver: no se usa para identificar.</summary>
    public string Description { get; init; } = "Unknown";
    /// <summary><c>InterfaceGuid</c> de <c>MSFT_NetAdapter</c>, en minúsculas y con llaves. Clave primaria.</summary>
    public string? InterfaceGuid { get; init; }
    /// <summary><c>NetCfgInstanceId</c> leído de la clase de red del registro. Debe coincidir con <see cref="InterfaceGuid"/>.</summary>
    public string? NetCfgInstanceId { get; init; }
    /// <summary><c>PnPDeviceID</c>: contiene el hardware ID (p. ej. <c>PCI\VEN_8086&amp;DEV_15F3</c>).</summary>
    public string? HardwareId { get; init; }
    public NetworkKind Kind { get; init; }
    /// <summary>Velocidad de enlace negociada, en bits por segundo. <c>null</c> si no se pudo leer.</summary>
    public ulong? LinkSpeedBitsPerSecond { get; init; }
    /// <summary>Valor crudo de <c>NdisPhysicalMedium</c>. Se guarda para poder auditar el mapeo.</summary>
    public int? NdisPhysicalMedium { get; init; }
    public bool IsPhysical { get; init; }
    public string State { get; init; } = "Unknown";
    /// <summary>¿Es el adaptador por el que sale la ruta por defecto? Lo resuelve el probe de diagnóstico (5.5).</summary>
    public bool? IsDefaultRoute { get; init; }

    /// <summary>
    /// Valor de registro de <c>*EEE</c> leído por el diagnóstico (solo lectura). <c>null</c> = no se sabe: la
    /// propiedad no existe en este adaptador o no se pudo leer. <c>null</c> NO es "desactivado".
    /// </summary>
    public string? Eee { get; init; }
}

/// <summary>
/// Cómo se pide un adaptador. Un selector con TODOS los campos nulos no es válido: se exige al menos una
/// identidad de hardware, para que sea imposible "resolver por lo que sea".
/// </summary>
public sealed record NetworkAdapterSelector
{
    public string? InterfaceGuid { get; init; }
    public string? NetCfgInstanceId { get; init; }
    /// <summary>Fragmento del hardware ID que debe aparecer en <c>PnPDeviceID</c> (p. ej. <c>DEV_15F3</c>).</summary>
    public string? HardwareIdContains { get; init; }

    /// <summary>¿Hay al menos una identidad de hardware? Si no, el resolutor devuelve <c>null</c>.</summary>
    public bool HasHardwareIdentity =>
        !string.IsNullOrWhiteSpace(InterfaceGuid) || !string.IsNullOrWhiteSpace(NetCfgInstanceId) || !string.IsNullOrWhiteSpace(HardwareIdContains);
}
