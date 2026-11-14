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
    /// <summary>
    /// Build de Windows en el momento de la captura, como texto. Campo ADITIVO y opcional (Capa 2):
    /// sirve para atribuir un drift a una actualización de características. Los snapshots antiguos
    /// no lo tienen y se cargan igual (null).
    /// </summary>
    public string? HardwareBuild { get; init; }
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
