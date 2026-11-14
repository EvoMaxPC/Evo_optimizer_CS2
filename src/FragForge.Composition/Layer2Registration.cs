using FragForge.Actions.Inventory;
using FragForge.Core.Abstractions;
using FragForge.Core.Drift;
using FragForge.Core.Inventory;
using FragForge.Core.Myths;
using FragForge.Core.Recovery;
using FragForge.Core.Sessions;
using FragForge.Core.Tiers;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Composition;

/// <summary>
/// Registro de la Capa 2: inventario read-only, tiers, simulador de impacto, drift, mitos,
/// recuperación manual y huella.
///
/// Regla de plataforma: NINGÚN tipo marcado <c>[SupportedOSPlatform("windows")]</c> se registra como
/// genérico. Siempre se elige la implementación dentro de una fábrica con <c>OperatingSystem.IsWindows()</c>,
/// porque el analizador CA1416 no puede demostrar la comprobación a través del contenedor.
///
/// El catálogo de tiers se carga y se VALIDA aquí mismo: si service-tiers.json está mal, la aplicación
/// no arranca a medias, falla con el motivo.
/// </summary>
public static class Layer2Registration
{
    public static IServiceCollection AddLayer2Inventory(this IServiceCollection services)
    {
        // --- Datos embebidos, validados al cargar ---
        services.AddSingleton(_ => ServiceTierCatalog.LoadEmbedded());
        services.AddSingleton(_ => MythCatalog.LoadEmbedded());

        // --- Inventario read-only ---
        services.AddSingleton<IServiceInventorySource>(sp => OperatingSystem.IsWindows()
            ? new WindowsServiceInventorySource(sp.GetRequiredService<IRegistry>())
            : new UnsupportedServiceInventorySource());
        services.AddSingleton<ITaskInventorySource>(sp => OperatingSystem.IsWindows()
            ? new WindowsTaskInventorySource(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IProtectionPolicy>())
            : new UnsupportedTaskInventorySource());
        services.AddSingleton<IStartupInventorySource>(sp => OperatingSystem.IsWindows()
            ? new WindowsStartupInventorySource(sp.GetRequiredService<IRegistry>())
            : new UnsupportedStartupInventorySource());
        services.AddSingleton<IAppxInventorySource>(sp => OperatingSystem.IsWindows()
            ? new WindowsAppxInventorySource(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IProtectionPolicy>())
            : new UnsupportedAppxInventorySource());
        services.AddSingleton<ISystemInventory>(sp => OperatingSystem.IsWindows()
            ? new WindowsSystemInventory(
                sp.GetRequiredService<IServiceInventorySource>(),
                sp.GetRequiredService<ITaskInventorySource>(),
                sp.GetRequiredService<IStartupInventorySource>(),
                sp.GetRequiredService<IAppxInventorySource>(),
                sp.GetRequiredService<IHardwareProfiler>())
            : new UnsupportedSystemInventory(sp.GetRequiredService<IHardwareProfiler>()));

        // --- Simulador de impacto: siempre disponible, es puro cálculo sobre el inventario ---
        services.AddSingleton(sp => new ImpactSimulator(sp.GetRequiredService<ServiceTierCatalog>()));

        // --- Drift, recuperación, sesiones ---
        services.AddSingleton<DriftService>();
        services.AddSingleton<RecoveryWriter>();
        services.AddSingleton<BenchmarkSessionStore>();

        // --- Huella (60 s): fábrica por plataforma, nunca registro genérico de un tipo Windows-only ---
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton(sp => new FootprintProbe(sp.GetRequiredService<ISensorSampler>()));
        }
        else
        {
            services.AddSingleton(_ => new UnsupportedFootprintProbe());
        }
        return services;
    }
}
