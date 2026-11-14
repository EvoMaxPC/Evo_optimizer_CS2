## 3e. FragForge.Cli — fragforge.exe

### `src/FragForge.Cli/CommandLine.cs`  <sub>(44 líneas)</sub>

````csharp
namespace FragForge.Cli;

public sealed class ParsedArgs
{
    public string Command { get; init; } = "help";
    public IReadOnlyList<string> Positionals { get; init; } = [];
    public IReadOnlyDictionary<string, List<string>> Options { get; init; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    public bool Has(string name) => Options.ContainsKey(name);
    public string? Value(string name) => Options.TryGetValue(name, out var values) ? values.LastOrDefault() : null;
    public IReadOnlyList<string> Values(string name) => Options.TryGetValue(name, out var values) ? values : [];
}
public static class CommandLineParser
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "json", "dry-run", "force", "no-restore-point", "enable-system-protection", "last", "allow-powershell", "help" };
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase) { "profile", "category", "risk", "out", "format", "snapshot", "label", "culture", "data-root", "cs2-path", "user-sid", "accept-risk", "risk-phrase" };
    public static ParsedArgs Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") return new ParsedArgs { Command = "help" };
        var command = args[0].ToLowerInvariant();
        var positionals = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) { positionals.Add(token); continue; }
            var name = token[2..];
            if (Flags.Contains(name)) { options[name] = ["true"]; continue; }
            if (!ValueOptions.Contains(name)) throw new ArgumentException($"Opción desconocida: --{name}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Falta el valor de --{name}");
            if (!options.TryGetValue(name, out var values)) { values = []; options[name] = values; }
            values.Add(args[++i]);
        }
        return new ParsedArgs { Command = command, Positionals = positionals, Options = options };
    }
}
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Preflight = 3;
    public const int Cancelled = 4;
    public const int RollbackCritical = 5;
}
````

### `src/FragForge.Cli/ConsoleOutput.cs`  <sub>(71 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Cli;

public static class ConsoleOutput
{
    public static void Json<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
    public static void Hardware(HardwareProfile h)
    {
        Console.WriteLine("=== Hardware / sistema ===");
        Console.WriteLine($"Equipo       : {h.MachineName} · {h.FormFactor.SystemModel ?? "Unknown"} · BIOS {h.FormFactor.BiosVersion ?? "Unknown"}");
        Console.WriteLine($"SO           : {h.Os.Name} {h.Os.Build}.{h.Os.Ubr} · {h.Os.Architecture} · {h.Os.Culture} · {h.Os.TimeZone}");
        Console.WriteLine($"Usuario      : {h.InteractiveUserName ?? "Unknown"} · SID {h.InteractiveUserSid ?? "Unknown"}");
        Console.WriteLine($"CPU          : {h.Cpu.Model} · {h.Cpu.PhysicalCores} núcleos / {h.Cpu.LogicalProcessors} hilos · P={h.Cpu.PCores?.ToString() ?? "?"} E={h.Cpu.ECores?.ToString() ?? "?"} · híbrida={h.Cpu.IsHybrid?.ToString() ?? "Unknown"}");
        Console.WriteLine($"RAM          : {h.Memory.TotalBytes:N0} bytes · canales={h.Memory.Channels} · módulos={h.Memory.Modules.Count} · velocidad={h.Memory.ConfiguredSpeedMtPerSecond?.ToString() ?? "Unknown"} MT/s");
        foreach (var module in h.Memory.Modules) Console.WriteLine($"  DIMM       : {module.BankLabel}/{module.DeviceLocator} {module.CapacityBytes:N0} bytes {module.PartNumber}");
        Console.WriteLine($"GPU          : {string.Join("; ", h.Gpus.Select(g => $"{g.Name} [{g.Vendor}] integrada={g.IsIntegrated} driver={g.DriverVersion}"))}");
        foreach (var disk in h.Disks) Console.WriteLine($"Disco        : {disk.Model} · {disk.Kind} · {disk.Bus} · {disk.SizeBytes:N0} bytes · salud={disk.Health} · SMART={disk.SmartPredictFailure?.ToString() ?? "Unavailable"}");
        foreach (var adapter in h.NetworkAdapters) Console.WriteLine($"Red          : {adapter.Name} · {adapter.Description} · {adapter.Kind} · {adapter.LinkSpeedBitsPerSecond?.ToString() ?? "?"} bps · driver={adapter.DriverVersion} · físico={adapter.IsPhysical}");
        foreach (var monitor in h.Monitors) Console.WriteLine($"Monitor      : {monitor.Name} · {monitor.Width}x{monitor.Height}@{monitor.RefreshRateHz}Hz ({monitor.Source})");
        Console.WriteLine($"Seguridad    : VBS={h.Security.Vbs} HVCI={h.Security.Hvci} SecureBoot={h.Security.SecureBoot} TPM={h.Security.Tpm} {h.Security.TpmVersion} HAGS={h.Security.Hags} MPO={h.Security.Mpo}");
        Console.WriteLine($"Energía      : {h.Power.ActiveSchemeName ?? "Unknown"} ({h.Power.ActiveSchemeGuid ?? "?"}) · PL1={h.Power.Pl1Watts?.ToString() ?? "Unknown"} W · PL2={h.Power.Pl2Watts?.ToString() ?? "Unknown"} W");
        Console.WriteLine($"Factor forma : portátil={h.FormFactor.IsPortable?.ToString() ?? "Unknown"} · batería={h.FormFactor.HasBattery} · chasis={string.Join(",", h.FormFactor.ChassisTypes)}");
        Console.WriteLine($"Playbooks    : {string.Join("; ", h.Playbooks.Select(p => $"{p.Name} [{p.Confidence}]"))}");
        foreach (var error in h.DeviceErrors) Console.WriteLine($"Device error : {error.Name} · código {error.ErrorCode} · {error.InstanceId}");
        Console.WriteLine($"Mantenimiento: reinicio pendiente={h.Maintenance.PendingReboot} ({string.Join("; ", h.Maintenance.PendingRebootReasons)}) · servicing={h.Maintenance.WindowsServicingActive} ({string.Join(",", h.Maintenance.ServicingProcesses)}) · admin={h.Maintenance.IsAdministrator} · libre={h.Maintenance.SystemDriveFreeBytes / 1024 / 1024} MiB · protección={h.Maintenance.SystemProtection}");
        Console.WriteLine($"CS2          : {h.Games.Cs2ExecutablePath ?? h.Games.Cs2DetectionSource}");
        foreach (var warning in h.ProbeWarnings) Console.WriteLine($"Aviso        : {warning}");
    }
    public static void Tweaks(IEnumerable<TweakStatus> statuses, IEnumerable<TweakDefinition> definitions)
    {
        var map = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine();
        Console.WriteLine($"{"ID",-42} {"Categoría",-12} {"Evidencia",-13} {"Riesgo",-13} {"Estado",-20} Motivo");
        foreach (var status in statuses)
        {
            var d = map[status.Id];
            Console.WriteLine($"{status.Id,-42} {d.Category,-12} {d.Evidence,-13} {d.Risk,-13} {status.State,-20} {status.Reason}");
            foreach (var observation in status.Observations) Console.WriteLine($"  {observation.Key}: {observation.Value} [{observation.Confidence}] {observation.Note}");
        }
    }
    public static void Definitions(IEnumerable<TweakDefinition> definitions, string culture)
    {
        Console.WriteLine($"{"ID",-42} {"Categoría",-12} {"Evidencia",-13} {"Riesgo",-13} Título");
        foreach (var d in definitions) Console.WriteLine($"{d.Id,-42} {d.Category,-12} {d.Evidence,-13} {d.Risk,-13} {d.Title.Resolve(culture)}");
    }
    public static void Batch(BatchReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"Operación: {report.OperationId} · éxito={report.Success} · dry-run={report.DryRun} · cancelado={report.Cancelled}");
        if (report.SnapshotId is not null) Console.WriteLine($"Snapshot : {report.SnapshotId}");
        if (report.RestorePointSequence is not null) Console.WriteLine($"Restore  : punto {report.RestorePointSequence}");
        foreach (var result in report.Results)
        {
            Console.WriteLine($"- {result.Id}: {result.State} · {result.Message}");
            foreach (var change in result.PlannedChanges) Console.WriteLine($"  [{change.ActionIndex}] {change.Kind}: {change.Description}");
            foreach (var warning in result.Warnings) Console.WriteLine($"  Aviso: {warning}");
        }
        foreach (var warning in report.Warnings) Console.WriteLine($"Aviso: {warning}");
        foreach (var error in report.Errors) Console.Error.WriteLine($"Error: {error}");
        if (report.HasCriticalFailure) Console.Error.WriteLine("CRÍTICO: una reversión quedó incompleta. No sigas aplicando cambios; revisa snapshot/journal y la guía de recuperación manual.");
    }
    public static void Preflight(PreflightReport report)
    {
        Console.WriteLine($"Preflight: {(report.CanApply ? "APTO" : "BLOQUEADO")}");
        foreach (var check in report.Checks) Console.WriteLine($"[{check.Severity}] {check.Id}: {check.Message}{(check.Remediation is null ? "" : " → " + check.Remediation)}");
    }
}
````

### `src/FragForge.Cli/FragForge.Cli.csproj`  <sub>(18 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>fragforge</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <InvariantGlobalization>false</InvariantGlobalization>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
  <!-- Publish settings apply only when a RID is supplied, e.g.
       dotnet publish src/FragForge.Cli/FragForge.Cli.csproj -c Release -r win-x64 -->
  <PropertyGroup Condition="'$(RuntimeIdentifier)' != ''">
    <SelfContained Condition="'$(SelfContained)' == ''">true</SelfContained>
    <PublishSingleFile Condition="'$(PublishSingleFile)' == ''">true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract Condition="'$(IncludeNativeLibrariesForSelfExtract)' == ''">true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile Condition="'$(EnableCompressionInSingleFile)' == ''">true</EnableCompressionInSingleFile>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../FragForge.Composition/FragForge.Composition.csproj" /></ItemGroup>
</Project>
````

### `src/FragForge.Cli/Program.cs`  <sub>(160 líneas)</sub>

````csharp
using FragForge.Composition;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FragForge.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var parsed = CommandLineParser.Parse(args);
            if (parsed.Command == "help") { Help(); return ExitCodes.Success; }
            var options = new ShellOptions(parsed.Value("data-root"), parsed.Value("culture") ?? "es-MX");
            using var host = await CompositionRoot.BuildAsync(options, cancellation.Token);
            var engine = host.Services.GetRequiredService<ITweakEngine>();
            var catalog = host.Services.GetRequiredService<ITweakCatalog>();
            var localizer = host.Services.GetRequiredService<Localizer>();
            var snapshots = host.Services.GetRequiredService<ISnapshotStore>();
            var journal = host.Services.GetRequiredService<IJournal>();
            var preflight = host.Services.GetRequiredService<IPreflightService>();
            var reports = host.Services.GetRequiredService<IReportService>();
            var profiler = host.Services.GetRequiredService<IHardwareProfiler>();
            var progress = new Progress<EngineProgress>(p => Console.Error.WriteLine($"[{p.Stage}] {p.TweakId ?? "system"} {p.Fraction:P0} {p.Message}"));
            switch (parsed.Command)
            {
                case "scan":
                {
                    var scan = await engine.ScanAsync(cancellation.Token, progress);
                    if (parsed.Has("json")) ConsoleOutput.Json(scan); else { ConsoleOutput.Hardware(scan.Hardware); ConsoleOutput.Tweaks(scan.Tweaks, catalog.Definitions); }
                    return ExitCodes.Success;
                }
                case "list":
                {
                    var definitions = catalog.Definitions.AsEnumerable();
                    if (parsed.Value("category") is { } category) definitions = definitions.Where(d => d.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
                    if (parsed.Value("risk") is { } risk) definitions = definitions.Where(d => d.Risk.ToString().Equals(risk, StringComparison.OrdinalIgnoreCase));
                    if (parsed.Has("json")) ConsoleOutput.Json(definitions.ToArray()); else ConsoleOutput.Definitions(definitions, localizer.Culture);
                    return ExitCodes.Success;
                }
                case "apply":
                {
                    var profile = ParseProfile(parsed.Value("profile") ?? (parsed.Positionals.Count > 0 ? "Balanced" : "Safe"));
                    var ids = parsed.Positionals.Count > 0 ? parsed.Positionals.ToArray() : ProfileSelector.Select(catalog.Definitions, profile).Select(d => d.Id).ToArray();
                    if (ids.Length == 0) throw new ArgumentException("apply requiere IDs o --profile con tweaks disponibles.");
                    var applyOptions = new ApplyOptions
                    {
                        DryRun = parsed.Has("dry-run"), Force = parsed.Has("force"), CreateRestorePoint = !parsed.Has("no-restore-point"), EnableSystemProtection = parsed.Has("enable-system-protection"), Profile = profile,
                        RiskAcknowledgements = parsed.Values("accept-risk").ToHashSet(StringComparer.OrdinalIgnoreCase), AdvancedRiskPhrase = parsed.Value("risk-phrase"), Cs2ExecutableOverride = parsed.Value("cs2-path"), TargetUserSid = parsed.Value("user-sid"), AllowPowerShellActions = parsed.Has("allow-powershell")
                    };
                    var report = await engine.ApplyAsync(ids, applyOptions, cancellation.Token, progress);
                    if (parsed.Has("json")) ConsoleOutput.Json(report); else ConsoleOutput.Batch(report);
                    return ReportExit(report);
                }
                case "revert":
                {
                    var request = new RevertRequest { Ids = parsed.Positionals.ToArray(), SnapshotId = parsed.Value("snapshot"), Last = parsed.Has("last"), Options = new RevertOptions { Force = parsed.Has("force"), TargetUserSid = parsed.Value("user-sid"), Cs2ExecutableOverride = parsed.Value("cs2-path") } };
                    var report = await engine.RevertAsync(request, cancellation.Token, progress);
                    if (parsed.Has("json")) ConsoleOutput.Json(report); else ConsoleOutput.Batch(report);
                    return ReportExit(report);
                }
                case "status":
                {
                    var scan = await engine.ScanAsync(cancellation.Token, progress);
                    var integrity = await journal.VerifyIntegrityAsync(cancellation.Token);
                    var list = await snapshots.ListAsync(cancellation.Token);
                    if (parsed.Has("json")) ConsoleOutput.Json(new { scan.Hardware, scan.Tweaks, JournalIntegrity = integrity, Snapshots = list.Count });
                    else { Console.WriteLine($"Journal: {(integrity.IsValid ? "íntegro" : "DAÑADO")} ({integrity.Entries} entradas) · snapshots={list.Count}"); ConsoleOutput.Tweaks(scan.Tweaks, catalog.Definitions); }
                    return integrity.IsValid ? ExitCodes.Success : ExitCodes.Failure;
                }
                case "snapshot":
                {
                    var sub = parsed.Positionals.FirstOrDefault() ?? "list";
                    if (sub == "create")
                    {
                        var snapshot = await engine.CreateSnapshotAsync(parsed.Value("label") ?? "manual", !parsed.Has("no-restore-point"), cancellation.Token, progress);
                        if (parsed.Has("json")) ConsoleOutput.Json(snapshot); else Console.WriteLine($"Snapshot creado: {snapshot.Id} · tweaks={snapshot.Tweaks.Count} · restore={snapshot.RestorePointSequence?.ToString() ?? "ninguno"}");
                        return ExitCodes.Success;
                    }
                    if (sub == "show")
                    {
                        var id = parsed.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("snapshot show requiere ID.");
                        ConsoleOutput.Json(await snapshots.GetAsync(id, cancellation.Token));
                        return ExitCodes.Success;
                    }
                    if (sub == "list")
                    {
                        var entries = await journal.ReadAllAsync(cancellation.Token);
                        var reverted = entries.Where(e => e.EventType == JournalEventType.SnapshotReverted && e.SnapshotId is not null).Select(e => e.SnapshotId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var items = (await snapshots.ListAsync(cancellation.Token)).Select(s => new SnapshotListItem(s.Id, s.Label, s.CreatedAt, s.Tweaks.Count, s.RestorePointSequence, reverted.Contains(s.Id))).ToArray();
                        if (parsed.Has("json")) ConsoleOutput.Json(items);
                        else foreach (var item in items) Console.WriteLine($"{item.Id}  {item.CreatedAt:O}  tweaks={item.TweakCount}  revertido={item.Reverted}  {item.Label}");
                        return ExitCodes.Success;
                    }
                    throw new ArgumentException("snapshot admite list|create|show.");
                }
                case "export-report":
                {
                    var output = parsed.Value("out") ?? throw new ArgumentException("export-report requiere --out.");
                    var format = parsed.Value("format") ?? Path.GetExtension(output).TrimStart('.').ToLowerInvariant();
                    await reports.ExportAsync(output, format, cancellation.Token);
                    Console.WriteLine($"Informe exportado: {Path.GetFullPath(output)}");
                    return ExitCodes.Success;
                }
                case "doctor":
                {
                    var hardware = await profiler.ProfileAsync(cancellation.Token, progress);
                    var check = await preflight.CheckAsync(new ApplyOptions { CreateRestorePoint = true, Profile = TweakProfile.Safe }, hardware, cancellation.Token);
                    var integrity = await journal.VerifyIntegrityAsync(cancellation.Token);
                    if (parsed.Has("json")) ConsoleOutput.Json(new { Preflight = check, CatalogCount = catalog.Definitions.Count, JournalIntegrity = integrity });
                    else { ConsoleOutput.Preflight(check); Console.WriteLine($"Catálogo: {catalog.Definitions.Count} tweaks válidos · journal {(integrity.IsValid ? "íntegro" : "DAÑADO: " + integrity.Error)}"); }
                    return check.CanApply && integrity.IsValid ? ExitCodes.Success : ExitCodes.Preflight;
                }
                default: throw new ArgumentException($"Comando desconocido: {parsed.Command}");
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operación cancelada. Si había una aplicación en curso, consulta el journal antes de continuar."); return ExitCodes.Cancelled; }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); Help(); return ExitCodes.Usage; }
        catch (CatalogValidationException ex) { Console.Error.WriteLine(ex.Message); foreach (var error in ex.Errors) Console.Error.WriteLine("  " + error); return ExitCodes.Preflight; }
        catch (PreflightFailedException ex) { Console.Error.WriteLine(ex.Message); ConsoleOutput.Preflight(ex.Report); return ExitCodes.Preflight; }
        catch (FragForgeException ex) { Console.Error.WriteLine($"[{ex.Code}] {ex.Message}"); return ex.Code == "rollback_failed" ? ExitCodes.RollbackCritical : ExitCodes.Failure; }
        catch (Exception ex) { Console.Error.WriteLine($"Error inesperado: {ex.GetType().Name}: {ex.Message}"); return ExitCodes.Failure; }
        finally { Console.CancelKeyPress -= handler; }
    }
    private static TweakProfile ParseProfile(string value) => Enum.TryParse<TweakProfile>(value, true, out var profile) ? profile : throw new ArgumentException("Perfil debe ser Safe, Balanced, Competitive o Advanced.");
    private static int ReportExit(BatchReport report) => report.HasCriticalFailure ? ExitCodes.RollbackCritical : report.Cancelled ? ExitCodes.Cancelled : report.Success ? ExitCodes.Success : ExitCodes.Failure;
    private static void Help()
    {
        Console.WriteLine("""
FragForge Capa 0 — optimizador reversible para Windows 11 / CS2

Uso:
  fragforge scan [--json] [--culture es-MX|en-US]
  fragforge list [--category X] [--risk Safe|Moderate|Advanced|Experimental] [--json]
  fragforge apply <ids...|--profile Safe|Balanced|Competitive|Advanced> [--dry-run] [--force]
                  [--no-restore-point] [--enable-system-protection] [--cs2-path RUTA]
                  [--user-sid SID] [--accept-risk ID] [--risk-phrase "ACEPTO EL RIESGO"]
                  [--allow-powershell] [--json]
  fragforge revert <ids...|--snapshot ID|--last> [--force] [--json]
  fragforge status [--json]
  fragforge snapshot list|create|show [ID] [--label TEXTO] [--no-restore-point] [--json]
  fragforge export-report --out reporte.html|json [--format html|json]
  fragforge doctor [--json]

Opciones globales: --data-root RUTA --culture es-MX|en-US
Códigos: 0 éxito; 1 fallo; 2 uso; 3 preflight/catálogo; 4 cancelación; 5 rollback crítico.
Seguridad: Force solo omite idempotencia/drift; nunca denylist, condiciones ni preflight.
Los tweaks Advanced/Experimental requieren perfil Advanced, --accept-risk ID y la frase exacta.
""");
    }
}
````

### `src/FragForge.Cli/app.manifest`  <sub>(15 líneas)</sub>

````xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="FragForge.Cli" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges>
    <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
  </requestedPrivileges></security></trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
  </application></compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
  </windowsSettings></application>
</assembly>
````

