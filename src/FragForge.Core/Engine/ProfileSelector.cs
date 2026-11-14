using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class ProfileSelector
{
    /// <summary>
    /// Los tweaks semilla que YA tenían el tag <c>opt-in</c> antes de esta corrección y llegaban a
    /// Competitive/Advanced. No se les cambia el perfil: el alcance de la corrección es el debloat.
    /// </summary>
    private static IReadOnlySet<string> LegacyOptInSeeds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cs2.fullscreen-optimizations-off",
        "network.i225v-eee-off",
        "power.fragforge-performance"
    };

    private static bool IsOptIn(TweakDefinition definition) => definition.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Regla del hotfix: un tweak marcado <c>opt-in</c> NO se selecciona automáticamente en NINGÚN
    /// perfil; se pide por id explícito (perfil Advanced). Única excepción: los semilla que ya eran
    /// opt-in antes, que conservan su perfil para no cambiar comportamiento fuera del debloat.
    /// </summary>
    private static bool OptInBlocksAutoSelection(TweakDefinition definition) => IsOptIn(definition) && !LegacyOptInSeeds.Contains(definition.Id);

    public static IReadOnlyList<TweakDefinition> Select(IEnumerable<TweakDefinition> definitions, TweakProfile profile) => definitions.Where(d => Allows(profile, d)).OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool Allows(TweakProfile profile, TweakDefinition definition) => profile switch
    {
        // Safe y Balanced ya excluían CUALQUIER opt-in: se deja igual, sin excepciones.
        TweakProfile.Safe => definition.Risk == RiskLevel.Safe && definition.Evidence != EvidenceLevel.Experimental && !IsOptIn(definition),
        TweakProfile.Balanced => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental && definition.Risk != RiskLevel.Experimental && !IsOptIn(definition),
        // Competitive y Advanced no filtraban opt-in: ahora sí, salvo los semilla que ya eran opt-in.
        TweakProfile.Competitive => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental && definition.Risk != RiskLevel.Experimental && !OptInBlocksAutoSelection(definition),
        TweakProfile.Advanced => !OptInBlocksAutoSelection(definition),
        _ => false
    };

    public static void EnsureRiskAccepted(TweakDefinition definition, ApplyOptions options)
    {
        if (!Allows(options.Profile, definition) && !(options.Profile == TweakProfile.Advanced)) throw new FragForgeException("profile_rejected", $"'{definition.Id}' no está permitido por el perfil {options.Profile}.");
        if (definition.Risk is RiskLevel.Advanced or RiskLevel.Experimental || definition.Actions.Any(a => a.Kind.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) || a.Kind.Equals("Bcdedit", StringComparison.OrdinalIgnoreCase)))
        {
            if (options.Profile != TweakProfile.Advanced || !options.RiskAcknowledgements.Contains(definition.Id) || options.AdvancedRiskPhrase != "ACEPTO EL RIESGO") throw new FragForgeException("risk_confirmation_required", $"'{definition.Id}' requiere perfil Advanced, aceptación explícita por ID y la frase exacta 'ACEPTO EL RIESGO'.");
        }
    }
}
