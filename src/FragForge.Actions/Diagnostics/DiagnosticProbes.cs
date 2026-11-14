using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Diagnostics;

public sealed class RegistryDiagnosticProbe(string key, IRegistry registry, string path, string name, Func<RegistryReadResult, (string Value, string Note)> interpret) : IDiagnosticProbe
{
    public string Key { get; } = key;
    public async Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        var result = await registry.ReadValueAsync(path, name, cancellationToken);
        var (value, note) = interpret(result);
        return new DiagnosticObservation(Key, value, path + "\\" + name, result.ValueExists ? "High" : "Unknown", note);
    }
}
public sealed class ProfileDiagnosticProbe(string key, Func<HardwareProfile, DiagnosticObservation> read) : IDiagnosticProbe
{
    public string Key { get; } = key;
    public Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(read(profile));
}
public static class DiagnosticProbeFactory
{
    /// <summary>
    /// Registros individuales de las sondas por defecto. Se exponen como fábricas (no como instancias fijas)
    /// para que cada sonda se registre por separado en el contenedor y <c>IEnumerable&lt;IDiagnosticProbe&gt;</c>
    /// se componga de registros independientes (Capa 1, Fase 0.2).
    /// </summary>
    public static IReadOnlyList<Func<IRegistry, IDiagnosticProbe>> Registrations { get; } =
    [
        registry => new RegistryDiagnosticProbe("hags", registry, @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", r => !r.ValueExists ? ("Unknown/default", "Ausente significa comportamiento predeterminado del controlador; no equivale necesariamente a desactivado.") : (Convert.ToString(r.Data) == "2" ? "Enabled" : Convert.ToString(r.Data) == "1" ? "Disabled" : $"Unknown ({r.Data})", "HwSchMode observado; no se modifica.")),
        registry => new RegistryDiagnosticProbe("mpo", registry, @"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", r => !r.ValueExists ? ("Unknown/default", "Ausente no prueba el estado efectivo de MPO; no se modifica.") : (Convert.ToString(r.Data) == "5" ? "DisabledByOverride" : $"Override={r.Data}", "OverlayTestMode=5 es el override conocido; otros valores requieren diagnóstico.")),
        _ => new ProfileDiagnosticProbe("vbs", p => new("vbs", p.Security.Vbs.ToString(), "DeviceGuard/WMI", p.Security.Vbs == FeatureState.Unknown ? "Unknown" : "Medium", "No se cambia VBS en el perfil por defecto.")),
        _ => new ProfileDiagnosticProbe("hvci", p => new("hvci", p.Security.Hvci.ToString(), "DeviceGuard/registro", p.Security.Hvci == FeatureState.Unknown ? "Unknown" : "Medium", "No se cambia HVCI en el perfil por defecto.")),
        _ => new ProfileDiagnosticProbe("secureBoot", p => new("secureBoot", p.Security.SecureBoot.ToString(), "Registro/UEFI", p.Security.SecureBoot == FeatureState.Unknown ? "Unknown" : "Medium", "Solo lectura.")),
        _ => new ProfileDiagnosticProbe("dualChannel", p => new("dualChannel", p.Memory.Channels.ToString(), "Win32_PhysicalMemory", p.Memory.Channels == MemoryChannelState.Unknown ? "Unknown" : "Medium", p.Memory.ChannelEvidence + " Dos módulos no prueban dual-channel.")),
        _ => new ProfileDiagnosticProbe("powerLimits", p => new("powerLimits", $"PL1={p.Power.Pl1Watts?.ToString() ?? "Unknown"} W; PL2={p.Power.Pl2Watts?.ToString() ?? "Unknown"} W", p.Power.PowerLimitSource, "Unknown", "DTT/IPF puede gobernar límites dinámicos; no se inventan valores.")),
        _ => new ProfileDiagnosticProbe("ssdHealth", p => new("ssdHealth", string.Join("; ", p.Disks.Select(d => $"{d.Model}: {d.Health}, SMART={(d.SmartPredictFailure?.ToString() ?? "Unavailable")}")), "WMI/MSFT_PhysicalDisk", "Medium", "Salud básica, no sustituye un análisis SMART completo."))
    ];

    public static IReadOnlyList<IDiagnosticProbe> CreateDefaults(IRegistry registry) => Registrations.Select(r => r(registry)).ToArray();
}
