using FragForge.Actions.Measurement;
using FragForge.Core.Abstractions;
using FragForge.Core.Experiments;
using FragForge.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FragForge.Composition;

/// <summary>
/// Registro de la Capa 1 (diagnóstico, línea base y comparación A/B).
///
/// Se invoca DESPUÉS de <c>AddFragForgeActions()</c> y ANTES de <c>ShellOptions.ConfigureServices</c>,
/// de modo que un tercero pueda sustituir cualquier servicio con un <c>AddSingleton</c> posterior.
/// </summary>
public static class Layer1Registration
{
    public static IServiceCollection AddLayer1Diagnostics(this IServiceCollection services)
    {
        // --- Herramientas externas (sin descargas, con hash obligatorio) ---
        services.AddSingleton(_ => ToolVerifier.LoadEmbedded());
        services.AddSingleton(sp => new ToolVerifier(sp.GetRequiredService<FragForgePaths>().Tools, sp.GetRequiredService<ToolAllowlist>()));
        services.AddSingleton<IToolProcessLauncher, ProcessTreeLauncher>();

        // --- Captura de frames ---
        services.AddSingleton<IFrameCapture>(sp => OperatingSystem.IsWindows()
            ? new PresentMonFrameCapture(sp.GetRequiredService<IToolProcessLauncher>(), sp.GetRequiredService<ToolVerifier>())
            : new UnsupportedFrameCapture());

        // --- Sensores PDH ---
        services.AddSingleton<ISensorSampler>(_ => OperatingSystem.IsWindows()
            ? new PerformanceCounterSensorSampler()
            : new UnsupportedSensorSampler());

        // --- Sondas adicionales de la Capa 1 (se suman a las 8 de Capa 0) ---
        services.AddSingleton<IDiagnosticProbe>(_ => OperatingSystem.IsWindows()
            ? new MonitorRefreshProbe(new DisplayModeProbe())
            : new UnsupportedMonitorRefreshProbe());
        services.AddSingleton<IDiagnosticProbe, CpuPowerCountersProbe>();
        services.AddSingleton<IDiagnosticProbe, PresentMonToolProbe>();

        // --- Experimentos puntuales ---
        // MouseRateExperiment está marcado [SupportedOSPlatform("windows")]: solo se registra allí.
        if (OperatingSystem.IsWindows()) services.AddSingleton<MouseRateExperiment>();
        services.AddSingleton<MemoryBandwidthExperiment>();
        services.AddSingleton<IFactStore, FileFactStore>();

        // --- Orquestador A/B ---
        services.AddSingleton<ExperimentStore>();
        services.AddSingleton<LocalEvidenceStore>();
        services.AddSingleton<ICooldownController, TimeCooldownController>();
        services.AddSingleton<IExperimentEnvironmentProbe>(_ => new MeasurementEnvironmentProbe());
        services.AddSingleton(sp => new ExperimentRunner(
            sp.GetRequiredService<ITweakEngine>(),
            sp.GetRequiredService<ITweakCatalog>(),
            sp.GetRequiredService<IHardwareProfiler>(),
            sp.GetRequiredService<IExperimentEnvironmentProbe>(),
            sp.GetRequiredService<IFrameCapture>(),
            sp.GetRequiredService<ISensorSampler>(),
            sp.GetRequiredService<ExperimentStore>(),
            sp.GetRequiredService<ICooldownController>(),
            sp.GetRequiredService<ILogger<ExperimentRunner>>()));

        // --- Hook de entorno: se registra como IExperimentHook individual (Fase 0.1) ---
        services.AddSingleton<IExperimentHook, EnvironmentSnapshotHook>();
        return services;
    }
}
