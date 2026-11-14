using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class DependencyResolver
{
    public static IReadOnlyList<TweakDefinition> Resolve(IEnumerable<string> ids, ITweakCatalog catalog)
    {
        var requested = ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) throw new FragForgeException("empty_selection", "Selecciona al menos un tweak.");
        var closure = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TweakDefinition>();
        var errors = new List<string>();
        foreach (var id in requested)
        {
            if (!catalog.TryGet(id, out var definition) || definition is null) errors.Add($"Tweak desconocido: '{id}'.");
            else Visit(definition);
        }
        if (errors.Count > 0) throw new FragForgeException("dependency_resolution", string.Join(" ", errors));
        foreach (var a in ordered)
        foreach (var b in ordered)
        {
            if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.ConflictsWith.Contains(b.Id, StringComparer.OrdinalIgnoreCase) || b.ConflictsWith.Contains(a.Id, StringComparer.OrdinalIgnoreCase)) errors.Add($"Conflicto entre '{a.Id}' y '{b.Id}'.");
        }
        if (errors.Count > 0) throw new FragForgeException("tweak_conflict", string.Join(" ", errors.Distinct(StringComparer.OrdinalIgnoreCase)));
        return ordered;
        void Visit(TweakDefinition definition)
        {
            if (visited.Contains(definition.Id)) return;
            if (!visiting.Add(definition.Id)) { errors.Add($"Ciclo de dependencias en '{definition.Id}'."); return; }
            foreach (var dependency in definition.DependsOn)
            {
                if (!catalog.TryGet(dependency, out var parent) || parent is null) { errors.Add($"'{definition.Id}' depende del tweak inexistente '{dependency}'."); continue; }
                Visit(parent);
            }
            visiting.Remove(definition.Id);
            visited.Add(definition.Id);
            closure[definition.Id] = definition;
            ordered.Add(definition);
        }
    }
}
