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
