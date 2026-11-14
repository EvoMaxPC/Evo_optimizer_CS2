using FragForge.Actions.Diagnostics;
using FragForge.Actions.Registry;
using FragForge.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Actions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra el adaptador de registro, la fábrica de acciones y las sondas de diagnóstico por defecto.
    /// NO registra <see cref="IProtectionPolicy"/>: la política es única y la registra el host
    /// (<c>CompositionRoot</c>) con la instancia cargada de disco (Capa 1, Fase 0.3).
    /// </summary>
    public static IServiceCollection AddFragForgeActions(this IServiceCollection services)
    {
        services.AddSingleton<IRegistry>(sp => OperatingSystem.IsWindows()
            ? new ProtectedRegistry(new WindowsRegistry(), sp.GetRequiredService<IProtectionPolicy>())
            : new ProtectedRegistry(new UnsupportedRegistry(), sp.GetRequiredService<IProtectionPolicy>()));
        services.AddSingleton<ITweakActionFactory, TweakActionFactory>();
        services.AddFragForgeDiagnosticProbes();
        return services;
    }

    /// <summary>
    /// Registra cada sonda por separado, de modo que <c>IEnumerable&lt;IDiagnosticProbe&gt;</c> se componga a
    /// partir de registros individuales y un tercero pueda añadir sondas con
    /// <c>services.AddSingleton&lt;IDiagnosticProbe, MiSonda&gt;()</c> desde <c>ShellOptions.ConfigureServices</c>.
    /// </summary>
    public static IServiceCollection AddFragForgeDiagnosticProbes(this IServiceCollection services)
    {
        for (var index = 0; index < DiagnosticProbeFactory.Registrations.Count; index++)
        {
            var registration = DiagnosticProbeFactory.Registrations[index];
            services.AddSingleton<IDiagnosticProbe>(sp => registration(sp.GetRequiredService<IRegistry>()));
        }
        return services;
    }
}
