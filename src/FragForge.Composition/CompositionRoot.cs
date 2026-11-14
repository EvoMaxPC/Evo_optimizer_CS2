using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Measurement;
using FragForge.Actions.Files;
using FragForge.Actions.Network;
using FragForge.Actions.Platform;
using FragForge.Actions.Services;
using FragForge.Actions.Tasks;
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Experiments;
using FragForge.Core.Localization;
using FragForge.Core.Logging;
using FragForge.Core.Persistence;
using FragForge.Core.Preflight;
using FragForge.Core.Sessions;
using FragForge.Core.Tiers;
using FragForge.Tweaks;
using FragForge.Tweaks.Localization;
using FragForge.Tweaks.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

namespace FragForge.Composition;

public sealed record ShellOptions(string? DataRoot = null, string Culture = "es-MX", bool ConsoleLogging = true, Action<IServiceCollection>? ConfigureServices = null);

public static class CompositionRoot
{
    public static async Task<IHost> BuildAsync(ShellOptions options, CancellationToken cancellationToken = default)
    {
        var paths = new FragForgePaths(options.DataRoot);
        paths.EnsureCreated();
        var policy = await ProtectionPolicy.LoadAsync(paths.AdditionalPolicy, cancellationToken);
        var localizer = await LocalizationLoader.LoadAsync(options.Culture, cancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net", LogLevel.Warning);
        var serilogConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.Logs, "fragforge-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true);
        if (options.ConsoleLogging) serilogConfig.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        var serilog = serilogConfig.CreateLogger();
        builder.Services.AddSerilog(serilog, dispose: true);
        builder.Services.AddSingleton<IUiLogSink, UiLogSink>();
        builder.Services.AddSingleton<ILoggerProvider, UiLoggerProvider>();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IApplicationContext>(new ApplicationContext(paths.Root, localizer.Culture));
        builder.Services.AddSingleton(localizer);
        builder.Services.AddSingleton<IProtectionPolicy>(policy);
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<IWmiQuery, WmiQuery>();
            builder.Services.AddSingleton<ICpuTopologyProbe, WindowsCpuTopologyProbe>();
            builder.Services.AddSingleton<IServiceController, WindowsServiceController>();
            builder.Services.AddSingleton<IScheduledTaskStore, WindowsScheduledTaskStore>();
            builder.Services.AddSingleton<INetworkAdapterController, WindowsNetworkAdapterController>();
            builder.Services.AddSingleton<ISystemRestoreService, SystemRestoreService>();
            builder.Services.AddSingleton<IGameLocator, SteamGameLocator>();
            // Capa 8.2: resolucion de cs2.exe por appmanifest_730.acf (installdir), aparte del locator.
            builder.Services.AddSingleton<ICs2Resolver, SteamCs2Resolver>();
            // Capa 8.6: inspector de SOLO LECTURA de la configuracion de CS2.
            builder.Services.AddSingleton<Cs2Inspector>();
            // Capa 9: modo sesion. Vive en Core y solo depende del motor y del catalogo.
            builder.Services.AddSingleton<GameSessionService>();
            builder.Services.AddSingleton<IHardwareProfiler, WindowsHardwareProfiler>();
        }
        else
        {
            builder.Services.AddSingleton<IWmiQuery, UnsupportedWmiQuery>();
            builder.Services.AddSingleton<ICpuTopologyProbe, UnsupportedCpuTopologyProbe>();
            builder.Services.AddSingleton<IServiceController, UnsupportedServiceController>();
            builder.Services.AddSingleton<IScheduledTaskStore, UnsupportedScheduledTaskStore>();
            builder.Services.AddSingleton<INetworkAdapterController, UnsupportedNetworkAdapterController>();
            builder.Services.AddSingleton<ISystemRestoreService, UnsupportedSystemRestoreService>();
            builder.Services.AddSingleton<IGameLocator, UnsupportedGameLocator>();
            builder.Services.AddSingleton<IHardwareProfiler, UnsupportedHardwareProfiler>();
        }
        builder.Services.AddFragForgeActions();
        builder.Services.AddSingleton<ITweakConditionFactory, ConditionFactory>();
        builder.Services.AddSingleton<JsonSchemaValidator>(_ => new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()));
        // El validador recibe el catálogo de tiers: la regla "Service solo si T1/T2" se aplica al cargar
        // el catálogo de tweaks, no en tiempo de aplicación. Se resuelve aquí para no depender del orden.
        builder.Services.AddSingleton<TweakCatalogValidator>(sp => new TweakCatalogValidator(
            sp.GetRequiredService<JsonSchemaValidator>(),
            sp.GetRequiredService<ITweakConditionFactory>(),
            sp.GetRequiredService<ITweakActionFactory>(),
            ServiceTierCatalog.LoadEmbedded()));
        builder.Services.AddSingleton<JsonTweakCatalogLoader>();
        builder.Services.AddSingleton<ITweakCatalog>(sp => sp.GetRequiredService<JsonTweakCatalogLoader>().LoadEmbeddedAsync(Path.Combine(paths.Root, "catalog")).GetAwaiter().GetResult());
        builder.Services.AddSingleton<ISnapshotStore, FileSnapshotStore>();
        builder.Services.AddSingleton<IJournal, FileJournal>();
        builder.Services.AddSingleton<IExecutionGate, FileExecutionGate>();
        // Capa 5.3 (R-J): preflight de BitLocker de SOLO LECTURA para cambios que reinician el enlace.
        builder.Services.AddSingleton<NetworkChangePreflight>();
        // Capa 5.5: sonda ICMP. Se registra como singleton para que el CLI no dependa de un fallback.
        builder.Services.AddSingleton<IPingProbe, SystemPingProbe>();
        builder.Services.AddSingleton<IPreflightService, PreflightService>();
        builder.Services.AddSingleton<ITweakEngine, TweakEngine>();
        builder.Services.AddSingleton<IReportService, ReportService>();
        builder.Services.AddLayer1Diagnostics();
        builder.Services.AddLayer2Inventory();
        options.ConfigureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}
public sealed class UnsupportedCpuTopologyProbe : ICpuTopologyProbe
{
    public Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, "Unsupported platform"));
}
