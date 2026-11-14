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

    /// <summary>
    /// Membresía EXPLÍCITA en perfiles. Si está presente (aunque sea <c>[]</c>), la membresía es
    /// exactamente esta lista y <see cref="FragForge.Core.Engine.ProfileSelector"/> no aplica la regla
    /// por riesgo/evidencia. <c>[]</c> significa "en ningún perfil": solo se puede aplicar por id.
    /// Ausente (<c>null</c>) = rige la regla por riesgo/evidencia.
    ///
    /// Gobierna SOLO la autoselección. Aplicar un tweak por id explícito sigue permitido en cualquier
    /// perfil (decisión del usuario): el motor no rechaza por este campo.
    /// </summary>
    public IReadOnlyList<string>? Profiles { get; init; }
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
