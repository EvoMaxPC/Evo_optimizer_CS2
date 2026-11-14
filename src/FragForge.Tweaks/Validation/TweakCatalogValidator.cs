using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Models;
using FragForge.Core.Tiers;

namespace FragForge.Tweaks.Validation;

public sealed class TweakCatalogValidator(JsonSchemaValidator schema, ITweakConditionFactory conditions, ITweakActionFactory actions, ServiceTierCatalog? tiers = null)
{
    /// <summary>
    /// Regla de la Capa 2: un tweak con acción <c>Service</c> solo es válido si el servicio figura
    /// explícitamente en <c>service-tiers.json</c> con tier T1 o T2. Cualquier otra cosa —T0, T3, T4,
    /// servicio desconocido o parámetro ilegible— hace fallar la carga del catálogo.
    /// Sin tabla de tiers (null) la regla no se aplica: es el modo de la Capa 0/1 para no romper sus pruebas.
    /// </summary>
    public ServiceTierCatalog Tiers => tiers ?? ServiceTierCatalog.Empty;

    public IReadOnlyList<string> ValidateStructure(JsonElement document) => schema.Validate(document);
    public IReadOnlyList<string> ValidateCatalog(IReadOnlyList<TweakDefinition> definitions)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!ids.Add(definition.Id)) errors.Add($"ID duplicado: {definition.Id}");
            if (definition.Title.EsMx.Length == 0 || definition.Title.EnUs.Length == 0 || definition.Description.EsMx.Length == 0 || definition.Description.EnUs.Length == 0) errors.Add($"{definition.Id}: textos localizados vacíos");
            if (definition.Actions.Count == 0) errors.Add($"{definition.Id}: sin acciones");
            if (!definition.ReadOnly && !definition.RequiresAdmin) errors.Add($"{definition.Id}: una acción mutante requiere Admin");
            if (definition.References.Count == 0) errors.Add($"{definition.Id}: sin referencias");
            if (definition.DependsOn.Contains(definition.Id, StringComparer.OrdinalIgnoreCase) || definition.ConflictsWith.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)) errors.Add($"{definition.Id}: dependencia/conflicto consigo mismo");
            foreach (var condition in definition.Conditions) { try { conditions.Create(condition); } catch (Exception ex) { errors.Add($"{definition.Id}: condición inválida: {ex.Message}"); } }
            foreach (var action in definition.Actions)
            {
                try { actions.Create(action); } catch (Exception ex) { errors.Add($"{definition.Id}: acción inválida: {ex.Message}"); }
                if (tiers is not null && action.Kind.Equals("Service", StringComparison.OrdinalIgnoreCase))
                {
                    var service = ImpactSimulator.ExtractServiceName(action);
                    if (string.IsNullOrWhiteSpace(service)) errors.Add($"{definition.Id}: acción Service sin nombre legible.");
                    else if (!tiers.IsApplicable(service!))
                        errors.Add($"{definition.Id}: el servicio '{service}' es {tiers.TierOf(service!)} en service-tiers.json; solo se permite T1/T2.");
                }
                if (action.Kind.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) || action.Kind.Equals("Bcdedit", StringComparison.OrdinalIgnoreCase))
                    if (definition.Risk is not (RiskLevel.Advanced or RiskLevel.Experimental)) errors.Add($"{definition.Id}: {action.Kind} requiere riesgo Advanced/Experimental");
            }
        }
        foreach (var definition in definitions)
        {
            foreach (var dependency in definition.DependsOn) if (!ids.Contains(dependency)) errors.Add($"{definition.Id}: dependencia inexistente {dependency}");
            foreach (var conflict in definition.ConflictsWith) if (!ids.Contains(conflict)) errors.Add($"{definition.Id}: conflicto inexistente {conflict}");
        }
        var catalog = new InMemoryCatalog(definitions);
        foreach (var definition in definitions)
        {
            try { DependencyResolver.Resolve([definition.Id], catalog); }
            catch (FragForgeException ex) { errors.Add($"{definition.Id}: {ex.Message}"); }
        }
        return errors;
    }
    private sealed class InMemoryCatalog(IReadOnlyList<TweakDefinition> definitions) : ITweakCatalog
    {
        private readonly IReadOnlyDictionary<string, TweakDefinition> _map = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<TweakDefinition> Definitions => definitions;
        public TweakDefinition Get(string id) => _map[id];
        public bool TryGet(string id, out TweakDefinition? definition) => _map.TryGetValue(id, out definition);
        public string GetDefinitionHash(string id) => "";
    }
}
