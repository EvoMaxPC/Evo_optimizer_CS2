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

/// <remarks>
/// <para>
/// <c>wmi</c> se conserva por compatibilidad de firma (lo pasa el contenedor), pero ya NO se usa: lo usaba el
/// resolutor que se construia a mano y se ha eliminado. Se descarta de forma explicita para que no quede mudo.
/// </para>
/// <para>
/// <c>adapterResolver</c> es NUEVO, ADITIVO y OPCIONAL (al final, con valor por defecto). Lo exige
/// <c>NetAdapterPropertyAction</c>, que necesita un <see cref="INetworkAdapterResolver"/> y no un
/// <c>INetworkAdapterController</c>: son interfaces distintas. Al ir al final, los llamadores existentes
/// compilan sin cambios.
/// </para>
/// <para>
/// Si falta, se usa <see cref="Network.UnsupportedNetworkAdapterResolver"/>, NO se lanza. Motivo (fallo
/// encontrado en la ronda 3): el validador del catalogo construye TODAS las acciones al cargar
/// (<c>TweakCatalogValidator.ValidateCatalog</c>), y el catalogo real incluye <c>network.i225v-eee-off</c>,
/// que es de tipo <c>NetAdapterProperty</c>. Los tests construyen la fabrica sin resolutor, asi que lanzar
/// aqui dejaba el catalogo real INCARGABLE en cualquier host sin <c>IWmiQuery</c>: no es un fallo de la
/// accion, es el arranque entero. Con el resolutor sin soporte la accion se CONSTRUYE y, al aplicarse,
/// <c>ResolveAsync</c> devuelve null y falla con <c>ActionNotApplicableException</c>. Eso es lo correcto por
/// R-L: no se inventa un adaptador; se dice que no aplica. El contenedor de produccion pasa siempre el real.
/// </para>
/// </remarks>
public sealed class TweakActionFactory(IRegistry registry, IProtectionPolicy policy, IServiceController services, IScheduledTaskStore tasks, INetworkAdapterController network, IProcessRunner runner, IEnumerable<IDiagnosticProbe> probes, IWmiQuery? wmi = null, INetworkAdapterResolver? adapterResolver = null) : ITweakActionFactory
{
    private readonly IReadOnlyList<IDiagnosticProbe> _probes = probes.ToArray();

    // Descartado a proposito: ver <remarks> de la clase. Sin esta linea, CS9113 (parameter 'wmi' is unread).
    private readonly IWmiQuery? _ = wmi;

    /// <summary>Resolutor por identidad de hardware. Opcional: ver <remarks> de la clase.</summary>
    private readonly INetworkAdapterResolver? _adapterResolver = adapterResolver;
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
            // nombre visible. El resolutor es el INYECTADO (lo elige el contenedor por plataforma); el lector
            // de propiedades se construye aquí con el runner que ya recibe la fábrica, para no añadir otro
            // parámetro al constructor.
            "netadapterproperty" => new NetAdapterPropertyAction(
                // Se usa el resolutor INYECTADO, no uno construido aqui: es el que el contenedor elige por
                // plataforma (Windows/Unsupported) y el que los tests pueden sustituir por un doble.
                // Construirlo a mano disparaba CA1416 (WindowsNetworkAdapterResolver es Windows-only y este
                // sitio es alcanzable en todas las plataformas) y ademas ignoraba la inyeccion.
                // Sin resolutor inyectado se usa el de "sin soporte" (lista vacia / null), que hace la accion
                // NO APLICABLE al aplicarse en vez de impedir que el catalogo se cargue. Ver <remarks>.
                _adapterResolver ?? new Network.UnsupportedNetworkAdapterResolver(),
                new Network.NetworkAdvancedPropertyReader(runner),
                policy, specification),
            "fileedit" => new FileEditAction(policy, specification),
            "powershell" => new PowerShellAction(runner, policy, specification),
            "inspectonly" => new InspectOnlyAction(_probes, specification),
            _ => throw new CatalogValidationException($"Acción desconocida '{specification.Kind}'.", [specification.Kind])
        };
    }
}
