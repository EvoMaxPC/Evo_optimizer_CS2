## 3d. FragForge.Composition — raíz de composición y reportes

### `src/FragForge.Composition/ApplicationContext.cs`  <sub>(10 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Composition;

public sealed class ApplicationContext(string dataRoot, string culture) : IApplicationContext
{
    public string Culture { get; set; } = culture;
    public string DataRoot { get; } = dataRoot;
    public string ApplicationVersion { get; } = typeof(ApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
````

### `src/FragForge.Composition/CompositionRoot.cs`  <sub>(98 líneas)</sub>

````csharp
using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Files;
using FragForge.Actions.Network;
using FragForge.Actions.Platform;
using FragForge.Actions.Services;
using FragForge.Actions.Tasks;
using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Logging;
using FragForge.Core.Persistence;
using FragForge.Core.Preflight;
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
        builder.Services.AddSingleton<IProtectionPolicy>(policy);
        builder.Services.AddSingleton<ITweakConditionFactory, ConditionFactory>();
        builder.Services.AddSingleton<JsonSchemaValidator>(_ => new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()));
        builder.Services.AddSingleton<TweakCatalogValidator>();
        builder.Services.AddSingleton<JsonTweakCatalogLoader>();
        builder.Services.AddSingleton<ITweakCatalog>(sp => sp.GetRequiredService<JsonTweakCatalogLoader>().LoadEmbeddedAsync(Path.Combine(paths.Root, "catalog")).GetAwaiter().GetResult());
        builder.Services.AddSingleton<ISnapshotStore, FileSnapshotStore>();
        builder.Services.AddSingleton<IJournal, FileJournal>();
        builder.Services.AddSingleton<IExecutionGate, FileExecutionGate>();
        builder.Services.AddSingleton<IPreflightService, PreflightService>();
        builder.Services.AddSingleton<IEnumerable<IExperimentHook>>([]);
        builder.Services.AddSingleton<ITweakEngine, TweakEngine>();
        builder.Services.AddSingleton<IReportService, ReportService>();
        options.ConfigureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}
public sealed class UnsupportedCpuTopologyProbe : ICpuTopologyProbe
{
    public Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, "Unsupported platform"));
}
````

### `src/FragForge.Composition/FragForge.Composition.csproj`  <sub>(14 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../FragForge.Core/FragForge.Core.csproj" />
    <ProjectReference Include="../FragForge.Actions/FragForge.Actions.csproj" />
    <ProjectReference Include="../FragForge.Tweaks/FragForge.Tweaks.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Serilog" />
    <PackageReference Include="Serilog.Extensions.Hosting" />
    <PackageReference Include="Serilog.Sinks.Console" />
    <PackageReference Include="Serilog.Sinks.File" />
    <PackageReference Include="Serilog.Formatting.Compact" />
  </ItemGroup>
</Project>
````

### `src/FragForge.Composition/ReportService.cs`  <sub>(64 líneas)</sub>

````csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Composition;

public sealed class ReportService(ITweakEngine engine, IPreflightService preflight, ISnapshotStore snapshots, IJournal journal, IApplicationContext context) : IReportService
{
    public async Task<ReportDocument> CreateAsync(CancellationToken cancellationToken = default)
    {
        var scan = await engine.ScanAsync(cancellationToken);
        var preflightReport = await preflight.CheckAsync(new ApplyOptions { CreateRestorePoint = true, Profile = TweakProfile.Safe }, scan.Hardware, cancellationToken);
        var entries = await journal.ReadAllAsync(cancellationToken);
        var integrity = await journal.VerifyIntegrityAsync(cancellationToken);
        var reverted = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshotItems = (await snapshots.ListAsync(cancellationToken)).Select(s => new SnapshotListItem(s.Id, s.Label, s.CreatedAt, s.Tweaks.Count, s.RestorePointSequence, reverted.Contains(s.Id))).ToArray();
        return new ReportDocument(1, DateTimeOffset.UtcNow, context.ApplicationVersion, scan.Hardware, scan.Tweaks, preflightReport, snapshotItems, integrity, entries.TakeLast(100).ToArray());
    }
    public async Task ExportAsync(string path, string format, CancellationToken cancellationToken = default)
    {
        var report = await CreateAsync(cancellationToken);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var normalized = format.ToLowerInvariant();
        if (normalized is not ("json" or "html")) throw new ArgumentException("Formato debe ser json o html.");
        if (normalized == "json") await AtomicFile.WriteAllTextAsync(full, JsonSerializer.Serialize(report, JsonDefaults.Options), cancellationToken);
        else await AtomicFile.WriteAllTextAsync(full, RenderHtml(report), cancellationToken);
    }
    private static string RenderHtml(ReportDocument report)
    {
        var h = report.Hardware;
        var builder = new StringBuilder("<!doctype html><html lang=\"es\"><meta charset=\"utf-8\"><title>FragForge report</title><style>body{font-family:Segoe UI,Arial,sans-serif;background:#101418;color:#e8edf2;margin:2rem}table{border-collapse:collapse;width:100%;margin:1rem 0}td,th{border:1px solid #39424e;padding:.45rem;text-align:left}th{background:#1c2530}code{color:#8fd3ff}.warn{color:#ffd479}.bad{color:#ff8a80}</style><h1>FragForge — informe Capa 0</h1>");
        builder.Append("<p>Generado: ").Append(E(report.CreatedAt.ToString("O"))).Append(" · Versión ").Append(E(report.ApplicationVersion)).Append("</p>");
        builder.Append("<h2>Hardware y sistema</h2><table>");
        Row(builder, "Equipo", h.MachineName + " / " + h.FormFactor.SystemModel);
        Row(builder, "SO", $"{h.Os.Name} {h.Os.Build}.{h.Os.Ubr} {h.Os.Architecture} {h.Os.Culture}");
        Row(builder, "CPU", $"{h.Cpu.Model}; P={h.Cpu.PCores?.ToString() ?? "?"}, E={h.Cpu.ECores?.ToString() ?? "?"}, hilos={h.Cpu.LogicalProcessors}, híbrida={h.Cpu.IsHybrid?.ToString() ?? "Unknown"}");
        Row(builder, "RAM", $"{h.Memory.TotalBytes} bytes; canales={h.Memory.Channels}; módulos={h.Memory.Modules.Count}; velocidad={h.Memory.ConfiguredSpeedMtPerSecond?.ToString() ?? "Unknown"}");
        Row(builder, "GPU", string.Join("; ", h.Gpus.Select(g => $"{g.Name} ({g.Vendor}, integrada={g.IsIntegrated})")));
        Row(builder, "Discos", string.Join("; ", h.Disks.Select(d => $"{d.Model} {d.Kind} {d.Bus} {d.Health} SMART={d.SmartPredictFailure?.ToString() ?? "Unavailable"}")));
        Row(builder, "Red", string.Join("; ", h.NetworkAdapters.Select(a => $"{a.Name}: {a.Description} {a.Kind} {a.LinkSpeedBitsPerSecond} bps driver={a.DriverVersion}")));
        Row(builder, "Monitores", string.Join("; ", h.Monitors.Select(m => $"{m.Name} {m.Width}x{m.Height}@{m.RefreshRateHz}Hz")));
        Row(builder, "Seguridad", $"VBS={h.Security.Vbs}; HVCI={h.Security.Hvci}; SecureBoot={h.Security.SecureBoot}; TPM={h.Security.Tpm} {h.Security.TpmVersion}");
        Row(builder, "Energía", $"{h.Power.ActiveSchemeName} {h.Power.ActiveSchemeGuid}; PL1={h.Power.Pl1Watts?.ToString() ?? "Unknown"}; PL2={h.Power.Pl2Watts?.ToString() ?? "Unknown"}");
        Row(builder, "Atlas", string.Join("; ", h.Playbooks.Select(p => $"{p.Name} [{p.Confidence}]: {string.Join(", ", p.Evidence)}")));
        Row(builder, "Dispositivos con error", string.Join("; ", h.DeviceErrors.Select(d => $"{d.Name} código={d.ErrorCode} {d.InstanceId}")));
        Row(builder, "CS2", h.Games.Cs2ExecutablePath ?? h.Games.Cs2DetectionSource);
        Row(builder, "Advertencias", string.Join(" | ", h.ProbeWarnings));
        builder.Append("</table><h2>Preflight</h2><table><tr><th>ID</th><th>Severidad</th><th>Mensaje</th></tr>");
        foreach (var check in report.Preflight.Checks) builder.Append("<tr><td>").Append(E(check.Id)).Append("</td><td>").Append(E(check.Severity.ToString())).Append("</td><td>").Append(E(check.Message)).Append("</td></tr>");
        builder.Append("</table><h2>Tweaks</h2><table><tr><th>ID</th><th>Estado</th><th>Motivo</th></tr>");
        foreach (var tweak in report.Tweaks) builder.Append("<tr><td>").Append(E(tweak.Id)).Append("</td><td>").Append(E(tweak.State.ToString())).Append("</td><td>").Append(E(tweak.Reason)).Append("</td></tr>");
        builder.Append("</table><h2>Snapshots</h2><table><tr><th>ID</th><th>Etiqueta</th><th>Creado</th><th>Tweaks</th><th>Revertido</th></tr>");
        foreach (var snapshot in report.Snapshots) builder.Append("<tr><td>").Append(E(snapshot.Id)).Append("</td><td>").Append(E(snapshot.Label)).Append("</td><td>").Append(E(snapshot.CreatedAt.ToString("O"))).Append("</td><td>").Append(snapshot.TweakCount).Append("</td><td>").Append(snapshot.Reverted).Append("</td></tr>");
        builder.Append("</table><p>Journal: ").Append(report.JournalIntegrity.IsValid ? "íntegro" : "DAÑADO").Append(" · entradas recientes: ").Append(report.RecentJournal.Count).Append("</p></html>");
        return builder.ToString();
        static void Row(StringBuilder b, string key, string? value) => b.Append("<tr><th>").Append(E(key)).Append("</th><td>").Append(E(value ?? "")).Append("</td></tr>");
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    }
}
````

### `src/FragForge.Composition/UnsupportedPlatformServices.cs`  <sub>(35 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Composition;

public sealed class UnsupportedWmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
}
public sealed class UnsupportedServiceController : IServiceController
{
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(new ServiceCapture { Name = name, Exists = false });
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false) => throw new PlatformNotSupportedException();
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedScheduledTaskStore : IScheduledTaskStore
{
    public Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new ScheduledTaskActionState { Path = path, Exists = false, Enabled = false });
    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedNetworkAdapterController : INetworkAdapterController
{
    public Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default) => Task.FromResult(new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false });
    public Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedSystemRestoreService : ISystemRestoreService
{
    public Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(FeatureState.NotPresent);
    public Task EnableProtectionAsync(CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();
}
public sealed class UnsupportedGameLocator : IGameLocator
{
    public Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default) => Task.FromResult(new GameInfo());
}
````

