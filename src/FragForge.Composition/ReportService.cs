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
        // E2: un journal ILEGIBLE no puede tumbar el informe. La app tiene que poder decir "no puedo leer
        // el journal, y este es el motivo" en vez de no arrancar el panel. Antes, ReadAllAsync (que no
        // captura nada, FileJournal.cs:46-57) propagaba JsonException/InvalidDataException y CreateAsync
        // se caia entero.
        //
        // Se reutiliza JournalIntegrity para llevarlo: ya forma parte de ReportDocument (que esta en
        // Abstractions y NO se toca) y ya tiene un campo Error para el motivo. IsValid=false + el mensaje
        // real de la excepcion. No se inventa "journal vacio": entries queda vacia, y ademas el informe
        // lleva marcado que no se pudo leer, que es la diferencia que importa.
        IReadOnlyList<JournalEntry> entries = [];
        var journalUnreadable = false;
        var unreadableReason = default(string);
        try
        {
            entries = await journal.ReadAllAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }   // cancelar lo pidio quien llama: no es un dato
        catch (Exception ex)
        {
            journalUnreadable = true;
            unreadableReason = ex.Message;
        }
        var integrity = journalUnreadable
            ? new JournalIntegrity(false, 0, $"El journal no se pudo leer: {unreadableReason}")
            : await journal.VerifyIntegrityAsync(cancellationToken);
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
