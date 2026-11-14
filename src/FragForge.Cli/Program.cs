using FragForge.Actions.Measurement;
using FragForge.Composition;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Experiments;
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
                case "tools":
                    return parsed.Positionals.FirstOrDefault() == "verify" ? await MeasurementCommands.ToolsVerifyAsync(host.Services, parsed) : throw new ArgumentException("tools admite verify.");
                case "maintenance":
                    return await MaintenanceCommands.RunAsync(host.Services, parsed);
                case "cs2":
                    return await Cs2Commands.RunAsync(host.Services, parsed, cancellation.Token);
                case "session":
                    return await SessionCommands.RunAsync(host.Services, parsed, cancellation.Token);
                case "probe":
                    return await MeasurementCommands.ProbeAsync(host.Services, parsed);
                case "net":
                    return await NetCommands.RunAsync(host.Services, parsed, cancellation.Token);
                case "mouse-test":
                    return await MeasurementCommands.MouseTestAsync(host.Services, parsed);
                case "mem-bench":
                    return await MeasurementCommands.MemBenchAsync(host.Services, parsed);
                case "baseline":
                    return await MeasurementCommands.BaselineAsync(host.Services, parsed);
                case "ab":
                    return await MeasurementCommands.AbAsync(host.Services, parsed);
                case "experiment":
                    return await MeasurementCommands.ExperimentAsync(host.Services, parsed);
                case "audit":
                    return await InventoryCommands.AuditAsync(host.Services, parsed, cancellation.Token);
                case "inventory":
                    return await InventoryCommands.InventoryAsync(host.Services, parsed, cancellation.Token);
                case "drift":
                    return await InventoryCommands.DriftAsync(host.Services, parsed, cancellation.Token);
                case "myths":
                    return InventoryCommands.Myths(host.Services, parsed);
                case "simulate":
                    return await InventoryCommands.SimulateAsync(host.Services, parsed, cancellation.Token);
                case "atlas":
                    return await InventoryCommands.AtlasAsync(host.Services, parsed, cancellation.Token);
                case "footprint":
                    return await InventoryCommands.FootprintAsync(host.Services, parsed, cancellation.Token);
                default: throw new ArgumentException($"Comando desconocido: {parsed.Command}");
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operación cancelada. Si había una aplicación en curso, consulta el journal antes de continuar."); return ExitCodes.Cancelled; }
        catch (MeasureNotAvailableException ex) { Console.Error.WriteLine("Medición no disponible: " + ex.Message); return ExitCodes.MeasurementInvalid; }
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
  fragforge tools verify [--json]
  fragforge probe [CLAVE] [--json]
  fragforge net probe --host HOST [--count N] [--json]
  fragforge net diagnose [--json]
  fragforge mouse-test [--duration 10] [--json]
  fragforge mem-bench [--json]
  fragforge baseline [--duration 120] [--runs 3] [--warmup 10] [--json]
  fragforge ab <ids...> [--runs 5] [--aa] [--mode interleaved|blocked] [--seed N] [--json]
  fragforge experiment list|show|resume|report [ID] [--out RUTA] [--json]

Capa 2 — inventario y debloat honesto (sin promesas de FPS):
  fragforge audit [--out RUTA] [--json]
  fragforge inventory [services|tasks|startup|appx] [--json]
  fragforge simulate <ids...> [--json]
  fragforge drift [--reapply] [--json]
  fragforge atlas [--json]
  fragforge myths [ID] [--json]
  fragforge footprint [--duration 60] [--label TEXT] [--json]

Mantenimiento (lista cerrada de verbos; requiere --confirm VERBO):
  fragforge maintenance retrim|component-cleanup [--confirm VERBO] [--dry-run] [--json]

Opciones globales: --data-root RUTA --culture es-MX|en-US
Códigos: 0 éxito; 1 fallo; 2 uso; 3 preflight/catálogo; 4 cancelación; 5 rollback crítico; 6 medición inválida.
Seguridad: Force solo omite idempotencia/drift; nunca denylist, condiciones ni preflight.
Los tweaks Advanced/Experimental requieren perfil Advanced, --accept-risk ID y la frase exacta.
""");
    }
}
