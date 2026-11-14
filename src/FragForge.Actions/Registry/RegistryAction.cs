using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Actions.Registry;

public class RegistryAction : ITweakAction
{
    private readonly IRegistry _registry;
    private readonly IProtectionPolicy _policy;
    private readonly RegistryActionParameters _parameters;
    public virtual string Kind => "Registry";
    public RegistryAction(IRegistry registry, IProtectionPolicy policy, ActionSpec specification)
    {
        _registry = registry;
        _policy = policy;
        _parameters = ActionParameters.Parse<RegistryActionParameters>(specification);
        if (_parameters.Values.Count == 0) throw new CatalogValidationException("Registry action requires at least one value.", ["values"]);
        foreach (var value in _parameters.Values)
        {
            RegistryPath.Canonicalize(value.Key);
            if (!RegistryValueConverter.SupportedKinds.Contains(value.Kind)) throw new CatalogValidationException($"Unsupported registry kind '{value.Kind}'.", [value.Kind]);
            if (value.Mode is not ("Set" or "AddToken")) throw new CatalogValidationException($"Unsupported registry mode '{value.Mode}'.", [value.Mode]);
            if (value.Mode == "AddToken" && (value.Kind != "String" || value.Value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(value.Value.GetString()) || value.Value.GetString()!.Contains('\n'))) throw new CatalogValidationException("AddToken requires a single-line String token.", [value.Name]);
            RegistryValueConverter.FromJson(value.Kind, value.Value);
            policy.EnsureRegistryAllowed(value.Key, value.Name);
        }
    }
    internal RegistryAction(IRegistry registry, IProtectionPolicy policy, RegistryActionParameters parameters)
    {
        _registry = registry; _policy = policy; _parameters = parameters;
        foreach (var value in parameters.Values) policy.EnsureRegistryAllowed(value.Key, value.Name);
    }
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var target in _parameters.Values)
        {
            var key = RegistryPath.ResolveUserHive(ActionParameters.ResolveToken(target.Key, context), context.TargetUserSid);
            var name = ActionParameters.ResolveToken(target.Name, context);
            _policy.EnsureRegistryAllowed(key, name);
        }
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var captures = new List<RegistryValueCapture>();
        foreach (var target in ResolveTargets(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var missing = new List<string>();
            foreach (var ancestor in RegistryPath.AncestorsFromRoot(target.Key))
                if (!await _registry.KeyExistsAsync(ancestor, cancellationToken)) missing.Add(ancestor);
            captures.Add(RegistryValueConverter.Capture(target.Key, target.Name, result, missing));
        }
        context.Report("capture", 0.25, $"{captures.Count} valores capturados, incluidos ausentes.");
        return new RegistryActionState { Values = captures };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) throw new ArgumentException("Registry snapshot required.", nameof(before));
        var targets = ResolveTargets(context);
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            if (target.Spec.Mode == "AddToken")
            {
                var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
                var original = current.ValueExists && current.Kind == "String" ? current.Data as string ?? "" : "";
                var token = desired.Data as string ?? throw new InvalidOperationException("Token missing.");
                if (HasToken(original, token)) continue;
                desired = new RegistryWriteValue("String", original.Length == 0 ? token : original + (original.EndsWith(' ') ? "" : " ") + token);
            }
            await _registry.WriteValueAsync(target.Key, target.Name, desired, cancellationToken);
            context.Report("apply", (double)(i + 1) / targets.Count, $"{target.Key}\\{target.Name} escrito.");
        }
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) return new(VerificationOutcome.Unknown, "Snapshot de registro inválido.");
        var targets = ResolveTargets(context);
        var matches = 0;
        var messages = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            bool ok;
            if (target.Spec.Mode == "AddToken")
            {
                var token = (string)desired.Data;
                var original = state.Values[i].ValueExisted ? state.Values[i].StringData ?? "" : "";
                ok = current.ValueExists && current.Kind == "String" && current.Data is string text && HasToken(text, token) && (original.Length == 0 || text.StartsWith(original, StringComparison.Ordinal));
            }
            else ok = RegistryValueConverter.DataEqual(desired, current);
            if (ok) matches++; else messages.Add($"{target.Key}\\{target.Name} no coincide.");
        }
        return matches == targets.Count ? VerificationResult.Match($"Registro: {matches}/{targets.Count} valores coinciden.") : new VerificationResult(matches == 0 ? VerificationOutcome.DoesNotMatch : VerificationOutcome.Partial, string.Join(" ", messages));
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) throw new ArgumentException("Registry snapshot required.", nameof(before));
        foreach (var capture in state.Values.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            _policy.EnsureRegistryAllowed(capture.Key, capture.Name);
            if (capture.ValueExisted) await _registry.WriteValueAsync(capture.Key, capture.Name, RegistryValueConverter.ToWriteValue(capture), cancellationToken);
            else await _registry.DeleteValueAsync(capture.Key, capture.Name, cancellationToken);
            foreach (var missing in capture.MissingAncestors)
            {
                var parent = missing[..missing.LastIndexOf('\\')];
                var child = missing[(missing.LastIndexOf('\\') + 1)..];
                _policy.EnsureRegistryAllowed(missing);
                await _registry.DeleteEmptySubKeyAsync(parent, child, cancellationToken);
            }
        }
        context.Report("revert", 1, "Registro restaurado exactamente; valores ausentes eliminados.");
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var after = await CaptureAsync(context, cancellationToken);
        return Core.Engine.ActionStateEquivalence.Equivalent(before, after) ? VerificationResult.Match("Registro idéntico al estado capturado.") : VerificationResult.Missing("El registro no volvió exactamente al estado capturado.");
    }
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        foreach (var target in ResolveTargets(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            var previous = current.ValueExists ? $"{current.Kind}={current.Data}" : "(no existía)";
            lines.Add($"{target.Key}\\{target.Name}: {previous} -> {target.Spec.Kind}={RegistryValueConverter.Describe(desired)} ({target.Spec.Mode}); revert restaura el valor previo o lo elimina.");
        }
        return string.Join(Environment.NewLine, lines);
    }
    private IReadOnlyList<(string Key, string Name, RegistryValueTarget Spec)> ResolveTargets(ActionExecutionContext context) => _parameters.Values.Select(v => (RegistryPath.ResolveUserHive(ActionParameters.ResolveToken(v.Key, context), context.TargetUserSid), ActionParameters.ResolveToken(v.Name, context), v)).ToArray();
    private static bool HasToken(string value, string token) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);
}
