## 3a. FragForge.Core — código completo

### `src/FragForge.Core/Abstractions/IApplicationServices.cs`  <sub>(16 líneas)</sub>

````csharp
using FragForge.Core.Models;

namespace FragForge.Core.Abstractions;

public sealed record ReportDocument(int SchemaVersion, DateTimeOffset CreatedAt, string ApplicationVersion, HardwareProfile Hardware, IReadOnlyList<TweakStatus> Tweaks, PreflightReport Preflight, IReadOnlyList<SnapshotListItem> Snapshots, JournalIntegrity JournalIntegrity, IReadOnlyList<JournalEntry> RecentJournal);
public interface IReportService
{
    Task<ReportDocument> CreateAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(string path, string format, CancellationToken cancellationToken = default);
}
public interface IApplicationContext
{
    string Culture { get; set; }
    string DataRoot { get; }
    string ApplicationVersion { get; }
}
````

### `src/FragForge.Core/Abstractions/IContracts.cs`  <sub>(110 líneas)</sub>

````csharp
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
````

### `src/FragForge.Core/Abstractions/IProcessRunner.cs`  <sub>(18 líneas)</sub>

````csharp
namespace FragForge.Core.Abstractions;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
public sealed record ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public string? StandardInput { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;
}
````

### `src/FragForge.Core/Abstractions/IRegistry.cs`  <sub>(14 líneas)</sub>

````csharp
namespace FragForge.Core.Abstractions;

public interface IRegistry
{
    Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default);
    Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default);
    Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default);
    Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default);
    Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default);
}
public sealed record RegistryReadResult(bool KeyExists, bool ValueExists, string? Kind, object? Data);
public sealed record RegistryWriteValue(string Kind, object Data);
````

### `src/FragForge.Core/Abstractions/ITweakAction.cs`  <sub>(32 líneas)</sub>

````csharp
using FragForge.Core.Models;

namespace FragForge.Core.Abstractions;

public sealed record ActionExecutionContext
{
    public required string OperationId { get; init; }
    public required HardwareProfile Hardware { get; init; }
    public string? ArtifactDirectory { get; init; }
    public string? TargetUserSid { get; init; }
    public bool AllowPowerShellActions { get; init; }
    public IProgress<ActionProgress>? Progress { get; init; }
    public void Report(string stage, double fraction, string message) => Progress?.Report(new ActionProgress(stage, fraction, message));
}

public interface ITweakAction
{
    string Kind { get; }
    Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default);
    Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
}

public interface ITweakActionFactory
{
    IReadOnlySet<string> SupportedKinds { get; }
    ITweakAction Create(ActionSpec specification);
}
````

### `src/FragForge.Core/Conditions/ConditionFactory.cs`  <sub>(175 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Conditions;

public sealed class ConditionFactory : ITweakConditionFactory
{
    private static readonly string[] Types = ["windows", "hybridCpu", "cpuVendor", "gpuVendor", "adapter", "memoryChannels", "security", "formFactor", "cs2", "atlas", "userHive", "all", "any", "not"];
    public IReadOnlySet<string> SupportedTypes { get; } = new HashSet<string>(Types, StringComparer.OrdinalIgnoreCase);
    public ITweakCondition Create(ConditionSpec specification)
    {
        var type = specification.Type.ToLowerInvariant();
        if (!SupportedTypes.Contains(type)) throw new CatalogValidationException($"Unknown condition '{specification.Type}'.", [specification.Type]);
        var json = specification.Parameters.ValueKind == JsonValueKind.Undefined ? "{}" : specification.Parameters.GetRawText();
        try
        {
            return type switch
            {
                "windows" => new WindowsCondition(Parse<WindowsParameters>(json)),
                "hybridcpu" => new HybridCpuCondition(Parse<ExpectedBoolParameters>(json)),
                "cpuvendor" => new CpuVendorCondition(Parse<VendorParameters>(json)),
                "gpuvendor" => new GpuVendorCondition(Parse<GpuParameters>(json)),
                "adapter" => new AdapterCondition(Parse<AdapterParameters>(json)),
                "memorychannels" => new MemoryCondition(Parse<MemoryParameters>(json)),
                "security" => new SecurityCondition(Parse<SecurityParameters>(json)),
                "formfactor" => new FormFactorCondition(Parse<FormFactorParameters>(json)),
                "cs2" => new Cs2Condition(Parse<Cs2Parameters>(json)),
                "atlas" => new AtlasCondition(Parse<ExpectedBoolParameters>(json)),
                "userhive" => new UserHiveCondition(Parse<ExpectedBoolParameters>(json)),
                "all" => new CompositeCondition(type, Parse<CompositeParameters>(json).Conditions, this, true),
                "any" => new CompositeCondition(type, Parse<CompositeParameters>(json).Conditions, this, false),
                "not" => new NotCondition(Parse<NotParameters>(json).Condition, this),
                _ => throw new CatalogValidationException($"Unknown condition '{type}'.", [type])
            };
        }
        catch (JsonException ex) { throw new CatalogValidationException($"Invalid parameters for condition '{type}'.", [ex.Message]); }
    }
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? throw new JsonException("Null condition parameters.");
}

internal sealed record WindowsParameters { public int MinBuild { get; init; } = 26100; public int MaxBuild { get; init; } = 26299; public string? Edition { get; init; } public string Architecture { get; init; } = "X64"; }
internal sealed record ExpectedBoolParameters { public bool Expected { get; init; } = true; }
internal sealed record VendorParameters { public required string Vendor { get; init; } }
internal sealed record GpuParameters { public required GpuVendor Vendor { get; init; } public bool DedicatedOnly { get; init; } }
internal sealed record AdapterParameters { public string? NameContains { get; init; } public string? DescriptionContains { get; init; } public NetworkKind? Kind { get; init; } public bool PhysicalOnly { get; init; } = true; }
internal sealed record MemoryParameters { public MemoryChannelState State { get; init; } = MemoryChannelState.Dual; }
internal sealed record SecurityParameters { public required string Feature { get; init; } public FeatureState State { get; init; } }
internal sealed record FormFactorParameters { public bool? Portable { get; init; } public bool? Battery { get; init; } }
internal sealed record Cs2Parameters { public bool Installed { get; init; } = true; public bool AllowAmbiguous { get; init; } }
internal sealed record CompositeParameters { public IReadOnlyList<ConditionSpec> Conditions { get; init; } = []; }
internal sealed record NotParameters { public required ConditionSpec Condition { get; init; } }

internal sealed class WindowsCondition(WindowsParameters p) : ITweakCondition
{
    public string Type => "windows";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        if (profile.IsUnsupportedPlatform || profile.Os.Build == 0) return ConditionResult.Unknown("No se detectó una build de Windows.");
        if (profile.Os.Build < p.MinBuild || profile.Os.Build > p.MaxBuild) return ConditionResult.Unmet($"Build {profile.Os.Build}; rango compatible {p.MinBuild}–{p.MaxBuild}.");
        if (!string.Equals(profile.Os.Architecture, p.Architecture, StringComparison.OrdinalIgnoreCase)) return ConditionResult.Unmet($"Arquitectura {profile.Os.Architecture}; se requiere {p.Architecture}.");
        if (p.Edition is not null && !profile.Os.Edition.Contains(p.Edition, StringComparison.OrdinalIgnoreCase)) return ConditionResult.Unmet($"Edición {profile.Os.Edition}; se requiere {p.Edition}.");
        return ConditionResult.Met($"Windows build {profile.Os.Build} {profile.Os.Edition} {profile.Os.Architecture}.");
    }
}
internal sealed class HybridCpuCondition(ExpectedBoolParameters p) : ITweakCondition
{
    public string Type => "hybridCpu";
    public ConditionResult Evaluate(HardwareProfile profile) => profile.Cpu.IsHybrid is null ? ConditionResult.Unknown("No se pudo consultar EfficiencyClass; no se asume CPU híbrida por el modelo.") : profile.Cpu.IsHybrid == p.Expected ? ConditionResult.Met($"CPU híbrida: {profile.Cpu.IsHybrid}.") : ConditionResult.Unmet($"CPU híbrida: {profile.Cpu.IsHybrid}; esperado {p.Expected}.");
}
internal sealed class CpuVendorCondition(VendorParameters p) : ITweakCondition
{
    public string Type => "cpuVendor";
    public ConditionResult Evaluate(HardwareProfile profile) => profile.Cpu.Vendor.Equals(p.Vendor, StringComparison.OrdinalIgnoreCase) ? ConditionResult.Met($"CPU {profile.Cpu.Vendor}.") : ConditionResult.Unmet($"CPU {profile.Cpu.Vendor}; se requiere {p.Vendor}.");
}
internal sealed class GpuVendorCondition(GpuParameters p) : ITweakCondition
{
    public string Type => "gpuVendor";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var matches = profile.Gpus.Where(g => g.Vendor == p.Vendor && (!p.DedicatedOnly || !g.IsIntegrated)).ToArray();
        return matches.Length > 0 ? ConditionResult.Met($"GPU compatible: {matches[0].Name}.") : ConditionResult.Unmet($"No se detectó GPU {p.Vendor} compatible.");
    }
}
internal sealed class AdapterCondition(AdapterParameters p) : ITweakCondition
{
    public string Type => "adapter";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var matches = profile.NetworkAdapters.Where(a => (!p.PhysicalOnly || a.IsPhysical) && (p.NameContains is null || a.Name.Contains(p.NameContains, StringComparison.OrdinalIgnoreCase)) && (p.DescriptionContains is null || a.Description.Contains(p.DescriptionContains, StringComparison.OrdinalIgnoreCase)) && (p.Kind is null || a.Kind == p.Kind)).ToArray();
        return matches.Length > 0 ? ConditionResult.Met($"Adaptador compatible: {matches[0].Name}.") : ConditionResult.Unmet("No se detectó el adaptador requerido.");
    }
}
internal sealed class MemoryCondition(MemoryParameters p) : ITweakCondition
{
    public string Type => "memoryChannels";
    public ConditionResult Evaluate(HardwareProfile profile) => profile.Memory.Channels == MemoryChannelState.Unknown ? ConditionResult.Unknown(profile.Memory.ChannelEvidence) : profile.Memory.Channels == p.State ? ConditionResult.Met($"Canales de RAM: {profile.Memory.Channels}.") : ConditionResult.Unmet($"Canales de RAM: {profile.Memory.Channels}; esperado {p.State}.");
}
internal sealed class SecurityCondition(SecurityParameters p) : ITweakCondition
{
    public string Type => "security";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var state = p.Feature.ToLowerInvariant() switch { "vbs" => profile.Security.Vbs, "hvci" => profile.Security.Hvci, "secureboot" => profile.Security.SecureBoot, "tpm" => profile.Security.Tpm, "hags" => profile.Security.Hags, "mpo" => profile.Security.Mpo, _ => FeatureState.Unknown };
        return state == FeatureState.Unknown ? ConditionResult.Unknown($"{p.Feature}: estado desconocido.") : state == p.State ? ConditionResult.Met($"{p.Feature}: {state}.") : ConditionResult.Unmet($"{p.Feature}: {state}; esperado {p.State}.");
    }
}
internal sealed class FormFactorCondition(FormFactorParameters p) : ITweakCondition
{
    public string Type => "formFactor";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        if (p.Portable is not null && profile.FormFactor.IsPortable != p.Portable) return ConditionResult.Unmet($"Portátil: {profile.FormFactor.IsPortable?.ToString() ?? "desconocido"}.");
        if (p.Battery is not null && profile.FormFactor.HasBattery != p.Battery) return ConditionResult.Unmet($"Batería: {profile.FormFactor.HasBattery}.");
        return ConditionResult.Met("Factor de forma compatible.");
    }
}
internal sealed class Cs2Condition(Cs2Parameters p) : ITweakCondition
{
    public string Type => "cs2";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var installed = !string.IsNullOrWhiteSpace(profile.Games.Cs2ExecutablePath) && (!profile.Games.Cs2Ambiguous || p.AllowAmbiguous);
        return installed == p.Installed ? ConditionResult.Met($"CS2 detectado: {installed}.") : ConditionResult.Unmet(profile.Games.Cs2Ambiguous ? "Hay varias instalaciones de CS2; selecciona una manualmente." : $"CS2 detectado: {installed}; esperado {p.Installed}.");
    }
}
internal sealed class AtlasCondition(ExpectedBoolParameters p) : ITweakCondition
{
    public string Type => "atlas";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var detected = profile.Playbooks.Any(x => x.Name.Contains("Atlas", StringComparison.OrdinalIgnoreCase));
        return detected == p.Expected ? ConditionResult.Met($"Atlas detectado: {detected} (heurística).") : ConditionResult.Unmet($"Atlas detectado: {detected}; esperado {p.Expected}.");
    }
}
internal sealed class UserHiveCondition(ExpectedBoolParameters p) : ITweakCondition
{
    public string Type => "userHive";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var available = !string.IsNullOrWhiteSpace(profile.InteractiveUserSid);
        return available == p.Expected ? ConditionResult.Met($"Hive del usuario interactivo: {profile.InteractiveUserName ?? profile.InteractiveUserSid}.") : ConditionResult.Unmet("No se pudo resolver el SID interactivo; no se modificará HKCU del proceso elevado.");
    }
}
internal sealed class CompositeCondition(string type, IReadOnlyList<ConditionSpec> specs, ITweakConditionFactory factory, bool all) : ITweakCondition
{
    private readonly IReadOnlyList<ITweakCondition> _children = specs.Select(factory.Create).ToArray();
    public string Type => type;
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var results = _children.Select(c => c.Evaluate(profile)).ToArray();
        if (all)
        {
            var unknown = results.FirstOrDefault(r => !r.IsKnown);
            if (unknown is not null) return unknown;
            var unmet = results.FirstOrDefault(r => !r.IsMet);
            return unmet is null ? ConditionResult.Met("Todas las condiciones se cumplen.") : unmet;
        }
        var met = results.FirstOrDefault(r => r.IsMet);
        if (met is not null) return met;
        var knownUnmet = results.FirstOrDefault(r => r.IsKnown);
        return knownUnmet ?? ConditionResult.Unknown("Ninguna condición alternativa pudo evaluarse.");
    }
}
internal sealed class NotCondition(ConditionSpec spec, ITweakConditionFactory factory) : ITweakCondition
{
    private readonly ITweakCondition _inner = factory.Create(spec);
    public string Type => "not";
    public ConditionResult Evaluate(HardwareProfile profile)
    {
        var result = _inner.Evaluate(profile);
        return !result.IsKnown ? result : result.IsMet ? ConditionResult.Unmet(result.Reason) : ConditionResult.Met(result.Reason);
    }
}
````

### `src/FragForge.Core/Engine/ActionStateEquivalence.cs`  <sub>(23 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class ActionStateEquivalence
{
    public static bool Equivalent(ActionState expected, ActionState actual) => (expected, actual) switch
    {
        (RegistryActionState a, RegistryActionState b) => a.Values.Count == b.Values.Count && a.Values.Zip(b.Values).All(p => RegistryEqual(p.First, p.Second)),
        (ServiceActionState a, ServiceActionState b) => a.Service == b.Service,
        (ScheduledTaskActionState a, ScheduledTaskActionState b) => a.Path == b.Path && a.Exists == b.Exists && a.Enabled == b.Enabled,
        (PowerPlanActionState a, PowerPlanActionState b) => a.PlanName == b.PlanName && a.PreviousActiveGuid == b.PreviousActiveGuid && a.PlanExisted == b.PlanExisted && a.OwnedPlanGuid == b.OwnedPlanGuid && a.Settings.SequenceEqual(b.Settings),
        (BcdActionState a, BcdActionState b) => a.Element == b.Element && a.Existed == b.Existed && a.PreviousValue == b.PreviousValue,
        (NetworkPropertyActionState a, NetworkPropertyActionState b) => a.Properties.SequenceEqual(b.Properties),
        (FileActionState a, FileActionState b) => a.Path == b.Path && a.Existed == b.Existed && a.ContentBase64 == b.ContentBase64 && a.LastWriteTimeUtc == b.LastWriteTimeUtc,
        (ScriptActionState a, ScriptActionState b) => JsonSerializer.Serialize(a.CapturedJson, JsonDefaults.Create(false)) == JsonSerializer.Serialize(b.CapturedJson, JsonDefaults.Create(false)),
        (InspectionActionState, InspectionActionState) => true,
        _ => false
    };
    private static bool RegistryEqual(RegistryValueCapture a, RegistryValueCapture b) => a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase) && a.Name.Equals(b.Name, StringComparison.Ordinal) && a.KeyExisted == b.KeyExisted && a.ValueExisted == b.ValueExisted && a.ValueKind == b.ValueKind && a.StringData == b.StringData && a.IntegerData == b.IntegerData && (a.MultiStringData ?? []).SequenceEqual(b.MultiStringData ?? []) && a.BinaryBase64 == b.BinaryBase64 && a.MissingAncestors.SequenceEqual(b.MissingAncestors, StringComparer.OrdinalIgnoreCase);
}
````

### `src/FragForge.Core/Engine/DependencyResolver.cs`  <sub>(46 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class DependencyResolver
{
    public static IReadOnlyList<TweakDefinition> Resolve(IEnumerable<string> ids, ITweakCatalog catalog)
    {
        var requested = ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) throw new FragForgeException("empty_selection", "Selecciona al menos un tweak.");
        var closure = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TweakDefinition>();
        var errors = new List<string>();
        foreach (var id in requested)
        {
            if (!catalog.TryGet(id, out var definition) || definition is null) errors.Add($"Tweak desconocido: '{id}'.");
            else Visit(definition);
        }
        if (errors.Count > 0) throw new FragForgeException("dependency_resolution", string.Join(" ", errors));
        foreach (var a in ordered)
        foreach (var b in ordered)
        {
            if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.ConflictsWith.Contains(b.Id, StringComparer.OrdinalIgnoreCase) || b.ConflictsWith.Contains(a.Id, StringComparer.OrdinalIgnoreCase)) errors.Add($"Conflicto entre '{a.Id}' y '{b.Id}'.");
        }
        if (errors.Count > 0) throw new FragForgeException("tweak_conflict", string.Join(" ", errors.Distinct(StringComparer.OrdinalIgnoreCase)));
        return ordered;
        void Visit(TweakDefinition definition)
        {
            if (visited.Contains(definition.Id)) return;
            if (!visiting.Add(definition.Id)) { errors.Add($"Ciclo de dependencias en '{definition.Id}'."); return; }
            foreach (var dependency in definition.DependsOn)
            {
                if (!catalog.TryGet(dependency, out var parent) || parent is null) { errors.Add($"'{definition.Id}' depende del tweak inexistente '{dependency}'."); continue; }
                Visit(parent);
            }
            visiting.Remove(definition.Id);
            visited.Add(definition.Id);
            closure[definition.Id] = definition;
            ordered.Add(definition);
        }
    }
}
````

### `src/FragForge.Core/Engine/ProfileSelector.cs`  <sub>(24 líneas)</sub>

````csharp
using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class ProfileSelector
{
    public static IReadOnlyList<TweakDefinition> Select(IEnumerable<TweakDefinition> definitions, TweakProfile profile) => definitions.Where(d => Allows(profile, d)).OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    public static bool Allows(TweakProfile profile, TweakDefinition definition) => profile switch
    {
        TweakProfile.Safe => definition.Risk == RiskLevel.Safe && definition.Evidence != EvidenceLevel.Experimental && !definition.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase),
        TweakProfile.Balanced => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental && definition.Risk != RiskLevel.Experimental && !definition.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase),
        TweakProfile.Competitive => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental && definition.Risk != RiskLevel.Experimental,
        TweakProfile.Advanced => true,
        _ => false
    };
    public static void EnsureRiskAccepted(TweakDefinition definition, ApplyOptions options)
    {
        if (!Allows(options.Profile, definition) && !(options.Profile == TweakProfile.Advanced)) throw new FragForgeException("profile_rejected", $"'{definition.Id}' no está permitido por el perfil {options.Profile}.");
        if (definition.Risk is RiskLevel.Advanced or RiskLevel.Experimental || definition.Actions.Any(a => a.Kind.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) || a.Kind.Equals("Bcdedit", StringComparison.OrdinalIgnoreCase)))
        {
            if (options.Profile != TweakProfile.Advanced || !options.RiskAcknowledgements.Contains(definition.Id) || options.AdvancedRiskPhrase != "ACEPTO EL RIESGO") throw new FragForgeException("risk_confirmation_required", $"'{definition.Id}' requiere perfil Advanced, aceptación explícita por ID y la frase exacta 'ACEPTO EL RIESGO'.");
        }
    }
}
````

### `src/FragForge.Core/Engine/TweakEngine.cs`  <sub>(576 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace FragForge.Core.Engine;

public sealed class TweakEngine(
    ITweakCatalog catalog,
    IHardwareProfiler profiler,
    ITweakActionFactory actionFactory,
    ITweakConditionFactory conditionFactory,
    ISnapshotStore snapshots,
    IJournal journal,
    ISystemRestoreService restore,
    IPreflightService preflight,
    IExecutionGate gate,
    IEnumerable<IExperimentHook> hooks,
    ILogger<TweakEngine> logger) : ITweakEngine
{
    private readonly IReadOnlyList<IExperimentHook> _hooks = hooks.OrderBy(h => h.Order).ToArray();

    public async Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        progress?.Report(new EngineProgress("hardware", null, 0, "Detectando hardware y estado del sistema."));
        var hardware = await profiler.ProfileAsync(cancellationToken, progress);
        var ownership = await ReadOwnershipAsync(cancellationToken);
        var statuses = new List<TweakStatus>();
        for (var i = 0; i < catalog.Definitions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = catalog.Definitions[i];
            progress?.Report(new EngineProgress("scan", definition.Id, (double)(i + 1) / catalog.Definitions.Count, definition.Title.EsMx));
            statuses.Add(await EvaluateAsync(definition, hardware, ownership, cancellationToken));
        }
        return new ScanReport(hardware, statuses, DateTimeOffset.UtcNow);
    }

    public async Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default)
    {
        var definition = catalog.Get(id);
        var hardware = await profiler.ProfileAsync(cancellationToken);
        var ownership = await ReadOwnershipAsync(cancellationToken);
        return await EvaluateAsync(definition, hardware, ownership, cancellationToken);
    }

    public async Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var hardware = await profiler.ProfileAsync(cancellationToken, progress);
        var options = new ApplyOptions { CreateRestorePoint = createRestorePoint, Profile = TweakProfile.Advanced };
        var preflightReport = await preflight.CheckAsync(options, hardware, cancellationToken);
        if (!preflightReport.CanApply) throw new PreflightFailedException("El preflight impide crear el snapshot con punto de restauración.", preflightReport);
        var plans = new List<ActionPlan>();
        foreach (var definition in catalog.Definitions.Where(d => !d.ReadOnly))
        {
            var plan = await TryBuildPlanAsync(definition, hardware, null, operationId, cancellationToken);
            if (plan is not null) plans.Add(plan);
        }
        var snapshot = await CaptureAndPersistAsync(operationId, label, hardware, plans, createRestorePoint, cancellationToken, progress);
        logger.LogInformation("Snapshot {SnapshotId} creado con {TweakCount} tweaks capturados.", snapshot.Id, snapshot.Tweaks.Count);
        return snapshot;
    }

    public async Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var results = new List<TweakApplyResult>();
        var errors = new List<string>();
        var warnings = new List<string>();
        string? snapshotId = null;
        long? restoreSequence = null;
        var cancelled = false;
        var critical = false;
        var failed = false;
        await AppendAsync(operationId, JournalEventType.OperationStarted, null, null, options.DryRun ? "Apply dry-run" : "Apply", cancellationToken);
        try
        {
            var ordered = DependencyResolver.Resolve(ids, catalog);
            var hardware = ApplyOverrides(await profiler.ProfileAsync(cancellationToken, progress), options.TargetUserSid, options.Cs2ExecutableOverride);
            var plans = new List<ActionPlan>();
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (definition.DependsOn.Any(skipped.Contains))
                {
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, TweakState.NotApplicable, "Una dependencia no está aplicada o no es aplicable."));
                    continue;
                }
                var condition = EvaluateConditions(definition, hardware);
                if (!condition.IsMet)
                {
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, TweakState.NotApplicable, condition.Reason));
                    continue;
                }
                if (!options.DryRun) ProfileSelector.EnsureRiskAccepted(definition, options);
                var plan = await TryBuildPlanAsync(definition, hardware, options, operationId, cancellationToken);
                if (plan is null)
                {
                    skipped.Add(definition.Id);
                    results.Add(Skipped(definition.Id, TweakState.NotApplicable, "El destino de la acción no está disponible en este equipo."));
                    continue;
                }
                plans.Add(plan);
            }
            if (options.DryRun)
            {
                foreach (var plan in plans)
                {
                    var changes = new List<DryRunChange>();
                    for (var i = 0; i < plan.Actions.Count; i++) changes.Add(new(plan.Definition.Id, i, plan.Actions[i].Kind, await plan.Actions[i].DryRunAsync(plan.Context, cancellationToken)));
                    var current = await VerifyPlanAsync(plan, cancellationToken);
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = current.State, Message = "Simulación sin cambios. " + current.Reason, PlannedChanges = changes });
                }
                return await FinishAsync(new BatchReport { OperationId = operationId, DryRun = true, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            if (plans.Count == 0)
            {
                warnings.Add("No hay tweaks aplicables; no se creó snapshot ni punto de restauración.");
                return await FinishAsync(new BatchReport { OperationId = operationId, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            var needsRestore = options.CreateRestorePoint && plans.Any(p => !p.Definition.ReadOnly);
            var effectiveOptions = options with { CreateRestorePoint = needsRestore };
            var preflightReport = await preflight.CheckAsync(effectiveOptions, hardware, cancellationToken);
            warnings.AddRange(preflightReport.Checks.Where(c => c.Severity == PreflightSeverity.Warning).Select(c => c.Message));
            if (!preflightReport.CanApply) throw new PreflightFailedException("El preflight bloqueó la aplicación: " + string.Join(" ", preflightReport.Checks.Where(c => c.Severity == PreflightSeverity.Blocking).Select(c => c.Message)), preflightReport);
            var ownership = await ReadOwnershipAsync(cancellationToken);
            var applicable = new List<ActionPlan>();
            foreach (var plan in plans)
            {
                var state = await VerifyPlanAsync(plan, cancellationToken);
                if (state.State is TweakState.Applied or TweakState.AppliedExternally && !options.Force)
                {
                    var owned = ownership.Contains(plan.Definition.Id);
                    results.Add(Skipped(plan.Definition.Id, owned ? TweakState.Applied : TweakState.AppliedExternally, owned ? "Ya está aplicado por FragForge; idempotencia." : "Ya coincide con el objetivo, posiblemente por Atlas/usuario. No se sobrescribe sin Force."));
                    continue;
                }
                applicable.Add(plan);
            }
            if (applicable.Count == 0)
            {
                warnings.Add("Todos los tweaks seleccionados ya coinciden; no hubo cambios.");
                return await FinishAsync(new BatchReport { OperationId = operationId, Success = true, Results = results, Warnings = warnings }, true, cancellationToken);
            }
            if (needsRestore && options.EnableSystemProtection)
            {
                var protection = await restore.GetProtectionStateAsync(cancellationToken);
                if (protection == FeatureState.Disabled)
                {
                    await restore.EnableProtectionAsync(cancellationToken);
                    protection = await restore.GetProtectionStateAsync(cancellationToken);
                    if (protection != FeatureState.Enabled) throw new PreflightFailedException("No se pudo confirmar la protección del sistema tras habilitarla; no se aplicará ningún cambio.", preflightReport);
                    warnings.Add("Protección del sistema habilitada explícitamente para esta operación.");
                }
                else if (protection != FeatureState.Enabled) throw new PreflightFailedException("La protección del sistema no pudo confirmarse; usa --no-restore-point si aceptas solo snapshot y journal.", preflightReport);
            }
            var snapshot = await CaptureAndPersistAsync(operationId, "apply: " + string.Join(", ", applicable.Select(p => p.Definition.Id)), hardware, applicable, needsRestore, cancellationToken, progress);
            snapshotId = snapshot.Id;
            restoreSequence = snapshot.RestorePointSequence;
            foreach (var plan in applicable)
            {
                plan.States.Clear();
                foreach (var captured in snapshot.Tweaks.First(t => t.TweakId == plan.Definition.Id).Actions)
                plan.States.Add(captured.State);
            }
            foreach (var plan in applicable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunHooksAsync(ExperimentPhase.BeforeApply, operationId, snapshotId, plan, hardware, snapshots.GetArtifactDirectory(snapshotId), cancellationToken, warnings, failOnError: true);
            }
            foreach (var plan in applicable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempted = new List<(ITweakAction Action, ActionState Before)>();
                var planWarnings = new List<string>();
                try
                {
                    await AppendAsync(operationId, JournalEventType.TweakApplyStarted, plan.Definition.Id, snapshotId, null, cancellationToken);
                    for (var i = 0; i < plan.Actions.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var action = plan.Actions[i];
                        var before = plan.States[i];
                        attempted.Add((action, before));
                        progress?.Report(new EngineProgress("apply", plan.Definition.Id, (double)i / plan.Actions.Count, $"Aplicando {action.Kind}."));
                        await action.ApplyAsync(before, plan.Context, cancellationToken);
                        var verification = await action.VerifyAsync(before, plan.Context, cancellationToken);
                        if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException($"{action.Kind}: {verification.Message}");
                        action.ReportReady(plan.Context, "verify", 1, verification.Message);
                    }
                    await AppendAsync(operationId, JournalEventType.TweakApplied, plan.Definition.Id, snapshotId, null, cancellationToken);
                    await RunHooksAsync(ExperimentPhase.AfterApply, operationId, snapshotId, plan, hardware, snapshots.GetArtifactDirectory(snapshotId), cancellationToken, planWarnings, failOnError: false);
                    if (plan.Definition.RequiresReboot) planWarnings.Add("Requiere reinicio para que todos los efectos sean activos.");
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = plan.Definition.ReadOnly ? TweakState.Inspected : TweakState.Applied, Message = plan.Definition.ReadOnly ? "Inspección completada sin cambios." : "Aplicado y verificado.", Changed = !plan.Definition.ReadOnly, Warnings = planWarnings });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed = true;
                    cancelled |= ex is OperationCanceledException;
                    errors.Add($"{plan.Definition.Id}: {ex.Message}");
                    logger.LogError(ex, "Fallo al aplicar {TweakId}; iniciando rollback del tweak.", plan.Definition.Id);
                    var rollbackErrors = await RollbackAsync(operationId, snapshotId, plan.Definition.Id, attempted, plan.Context);
                    critical |= rollbackErrors.Count > 0;
                    errors.AddRange(rollbackErrors);
                    results.Add(new TweakApplyResult { Id = plan.Definition.Id, State = rollbackErrors.Count > 0 ? TweakState.Unknown : TweakState.NotApplied, Message = ex.Message, RolledBack = rollbackErrors.Count == 0, RollbackFailed = rollbackErrors.Count > 0, Warnings = planWarnings });
                    break;
                }
            }
            if (results.Any(r => r.Changed)) warnings.Add("Se recomienda reiniciar antes de medir A/B si algún tweak lo requiere; no se reinicia automáticamente.");
        }
        catch (OperationCanceledException ex)
        {
            cancelled = true;
            failed = true;
            errors.Add("Operación cancelada: " + ex.Message);
            await AppendSafeAsync(operationId, JournalEventType.OperationFailed, null, snapshotId, ex.Message);
        }
        catch (Exception ex)
        {
            failed = true;
            errors.Add(ex.Message);
            logger.LogError(ex, "La operación {OperationId} falló antes de completar.", operationId);
            await AppendSafeAsync(operationId, JournalEventType.OperationFailed, null, snapshotId, ex.Message);
        }
        var success = !failed && !cancelled && !critical && results.All(r => !r.RollbackFailed);
        return await FinishAsync(new BatchReport { OperationId = operationId, SnapshotId = snapshotId, Success = success, Cancelled = cancelled, HasCriticalFailure = critical, RestorePointSequence = restoreSequence, Results = results, Errors = errors, Warnings = warnings }, success, CancellationToken.None);
    }

    public async Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken);
        var operationId = NewId();
        var results = new List<TweakApplyResult>();
        var errors = new List<string>();
        var warnings = new List<string>();
        var critical = false;
        await AppendAsync(operationId, JournalEventType.OperationStarted, null, null, "Revert", cancellationToken);
        try
        {
            var modes = (request.Ids.Count > 0 ? 1 : 0) + (request.SnapshotId is not null ? 1 : 0) + (request.Last ? 1 : 0);
            if (modes != 1) throw new FragForgeException("revert_selection", "Usa exactamente uno de: IDs, --snapshot o --last.");
            var integrity = await journal.VerifyIntegrityAsync(cancellationToken);
            if (!integrity.IsValid) throw new FragForgeException("journal_integrity", "El journal no es íntegro: " + integrity.Error);
            var entries = await journal.ReadAllAsync(cancellationToken);
            var selections = SelectRevertSnapshots(request, entries);
            var currentHardware = await profiler.ProfileAsync(cancellationToken, progress);
            foreach (var selection in selections)
            {
                var snapshot = await snapshots.GetAsync(selection.SnapshotId, cancellationToken);
                var snapshotEntries = entries.Where(e => e.SnapshotId == snapshot.Id && e.EventType == JournalEventType.TweakApplied).ToArray();
                var previouslyReverted = RevertedIds(entries, snapshot.Id);
                if (snapshotEntries.Length > 0 && snapshotEntries.All(e => previouslyReverted.Contains(e.TweakId!)) && !request.Options.Force) throw new FragForgeException("snapshot_already_reverted", $"El snapshot {snapshot.Id} ya fue revertido. Force permite restaurarlo de nuevo, con riesgo de pisar cambios posteriores.");
                var selectedTweaks = selection.Ids.Count > 0 ? snapshot.Tweaks.Where(t => selection.Ids.Contains(t.TweakId, StringComparer.OrdinalIgnoreCase)).ToArray()
                    : request.Last ? snapshot.Tweaks.Where(t => snapshotEntries.Any(e => e.TweakId == t.TweakId) && (request.Options.Force || !previouslyReverted.Contains(t.TweakId))).ToArray()
                    : snapshot.Tweaks.Where(t => request.Options.Force || !previouslyReverted.Contains(t.TweakId)).ToArray();
                if (selection.Ids.Count > 0 && selectedTweaks.Length != selection.Ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new FragForgeException("revert_selection", "Algún ID no pertenece al snapshot seleccionado.");
                var hardware = currentHardware with { Games = snapshot.Hardware?.Games ?? currentHardware.Games };
                if (request.Options.Cs2ExecutableOverride is not null) hardware = ApplyOverrides(hardware, null, request.Options.Cs2ExecutableOverride);
                if (request.Options.TargetUserSid is not null) hardware = ApplyOverrides(hardware, request.Options.TargetUserSid, null);
                var plans = new List<RevertPlan>();
                await AppendAsync(operationId, JournalEventType.RevertStarted, null, snapshot.Id, null, cancellationToken);
                foreach (var tweak in selectedTweaks.Reverse())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var context = CreateContext(operationId, hardware, snapshots.GetArtifactDirectory(snapshot.Id), request.Options.TargetUserSid ?? snapshot.Hardware?.InteractiveUserSid, true);
                    var actions = new List<(ITweakAction Action, ActionState Before)>();
                    foreach (var captured in tweak.Actions.AsEnumerable().Reverse())
                    {
                        var action = actionFactory.Create(tweak.Definition.Actions[captured.Index]);
                        await action.PreflightAsync(context, cancellationToken);
                        var drift = await action.VerifyAsync(captured.State, context, cancellationToken);
                        if (drift.Outcome != VerificationOutcome.Matches && !request.Options.Force && !tweak.Definition.ReadOnly) throw new FragForgeException("revert_drift", $"'{tweak.TweakId}' cambió desde la aplicación ({drift.Message}). Revisa el estado o usa --force para restaurar exactamente el snapshot.");
                        actions.Add((action, captured.State));
                    }
                    plans.Add(new RevertPlan(tweak, context, actions));
                }
                var snapshotFailed = false;
                foreach (var plan in plans)
                {
                    var localErrors = new List<string>();
                    foreach (var (action, before) in plan.Actions)
                    {
                        try
                        {
                            progress?.Report(new EngineProgress("revert", plan.Tweak.TweakId, 0.5, $"Restaurando {action.Kind}."));
                            await action.RevertAsync(before, plan.Context, CancellationToken.None);
                            var verification = await action.VerifyRevertAsync(before, plan.Context, CancellationToken.None);
                            if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException(verification.Message);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            localErrors.Add($"{plan.Tweak.TweakId}/{action.Kind}: {ex.Message}");
                            logger.LogError(ex, "Fallo al revertir {TweakId}.", plan.Tweak.TweakId);
                        }
                    }
                    errors.AddRange(localErrors);
                    critical |= localErrors.Count > 0;
                    snapshotFailed |= localErrors.Count > 0;
                    results.Add(new TweakApplyResult { Id = plan.Tweak.TweakId, State = localErrors.Count > 0 ? TweakState.Unknown : TweakState.NotApplied, Message = localErrors.Count > 0 ? "Reversión incompleta; revisa el journal y el snapshot." : "Estado previo restaurado y verificado.", Changed = localErrors.Count == 0, RollbackFailed = localErrors.Count > 0 });
                    await AppendAsync(operationId, localErrors.Count > 0 ? JournalEventType.RevertFailed : JournalEventType.RevertCompleted, plan.Tweak.TweakId, snapshot.Id, string.Join(" ", localErrors), CancellationToken.None);
                    if (localErrors.Count == 0)
                    {
                        var actionPlan = new ActionPlan(plan.Tweak.Definition, plan.Actions.Select(a => a.Action).ToList(), plan.Tweak.Actions.Select(a => a.State).ToList(), plan.Context, "");
                        await RunHooksAsync(ExperimentPhase.AfterRevert, operationId, snapshot.Id, actionPlan, hardware, snapshots.GetArtifactDirectory(snapshot.Id), CancellationToken.None, warnings, false);
                    }
                }
                var updatedEntries = await journal.ReadAllAsync(CancellationToken.None);
                var appliedIds = snapshotEntries.Where(e => e.TweakId is not null).Select(e => e.TweakId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var revertedIds = RevertedIds(updatedEntries, snapshot.Id);
                if (!snapshotFailed && (appliedIds.Length == 0 || appliedIds.All(revertedIds.Contains))) await AppendAsync(operationId, JournalEventType.SnapshotReverted, null, snapshot.Id, null, CancellationToken.None);
            }
        }
        catch (OperationCanceledException ex) { errors.Add("Reversión cancelada antes de iniciar el siguiente bloque: " + ex.Message); }
        catch (Exception ex) { errors.Add(ex.Message); logger.LogError(ex, "Falló la reversión {OperationId}.", operationId); }
        var success = errors.Count == 0 && !critical && results.All(r => !r.RollbackFailed);
        await AppendSafeAsync(operationId, success ? JournalEventType.OperationCompleted : JournalEventType.OperationFailed, null, request.SnapshotId, success ? "Reversión completada." : string.Join(" ", errors));
        return new BatchReport { OperationId = operationId, SnapshotId = request.SnapshotId, Success = success, HasCriticalFailure = critical, Results = results, Errors = errors, Warnings = warnings };
    }

    private async Task<TweakStatus> EvaluateAsync(TweakDefinition definition, HardwareProfile hardware, IReadOnlySet<string> owned, CancellationToken cancellationToken)
    {
        var condition = EvaluateConditions(definition, hardware);
        if (!condition.IsMet) return new(definition.Id, TweakState.NotApplicable, condition.Reason, [], []);
        try
        {
            var context = CreateContext("scan", hardware, null, hardware.InteractiveUserSid, false);
            var actions = definition.Actions.Select(actionFactory.Create).ToArray();
            foreach (var action in actions) await action.PreflightAsync(context, cancellationToken);
            var states = new List<ActionState>();
            var verifications = new List<VerificationResult>();
            foreach (var action in actions)
            {
                var before = await action.CaptureAsync(context, cancellationToken);
                states.Add(before);
                verifications.Add(await action.VerifyAsync(before, context, cancellationToken));
            }
            var observations = states.OfType<InspectionActionState>().SelectMany(s => s.Observations).ToArray();
            if (definition.ReadOnly) return new(definition.Id, TweakState.Inspected, "Solo lectura; no hay cambios que aplicar.", verifications, observations);
            var state = Classify(verifications, owned.Contains(definition.Id));
            var reason = string.Join(" ", verifications.Select(v => v.Message));
            return new(definition.Id, state, reason, verifications, observations);
        }
        catch (ActionNotApplicableException ex) { return new(definition.Id, TweakState.NotApplicable, ex.Message, [], []); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(definition.Id, TweakState.Unknown, ex.Message, [], []); }
    }

    private static TweakState Classify(IReadOnlyList<VerificationResult> results, bool owned)
    {
        if (results.Count == 0 || results.Any(r => r.Outcome == VerificationOutcome.Unknown)) return TweakState.Unknown;
        var matches = results.Count(r => r.Outcome == VerificationOutcome.Matches);
        if (matches == results.Count) return owned ? TweakState.Applied : TweakState.AppliedExternally;
        if (matches == 0 && results.All(r => r.Outcome == VerificationOutcome.DoesNotMatch)) return TweakState.NotApplied;
        return TweakState.PartiallyApplied;
    }

    private ConditionResult EvaluateConditions(TweakDefinition definition, HardwareProfile hardware)
    {
        foreach (var specification in definition.Conditions)
        {
            var result = conditionFactory.Create(specification).Evaluate(hardware);
            if (!result.IsKnown) return ConditionResult.Unknown(result.Reason);
            if (!result.IsMet) return result;
        }
        return ConditionResult.Met(definition.Conditions.Count == 0 ? "Sin condiciones restrictivas." : "Todas las condiciones se cumplen.");
    }

    private async Task<ActionPlan?> TryBuildPlanAsync(TweakDefinition definition, HardwareProfile hardware, ApplyOptions? options, string operationId, CancellationToken cancellationToken)
    {
        var context = CreateContext(operationId, hardware, null, options?.TargetUserSid ?? hardware.InteractiveUserSid, options?.AllowPowerShellActions ?? false);
        var actions = new List<ITweakAction>();
        try
        {
            foreach (var specification in definition.Actions)
            {
                var action = actionFactory.Create(specification);
                await action.PreflightAsync(context, cancellationToken);
                actions.Add(action);
            }
        }
        catch (ActionNotApplicableException) { return null; }
        return new ActionPlan(definition, actions, [], context, EvaluateConditions(definition, hardware).Reason);
    }

    private async Task<SystemSnapshot> CaptureAndPersistAsync(string operationId, string label, HardwareProfile hardware, IReadOnlyList<ActionPlan> plans, bool createRestorePoint, CancellationToken cancellationToken, IProgress<EngineProgress>? progress)
    {
        var snapshotId = NewId();
        var artifactDirectory = snapshots.GetArtifactDirectory(snapshotId);
        Directory.CreateDirectory(artifactDirectory);
        var captured = new List<TweakSnapshot>();
        var registryStates = new List<RegistryActionState>();
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = plan.Context with { OperationId = operationId, ArtifactDirectory = artifactDirectory, Progress = new ProgressAdapter(progress, plan.Definition.Id) };
            plan.Context = context;
            var actions = new List<CapturedAction>();
            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var state = await plan.Actions[i].CaptureAsync(context, cancellationToken);
                actions.Add(new CapturedAction(i, plan.Actions[i].Kind, state));
                if (state is RegistryActionState registry) registryStates.Add(registry);
            }
            captured.Add(new TweakSnapshot { TweakId = plan.Definition.Id, DefinitionSha256 = catalog.GetDefinitionHash(plan.Definition.Id), Definition = plan.Definition, Actions = actions });
            await AppendAsync(operationId, JournalEventType.TweakCaptureCompleted, plan.Definition.Id, snapshotId, null, cancellationToken);
        }
        if (registryStates.Count > 0) await RegistryBackupExporter.WriteAsync(Path.Combine(artifactDirectory, "registry-before.reg"), registryStates, cancellationToken);
        long? sequence = null;
        if (createRestorePoint && plans.Any(p => !p.Definition.ReadOnly))
        {
            progress?.Report(new EngineProgress("restore-point", null, 0.1, "Creando punto de restauración."));
            var restoreResult = await restore.CreateAsync("FragForge " + operationId[..8], cancellationToken);
            sequence = restoreResult.Sequence;
            await AppendAsync(operationId, JournalEventType.RestorePointCreated, null, snapshotId, restoreResult.Message, cancellationToken);
        }
        var snapshot = new SystemSnapshot { Id = snapshotId, Label = label, Hardware = hardware, RestorePointSequence = sequence, Tweaks = captured };
        await snapshots.SaveAsync(snapshot, cancellationToken);
        await AppendAsync(operationId, JournalEventType.SnapshotCreated, null, snapshotId, label, cancellationToken);
        return snapshot;
    }

    private async Task<IReadOnlyList<string>> RollbackAsync(string operationId, string snapshotId, string tweakId, IReadOnlyList<(ITweakAction Action, ActionState Before)> attempted, ActionExecutionContext context)
    {
        var errors = new List<string>();
        await AppendSafeAsync(operationId, JournalEventType.TweakRollbackStarted, tweakId, snapshotId, null);
        foreach (var (action, before) in attempted.AsEnumerable().Reverse())
        {
            try
            {
                await action.RevertAsync(before, context, CancellationToken.None);
                var verification = await action.VerifyRevertAsync(before, context, CancellationToken.None);
                if (verification.Outcome != VerificationOutcome.Matches) throw new VerificationFailedException(verification.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add($"{tweakId}/{action.Kind}: {ex.Message}"); }
        }
        await AppendSafeAsync(operationId, errors.Count == 0 ? JournalEventType.TweakRolledBack : JournalEventType.TweakRollbackFailed, tweakId, snapshotId, string.Join(" ", errors));
        return errors;
    }

    private async Task<TweakStatus> VerifyPlanAsync(ActionPlan plan, CancellationToken cancellationToken)
    {
        var states = new List<ActionState>();
        var results = new List<VerificationResult>();
        foreach (var action in plan.Actions)
        {
            var before = await action.CaptureAsync(plan.Context, cancellationToken);
            states.Add(before);
            results.Add(await action.VerifyAsync(before, plan.Context, cancellationToken));
        }
        plan.States.Clear();
        plan.States.AddRange(states);
        return new(plan.Definition.Id, Classify(results, false), string.Join(" ", results.Select(r => r.Message)), results, states.OfType<InspectionActionState>().SelectMany(s => s.Observations).ToArray());
    }

    private async Task RunHooksAsync(ExperimentPhase phase, string operationId, string snapshotId, ActionPlan plan, HardwareProfile hardware, string artifactDirectory, CancellationToken cancellationToken, List<string> warnings, bool failOnError)
    {
        foreach (var hook in _hooks)
        {
            try
            {
                var artifacts = await hook.RunAsync(new ExperimentContext(operationId, snapshotId, phase, plan.Definition, hardware, DateTimeOffset.UtcNow, artifactDirectory), cancellationToken);
                await AppendAsync(operationId, JournalEventType.HookCompleted, plan.Definition.Id, snapshotId, $"{phase}: {hook.GetType().Name}; artifacts={artifacts.Count}", cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (failOnError) throw new FragForgeException("experiment_hook_failed", $"El gancho {phase} falló antes de aplicar: {ex.Message}", ex);
                warnings.Add($"Gancho {phase}: {ex.Message}");
            }
        }
    }

    private ActionExecutionContext CreateContext(string operationId, HardwareProfile hardware, string? artifactDirectory, string? userSid, bool allowPowerShell) => new()
    {
        OperationId = operationId, Hardware = hardware, ArtifactDirectory = artifactDirectory, TargetUserSid = userSid, AllowPowerShellActions = allowPowerShell
    };

    private static HardwareProfile ApplyOverrides(HardwareProfile profile, string? userSid, string? cs2Path)
    {
        if (userSid is not null && !System.Text.RegularExpressions.Regex.IsMatch(userSid, @"^S-1-\d+(?:-\d+)+$")) throw new FragForgeException("invalid_sid", "SID de usuario inválido.");
        if (cs2Path is not null)
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cs2Path));
            if (!Path.GetFileName(full).Equals("cs2.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new FragForgeException("invalid_cs2_path", "La ruta manual debe apuntar a un cs2.exe existente.");
            profile = profile with { Games = new GameInfo { Cs2ExecutablePath = full, Cs2DetectionSource = "Manual", Cs2Candidates = [full] } };
        }
        if (userSid is not null) profile = profile with { InteractiveUserSid = userSid };
        return profile;
    }

    private async Task<HashSet<string>> ReadOwnershipAsync(CancellationToken cancellationToken)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var entries = await journal.ReadAllAsync(cancellationToken);
            var revertedSnapshots = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.EventType == JournalEventType.TweakApplied && entry.TweakId is not null && entry.SnapshotId is not null && !revertedSnapshots.Contains(entry.SnapshotId)) owned.Add(entry.TweakId);
                if (entry.EventType is JournalEventType.TweakRolledBack or JournalEventType.RevertCompleted && entry.TweakId is not null) owned.Remove(entry.TweakId);
            }
            foreach (var entry in entries.Where(e => e.EventType == JournalEventType.SnapshotReverted))
                foreach (var applied in entries.Where(e => e.SnapshotId == entry.SnapshotId && e.EventType == JournalEventType.TweakApplied && e.TweakId is not null)) owned.Remove(applied.TweakId!);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { logger.LogWarning(ex, "No se pudo leer el journal; el estado coincidente se marcará externo."); }
        return owned;
    }

    private static IReadOnlyList<(string SnapshotId, IReadOnlyList<string> Ids)> SelectRevertSnapshots(RevertRequest request, IReadOnlyList<JournalEntry> entries)
    {
        if (request.SnapshotId is not null) return [(request.SnapshotId, [])];
        var reverted = entries.Where(e => e.EventType == JournalEventType.RevertCompleted && e.TweakId is not null).Select(e => e.TweakId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshotReverted = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = entries.Where(e => e.EventType == JournalEventType.TweakApplied && e.TweakId is not null && e.SnapshotId is not null && !snapshotReverted.Contains(e.SnapshotId)).ToArray();
        if (request.Last)
        {
            var last = applied.LastOrDefault(e => request.Options.Force || !reverted.Contains(e.TweakId!));
            if (last is null) throw new FragForgeException("no_snapshot", "No hay un snapshot aplicado pendiente de revertir.");
            return [(last.SnapshotId!, [])];
        }
        var result = new List<(string, IReadOnlyList<string>)>();
        foreach (var id in request.Ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = applied.LastOrDefault(e => e.TweakId!.Equals(id, StringComparison.OrdinalIgnoreCase) && (request.Options.Force || !reverted.Contains(id)));
            if (entry is null) throw new FragForgeException("no_snapshot", $"No hay una aplicación propia reversible de '{id}'. No se deshacen cambios externos/Atlas sin un snapshot explícito.");
            result.Add((entry.SnapshotId!, [id]));
        }
        return result.GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, (IReadOnlyList<string>)g.SelectMany(x => x.Item2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
    }

    private static HashSet<string> RevertedIds(IReadOnlyList<JournalEntry> entries, string snapshotId)
    {
        var ids = entries.Where(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.RevertCompleted && e.TweakId is not null).Select(e => e.TweakId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entries.Any(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.SnapshotReverted))
            foreach (var entry in entries.Where(e => e.SnapshotId == snapshotId && e.EventType == JournalEventType.TweakApplied && e.TweakId is not null)) ids.Add(entry.TweakId!);
        return ids;
    }

    private async Task<BatchReport> FinishAsync(BatchReport report, bool success, CancellationToken cancellationToken)
    {
        await AppendSafeAsync(report.OperationId, success ? JournalEventType.OperationCompleted : JournalEventType.OperationFailed, null, report.SnapshotId, success ? "Operación completada." : string.Join(" ", report.Errors));
        return report;
    }
    private Task AppendAsync(string operationId, JournalEventType type, string? tweakId, string? snapshotId, string? message, CancellationToken cancellationToken) => journal.AppendAsync(new JournalEntry { EntryId = NewId(), OperationId = operationId, EventType = type, TweakId = tweakId, SnapshotId = snapshotId, Message = message }, cancellationToken);
    private async Task AppendSafeAsync(string operationId, JournalEventType type, string? tweakId, string? snapshotId, string? message)
    {
        try { await AppendAsync(operationId, type, tweakId, snapshotId, message, CancellationToken.None); }
        catch (Exception ex) { logger.LogCritical(ex, "No se pudo escribir el journal. No continúes aplicando cambios."); throw new RollbackException("El journal no pudo persistirse; revisa disco/permisos antes de continuar.", ex); }
    }
    private static TweakApplyResult Skipped(string id, TweakState state, string message) => new() { Id = id, State = state, Message = message };
    private static string NewId() => Guid.NewGuid().ToString("D");
    private sealed class ActionPlan(TweakDefinition definition, IReadOnlyList<ITweakAction> actions, List<ActionState> states, ActionExecutionContext context, string conditionReason)
    {
        public TweakDefinition Definition { get; } = definition;
        public IReadOnlyList<ITweakAction> Actions { get; } = actions;
        public List<ActionState> States { get; } = states;
        public ActionExecutionContext Context { get; set; } = context;
        public string ConditionReason { get; } = conditionReason;
    }
    private sealed record RevertPlan(TweakSnapshot Tweak, ActionExecutionContext Context, IReadOnlyList<(ITweakAction Action, ActionState Before)> Actions);
    private sealed class ProgressAdapter(IProgress<EngineProgress>? outer, string tweakId) : IProgress<ActionProgress>
    {
        public void Report(ActionProgress value) => outer?.Report(new EngineProgress(value.Stage, tweakId, value.Fraction, value.Message));
    }
}

internal static class ActionProgressExtensions
{
    public static void ReportReady(this ITweakAction action, ActionExecutionContext context, string stage, double fraction, string message) => context.Report(stage, fraction, message);
}
````

### `src/FragForge.Core/FragForge.Core.csproj`  <sub>(10 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <EmbeddedResource Include="Policy/denylist.json" />
    <None Include="Policy/denylist.json" CopyToOutputDirectory="PreserveNewest" TargetPath="Policy/denylist.json" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  </ItemGroup>
</Project>
````

### `src/FragForge.Core/Localization/Localizer.cs`  <sub>(22 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Logging;

namespace FragForge.Core.Localization;

public sealed class Localizer
{
    private readonly IReadOnlyDictionary<string, string> _strings;
    public string Culture { get; }
    public Localizer(string culture, IReadOnlyDictionary<string, string> strings)
    {
        Culture = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "es-MX";
        _strings = strings;
    }
    public string this[string key] => _strings.TryGetValue(key, out var value) ? value : key;
    public string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.GetCultureInfo(Culture), this[key], args);
    public static Localizer FromJson(string culture, string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Options) ?? throw new InvalidDataException("Localization file is empty.");
        return new Localizer(culture, values);
    }
}
````

### `src/FragForge.Core/Localization/UiLogSink.cs`  <sub>(36 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Core.Localization;

public sealed class UiLogSink : IUiLogSink
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _history = new();
    private readonly List<Action<string>> _subscribers = [];
    private readonly object _gate = new();
    public void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {message}";
        _history.Enqueue(line);
        while (_history.Count > 500 && _history.TryDequeue(out _)) { }
        lock (_gate) { foreach (var subscriber in _subscribers.ToArray()) subscriber(line); }
        MessageWritten?.Invoke(this, line);
    }
    public event EventHandler<string>? MessageWritten;
    public IReadOnlyList<string> History => _history.ToArray();
    public IObservable<string> Messages => new LogObservable(this);
    private sealed class LogObservable(UiLogSink owner) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            foreach (var line in owner._history.ToArray()) observer.OnNext(line);
            void Handler(string line) => observer.OnNext(line);
            lock (owner._gate) owner._subscribers.Add(Handler);
            return new Subscription(() => { lock (owner._gate) owner._subscribers.Remove(Handler); });
        }
    }
    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
````

### `src/FragForge.Core/Logging/JsonDefaults.cs`  <sub>(19 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FragForge.Core.Logging;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();
    public static JsonSerializerOptions Create(bool indented = true) => new()
    {
        WriteIndented = indented,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(null, allowIntegerValues: false) },
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
````

### `src/FragForge.Core/Logging/UiLogger.cs`  <sub>(22 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace FragForge.Core.Logging;

public sealed class UiLoggerProvider(IUiLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new UiLogger(categoryName, sink);
    public void Dispose() { }
    private sealed class UiLogger(string category, IUiLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"{category}: {formatter(state, exception)}";
            if (exception is not null) message += $" | {exception.GetType().Name}: {exception.Message}";
            sink.Write(logLevel.ToString(), message);
        }
    }
}
````

### `src/FragForge.Core/Models/ActionState.cs`  <sub>(124 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FragForge.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$actionState")]
[JsonDerivedType(typeof(RegistryActionState), "registry")]
[JsonDerivedType(typeof(ServiceActionState), "service")]
[JsonDerivedType(typeof(ScheduledTaskActionState), "scheduledTask")]
[JsonDerivedType(typeof(PowerPlanActionState), "powerPlan")]
[JsonDerivedType(typeof(BcdActionState), "bcd")]
[JsonDerivedType(typeof(NetworkPropertyActionState), "network")]
[JsonDerivedType(typeof(FileActionState), "file")]
[JsonDerivedType(typeof(ScriptActionState), "script")]
[JsonDerivedType(typeof(InspectionActionState), "inspection")]
public abstract record ActionState
{
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed record RegistryValueCapture
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public bool KeyExisted { get; init; }
    public bool ValueExisted { get; init; }
    public string? ValueKind { get; init; }
    public string? StringData { get; init; }
    public long? IntegerData { get; init; }
    public IReadOnlyList<string>? MultiStringData { get; init; }
    public string? BinaryBase64 { get; init; }
    public IReadOnlyList<string> MissingAncestors { get; init; } = [];
}

public sealed record RegistryActionState : ActionState
{
    public override string Kind => "Registry";
    public IReadOnlyList<RegistryValueCapture> Values { get; init; } = [];
}

public sealed record ServiceCapture
{
    public required string Name { get; init; }
    public bool Exists { get; init; }
    public string? StartMode { get; init; }
    public string? State { get; init; }
    public bool? DelayedAutoStart { get; init; }
    public bool DelayedAutoStartExisted { get; init; }
}

public sealed record ServiceActionState : ActionState
{
    public override string Kind => "Service";
    public required ServiceCapture Service { get; init; }
}

public sealed record ScheduledTaskActionState : ActionState
{
    public override string Kind => "ScheduledTask";
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public bool Enabled { get; init; }
    public string? XmlDefinition { get; init; }
}

public sealed record PowerSettingCapture(string SubgroupGuid, string SettingGuid, int AcIndex, int DcIndex);
public sealed record PowerPlanActionState : ActionState
{
    public override string Kind => "PowerPlan";
    public required string PlanName { get; init; }
    public string? PreviousActiveGuid { get; init; }
    public string? OwnedPlanGuid { get; init; }
    public bool PlanExisted { get; init; }
    public IReadOnlyList<PowerSettingCapture> Settings { get; init; } = [];
    public string? ExportArtifact { get; init; }
}

public sealed record BcdActionState : ActionState
{
    public override string Kind => "Bcdedit";
    public required string Element { get; init; }
    public bool Existed { get; init; }
    public string? PreviousValue { get; init; }
    public required string ExportArtifact { get; init; }
}

public sealed record NetworkPropertyCapture
{
    public required string AdapterName { get; init; }
    public required string Keyword { get; init; }
    public bool Exists { get; init; }
    public IReadOnlyList<string> RegistryValue { get; init; } = [];
    public string? DisplayValue { get; init; }
}
public sealed record NetworkPropertyActionState : ActionState
{
    public override string Kind => "NetAdapterProperty";
    public IReadOnlyList<NetworkPropertyCapture> Properties { get; init; } = [];
}

public sealed record FileActionState : ActionState
{
    public override string Kind => "FileEdit";
    public required string Path { get; init; }
    public bool Existed { get; init; }
    public string? ContentBase64 { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }
    public IReadOnlyList<string> MissingDirectories { get; init; } = [];
}

public sealed record ScriptActionState : ActionState
{
    public override string Kind => "PowerShell";
    public required string ScriptSha256 { get; init; }
    public required JsonElement CapturedJson { get; init; }
}

public sealed record InspectionActionState : ActionState
{
    public override string Kind => "InspectOnly";
    public IReadOnlyList<DiagnosticObservation> Observations { get; init; } = [];
}

public sealed record CapturedAction(int Index, string Kind, ActionState State);
````

### `src/FragForge.Core/Models/Enums.cs`  <sub>(15 líneas)</sub>

````csharp
namespace FragForge.Core.Models;

public enum EvidenceLevel { High, Medium, Low, Experimental }
public enum RiskLevel { Safe, Moderate, Advanced, Experimental }
public enum TweakState { NotApplied, Applied, PartiallyApplied, NotApplicable, Unknown, AppliedExternally, Inspected }
public enum TweakProfile { Safe, Balanced, Competitive, Advanced }
public enum VerificationOutcome { Matches, DoesNotMatch, Partial, Unknown }
public enum FeatureState { Unknown, Enabled, Disabled, NotPresent }
public enum MemoryChannelState { Unknown, Single, Dual, Other }
public enum GpuVendor { Unknown, Intel, Nvidia, Amd, Microsoft, Other }
public enum DiskKind { Unknown, Hdd, Ssd, Nvme, Scm }
public enum NetworkKind { Unknown, Ethernet, WiFi, Other }
public enum PreflightSeverity { Info, Warning, Blocking }
public enum ExperimentPhase { BeforeApply, AfterApply, AfterRevert }
public enum JournalEventType { OperationStarted, SnapshotCreated, RestorePointCreated, TweakCaptureCompleted, TweakApplyStarted, TweakApplied, TweakSkipped, TweakRollbackStarted, TweakRolledBack, TweakRollbackFailed, OperationCompleted, OperationFailed, RevertStarted, RevertCompleted, RevertFailed, SnapshotReverted, PolicyRejected, HookCompleted }
````

### `src/FragForge.Core/Models/FragForgeException.cs`  <sub>(20 líneas)</sub>

````csharp
namespace FragForge.Core.Models;

public class FragForgeException : Exception
{
    public string Code { get; }
    public FragForgeException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}
public sealed class CatalogValidationException(string message, IReadOnlyList<string> errors) : FragForgeException("catalog_invalid", message)
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
public sealed class ProtectedTargetException(string target, string reason) : FragForgeException("protected_target", $"El destino '{target}' está protegido: {reason}");
public sealed class ActionNotApplicableException(string message) : FragForgeException("action_not_applicable", message);
public sealed class VerificationFailedException(string message) : FragForgeException("verification_failed", message);
public sealed class PreflightFailedException(string message, PreflightReport report) : FragForgeException("preflight_failed", message)
{
    public PreflightReport Report { get; } = report;
}
public sealed class RollbackException(string message, Exception? inner = null) : FragForgeException("rollback_failed", message, inner);
public sealed class ProcessFailedException(string executable, int exitCode, string error) : FragForgeException("process_failed", $"'{executable}' terminó con código {exitCode}: {error}");
````

### `src/FragForge.Core/Models/HardwareProfile.cs`  <sub>(171 líneas)</sub>

````csharp
namespace FragForge.Core.Models;

public sealed record HardwareProfile
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public string MachineName { get; init; } = Environment.MachineName;
    public string? InteractiveUserName { get; init; }
    public string? InteractiveUserSid { get; init; }
    public string ProcessUserSid { get; init; } = "";
    public OsInfo Os { get; init; } = new();
    public CpuInfo Cpu { get; init; } = new();
    public MemoryInfo Memory { get; init; } = new();
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];
    public SecurityInfo Security { get; init; } = new();
    public PowerInfo Power { get; init; } = new();
    public FormFactorInfo FormFactor { get; init; } = new();
    public IReadOnlyList<PlaybookDetection> Playbooks { get; init; } = [];
    public IReadOnlyList<DeviceErrorInfo> DeviceErrors { get; init; } = [];
    public GameInfo Games { get; init; } = new();
    public SystemMaintenanceInfo Maintenance { get; init; } = new();
    public IReadOnlyList<DiagnosticObservation> Observations { get; init; } = [];
    public IReadOnlyList<string> ProbeWarnings { get; init; } = [];
    public bool IsUnsupportedPlatform { get; init; }
}

public sealed record OsInfo
{
    public string Name { get; init; } = "Unknown";
    public string Edition { get; init; } = "Unknown";
    public string Version { get; init; } = "Unknown";
    public int Build { get; init; }
    public int Ubr { get; init; }
    public string Architecture { get; init; } = "Unknown";
    public string Culture { get; init; } = "es-MX";
    public string TimeZone { get; init; } = "Unknown";
}

public sealed record CpuInfo
{
    public string Model { get; init; } = "Unknown";
    public string Vendor { get; init; } = "Unknown";
    public int PhysicalCores { get; init; }
    public int LogicalProcessors { get; init; }
    public int? PCores { get; init; }
    public int? ECores { get; init; }
    public bool? IsHybrid { get; init; }
    public string TopologySource { get; init; } = "Unknown";
    public bool? FirmwareVirtualizationEnabled { get; init; }
    public int? BaseClockMhz { get; init; }
}

public sealed record MemoryInfo
{
    public ulong TotalBytes { get; init; }
    public MemoryChannelState Channels { get; init; } = MemoryChannelState.Unknown;
    public string ChannelEvidence { get; init; } = "WMI does not expose memory-controller interleaving reliably.";
    public int? ConfiguredSpeedMtPerSecond { get; init; }
    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = [];
}

public sealed record MemoryModuleInfo
{
    public string? Manufacturer { get; init; }
    public string? PartNumber { get; init; }
    public string? BankLabel { get; init; }
    public string? DeviceLocator { get; init; }
    public ulong CapacityBytes { get; init; }
    public int? SpeedMtPerSecond { get; init; }
    public int? ConfiguredSpeedMtPerSecond { get; init; }
    public int? SmbiosMemoryType { get; init; }
}

public sealed record GpuInfo
{
    public string Name { get; init; } = "Unknown";
    public GpuVendor Vendor { get; init; }
    public string? DriverVersion { get; init; }
    public ulong? ReportedAdapterRamBytes { get; init; }
    public ulong? SharedMemoryBudgetBytes { get; init; }
    public bool IsIntegrated { get; init; }
    public string MemoryNote { get; init; } = "AdapterRAM is a firmware/reporting value, not a measurement of usable shared VRAM.";
}

public sealed record DiskInfo
{
    public string Model { get; init; } = "Unknown";
    public string? SerialNumber { get; init; }
    public ulong SizeBytes { get; init; }
    public DiskKind Kind { get; init; }
    public string Bus { get; init; } = "Unknown";
    public string Health { get; init; } = "Unknown";
    public bool? SmartPredictFailure { get; init; }
    public string SmartSource { get; init; } = "Unavailable";
    public ulong FreeBytes { get; init; }
    public string? SystemDrive { get; init; }
}

public sealed record NetworkAdapterInfo
{
    public string Name { get; init; } = "Unknown";
    public string Description { get; init; } = "Unknown";
    public NetworkKind Kind { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverProvider { get; init; }
    public ulong? LinkSpeedBitsPerSecond { get; init; }
    public bool IsPhysical { get; init; }
    public string State { get; init; } = "Unknown";
}

public sealed record MonitorInfo
{
    public string Name { get; init; } = "Unknown";
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? RefreshRateHz { get; init; }
    public string Source { get; init; } = "Unknown";
}

public sealed record SecurityInfo
{
    public FeatureState Vbs { get; init; } = FeatureState.Unknown;
    public FeatureState Hvci { get; init; } = FeatureState.Unknown;
    public FeatureState SecureBoot { get; init; } = FeatureState.Unknown;
    public FeatureState Tpm { get; init; } = FeatureState.Unknown;
    public string? TpmVersion { get; init; }
    public FeatureState Hags { get; init; } = FeatureState.Unknown;
    public FeatureState Mpo { get; init; } = FeatureState.Unknown;
    public string Evidence { get; init; } = "No probe has run.";
}

public sealed record PowerInfo
{
    public string? ActiveSchemeGuid { get; init; }
    public string? ActiveSchemeName { get; init; }
    public int? Pl1Watts { get; init; }
    public int? Pl2Watts { get; init; }
    public string PowerLimitSource { get; init; } = "Unavailable; DTT/IPF may own PL1/PL2.";
}

public sealed record FormFactorInfo
{
    public bool? IsPortable { get; init; }
    public bool HasBattery { get; init; }
    public IReadOnlyList<string> ChassisTypes { get; init; } = [];
    public string? SystemModel { get; init; }
    public string? BiosVersion { get; init; }
}

public sealed record PlaybookDetection(string Name, string Confidence, IReadOnlyList<string> Evidence);
public sealed record DeviceErrorInfo(string Name, string InstanceId, int ErrorCode, string? DriverName);
public sealed record GameInfo
{
    public string? Cs2ExecutablePath { get; init; }
    public string Cs2DetectionSource { get; init; } = "Not detected";
    public IReadOnlyList<string> Cs2Candidates { get; init; } = [];
    public bool Cs2Ambiguous { get; init; }
}
public sealed record SystemMaintenanceInfo
{
    public bool PendingReboot { get; init; }
    public IReadOnlyList<string> PendingRebootReasons { get; init; } = [];
    public bool WindowsServicingActive { get; init; }
    public IReadOnlyList<string> ServicingProcesses { get; init; } = [];
    public FeatureState SystemProtection { get; init; } = FeatureState.Unknown;
    public bool IsAdministrator { get; init; }
    public ulong SystemDriveFreeBytes { get; init; }
}
public sealed record DiagnosticObservation(string Key, string Value, string Source, string Confidence, string? Note = null);
````

### `src/FragForge.Core/Models/LocalizedText.cs`  <sub>(12 líneas)</sub>

````csharp
using System.Text.Json.Serialization;

namespace FragForge.Core.Models;

public sealed record LocalizedText
{
    [JsonPropertyName("es-MX")]
    public required string EsMx { get; init; }
    [JsonPropertyName("en-US")]
    public required string EnUs { get; init; }
    public string Resolve(string? culture) => culture?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? EnUs : EsMx;
}
````

### `src/FragForge.Core/Models/Results.cs`  <sub>(86 líneas)</sub>

````csharp
namespace FragForge.Core.Models;

public sealed record VerificationResult(VerificationOutcome Outcome, string Message)
{
    public static VerificationResult Match(string message = "El estado coincide.") => new(VerificationOutcome.Matches, message);
    public static VerificationResult Missing(string message) => new(VerificationOutcome.DoesNotMatch, message);
}

public sealed record ConditionResult(bool IsMet, bool IsKnown, string Reason)
{
    public static ConditionResult Met(string reason) => new(true, true, reason);
    public static ConditionResult Unmet(string reason) => new(false, true, reason);
    public static ConditionResult Unknown(string reason) => new(false, false, reason);
}

public sealed record TweakStatus(string Id, TweakState State, string Reason, IReadOnlyList<VerificationResult> ActionResults, IReadOnlyList<DiagnosticObservation> Observations);
public sealed record ScanReport(HardwareProfile Hardware, IReadOnlyList<TweakStatus> Tweaks, DateTimeOffset CreatedAt);
public sealed record EngineProgress(string Stage, string? TweakId, double Fraction, string Message);
public sealed record ActionProgress(string Stage, double Fraction, string Message);

public sealed record ApplyOptions
{
    public bool DryRun { get; init; }
    public bool Force { get; init; }
    public bool CreateRestorePoint { get; init; } = true;
    public bool EnableSystemProtection { get; init; }
    public TweakProfile Profile { get; init; } = TweakProfile.Safe;
    public IReadOnlySet<string> RiskAcknowledgements { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public string? AdvancedRiskPhrase { get; init; }
    public string? Cs2ExecutableOverride { get; init; }
    public string? TargetUserSid { get; init; }
    public bool AllowPowerShellActions { get; init; }
}

public sealed record RevertOptions
{
    public bool Force { get; init; }
    public string? TargetUserSid { get; init; }
    public string? Cs2ExecutableOverride { get; init; }
}

public sealed record RevertRequest
{
    public IReadOnlyList<string> Ids { get; init; } = [];
    public string? SnapshotId { get; init; }
    public bool Last { get; init; }
    public RevertOptions Options { get; init; } = new();
}

public sealed record DryRunChange(string TweakId, int ActionIndex, string Kind, string Description);
public sealed record TweakApplyResult
{
    public required string Id { get; init; }
    public required TweakState State { get; init; }
    public required string Message { get; init; }
    public bool Changed { get; init; }
    public bool RolledBack { get; init; }
    public bool RollbackFailed { get; init; }
    public IReadOnlyList<DryRunChange> PlannedChanges { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record BatchReport
{
    public required string OperationId { get; init; }
    public string? SnapshotId { get; init; }
    public bool DryRun { get; init; }
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public bool HasCriticalFailure { get; init; }
    public long? RestorePointSequence { get; init; }
    public IReadOnlyList<TweakApplyResult> Results { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PreflightCheck(string Id, PreflightSeverity Severity, string Message, string? Remediation = null);
public sealed record PreflightReport(bool CanApply, IReadOnlyList<PreflightCheck> Checks)
{
    public static PreflightReport Passed(IReadOnlyList<PreflightCheck> checks) => new(!checks.Any(c => c.Severity == PreflightSeverity.Blocking), checks);
}
public sealed record RestorePointResult(long? Sequence, string Message);
public sealed record ExperimentContext(string OperationId, string? SnapshotId, ExperimentPhase Phase, TweakDefinition Tweak, HardwareProfile Hardware, DateTimeOffset Timestamp, string? ArtifactDirectory = null);
public sealed record ExperimentArtifact(string Name, string Path, string ContentType);
public sealed record BenchmarkMeasurement(string Metric, double Value, string Unit, IReadOnlyDictionary<string, string> Metadata);
public sealed record BenchmarkSession(string Id, string Label, IReadOnlyList<BenchmarkMeasurement> Measurements, IReadOnlyList<ExperimentArtifact> Artifacts);
````

### `src/FragForge.Core/Models/Snapshot.cs`  <sub>(43 líneas)</sub>

````csharp
using System.Text.Json;

namespace FragForge.Core.Models;

public sealed record TweakSnapshot
{
    public required string TweakId { get; init; }
    public required string DefinitionSha256 { get; init; }
    public required TweakDefinition Definition { get; init; }
    public required IReadOnlyList<CapturedAction> Actions { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SystemSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string CreatedByVersion { get; init; } = "0.1.0";
    public HardwareProfile? Hardware { get; init; }
    public long? RestorePointSequence { get; init; }
    public IReadOnlyList<TweakSnapshot> Tweaks { get; init; } = [];
    public IReadOnlyList<string> Artifacts { get; init; } = [];
}

public sealed record JournalEntry
{
    public required string EntryId { get; init; }
    public required string OperationId { get; init; }
    public required JournalEventType EventType { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TweakId { get; init; }
    public string? SnapshotId { get; init; }
    public string? Message { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Data { get; init; }
    public string? PreviousHash { get; init; }
    public string Hash { get; init; } = "";
}

public sealed record JournalIntegrity(bool IsValid, int Entries, string? Error);
public sealed record SnapshotListItem(string Id, string Label, DateTimeOffset CreatedAt, int TweakCount, long? RestorePointSequence, bool Reverted);
````

### `src/FragForge.Core/Models/TweakDefinition.cs`  <sub>(51 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FragForge.Core.Models;

public sealed record TweakDefinition
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required LocalizedText Title { get; init; }
    public required LocalizedText Description { get; init; }
    public required EvidenceLevel Evidence { get; init; }
    public required RiskLevel Risk { get; init; }
    public bool RequiresReboot { get; init; }
    public bool RequiresAdmin { get; init; } = true;
    public IReadOnlyList<ConditionSpec> Conditions { get; init; } = [];
    public IReadOnlyList<ActionSpec> Actions { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public required ExpectedImpact ExpectedImpact { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public LocalizedText? Notes { get; init; }
    public LocalizedText? WhenNotToApply { get; init; }
    public bool ReadOnly => Actions.Count > 0 && Actions.All(a => a.Kind.Equals("InspectOnly", StringComparison.OrdinalIgnoreCase));
}

public sealed record ConditionSpec
{
    public required string Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Parameters { get; init; }
}

public sealed record ActionSpec
{
    public required string Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Parameters { get; init; }
}

public sealed record ExpectedImpact
{
    public string Fps { get; init; } = "None";
    [JsonPropertyName("1%lows")]
    public string OnePercentLows { get; init; } = "None";
    public string InputLatency { get; init; } = "None";
    public string Network { get; init; } = "None";
    public string Boot { get; init; } = "None";
    public string Thermals { get; init; } = "None";
}
````

### `src/FragForge.Core/Persistence/AtomicFile.cs`  <sub>(36 líneas)</sub>

````csharp
namespace FragForge.Core.Persistence;

public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException("Invalid file path.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 64 * 1024, leaveOpen: true);
                await using (writer.ConfigureAwait(false)) { await writer.WriteAsync(content.AsMemory(), cancellationToken); await writer.FlushAsync(cancellationToken); }
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException("Invalid file path.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false)) { await stream.WriteAsync(content, cancellationToken); await stream.FlushAsync(cancellationToken); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
````

### `src/FragForge.Core/Persistence/FileExecutionGate.cs`  <sub>(31 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Core.Persistence;

public sealed class FileExecutionGate(FragForgePaths paths) : IExecutionGate
{
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
                return new Lease(stream);
            }
            catch (IOException) { await Task.Delay(100, cancellationToken); }
            catch (UnauthorizedAccessException) { await Task.Delay(100, cancellationToken); }
        }
    }
    private sealed class Lease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
public sealed class NullExecutionGate : IExecutionGate
{
    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAsyncDisposable>(new Lease());
    private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
````

### `src/FragForge.Core/Persistence/FileJournal.cs`  <sub>(76 líneas)</sub>

````csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed class FileJournal(FragForgePaths paths) : IJournal
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Journal)!);
            string? previousHash = null;
            if (File.Exists(paths.Journal))
            {
                var lines = await File.ReadAllLinesAsync(paths.Journal, cancellationToken);
                if (lines.Length > 0)
                {
                    var last = JsonSerializer.Deserialize<JournalEntry>(lines[^1], JsonDefaults.Options) ?? throw new InvalidDataException("Journal corrupto.");
                    if (ComputeHash(last with { Hash = "" }) != last.Hash) throw new InvalidDataException("La última entrada del journal no pasó la verificación de integridad.");
                    previousHash = last.Hash;
                }
            }
            var unsigned = entry with { PreviousHash = previousHash, Hash = "" };
            var signed = unsigned with { Hash = ComputeHash(unsigned) };
            var line = JsonSerializer.Serialize(signed, JsonDefaults.Create(false));
            var stream = new FileStream(paths.Journal, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
                await using (writer.ConfigureAwait(false)) { await writer.WriteLineAsync(line.AsMemory(), cancellationToken); await writer.FlushAsync(cancellationToken); }
                await stream.FlushAsync(cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.Journal)) return [];
            var entries = new List<JournalEntry>();
            foreach (var line in await File.ReadAllLinesAsync(paths.Journal, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Línea vacía en journal.");
                entries.Add(JsonSerializer.Deserialize<JournalEntry>(line, JsonDefaults.Options) ?? throw new InvalidDataException("Entrada de journal nula."));
            }
            return entries;
        }
        finally { _gate.Release(); }
    }
    public async Task<JournalIntegrity> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await ReadAllAsync(cancellationToken);
            string? previous = null;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.PreviousHash != previous || ComputeHash(entry with { Hash = "" }) != entry.Hash) return new JournalIntegrity(false, i, $"Integridad inválida en entrada {i + 1}.");
                previous = entry.Hash;
            }
            return new JournalIntegrity(true, entries.Count, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { return new JournalIntegrity(false, 0, ex.Message); }
    }
    private static string ComputeHash(JournalEntry entry) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonDefaults.Create(false))))).ToLowerInvariant();
}
````

### `src/FragForge.Core/Persistence/FileSnapshotStore.cs`  <sub>(57 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed partial class FileSnapshotStore(FragForgePaths paths) : ISnapshotStore
{
    public string RootPath => paths.Snapshots;
    public string GetArtifactDirectory(string snapshotId) => Path.Combine(paths.Snapshots, ValidateId(snapshotId), "artifacts");
    public async Task SaveAsync(SystemSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateId(snapshot.Id);
        if (snapshot.SchemaVersion != 1) throw new InvalidDataException($"Unsupported snapshot schema {snapshot.SchemaVersion}.");
        var directory = Path.Combine(paths.Snapshots, snapshot.Id);
        Directory.CreateDirectory(directory);
        var artifacts = Directory.Exists(GetArtifactDirectory(snapshot.Id))
            ? Directory.GetFiles(GetArtifactDirectory(snapshot.Id), "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : [];
        var final = snapshot with { Artifacts = artifacts };
        var json = JsonSerializer.Serialize(final, JsonDefaults.Options);
        await AtomicFile.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), json, cancellationToken);
    }
    public async Task<SystemSnapshot> GetAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.Snapshots, ValidateId(snapshotId), "snapshot.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"No existe el snapshot '{snapshotId}'.", path);
        var snapshot = JsonSerializer.Deserialize<SystemSnapshot>(await File.ReadAllTextAsync(path, cancellationToken), JsonDefaults.Options) ?? throw new InvalidDataException("Snapshot vacío.");
        if (snapshot.SchemaVersion != 1 || snapshot.Id != snapshotId) throw new InvalidDataException("Snapshot version/identity mismatch.");
        foreach (var artifact in snapshot.Artifacts)
        {
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, artifact));
            if (!full.StartsWith(Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Artifact path escapes snapshot directory.");
        }
        return snapshot;
    }
    public async Task<IReadOnlyList<SystemSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.Snapshots)) return [];
        var result = new List<SystemSnapshot>();
        foreach (var directory in Directory.GetDirectories(paths.Snapshots).OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, "snapshot.json"))) result.Add(await GetAsync(Path.GetFileName(directory), cancellationToken));
        }
        return result.OrderByDescending(s => s.CreatedAt).ToArray();
    }
    private static string ValidateId(string id)
    {
        if (!IdRegex().IsMatch(id)) throw new ArgumentException("Identificador de snapshot inválido.", nameof(id));
        return id;
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$")]
    private static partial Regex IdRegex();
}
````

### `src/FragForge.Core/Persistence/FragForgePaths.cs`  <sub>(20 líneas)</sub>

````csharp
namespace FragForge.Core.Persistence;

public sealed class FragForgePaths
{
    public string Root { get; }
    public string Snapshots => Path.Combine(Root, "snapshots");
    public string Journal => Path.Combine(Root, "journal", "journal.ndjson");
    public string Logs => Path.Combine(Root, "logs");
    public string Reports => Path.Combine(Root, "reports");
    public string AdditionalPolicy => Path.Combine(Root, "policy", "denylist.json");
    public string LockFile => Path.Combine(Root, ".fragforge.lock");
    public FragForgePaths(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FragForge") : Path.GetFullPath(root);
    }
    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, Snapshots, Path.GetDirectoryName(Journal)!, Logs, Reports, Path.GetDirectoryName(AdditionalPolicy)! }) Directory.CreateDirectory(path);
    }
}
````

### `src/FragForge.Core/Persistence/ProtectionPolicy.cs`  <sub>(122 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed partial class ProtectionPolicy : IProtectionPolicy
{
    private readonly PolicyDocument _document;
    private ProtectionPolicy(PolicyDocument document) => _document = document;
    public static ProtectionPolicy CreateDefault()
    {
        using var stream = typeof(ProtectionPolicy).Assembly.GetManifestResourceStream("FragForge.Core.Policy.denylist.json") ?? throw new InvalidDataException("Embedded denylist is missing.");
        using var reader = new StreamReader(stream);
        var document = JsonSerializer.Deserialize<PolicyDocument>(reader.ReadToEnd(), JsonDefaults.Options) ?? throw new InvalidDataException("Embedded denylist is empty.");
        return new ProtectionPolicy(document);
    }
    public static async Task<ProtectionPolicy> LoadAsync(string? additionalPolicyPath, CancellationToken cancellationToken = default)
    {
        var policy = CreateDefault();
        if (string.IsNullOrWhiteSpace(additionalPolicyPath) || !File.Exists(additionalPolicyPath)) return policy;
        var extra = JsonSerializer.Deserialize<PolicyDocument>(await File.ReadAllTextAsync(additionalPolicyPath, cancellationToken), JsonDefaults.Options) ?? throw new InvalidDataException("Additional denylist is empty.");
        if (extra.SchemaVersion != 1) throw new InvalidDataException("Unsupported denylist schema version.");
        policy._document.Services.AddRange(extra.Services);
        policy._document.RegistryKeyPrefixes.AddRange(extra.RegistryKeyPrefixes);
        policy._document.RegistryValueNames.AddRange(extra.ValueNames());
        policy._document.TaskPrefixes.AddRange(extra.TaskPrefixes);
        policy._document.BcdDenyElements.AddRange(extra.BcdDenyElements);
        policy._document.FilePrefixes.AddRange(extra.FilePrefixes);
        policy._document.NetworkPropertyKeywords.AddRange(extra.NetworkPropertyKeywords);
        policy._document.PowerShellDeniedTokens.AddRange(extra.PowerShellDeniedTokens);
        return policy;
    }
    public void EnsureRegistryAllowed(string key, string? valueName = null)
    {
        var canonical = RegistryPath.Canonicalize(key);
        foreach (var prefix in _document.RegistryKeyPrefixes)
        {
            var normalized = NormalizePrefix(prefix);
            if (MatchesPrefix(canonical, normalized)) throw new ProtectedTargetException(canonical, "prefijo de clave protegida");
        }
        var serviceMatch = ServiceKeyRegex().Match(canonical);
        if (serviceMatch.Success) EnsureServiceAllowed(serviceMatch.Groups[1].Value);
        if (valueName is not null && _document.RegistryValueNames.Contains(valueName, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException($"{canonical}\\{valueName}", "valor de ejecución/seguridad protegido");
        if (canonical.Contains("\\Image File Execution Options\\", StringComparison.OrdinalIgnoreCase))
        {
            var image = canonical.Split("\\Image File Execution Options\\", StringSplitOptions.None)[1].Split('\\')[0];
            if (image.Equals("csrss.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("lsass.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("services.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("MsMpEng.exe", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(canonical, "imagen crítica de Windows");
        }
    }
    public void EnsureServiceAllowed(string name)
    {
        if (_document.Services.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException(name, "servicio esencial, de seguridad o protegido por el perfil Intel");
    }
    public void EnsureTaskAllowed(string path)
    {
        var normalized = "\\" + path.Trim().Trim('\\').Replace('/', '\\');
        if (_document.TaskPrefixes.Any(p => MatchesPrefix(normalized, "\\" + p.Trim('\\')))) throw new ProtectedTargetException(normalized, "tarea de seguridad/mantenimiento protegida");
    }
    public void EnsureBcdAllowed(string element)
    {
        if (_document.BcdDenyElements.Contains(element, StringComparer.OrdinalIgnoreCase) || !_document.BcdAllowedElements.Contains(element, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException(element, "elemento BCD no permitido por la política");
    }
    public void EnsureFileAllowed(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var key = PolicyPath(path);
        foreach (var prefix in _document.FilePrefixes)
        {
            var normalized = PolicyPath(prefix);
            if (key.Equals(normalized, StringComparison.OrdinalIgnoreCase) || key.StartsWith(normalized.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "ruta de sistema protegida");
            var expandedPrefix = Path.GetFullPath(Environment.ExpandEnvironmentVariables(prefix));
            if (full.Equals(expandedPrefix, StringComparison.OrdinalIgnoreCase) || full.StartsWith(expandedPrefix.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "ruta de sistema protegida");
        }
        var reduced = key.ToLowerInvariant();
        if (reduced.StartsWith("%systemroot%\\", StringComparison.OrdinalIgnoreCase) && !reduced.StartsWith("%systemroot%\\system32\\config\\systemprofile", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "solo se permiten archivos de configuración de usuario/juego, nunca rutas de Windows");
        if (full.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "FileEdit solo admite archivos de configuración de texto");
    }
    public void EnsurePowerShellAllowed(string script)
    {
        foreach (var token in _document.PowerShellDeniedTokens)
            if (script.Contains(token, StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException("PowerShell", $"token bloqueado '{token}'");
        if (script.Contains("http://", StringComparison.OrdinalIgnoreCase) || script.Contains("https://", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException("PowerShell", "acceso de red prohibido");
    }
    public void EnsureNetworkPropertyAllowed(string adapterName, string keyword)
    {
        if (_document.NetworkPropertyKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException($"{adapterName}:{keyword}", "propiedad de red fuera del alcance seguro de Capa 0");
        if (keyword.Length is 0 or > 128) throw new ProtectedTargetException(keyword, "se requiere una palabra clave exacta");
        var stripped = keyword.StartsWith('*') ? keyword[1..] : keyword;
        if (stripped.Contains('*') || stripped.Contains('?') || stripped.Length == 0) throw new ProtectedTargetException(keyword, "solo se admite el prefijo '*' del controlador; sin otros comodines");
    }
    private static string PolicyPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path).Replace('/', '\\');
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows)?.Replace('/', '\\');
        if (!string.IsNullOrEmpty(systemRoot)) expanded = expanded.Replace(systemRoot.TrimEnd('\\'), "%SystemRoot%", StringComparison.OrdinalIgnoreCase);
        var marker = expanded.IndexOf("\\windows\\", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) expanded = "%SystemRoot%" + expanded[(marker + "\\windows".Length)..];
        return expanded.Replace("%SYSTEMROOT%", "%SystemRoot%", StringComparison.OrdinalIgnoreCase).Replace("\\", "\\");
    }
    private static string NormalizePrefix(string prefix) => RegistryPath.Canonicalize(Environment.ExpandEnvironmentVariables(prefix));
    private static bool MatchesPrefix(string value, string prefix) => value.Equals(prefix, StringComparison.OrdinalIgnoreCase) || value.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex(@"^HKLM\\SYSTEM\\(?:CurrentControlSet|ControlSet\d{3})\\SERVICES\\([^\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceKeyRegex();
}

public sealed record PolicyDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<string> Services { get; init; } = [];
    public List<string> RegistryKeyPrefixes { get; init; } = [];
    public List<string> RegistryValueNames { get; init; } = [];
    public List<string> TaskPrefixes { get; init; } = [];
    public List<string> BcdDenyElements { get; init; } = [];
    public List<string> BcdAllowedElements { get; init; } = ["disabledynamictick"];
    public List<string> FilePrefixes { get; init; } = [];
    public List<string> NetworkPropertyKeywords { get; init; } = [];
    public List<string> PowerShellDeniedTokens { get; init; } = [];
    public IEnumerable<string> ValueNames() => RegistryValueNames;
}
````

### `src/FragForge.Core/Persistence/RegistryBackupExporter.cs`  <sub>(38 líneas)</sub>

````csharp
using System.Globalization;
using System.Text;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public static class RegistryBackupExporter
{
    public static async Task WriteAsync(string path, IEnumerable<RegistryActionState> states, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n; FragForge scoped value backup. Absent values are represented by value deletion, never whole-key deletion.\r\n");
        foreach (var state in states)
        foreach (var value in state.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append('[').Append(RegistryPath.ToFullRoot(value.Key)).Append("]\r\n");
            var name = value.Name.Length == 0 ? "@" : "\"" + Escape(value.Name) + "\"";
            if (!value.ValueExisted) { builder.Append(name).Append("=-\r\n\r\n"); continue; }
            builder.Append(name).Append('=');
            switch (value.ValueKind)
            {
                case "String": builder.Append('"').Append(Escape(value.StringData ?? "")).Append('"'); break;
                case "ExpandString": builder.Append("hex(2):").Append(BytesHex(Encoding.Unicode.GetBytes((value.StringData ?? "") + "\0"))); break;
                case "DWord": builder.Append("dword:").Append(((uint)(value.IntegerData ?? 0)).ToString("x8", CultureInfo.InvariantCulture)); break;
                case "QWord": builder.Append("hex(b):").Append(BytesHex(BitConverter.GetBytes(unchecked((ulong)(value.IntegerData ?? 0))))); break;
                case "Binary": builder.Append("hex:").Append(BytesHex(Convert.FromBase64String(value.BinaryBase64 ?? ""))); break;
                case "MultiString":
                    var text = string.Join("\0", value.MultiStringData ?? []) + "\0\0";
                    builder.Append("hex(7):").Append(BytesHex(Encoding.Unicode.GetBytes(text))); break;
                default: throw new InvalidDataException($"Unsupported registry kind '{value.ValueKind}'.");
            }
            builder.Append("\r\n\r\n");
        }
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.Unicode, cancellationToken);
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    private static string BytesHex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}
````

### `src/FragForge.Core/Persistence/RegistryPath.cs`  <sub>(44 líneas)</sub>

````csharp
namespace FragForge.Core.Persistence;

public static class RegistryPath
{
    public static string Canonicalize(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0') || key.Length > 32767) throw new ArgumentException("Invalid registry key.", nameof(key));
        var parts = key.Trim().TrimEnd('\\').Split('\\');
        parts[0] = parts[0].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => "HKLM",
            "HKEY_CURRENT_USER" or "HKCU" => "HKCU",
            "HKEY_USERS" or "HKU" => "HKU",
            "HKEY_CLASSES_ROOT" or "HKCR" => "HKCR",
            "HKEY_CURRENT_CONFIG" or "HKCC" => "HKCC",
            _ => throw new ArgumentException($"Unsupported registry root '{parts[0]}'.", nameof(key))
        };
        if (parts.Any(p => p is "." or "..")) throw new ArgumentException("Relative registry segments are not allowed.", nameof(key));
        return string.Join('\\', parts);
    }
    public static string ToFullRoot(string key)
    {
        var canonical = Canonicalize(key);
        return canonical.StartsWith("HKLM\\", StringComparison.Ordinal) ? "HKEY_LOCAL_MACHINE" + canonical[4..]
            : canonical.StartsWith("HKCU\\", StringComparison.Ordinal) ? "HKEY_CURRENT_USER" + canonical[4..]
            : canonical.StartsWith("HKU\\", StringComparison.Ordinal) ? "HKEY_USERS" + canonical[3..]
            : canonical.StartsWith("HKCR\\", StringComparison.Ordinal) ? "HKEY_CLASSES_ROOT" + canonical[4..]
            : "HKEY_CURRENT_CONFIG" + canonical[4..];
    }
    public static string ResolveUserHive(string key, string? userSid)
    {
        var canonical = Canonicalize(key);
        if (!canonical.StartsWith("HKCU\\", StringComparison.Ordinal)) return canonical;
        if (string.IsNullOrWhiteSpace(userSid)) throw new InvalidOperationException("The interactive user SID is required for HKCU actions.");
        if (!userSid.StartsWith("S-1-", StringComparison.Ordinal) || userSid.Contains('\\') || userSid.Contains('/') || userSid.Contains("..")) throw new ArgumentException("Invalid user SID.", nameof(userSid));
        return "HKU\\" + userSid + canonical[4..];
    }
    public static IEnumerable<string> AncestorsFromRoot(string key)
    {
        var canonical = Canonicalize(key);
        var parts = canonical.Split('\\');
        for (var i = parts.Length; i >= 2; i--) yield return string.Join('\\', parts.Take(i));
    }
}
````

### `src/FragForge.Core/Policy/denylist.json`  <sub>(53 líneas)</sub>

````json
{
  "schemaVersion": 1,
  "services": [
    "WinDefend", "WdNisSvc", "SecurityHealthService", "Sense", "WebThreatDefSvc", "wscsvc",
    "mpssvc", "BFE", "RpcSs", "DcomLaunch", "PlugPlay", "Power", "EventLog", "CryptSvc",
    "TrustedInstaller", "Winmgmt", "ProfSvc", "gpsvc", "SamSs", "Schedule", "SENS",
    "Dnscache", "Dhcp", "NlaSvc", "nsi", "netprofm", "NcbService", "BrokerInfrastructure",
    "CoreMessagingRegistrar", "StateRepository", "SystemEventsBroker", "TimeBrokerSvc",
    "TokenBroker", "UserManager", "ShellHwDetection", "wuauserv", "UsoSvc", "WaaSMedicSvc",
    "BITS", "DoSvc", "TBS", "KeyIso", "VaultSvc", "EFS", "iphlpsvc", "WinHttpAutoProxySvc",
    "ipfsvc", "jhi_service", "cplspcon", "WMIRegistrationService"
  ],
  "registryKeyPrefixes": [
    "HKLM\\SOFTWARE\\Microsoft\\Windows Defender",
    "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender",
    "HKLM\\SOFTWARE\\Policies\\Microsoft\\WindowsFirewall",
    "HKLM\\SYSTEM\\CurrentControlSet\\Services\\SharedAccess",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\SecureBoot",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\DeviceGuard",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Lsa",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\CI",
    "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management",
    "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System",
    "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate",
    "HKLM\\SOFTWARE\\Microsoft\\WindowsUpdate",
    "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate",
    "HKLM\\BCD00000000"
  ],
  "registryValueNames": [ "Debugger", "GlobalFlag", "VerifierDlls", "VerifierFlags", "MitigationOptions", "MitigationAuditOptions", "DisableExceptionChainValidation" ],
  "taskPrefixes": [
    "\\Microsoft\\Windows\\Windows Defender",
    "\\Microsoft\\Windows\\WindowsUpdate",
    "\\Microsoft\\Windows\\UpdateOrchestrator",
    "\\Microsoft\\Windows\\BitLocker",
    "\\Microsoft\\Windows\\TPM",
    "\\Microsoft\\Windows\\CertificateServicesClient"
  ],
  "bcdDenyElements": [ "testsigning", "nointegritychecks", "hypervisorlaunchtype", "nx", "pae", "safeboot", "bootstatuspolicy", "recoveryenabled", "isolatedcontext", "vsmlaunchtype", "loadoptions", "path", "device", "osdevice" ],
  "bcdAllowedElements": [ "disabledynamictick" ],
  "filePrefixes": [
    "%SystemRoot%\\System32\\config",
    "%SystemRoot%\\System32\\drivers",
    "%SystemRoot%\\System32\\drivers\\etc\\hosts",
    "%SystemRoot%\\System32\\DriverStore",
    "%SystemRoot%\\Boot",
    "%ProgramData%\\Microsoft\\Windows Defender",
    "%SystemDrive%\\Boot",
    "%SystemDrive%\\bootmgr",
    "%SystemDrive%\\$WINDOWS.~BT"
  ],
  "networkPropertyKeywords": [ "*DeviceSleepOnDisconnect", "*WakeOnMagicPacket", "*WakeOnPattern", "*NetworkAddress", "*FlowControl", "*RSS", "*RscIPv4", "*RscIPv6", "*InterruptModeration" ],
  "powerShellDeniedTokens": [ "Invoke-Expression", "IEX", "DownloadString", "DownloadFile", "Invoke-WebRequest", "Invoke-RestMethod", "FromBase64String", "EncodedCommand", "Add-Type", "Start-Process", "Restart-Computer", "Stop-Computer", "Set-MpPreference", "Set-NetFirewallProfile", "bcdedit", "reg.exe", "sc.exe", "vssadmin", "WinDefend", "mpssvc", "BFE", "ipfsvc", "jhi_service", "cplspcon", "WMIRegistrationService" ]
}
````

### `src/FragForge.Core/Preflight/PreflightService.cs`  <sub>(34 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Core.Preflight;

public sealed class PreflightService(ISystemRestoreService restore, ITweakCatalog catalog) : IPreflightService
{
    public const ulong MinimumFreeBytes = 512UL * 1024 * 1024;
    public async Task<PreflightReport> CheckAsync(ApplyOptions options, HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        var checks = new List<PreflightCheck>();
        if (options.DryRun)
        {
            checks.Add(new("dry-run", PreflightSeverity.Info, "Simulación: no se modificará el sistema ni se creará un punto de restauración."));
            return PreflightReport.Passed(checks);
        }
        checks.Add(profile.IsUnsupportedPlatform ? new("platform", PreflightSeverity.Blocking, "Las acciones reales requieren Windows 11 x64.", "Ejecuta FragForge en el equipo objetivo.") : new("platform", PreflightSeverity.Info, "Plataforma Windows detectada."));
        checks.Add(profile.Os.Build is >= 26100 and <= 26299 ? new("os", PreflightSeverity.Info, $"Windows 11 build {profile.Os.Build} dentro del rango validado de Capa 0.") : new("os", PreflightSeverity.Blocking, $"Build {profile.Os.Build} fuera del rango 26100–26299.", "Actualiza el catálogo y valida compatibilidad antes de aplicar."));
        checks.Add(profile.Maintenance.IsAdministrator ? new("admin", PreflightSeverity.Info, "Proceso elevado.") : new("admin", PreflightSeverity.Blocking, "Se requieren privilegios de administrador.", "Acepta UAC o inicia la terminal como administrador."));
        if (profile.Maintenance.PendingReboot) checks.Add(new("reboot", PreflightSeverity.Blocking, "Hay un reinicio pendiente: " + string.Join("; ", profile.Maintenance.PendingRebootReasons), "Reinicia Windows y vuelve a escanear. Force no omite esta protección."));
        if (profile.Maintenance.WindowsServicingActive) checks.Add(new("servicing", PreflightSeverity.Blocking, "Windows Update/COMPONENTES está activo: " + string.Join(", ", profile.Maintenance.ServicingProcesses), "Espera a que termine TiWorker/TrustedInstaller; no los detengas."));
        checks.Add(profile.Maintenance.SystemDriveFreeBytes >= MinimumFreeBytes ? new("disk", PreflightSeverity.Info, $"Espacio libre: {profile.Maintenance.SystemDriveFreeBytes / 1024 / 1024} MiB.") : new("disk", PreflightSeverity.Blocking, "Menos de 512 MiB libres en la unidad de sistema.", "Libera espacio antes de crear snapshots o puntos de restauración."));
        checks.Add(catalog.Definitions.Count > 0 ? new("catalog", PreflightSeverity.Info, $"Catálogo válido: {catalog.Definitions.Count} tweaks.") : new("catalog", PreflightSeverity.Blocking, "El catálogo está vacío."));
        if (options.CreateRestorePoint)
        {
            var state = await restore.GetProtectionStateAsync(cancellationToken);
            if (state == FeatureState.Enabled) checks.Add(new("restore", PreflightSeverity.Info, "Protección del sistema habilitada."));
            else if (options.EnableSystemProtection && state != FeatureState.NotPresent) checks.Add(new("restore", PreflightSeverity.Warning, "La protección del sistema se habilitará para la unidad de sistema porque lo solicitaste explícitamente."));
            else checks.Add(new("restore", PreflightSeverity.Blocking, state == FeatureState.NotPresent ? "System Restore no está disponible en esta edición." : "La protección del sistema está desactivada o no pudo confirmarse.", "Habilítala en Protección del sistema o usa --enable-system-protection; --no-restore-point solo omite el punto, no el snapshot/journal."));
        }
        else checks.Add(new("restore", PreflightSeverity.Warning, "Punto de restauración omitido por opción explícita. El snapshot y journal siguen siendo obligatorios."));
        return PreflightReport.Passed(checks);
    }
}
````

