using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public static class Spec
{
    public static ActionSpec Action(string kind, object? parameters = null) => new() { Kind = kind, Parameters = parameters is null ? default : JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options) };
    public static ConditionSpec Condition(string type, object? parameters = null) => new() { Type = type, Parameters = parameters is null ? default : JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options) };
    public static object Registry(params object[] values) => new { values };
    public static object Value(string key, string name, string kind, object value, string mode = "Set") => new { key, name, kind, value, mode };
}

public static class Tweak
{
    public static TweakDefinition Create(string id, ActionSpec action, RiskLevel risk = RiskLevel.Safe, EvidenceLevel evidence = EvidenceLevel.High, IReadOnlyList<ConditionSpec>? conditions = null, IReadOnlyList<string>? dependsOn = null, IReadOnlyList<string>? conflictsWith = null, IReadOnlyList<string>? tags = null, bool requiresReboot = false, bool requiresAdmin = true) => new()
    {
        Id = id,
        Category = "Latency",
        Title = new LocalizedText { EsMx = "Prueba " + id, EnUs = "Test " + id },
        Description = new LocalizedText { EsMx = "Descripción " + id, EnUs = "Description " + id },
        Evidence = evidence,
        Risk = risk,
        RequiresReboot = requiresReboot,
        RequiresAdmin = requiresAdmin,
        Conditions = conditions ?? [],
        Actions = [action],
        Tags = tags ?? [],
        ConflictsWith = conflictsWith ?? [],
        DependsOn = dependsOn ?? [],
        ExpectedImpact = new ExpectedImpact(),
        References = ["https://example.invalid/test"],
        Notes = new LocalizedText { EsMx = "Notas", EnUs = "Notes" },
        WhenNotToApply = new LocalizedText { EsMx = "No aplicar", EnUs = "Do not apply" }
    };
}

public sealed record ScriptedActionOptions
{
    public bool FailApply { get; init; }
    public bool FailVerify { get; init; }
    public bool FailRevert { get; init; }
    public Action<string>? OnRevert { get; init; }
}

public sealed class ScriptedAction(ScriptedActionOptions options, List<string> trace, string label) : ITweakAction
{
    private bool _applied;
    public string Kind => "Fake";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult<ActionState>(new InspectionActionState());
    public Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("apply:" + label);
        if (options.FailApply) throw new VerificationFailedException("apply failed (fake)");
        _applied = true;
        return Task.CompletedTask;
    }
    public Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("verify:" + label);
        if (options.FailVerify) return Task.FromResult(VerificationResult.Missing("verify failed (fake)"));
        return Task.FromResult(_applied ? VerificationResult.Match("ok (fake)") : VerificationResult.Missing("not applied yet (fake)"));
    }
    public Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        trace.Add("revert:" + label);
        options.OnRevert?.Invoke(label);
        if (options.FailRevert) throw new InvalidOperationException("revert failed (fake)");
        _applied = false;
        return Task.CompletedTask;
    }
    public Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(VerificationResult.Match("reverted (fake)"));
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult("fake dry-run: " + label);
}

public sealed class FakeActionFactory(IRegistry registry, IProtectionPolicy policy, Dictionary<string, ScriptedActionOptions>? scripts = null) : ITweakActionFactory
{
    public List<string> Trace { get; } = [];
    public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Registry", "Fake" };
    public ITweakAction Create(ActionSpec specification)
    {
        if (specification.Kind.Equals("Registry", StringComparison.OrdinalIgnoreCase)) return new FragForge.Actions.Registry.RegistryAction(registry, policy, specification);
        var id = specification.Parameters.ValueKind == JsonValueKind.Undefined ? "fake" : specification.Parameters.GetProperty("id").GetString() ?? "fake";
        var options = scripts is not null && scripts.TryGetValue(id, out var scripted) ? scripted : new ScriptedActionOptions();
        return new ScriptedAction(options, Trace, id);
    }
}

public sealed class NullExperimentHook : IExperimentHook
{
    public int Order => 0;
    public Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExperimentArtifact>>([]);
}

public sealed class ThrowingExperimentHook : IExperimentHook
{
    public int Order => 0;
    public Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("hook failed (fake)");
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; }
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fragforge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Catálogo mínimo para tests: los IDs indicados con una acción InspectOnly (solo lectura) y sin
/// requisitos de reinicio. No toca el catálogo embebido.
/// </summary>
public sealed class FakeCatalog : ITweakCatalog
{
    private readonly Dictionary<string, TweakDefinition> _definitions;

    public FakeCatalog(params string[] ids)
    {
        _definitions = ids.ToDictionary(
            id => id,
            id => new TweakDefinition
            {
                Id = id,
                Category = "test",
                Title = new LocalizedText { EsMx = id, EnUs = id },
                Description = new LocalizedText { EsMx = id, EnUs = id },
                Evidence = EvidenceLevel.High,
                Risk = RiskLevel.Safe,
                RequiresReboot = false,
                Actions = [new ActionSpec { Kind = "InspectOnly" }],
                ExpectedImpact = new ExpectedImpact()
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<TweakDefinition> Definitions => _definitions.Values.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    public TweakDefinition Get(string id) => _definitions.TryGetValue(id, out var definition) ? definition : throw new KeyNotFoundException(id);
    public bool TryGet(string id, out TweakDefinition? definition) => _definitions.TryGetValue(id, out definition);
    public string GetDefinitionHash(string id) => _definitions.ContainsKey(id) ? "fake-hash" : throw new KeyNotFoundException(id);
}
