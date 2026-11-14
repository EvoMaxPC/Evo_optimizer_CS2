using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Core.Drift;

public enum DriftKind
{
    /// <summary>FragForge lo aplicó y ya no está aplicado: lo deshizo Windows Update, otra herramienta o el usuario.</summary>
    Lost,
    /// <summary>FragForge lo revirtió y ahora está aplicado: alguien lo volvió a poner.</summary>
    Reappeared,
    /// <summary>El snapshot registraba el tweak pero el estado actual no se puede leer.</summary>
    Unknown
}

public sealed record DriftItem(string TweakId, DriftKind Kind, string Expected, string Actual, string Explanation, string? SnapshotId, DateTimeOffset? AppliedAt);

public sealed record DriftReport(DateTimeOffset CapturedAt, string OsBuild, IReadOnlyList<DriftItem> Items, IReadOnlyList<string> Warnings)
{
    public bool HasDrift => Items.Any(i => i.Kind != DriftKind.Unknown);
    public string Summary => HasDrift
        ? $"{Items.Count(i => i.Kind == DriftKind.Lost)} tweak(s) perdido(s), {Items.Count(i => i.Kind == DriftKind.Reappeared)} reaparecido(s)."
        : Items.Count == 0 ? "Sin drift: nada aplicado por FragForge sigue aplicado o se perdió." : "Solo hay elementos con estado ilegible.";
}

/// <summary>
/// Detección de drift. Compara lo que FragForge aplicó (journal + snapshots) contra el estado actual
/// del sistema.
///
/// Se apoya en el JOURNAL (fuente de intención) y en los snapshots NO revertidos (fuente de estado
/// capturado). Un tweak aplicado que ya no está aplicado es drift "perdido"; un tweak revertido que
/// volvió a aparecer aplicado es drift "reaparecido".
///
/// La atribución a una actualización de características usa el build de Windows guardado en el
/// snapshot (campo ADITIVO y opcional: los snapshots antiguos siguen cargando porque el campo es
/// anulable y la deserialización es tolerante a su ausencia).
/// </summary>
public sealed class DriftService(IJournal journal, ISnapshotStore snapshots, ITweakEngine engine, ITweakCatalog catalog, IHardwareProfiler profiler)
{
    public async Task<DriftReport> DetectAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var hardware = await profiler.ProfileAsync(cancellationToken);
        var currentBuild = hardware.Os.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var entries = await journal.ReadAllAsync(cancellationToken);
        var allSnapshots = await snapshots.ListAsync(cancellationToken);
        var byId = allSnapshots.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        // Última intención por tweak según el journal.
        var lastIntent = new Dictionary<string, (JournalEventType Event, DateTimeOffset At, string? SnapshotId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => e.TweakId is not null))
        {
            var id = entry.TweakId!;
            if (entry.EventType is JournalEventType.TweakApplied or JournalEventType.TweakRolledBack or JournalEventType.SnapshotReverted)
                if (!lastIntent.TryGetValue(id, out var existing) || entry.Timestamp >= existing.At)
                    lastIntent[id] = (entry.EventType, entry.Timestamp, entry.SnapshotId);
        }

        var items = new List<DriftItem>();
        foreach (var (tweakId, intent) in lastIntent.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.TryGet(tweakId, out _))
            {
                warnings.Add($"{tweakId}: está en el journal pero ya no existe en el catálogo; no se evalúa.");
                continue;
            }

            TweakStatus status;
            try { status = await engine.GetStateAsync(tweakId, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(new DriftItem(tweakId, DriftKind.Unknown, intent.Event.ToString(), "Error: " + ex.Message, "El estado no se pudo leer.", intent.SnapshotId, intent.At));
                continue;
            }

            var appliedNow = status.State is TweakState.Applied or TweakState.AppliedExternally;
            var appliedByFragForge = intent.Event == JournalEventType.TweakApplied;
            string? buildAtApply = null;
            if (intent.SnapshotId is not null && byId.TryGetValue(intent.SnapshotId, out var snapshot))
                buildAtApply = snapshot.HardwareBuild ?? snapshot.Hardware?.Os.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (appliedByFragForge && !appliedNow)
            {
                items.Add(new DriftItem(
                    tweakId,
                    DriftKind.Lost,
                    "Applied",
                    status.State.ToString(),
                    ExplainLoss(buildAtApply, currentBuild, status),
                    intent.SnapshotId,
                    intent.At));
            }
            else if (!appliedByFragForge && appliedNow)
            {
                items.Add(new DriftItem(
                    tweakId,
                    DriftKind.Reappeared,
                    "NotApplied",
                    status.State.ToString(),
                    "FragForge lo había revertido y ahora vuelve a estar aplicado. Probablemente otra herramienta o el propio Windows lo repuso.",
                    intent.SnapshotId,
                    intent.At));
            }
        }

        return new DriftReport(DateTimeOffset.UtcNow, currentBuild, items, warnings);
    }

    private static string ExplainLoss(string? buildAtApply, string currentBuild, TweakStatus status)
    {
        var reason = $"Se aplicó y ahora está {status.State}.";
        if (buildAtApply is not null && buildAtApply != currentBuild)
            return reason + $" El build de Windows cambió de {buildAtApply} a {currentBuild}: probablemente una actualización de características lo revirtió.";
        if (status.State == TweakState.NotApplicable)
            return reason + " El objetivo ya no existe en este equipo (servicio o tarea eliminados por una actualización).";
        return reason + " Sin cambio de build registrado: revisa el journal antes de reaplicar.";
    }
}
