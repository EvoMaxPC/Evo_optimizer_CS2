using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Actions.Bcd;
using FragForge.Actions.Diagnostics;
using FragForge.Actions.Files;
using FragForge.Actions.Network;
using FragForge.Actions.Power;
using FragForge.Actions.Registry;
using FragForge.Actions.Scripting;
using FragForge.Actions.Services;
using FragForge.Actions.Tasks;

namespace FragForge.Actions.DependencyInjection;

public sealed class TweakActionFactory(IRegistry registry, IProtectionPolicy policy, IServiceController services, IScheduledTaskStore tasks, INetworkAdapterController network, IProcessRunner runner, IEnumerable<IDiagnosticProbe> probes, IWmiQuery? wmi = null) : ITweakActionFactory
{
    private readonly IReadOnlyList<IDiagnosticProbe> _probes = probes.ToArray();
    /// <summary>
    /// Opcional y ADITIVO: solo lo necesita <c>NetAdapterProperty</c> (Capa 5.1). Si falta, esa acción
    /// falla al construirse con un mensaje claro en vez de resolver el adaptador por su nombre visible.
    /// Se deja opcional para no romper las llamadas existentes que no tocan red.
    /// </summary>
    private readonly IWmiQuery? _wmi = wmi;
    public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Registry", "IfeoPerfOptions", "Service", "ScheduledTask", "PowerPlan", "Bcdedit", "NetAdapterProperty", "FileEdit", "PowerShell", "InspectOnly" };
    public ITweakAction Create(ActionSpec specification)
    {
        if (!SupportedKinds.Contains(specification.Kind)) throw new CatalogValidationException($"Acción desconocida '{specification.Kind}'.", [specification.Kind]);
        return specification.Kind.ToLowerInvariant() switch
        {
            "registry" => new RegistryAction(registry, policy, specification),
            "ifeoperfoptions" => new IfeoPerfOptionsAction(registry, policy, specification),
            "service" => new ServiceAction(services, policy, specification),
            "scheduledtask" => new ScheduledTaskAction(tasks, policy, specification),
            "powerplan" => new PowerPlanAction(runner, specification),
            "bcdedit" => new BcdeditAction(runner, policy, specification),
                        // Capa 5.1: la acción se identifica por hardware (InterfaceGuid / NetCfgInstanceId), no por el
            // nombre visible. El resolutor y el lector de propiedades se construyen aquí con las mismas
            // dependencias que ya recibe la fábrica, para no cambiar su constructor (lo usan 7 tests).
            "netadapterproperty" => new NetAdapterPropertyAction(
                new Network.WindowsNetworkAdapterResolver(_wmi ?? throw new CatalogValidationException("NetAdapterProperty requiere IWmiQuery.", ["wmi"]), registry),
                new Network.NetworkAdvancedPropertyReader(runner),
                policy, specification),
            "fileedit" => new FileEditAction(policy, specification),
            "powershell" => new PowerShellAction(runner, policy, specification),
            "inspectonly" => new InspectOnlyAction(_probes, specification),
            _ => throw new CatalogValidationException($"Acción desconocida '{specification.Kind}'.", [specification.Kind])
        };
    }
}
