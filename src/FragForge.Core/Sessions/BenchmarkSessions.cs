using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Core.Sessions;

/// <summary>
/// Almacén de <see cref="BenchmarkSession"/> con versión de esquema. El tipo original de Capa 0
/// (<c>Core/Models/Results.cs</c>) no la tenía; aquí se envuelve sin modificarlo, para no romper
/// el contrato existente ni el <c>ReportDocument</c>.
///
/// Se guarda en <c>reports\sessions\&lt;id&gt;.json</c>.
/// </summary>
public sealed class BenchmarkSessionStore(FragForgePaths paths)
{
    public string Root => Path.Combine(paths.Reports, "sessions");

    public async Task SaveAsync(BenchmarkSession session, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.Id) || session.Id.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException($"Identificador de sesión inválido: '{session.Id}'.", nameof(session));
        Directory.CreateDirectory(Root);
        var document = new BenchmarkSessionDocument { Session = session };
        await AtomicFile.WriteAllTextAsync(Path.Combine(Root, session.Id + ".json"), JsonSerializer.Serialize(document, JsonDefaults.Options), cancellationToken);
    }

    public async Task<BenchmarkSession?> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Root, id + ".json");
        if (!File.Exists(path)) return null;
        var document = JsonSerializer.Deserialize<BenchmarkSessionDocument>(await File.ReadAllTextAsync(path, cancellationToken), JsonDefaults.Options);
        return document?.Session;
    }

    public Task<IReadOnlyList<BenchmarkSession>> ListAsync(string? label = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Root)) return Task.FromResult<IReadOnlyList<BenchmarkSession>>([]);
        var sessions = new List<BenchmarkSession>();
        foreach (var file in Directory.GetFiles(Root, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = JsonSerializer.Deserialize<BenchmarkSessionDocument>(File.ReadAllText(file), JsonDefaults.Options);
                if (document?.Session is null) continue;
                if (label is not null && !document.Session.Label.Equals(label, StringComparison.OrdinalIgnoreCase)) continue;
                sessions.Add(document.Session);
            }
            catch (JsonException) { continue; }
        }
        return Task.FromResult<IReadOnlyList<BenchmarkSession>>(sessions);
    }
}

public sealed record BenchmarkSessionDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required BenchmarkSession Session { get; init; }
}

/// <summary>
/// Comparación simple antes/después para cuando NO hay estadística de Capa 1 disponible.
/// Se marca explícitamente como INDICATIVA y nunca se presenta como un veredicto.
/// </summary>
public sealed record FootprintComparison(string Metric, double Before, double After, string Unit, double ChangePercent, bool Indicative, string Note)
{
    public string Direction => ChangePercent < 0 ? "baja" : ChangePercent > 0 ? "sube" : "igual";
}

public static class FootprintComparer
{
    /// <summary>
    /// Diferencia porcentual simple, marcada como indicativa. Si existe Capa 1, el llamador debe usar
    /// <c>VerdictCalculator</c> en su lugar; esto es el camino mínimo y honesto.
    /// </summary>
    public static IReadOnlyList<FootprintComparison> Compare(BenchmarkSession before, BenchmarkSession after)
    {
        var afterByMetric = after.Measurements.ToDictionary(m => m.Metric, StringComparer.OrdinalIgnoreCase);
        var comparisons = new List<FootprintComparison>();
        foreach (var baseline in before.Measurements)
        {
            if (!afterByMetric.TryGetValue(baseline.Metric, out var current)) continue;
            var change = baseline.Value == 0 ? 0 : (current.Value - baseline.Value) / Math.Abs(baseline.Value) * 100.0;
            comparisons.Add(new FootprintComparison(
                baseline.Metric,
                baseline.Value,
                current.Value,
                current.Unit,
                change,
                Indicative: true,
                Note: "Diferencia simple sin intervalo de confianza: INDICATIVA. Para afirmar algo, mide con FragForge A/B."));
        }
        return comparisons;
    }
}
