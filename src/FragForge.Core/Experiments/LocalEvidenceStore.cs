using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Persistence;

namespace FragForge.Core.Experiments;

/// <summary>
/// Guarda en <c>experiments\local-evidence.json</c> el resultado de cada experimento por tweak.
///
/// IMPORTANTE: esto NO edita el catálogo embebido. La promoción de Evidence en el JSON del catálogo es
/// una decisión del desarrollador, y este archivo solo aporta la sugerencia con su fecha y build.
/// </summary>
public sealed class LocalEvidenceStore(FragForgePaths paths)
{
    public async Task<LocalEvidenceDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.LocalEvidence)) return new LocalEvidenceDocument();
        try
        {
            var json = await File.ReadAllTextAsync(paths.LocalEvidence, cancellationToken);
            return JsonSerializer.Deserialize<LocalEvidenceDocument>(json, JsonDefaults.Options) ?? new LocalEvidenceDocument();
        }
        catch (JsonException) { return new LocalEvidenceDocument(); }
    }

    public async Task RecordAsync(ExperimentState state, CancellationToken cancellationToken = default)
    {
        var verdict = state.Verdict;
        if (verdict?.Effect is null) return;
        var document = await ReadAsync(cancellationToken);
        var entries = document.Entries.ToList();
        foreach (var tweakId in state.Plan.TweakIds)
        {
            entries.RemoveAll(e => e.TweakId.Equals(tweakId, StringComparison.OrdinalIgnoreCase) && e.SessionId.Equals(state.Plan.SessionId, StringComparison.OrdinalIgnoreCase));
            entries.Add(new LocalEvidence
            {
                TweakId = tweakId,
                Verdict = verdict.Kind,
                Confidence = verdict.Confidence,
                P1LowChangePct = verdict.Effect.P1LowChangePct,
                AvgFpsChangePct = verdict.Effect.AvgFpsChangePct,
                ConfidenceInterval = verdict.Effect.P1LowBootstrap.IsValid
                    ? $"[{verdict.Effect.P1LowBootstrap.LowerBound:F2}, {verdict.Effect.P1LowBootstrap.UpperBound:F2}]"
                    : null,
                SessionId = state.Plan.SessionId,
                WindowsBuild = state.Environment?.OsBuild ?? "",
                SuggestedEvidenceLevel = Suggest(verdict),
                Note = "Sugerencia local; el catálogo embebido no se modifica automáticamente."
            });
        }
        var updated = new LocalEvidenceDocument { SchemaVersion = document.SchemaVersion, Entries = entries.OrderBy(e => e.TweakId, StringComparer.OrdinalIgnoreCase).ToArray() };
        Directory.CreateDirectory(paths.Experiments);
        await AtomicFile.WriteAllTextAsync(paths.LocalEvidence, JsonSerializer.Serialize(updated, JsonDefaults.Options), cancellationToken);
    }

    /// <summary>Sugerencia textual para el desarrollador. Nunca se aplica sola.</summary>
    private static string? Suggest(ExperimentVerdict verdict) => (verdict.Kind, verdict.Confidence) switch
    {
        (ExperimentVerdictKind.Improvement, VerdictConfidence.Confirmed) => "High",
        (ExperimentVerdictKind.Improvement, VerdictConfidence.Indicative) => "Medium",
        (ExperimentVerdictKind.NoDifference, _) => "Low",
        (ExperimentVerdictKind.Regression, _) => "Experimental",
        _ => null
    };

    public async Task<IReadOnlyList<LocalEvidence>> ForTweakAsync(string tweakId, CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).Entries.Where(e => e.TweakId.Equals(tweakId, StringComparison.OrdinalIgnoreCase)).ToArray();
}
