using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Registry;

public sealed class IfeoPerfOptionsAction : ITweakAction
{
    private readonly RegistryAction _inner;
    public string Kind => "IfeoPerfOptions";
    public IfeoPerfOptionsAction(IRegistry registry, IProtectionPolicy policy, ActionSpec specification)
    {
        var p = ActionParameters.Parse<IfeoParameters>(specification);
        if (string.IsNullOrWhiteSpace(p.ImageName) || p.ImageName.Contains('\\') || p.ImageName.Contains('/') || p.ImageName.Contains('*') || p.ImageName.Contains('?')) throw new CatalogValidationException("IFEO requires an exact image file name.", [p.ImageName]);
        if (p.PriorityClass is not (1 or 2 or 3 or 5 or 6)) throw new CatalogValidationException("Realtime (4) and unknown CPU priority classes are forbidden.", [p.PriorityClass.ToString()]);
        var parameters = new RegistryActionParameters { Values = [new RegistryValueTarget { Key = $@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{p.ImageName}\PerfOptions", Name = "CpuPriorityClass", Kind = "DWord", Value = JsonSerializer.SerializeToElement(p.PriorityClass) }] };
        _inner = new RegistryAction(registry, policy, parameters);
    }
    public Task PreflightAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.PreflightAsync(c, t);
    public Task<ActionState> CaptureAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.CaptureAsync(c, t);
    public Task ApplyAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.ApplyAsync(s, c, t);
    public Task<VerificationResult> VerifyAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.VerifyAsync(s, c, t);
    public Task RevertAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.RevertAsync(s, c, t);
    public Task<VerificationResult> VerifyRevertAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.VerifyRevertAsync(s, c, t);
    public Task<string> DryRunAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.DryRunAsync(c, t);
}
internal sealed record IfeoParameters { public string ImageName { get; init; } = "cs2.exe"; public int PriorityClass { get; init; } = 3; }
