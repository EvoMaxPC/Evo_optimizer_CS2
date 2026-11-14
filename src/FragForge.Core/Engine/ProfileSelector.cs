using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class ProfileSelector
{
    public static IReadOnlyList<TweakDefinition> Select(IEnumerable<TweakDefinition> definitions, TweakProfile profile) => definitions.Where(d => Allows(profile, d)).OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    public static bool Allows(TweakProfile profile, TweakDefinition definition) => profile switch
    {
        TweakProfile.Safe => definition.Risk == RiskLevel.Safe && definition.Evidence != EvidenceLevel.Experimental && !definition.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase),
        TweakProfile.Balanced => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental && definition.Risk != RiskLevel.Experimental && !definition.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase),
        TweakProfile.Competitive => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental && definition.Risk != RiskLevel.Experimental,
        TweakProfile.Advanced => true,
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
