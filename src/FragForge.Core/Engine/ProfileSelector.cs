using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class ProfileSelector
{
    /// <summary>
    /// Membresía de un tweak en un perfil. Tres casos, en orden:
    /// <list type="number">
    /// <item>El tweak declara <c>profiles</c>: manda ese campo, EXACTO. <c>[]</c> = ningún perfil.</item>
    /// <item>Sin <c>profiles</c> y con tag <c>opt-in</c>: fuera de los cuatro perfiles, SIN excepciones.
    /// Un tweak opt-in se aplica solo por id explícito.</item>
    /// <item>Sin <c>profiles</c> ni <c>opt-in</c>: regla por riesgo/evidencia, la de siempre.</item>
    /// </list>
    /// Este método gobierna únicamente la AUTOSELECCIÓN. Aplicar un tweak por id sigue permitido en
    /// cualquier perfil (decisión explícita del usuario): el motor no rechaza por pertenencia a perfil.
    /// </summary>
    public static bool Allows(TweakProfile profile, TweakDefinition definition)
    {
        if (definition.Profiles is not null) return definition.Profiles.Contains(profile.ToString(), StringComparer.OrdinalIgnoreCase);
        if (IsOptIn(definition)) return false;
        return profile switch
        {
            TweakProfile.Safe => definition.Risk == RiskLevel.Safe && definition.Evidence != EvidenceLevel.Experimental,
            TweakProfile.Balanced => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental,
            TweakProfile.Competitive => definition.Risk is RiskLevel.Safe or RiskLevel.Moderate && definition.Evidence != EvidenceLevel.Experimental,
            TweakProfile.Advanced => true,
            _ => false
        };
    }

    private static bool IsOptIn(TweakDefinition definition) => definition.Tags.Contains("opt-in", StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TweakDefinition> Select(IEnumerable<TweakDefinition> definitions, TweakProfile profile) => definitions.Where(d => Allows(profile, d)).OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Perfiles en los que aparece un tweak, para tests y reportes.</summary>
    public static IReadOnlyList<TweakProfile> ProfilesOf(TweakDefinition definition) =>
        new[] { TweakProfile.Safe, TweakProfile.Balanced, TweakProfile.Competitive, TweakProfile.Advanced }.Where(p => Allows(p, definition)).ToArray();

    public static void EnsureRiskAccepted(TweakDefinition definition, ApplyOptions options)
    {
        if (!Allows(options.Profile, definition) && !(options.Profile == TweakProfile.Advanced)) throw new FragForgeException("profile_rejected", $"'{definition.Id}' no está permitido por el perfil {options.Profile}.");
        if (definition.Risk is RiskLevel.Advanced or RiskLevel.Experimental || definition.Actions.Any(a => a.Kind.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) || a.Kind.Equals("Bcdedit", StringComparison.OrdinalIgnoreCase)))
        {
            if (options.Profile != TweakProfile.Advanced || !options.RiskAcknowledgements.Contains(definition.Id) || options.AdvancedRiskPhrase != "ACEPTO EL RIESGO") throw new FragForgeException("risk_confirmation_required", $"'{definition.Id}' requiere perfil Advanced, aceptación explícita por ID y la frase exacta 'ACEPTO EL RIESGO'.");
        }
    }
}
