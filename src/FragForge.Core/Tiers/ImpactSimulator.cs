using FragForge.Core.Inventory;
using FragForge.Core.Models;

namespace FragForge.Core.Tiers;

/// <summary>Un id del catálogo que se quiere aplicar, con el impacto que tendría.</summary>
public sealed record ImpactAssessment(string TweakId, IReadOnlyList<string> Services, IReadOnlyList<string> BlockedBy, IReadOnlyList<string> AffectedDependents, IReadOnlyList<string> Notes)
{
    public bool IsSafe => BlockedBy.Count == 0;
}

public sealed record ImpactReport(IReadOnlyList<ImpactAssessment> Assessments, IReadOnlyList<string> Blockers)
{
    public bool Safe => Blockers.Count == 0;
    public string Summary => Safe
        ? $"Simulación correcta: {Assessments.Count} tweak(s) sin bloqueos."
        : $"BLOQUEADO: {string.Join(" | ", Blockers)}";
}

/// <summary>
/// Simulador de impacto: dado un conjunto de ids y el inventario real, recorre el grafo de dependencias
/// y dice qué se vería afectado antes de tocar nada.
///
/// Reglas:
/// <list type="bullet">
/// <item>Un servicio T0 o ausente de la tabla de tiers bloquea la simulación entera.</item>
/// <item>Si un servicio T0 depende, directa o transitivamente, del servicio que se quiere tocar, se bloquea.</item>
/// <item>Los dependientes de T1/T2/T3 se listan como advertencia, no como bloqueo.</item>
/// </list>
/// Se ejecuta en dry-run y antes de cualquier Apply real. No modifica el sistema.
/// </summary>
public sealed class ImpactSimulator(ServiceTierCatalog tiers)
{
    public ImpactReport Simulate(IEnumerable<string> tweakIds, IReadOnlyList<TweakDefinition> definitions, SystemInventory inventory)
    {
        var byId = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var serviceIndex = inventory.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var assessments = new List<ImpactAssessment>();
        var blockers = new List<string>();

        foreach (var tweakId in tweakIds)
        {
            if (!byId.TryGetValue(tweakId, out var definition))
            {
                blockers.Add($"{tweakId}: no está en el catálogo.");
                continue;
            }
            var services = definition.Actions
                .Where(a => a.Kind.Equals("Service", StringComparison.OrdinalIgnoreCase))
                .Select(ExtractServiceName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToArray();
            if (services.Length == 0) { assessments.Add(new ImpactAssessment(tweakId, [], [], [], ["No toca servicios: sin impacto en el grafo."])); continue; }

            var blockedBy = new List<string>();
            var affected = new List<string>();
            var notes = new List<string>();
            foreach (var service in services)
            {
                var tier = tiers.TierOf(service);
                if (tier == SystemTier.NeverTouch)
                    blockedBy.Add($"{service} es T0 (núcleo o protegido) o no figura en service-tiers.json.");
                else if (!tiers.IsApplicable(service))
                    blockedBy.Add($"{service} es {tier}; solo T1 y T2 son aplicables.");
                else if (!serviceIndex.ContainsKey(service))
                    notes.Add($"{service} no existe en este equipo: el tweak quedará NotApplicable, no es un fallo.");

                var coreDependents = TransitiveDependents(service, serviceIndex).Where(d => tiers.TierOf(d) == SystemTier.NeverTouch).ToArray();
                if (coreDependents.Length > 0)
                    blockedBy.Add($"{service}: de él dependen servicios T0 ({string.Join(", ", coreDependents)}).");

                foreach (var dependent in TransitiveDependents(service, serviceIndex).Where(d => tiers.TierOf(d) != SystemTier.NeverTouch))
                    affected.Add($"{dependent} ({tiers.TierOf(dependent)})");
            }

            assessments.Add(new ImpactAssessment(tweakId, services, blockedBy, affected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), notes));
            blockers.AddRange(blockedBy.Select(b => $"{tweakId}: {b}"));
        }

        return new ImpactReport(assessments, blockers);
    }

    /// <summary>Dependientes transitivos, sin ciclos, sin incluir el propio servicio.</summary>
    public static IReadOnlySet<string> TransitiveDependents(string service, IReadOnlyDictionary<string, ServiceEntry> index)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(service);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!index.TryGetValue(current, out var entry)) continue;
            foreach (var dependent in entry.Dependents)
                if (seen.Add(dependent)) queue.Enqueue(dependent);
        }
        seen.Remove(service);
        return seen;
    }

    /// <summary>El nombre del servicio de una acción Service, leído del JSON de parámetros.</summary>
    public static string? ExtractServiceName(ActionSpec action)
    {
        if (action.Parameters.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (action.Parameters.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String) return name.GetString();
        if (action.Parameters.TryGetProperty("Name", out var capital) && capital.ValueKind == System.Text.Json.JsonValueKind.String) return capital.GetString();
        return null;
    }
}
