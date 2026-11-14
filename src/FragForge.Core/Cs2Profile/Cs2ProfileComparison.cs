namespace FragForge.Core.Cs2Profile;

/// <summary>Relación entre el valor actual y el del perfil, por opción.</summary>
public enum ValueRelation
{
    /// <summary>El juego no tiene ese valor registrado (no se leyó, o no existe).</summary>
    NotPresent,

    /// <summary>Ya tiene exactamente el valor del perfil.</summary>
    Same,

    /// <summary>Tiene otro valor.</summary>
    Different,

    /// <summary>La opción es de lanzamiento y no está en las opciones actuales de Steam.</summary>
    MissingLaunchOption
}

/// <summary>Una fila de la comparación. Todo en texto: no se reinterpretan valores ajenos.</summary>
public sealed record OptionComparison(
    string Id,
    string Text,
    string? CurrentValue,
    string? ProfileValue,
    ValueRelation Relation,
    /// <summary>Dónde se leyó el valor actual: "autoexec", "convars guardadas" o null.</summary>
    string? CurrentSource);

/// <summary>Resultado de comparar el perfil con lo que hay en el equipo. SOLO LECTURA.</summary>
public sealed record ProfileComparison
{
    public IReadOnlyList<OptionComparison> Options { get; init; } = [];

    /// <summary>Opciones del perfil que ya están igual: no hace falta tocarlas.</summary>
    public int SameCount => Options.Count(o => o.Relation == ValueRelation.Same);

    /// <summary>Opciones del perfil que cambiarían algo.</summary>
    public int DifferentCount => Options.Count(o => o.Relation == ValueRelation.Different);

    /// <summary>Avisos en texto, incluidos los de precedencia no verificada.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Compara un perfil con la configuración observada. NO escribe: recibe lo que ya se leyó.
/// </summary>
/// <remarks>
/// <para>
/// <b>Qué se compara y qué no.</b> Se compara SOLO por texto exacto tras recortar espacios. No se normalizan
/// valores: si el juego guarda <c>0</c> y el perfil dice <c>0.0</c>, se informa como distinto en vez de
/// decidir que son lo mismo. Interpretar valores ajenos es cómo se acaba diciendo algo falso.
/// </para>
/// <para>
/// <b>Precedencia.</b> Si una convar aparece tanto en <c>autoexec.cfg</c> como en las convars guardadas por la
/// cuenta y con valores distintos, NO se afirma cuál gana: en esta ronda no hay fuente citable que lo
/// determine. Se informa de las dos y se dice explícitamente "no verificado".
/// </para>
/// </remarks>
public static class Cs2ProfileComparer
{
    /// <summary>
    /// Compara. <paramref name="autoexecValues"/> y <paramref name="storedConvars"/> vienen del inspector
    /// (solo lectura); <paramref name="currentLaunchOptions"/> son las opciones de lanzamiento actuales.
    /// </summary>
    public static ProfileComparison Compare(
        Cs2ProfilePreferences preferences,
        Cs2OptionCatalog catalog,
        IReadOnlyDictionary<string, string>? autoexecValues,
        IReadOnlyDictionary<string, string>? storedConvars,
        string? currentLaunchOptions)
    {
        var plan = Cs2ProfilePlanner.Plan(preferences, catalog);
        var rows = new List<OptionComparison>();
        var warnings = new List<string>(plan.Warnings);

        foreach (var planned in plan.CfgOptions)
        {
            // Se prefiere autoexec si está: es lo que el usuario escribió a mano. La guardada se usa si no.
            var fromAutoexec = TryGet(autoexecValues, planned.Text);
            var fromStored = TryGet(storedConvars, planned.Text);

            string? current;
            string? source;
            if (fromAutoexec is not null) { current = fromAutoexec; source = "autoexec"; }
            else if (fromStored is not null) { current = fromStored; source = "convars guardadas"; }
            else { current = null; source = null; }

            if (fromAutoexec is not null && fromStored is not null && !Same(fromAutoexec, fromStored))
            {
                // Sin fuente citable no se decide cuál manda: se dicen los dos valores y se marca como no verificado.
                warnings.Add($"'{planned.Text}' aparece en autoexec ('{fromAutoexec}') y en las convars guardadas ('{fromStored}') " +
                             "con valores distintos. La precedencia entre ambos no está verificada en esta versión.");
            }

            rows.Add(new OptionComparison(
                planned.Id, planned.Text, current, planned.Value,
                current is null ? ValueRelation.NotPresent : Same(current, planned.Value) ? ValueRelation.Same : ValueRelation.Different,
                source));
        }

        foreach (var planned in plan.LaunchOptions)
        {
            var present = ContainsToken(currentLaunchOptions, planned.Text);
            rows.Add(new OptionComparison(
                planned.Id, planned.Text, present ? planned.Text : null, null,
                present ? ValueRelation.Same : ValueRelation.MissingLaunchOption,
                present ? "opciones de lanzamiento" : null));
        }

        if (currentLaunchOptions is null)
            warnings.Add("No se pudieron leer las opciones de lanzamiento actuales: las de Steam se comparan como ausentes, no como distintas.");

        return new ProfileComparison { Options = rows, Warnings = warnings };
    }

    private static string? TryGet(IReadOnlyDictionary<string, string>? values, string key)
    {
        if (values is null) return null;
        if (values.TryGetValue(key, out var exact)) return exact;
        // Los diccionarios del inspector son OrdinalIgnoreCase; se cubre también el caso de uno ajeno.
        foreach (var pair in values)
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return null;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    /// <summary>¿Está el argumento entre las opciones de lanzamiento, como token separado?</summary>
    private static bool ContainsToken(string? launchOptions, string token)
    {
        if (string.IsNullOrWhiteSpace(launchOptions)) return false;
        return launchOptions
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase));
    }
}
