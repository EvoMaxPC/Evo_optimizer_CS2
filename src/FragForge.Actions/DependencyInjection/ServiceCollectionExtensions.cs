using FragForge.Core.Abstractions;
using FragForge.Actions.Diagnostics;
using FragForge.Actions.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Actions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFragForgeActions(this IServiceCollection services)
    {
        services.AddSingleton<IProtectionPolicy>(Core.Persistence.ProtectionPolicy.CreateDefault());
        services.AddSingleton<IRegistry>(sp => OperatingSystem.IsWindows() ? new ProtectedRegistry(new WindowsRegistry(), sp.GetRequiredService<IProtectionPolicy>()) : new ProtectedRegistry(new UnsupportedRegistry(), sp.GetRequiredService<IProtectionPolicy>()));
        services.AddSingleton<ITweakActionFactory, TweakActionFactory>();
        services.AddSingleton<IEnumerable<IDiagnosticProbe>>(sp => DiagnosticProbeFactory.CreateDefaults(sp.GetRequiredService<IRegistry>()));
        return services;
    }
}
