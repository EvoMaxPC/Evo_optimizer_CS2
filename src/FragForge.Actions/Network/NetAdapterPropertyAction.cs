using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

public sealed class NetAdapterPropertyAction(INetworkAdapterController controller, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly NetworkParameters _parameters = ActionParameters.Parse<NetworkParameters>(specification);
    public string Kind => "NetAdapterProperty";
    public async Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        if (_parameters.Properties.Count == 0) throw new CatalogValidationException("Network action requires properties.", ["properties"]);
        foreach (var property in _parameters.Properties)
        {
            policy.EnsureNetworkPropertyAllowed(adapter, property.Keyword);
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            if (!current.Exists) throw new ActionNotApplicableException($"El adaptador {adapter} no expone '{property.Keyword}'.");
        }
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        var captures = new List<NetworkPropertyCapture>();
        foreach (var property in _parameters.Properties) captures.Add(await controller.GetAsync(adapter, property.Keyword, cancellationToken));
        return new NetworkPropertyActionState { Properties = captures };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        foreach (var property in _parameters.Properties) { policy.EnsureNetworkPropertyAllowed(adapter, property.Keyword); await controller.SetAsync(adapter, property.Keyword, property.Value, cancellationToken); }
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        foreach (var property in _parameters.Properties)
        {
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            if (!current.Exists || !current.RegistryValue.SequenceEqual(property.Value, StringComparer.Ordinal)) return VerificationResult.Missing($"{adapter}:{property.Keyword} no coincide.");
        }
        return VerificationResult.Match("Propiedades avanzadas del adaptador verificadas.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        foreach (var property in ((NetworkPropertyActionState)before).Properties.AsEnumerable().Reverse())
        {
            policy.EnsureNetworkPropertyAllowed(property.AdapterName, property.Keyword);
            if (!property.Exists) throw new InvalidOperationException("Cannot remove an advanced property; capture required an existing property.");
            await controller.SetAsync(property.AdapterName, property.Keyword, property.RegistryValue, cancellationToken);
        }
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Propiedades de red restauradas.") : VerificationResult.Missing("Las propiedades de red no volvieron al estado capturado.");
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        var lines = new List<string>();
        foreach (var property in _parameters.Properties)
        {
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            lines.Add($"{adapter}:{property.Keyword}: [{string.Join(",", current.RegistryValue)}] -> [{string.Join(",", property.Value)}]. Puede renegociar el enlace; revert restaura el valor previo.");
        }
        return string.Join(Environment.NewLine, lines);
    }
    private string ResolveAdapter(ActionExecutionContext context)
    {
        var name = _parameters.AdapterName;
        if (!name.StartsWith("{AdapterDescription:", StringComparison.Ordinal)) return name;
        var description = name["{AdapterDescription:".Length..^1];
        var matches = context.Hardware.NetworkAdapters.Where(a => a.IsPhysical && a.Description.Contains(description, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ActionNotApplicableException($"Se requiere exactamente un adaptador físico '{description}'; encontrados {matches.Length}.");
        return matches[0].Name;
    }
}
internal sealed record NetworkParameters { public required string AdapterName { get; init; } public IReadOnlyList<NetworkPropertyTarget> Properties { get; init; } = []; }
internal sealed record NetworkPropertyTarget { public required string Keyword { get; init; } public IReadOnlyList<string> Value { get; init; } = []; }
