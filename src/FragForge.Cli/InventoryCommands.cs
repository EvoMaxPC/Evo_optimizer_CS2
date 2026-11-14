using FragForge.Actions.Inventory;
using FragForge.Core.Abstractions;
using FragForge.Core.Atlas;
using FragForge.Core.Drift;
using FragForge.Core.Inventory;
using FragForge.Core.Localization;
using FragForge.Core.Logging;
using FragForge.Core.Myths;
using FragForge.Core.Models;
using FragForge.Core.Sessions;
using FragForge.Core.Persistence;
using FragForge.Core.Tiers;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Cli;

/// <summary>
/// Comandos de la Capa 2. Todos son de LECTURA salvo <c>drift --reapply</c>, que reaplica exactamente
/// los tweaks que FragForge ya había aplicado y perdió, y <c>footprint</c>, que solo escribe su informe.
///
/// El inventario se guarda en <c>reports\audit-&lt;fecha&gt;.json</c> para que la evidencia quede en disco
/// aunque la consola se cierre.
/// </summary>
public static class InventoryCommands
{
    public static async Task<int> AuditAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var inventory = await services.GetRequiredService<ISystemInventory>().BuildAsync(cancellationToken);
        var tiers = services.GetRequiredService<ServiceTierCatalog>();
        var report = AuditReport.From(inventory, tiers);

        var paths = services.GetRequiredService<FragForgePaths>();
        Directory.CreateDirectory(paths.Reports);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var output = parsed.Value("out") ?? Path.Combine(paths.Reports, "audit-" + stamp + ".json");
        await AtomicFile.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(report, JsonDefaults.Options), cancellationToken);

        if (parsed.Has("json")) ConsoleOutput.Json(report);
        else
        {
            Console.WriteLine($"Inventario de {inventory.MachineName} · Windows {inventory.OsBuild} · {(inventory.IsComplete ? "completo" : "INCOMPLETO")}");
            Console.WriteLine($"  Servicios: {inventory.Services.Count} (+{inventory.UserServiceTemplates.Count} plantillas por usuario)");
            Console.WriteLine($"  Tareas:    {inventory.Tasks.Count}  Arranque: {inventory.Startup.Count}  Appx: {inventory.Appx.Count}");
            if (report.ApplicableServices.Count > 0) Console.WriteLine($"  Servicios en tier aplicable: {string.Join(", ", report.ApplicableServices)}");
            if (report.UnknownServices.Count > 0) Console.WriteLine($"  Servicios fuera de la tabla (T0 por omisión): {report.UnknownServices.Count}");
            foreach (var warning in inventory.Warnings) Console.Error.WriteLine("  AVISO: " + warning);
            Console.WriteLine("Informe: " + output);
        }
        // Un inventario incompleto NO es un éxito: si faltó una fuente, el código lo dice.
        return inventory.IsComplete ? ExitCodes.Success : ExitCodes.Failure;
    }

    public static async Task<int> DriftAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var drift = services.GetRequiredService<DriftService>();
        var report = await drift.DetectAsync(cancellationToken);

        if (parsed.Has("json")) ConsoleOutput.Json(report);
        else
        {
            Console.WriteLine(report.Summary);
            foreach (var item in report.Items)
                Console.WriteLine($"  [{item.Kind}] {item.TweakId}: {item.Expected} → {item.Actual}. {item.Explanation}");
            foreach (var warning in report.Warnings) Console.Error.WriteLine("  AVISO: " + warning);
        }

        if (!parsed.Has("reapply")) return ExitCodes.Success;
        var lost = report.Items.Where(i => i.Kind == DriftKind.Lost).Select(i => i.TweakId).ToArray();
        if (lost.Length == 0) { Console.WriteLine("No hay nada que reaplicar: no se perdió ningún tweak aplicado."); return ExitCodes.Success; }

        var engine = services.GetRequiredService<ITweakEngine>();
        var batch = await engine.ApplyAsync(lost, new ApplyOptions(), cancellationToken);
        if (!parsed.Has("json")) ConsoleOutput.Batch(batch);
        return batch.HasCriticalFailure ? ExitCodes.RollbackCritical : batch.Success ? ExitCodes.Success : ExitCodes.Failure;
    }

    public static int Myths(IServiceProvider services, ParsedArgs parsed)
    {
        var catalog = services.GetRequiredService<MythCatalog>();
        var culture = services.GetRequiredService<Localizer>().Culture;
        var id = parsed.Positionals.FirstOrDefault();
        var myths = id is null ? catalog.Entries : catalog.Entries.Where(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (parsed.Has("json")) { ConsoleOutput.Json(myths); return ExitCodes.Success; }
        if (id is not null && !myths.Any()) { Console.Error.WriteLine($"No hay un mito con id '{id}'. Prueba: fragforge myths"); return ExitCodes.Usage; }
        foreach (var myth in myths)
        {
            Console.WriteLine($"[{myth.Verdict}] {myth.Title.Resolve(culture)}");
            Console.WriteLine("  " + myth.Why.Resolve(culture));
            if (myth.WhatHelpsInstead is not null) Console.WriteLine("  Qué ayuda: " + myth.WhatHelpsInstead.Resolve(culture));
            Console.WriteLine("  Referencia: " + myth.Reference);
            Console.WriteLine();
        }
        return ExitCodes.Success;
    }

    public static async Task<int> InventoryAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var inventory = await services.GetRequiredService<ISystemInventory>().BuildAsync(cancellationToken);
        var tiers = services.GetRequiredService<ServiceTierCatalog>();
        var section = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();

        if (parsed.Has("json"))
        {
            ConsoleOutput.Json(section switch
            {
                "services" => inventory.Services,
                "tasks" => inventory.Tasks,
                "startup" => inventory.Startup,
                "appx" => inventory.Appx,
                _ => inventory
            });
            return inventory.IsComplete ? ExitCodes.Success : ExitCodes.Failure;
        }

        switch (section)
        {
            case "services":
                foreach (var service in inventory.Services.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{tiers.TierOf(service.Name),-3} {service.StartMode,-9} {service.State,-8} {service.Name}");
                break;
            case "tasks":
                foreach (var task in inventory.Tasks) Console.WriteLine($"{(task.Enabled ? "on " : "off")} {task.Path}");
                break;
            case "startup":
                foreach (var entry in inventory.Startup)
                    Console.WriteLine($"{Approval(entry),-10} {entry.Location,-16} {entry.Name} → {Truncate(entry.Command, 80)}");
                break;
            case "appx":
                foreach (var entry in inventory.Appx) Console.WriteLine($"{(entry.IsFramework == true ? "[fw]" : "    ")} {entry.Name}");
                break;
            case null:
                Console.WriteLine($"{inventory.Services.Count} servicios · {inventory.Tasks.Count} tareas · {inventory.Startup.Count} entradas de arranque · {inventory.Appx.Count} paquetes Appx");
                break;
            default:
                Console.Error.WriteLine("inventory admite: services, tasks, startup, appx.");
                return ExitCodes.Usage;
        }
        foreach (var warning in inventory.Warnings) Console.Error.WriteLine("  AVISO: " + warning);
        return inventory.IsComplete ? ExitCodes.Success : ExitCodes.Failure;
    }

    public static async Task<int> SimulateAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var ids = parsed.Positionals.ToArray();
        if (ids.Length == 0) { Console.Error.WriteLine("simulate requiere al menos un id de tweak."); return ExitCodes.Usage; }
        var catalog = services.GetRequiredService<ITweakCatalog>();
        var inventory = await services.GetRequiredService<ISystemInventory>().BuildAsync(cancellationToken);
        var report = services.GetRequiredService<ImpactSimulator>().Simulate(ids, catalog.Definitions, inventory);

        if (parsed.Has("json")) ConsoleOutput.Json(report);
        else
        {
            Console.WriteLine(report.Summary);
            foreach (var assessment in report.Assessments)
            {
                Console.WriteLine($"  {assessment.TweakId}: {(assessment.IsSafe ? "viable" : "BLOQUEADO")}");
                foreach (var service in assessment.Services) Console.WriteLine($"    servicio {service} → tier {(services.GetRequiredService<ServiceTierCatalog>().TierOf(service))}");
                foreach (var dependent in assessment.AffectedDependents) Console.WriteLine($"    dependiente afectado: {dependent}");
                foreach (var note in assessment.Notes) Console.WriteLine($"    nota: {note}");
            }
            foreach (var blocker in report.Blockers) Console.Error.WriteLine("  BLOQUEO: " + blocker);
        }
        return report.Safe ? ExitCodes.Success : ExitCodes.Preflight;
    }

    public static async Task<int> AtlasAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var hardware = await services.GetRequiredService<IHardwareProfiler>().ProfileAsync(cancellationToken);
        var profile = AtlasProfile.FromHardware(hardware);
        if (parsed.Has("json")) ConsoleOutput.Json(profile);
        else
        {
            Console.WriteLine($"Atlas: {profile.Confidence} — {profile.Summary}");
            foreach (var marker in profile.Markers)
                Console.WriteLine($"  [{(marker.Detected == true ? "sí" : marker.Detected == false ? "no" : "?")}] {marker.Name}: {marker.Description} ({marker.Evidence})");
            Console.WriteLine(profile.Detected
                ? "Los tweaks con condición Atlas están disponibles. La detección es heurística y no demuestra que Atlas siga instalado."
                : "Sin marcadores suficientes: los tweaks con condición Atlas NO se aplicarán.");
        }
        return ExitCodes.Success;
    }

    public static async Task<int> FootprintAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var seconds = parsed.Value("duration") is { } text && double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 60.0;
        var label = parsed.Value("label") ?? "footprint";

        BenchmarkSession session;
        if (OperatingSystem.IsWindows() && services.GetService<FootprintProbe>() is { } probe)
            session = await probe.RunAsync(label, seconds, cancellationToken);
        else
            session = await services.GetRequiredService<UnsupportedFootprintProbe>().RunAsync(label, seconds, cancellationToken);

        await services.GetRequiredService<BenchmarkSessionStore>().SaveAsync(session, cancellationToken);
        if (parsed.Has("json")) ConsoleOutput.Json(session);
        else
        {
            Console.WriteLine($"Huella '{session.Id}' ({session.Measurements.Count} métricas)");
            foreach (var measurement in session.Measurements.Where(m => m.Unit.Length > 0))
                Console.WriteLine($"  {measurement.Metric,-22} {measurement.Value,10:0.##} {measurement.Unit}");
            foreach (var note in session.Measurements.Where(m => m.Metadata.ContainsKey("note") && m.Metadata["note"].Length > 0))
                Console.Error.WriteLine($"  AVISO {note.Metric}: {note.Metadata["note"]}");
            Console.WriteLine("Sin efecto medible esperado en FPS: esto describe la huella del sistema, no promete rendimiento.");
        }
        return ExitCodes.Success;
    }

    private static string Approval(StartupEntry entry) => entry.ApprovedEnabled switch
    {
        true => "habilitado",
        false => "deshabilitado",
        null => "desconocido"
    };

    private static string Truncate(string? value, int length) => string.IsNullOrEmpty(value) ? "" : value.Length <= length ? value : value[..length] + "…";
}
