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
