using System.Globalization;

namespace FragForge.Core.Cs2Profile;

/// <summary>Una opción que el generador va a emitir, ya con su valor resuelto.</summary>
/// <param name="Id">Id del catálogo.</param>
/// <param name="Text">Comando o argumento exacto.</param>
/// <param name="Value">Valor a escribir. Null = opción sin valor (los argumentos de Steam).</param>
public sealed record PlannedOption(string Id, string Text, string? Value);

/// <summary>Resultado de resolver las preferencias: lo que se emitirá y lo que no se pudo decidir.</summary>
public sealed record ProfilePlan
{
    public IReadOnlyList<PlannedOption> CfgOptions { get; init; } = [];
    public IReadOnlyList<PlannedOption> LaunchOptions { get; init; } = [];
    /// <summary>Advertencias en texto. Nunca se sustituye un dato ausente por un número: se avisa.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Traduce preferencias + catálogo a opciones concretas. PURA: no hace E/S, no lee el reloj, no toca Steam.
/// </summary>
/// <remarks>
/// Regla dura de esta capa: si falta un dato de entrada (el refresco), NO se inventa. Se emite una
/// advertencia y la opción que dependía de él no se escribe. Un fps_max inventado sería peor que no tenerlo.
/// </remarks>
public static class Cs2ProfilePlanner
{
    /// <summary>
    /// Factor por defecto del tope derivado del refresco. HEURÍSTICA NO VALIDADA: se usa el propio refresco
    /// (1.0) porque no hay medición que justifique otro número en esta ronda.
    /// </summary>
    public const double DefaultRefreshFactor = 1.0;

    /// <summary>
    /// Valor de <c>engine_no_focus_sleep</c> cuando se quiere que el motor NO duerma al perder el foco.
    /// Es 0 porque la convar cuenta MILISEGUNDOS de espera: dormir 0 ms es no dormir.
    /// </summary>
    /// <remarks>
    /// NO está verificado en esta instalación (no hay fuente citable): es el comportamiento esperado según
    /// la semántica de la convar. Lo único OBSERVADO en la configuración real del usuario es el valor 20,
    /// que es el valor, no la semántica. Si esto resultara falso, este valor y la descripción del catálogo
    /// estarían al revés.
    /// </remarks>
    public const string SleepDisabledValue = "0";

    /// <summary>
    /// Valor cuando SÍ se quiere dormir al perder el foco. 1 ms es el valor positivo mínimo: la convar es de
    /// milisegundos y el usuario pidió un sí/no, no una cantidad. HEURÍSTICA NO VALIDADA.
    /// </summary>
    public const string SleepEnabledValue = "1";

    public static ProfilePlan Plan(Cs2ProfilePreferences preferences, Cs2OptionCatalog catalog)
    {
        var warnings = new List<string>();
        var selected = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // 1) Lo que el usuario tildó. Los ids desconocidos no se escriben en silencio: se avisa.
        foreach (var choice in preferences.Options)
        {
            var option = catalog.Find(choice.Id);
            if (option is null) { warnings.Add($"La opción '{choice.Id}' no está en el catálogo: se ignora."); continue; }
            if (!IsAllowed(option, preferences, warnings)) continue;
            selected[option.Id] = choice.Value ?? option.DefaultValue;
        }

        // 2) Tope de FPS: manda sobre cualquier fps_max tildado a mano (evita duplicados y contradicciones).
        if (preferences.FpsCap.Mode != FpsCapMode.SinTope && catalog.Find("fps_max") is { } fpsMax)
        {
            var cap = ResolveCap(preferences, warnings);
            if (cap is null) selected.Remove(fpsMax.Id);
            else selected[fpsMax.Id] = cap.Value.ToString(CultureInfo.InvariantCulture);
        }

        // 3) Interruptores explícitos de VSync y de dormir fuera de foco.
        if (preferences.VSync is { } vsync && catalog.Find("mat_vsync") is { } matVsync)
            selected[matVsync.Id] = vsync ? "1" : "0";
        if (preferences.SleepWhenUnfocused is { } sleep && catalog.Find("engine_no_focus_sleep") is { } sleepOption)
        {
            // La convar es de MILISEGUNDOS y se ESPERA que 0 signifique "no dormir" (comportamiento no
            // verificado en esta instalación; ver el catálogo). Por eso la correspondencia es directa:
            // pedir que duerma -> valor > 0; pedir que no duerma -> 0.
            // Invertirlo ponía el juego a dormir justo cuando el usuario pedía lo contrario.
            selected[sleepOption.Id] = sleep ? SleepEnabledValue : SleepDisabledValue;
        }

        // 4) Orden determinista: el del catálogo, para que dos ejecuciones den el mismo texto.
        var cfg = new List<PlannedOption>();
        var launch = new List<PlannedOption>();
        foreach (var option in catalog.Options)
        {
            if (!selected.TryGetValue(option.Id, out var value)) continue;
            var planned = new PlannedOption(option.Id, option.Text, option.ValueType == Cs2OptionValueType.None ? null : value);
            (option.Kind == Cs2OptionKind.Cfg ? cfg : launch).Add(planned);
        }

        return new ProfilePlan { CfgOptions = cfg, LaunchOptions = launch, Warnings = warnings };
    }

    /// <summary>Tope efectivo, o null si no se puede decidir sin inventar un número.</summary>
    private static int? ResolveCap(Cs2ProfilePreferences preferences, List<string> warnings)
    {
        switch (preferences.FpsCap.Mode)
        {
            case FpsCapMode.Fijo:
                if (preferences.FpsCap.FixedFps is { } fixedFps && fixedFps > 0) return fixedFps;
                warnings.Add("El tope de FPS es 'Fijo' pero no tiene un valor mayor que 0: no se escribirá fps_max.");
                return null;

            case FpsCapMode.DerivadoDelRefresco:
                if (preferences.MeasuredRefreshHz is not { } hz || hz <= 0)
                {
                    // R-L: el refresco no se conoce, así que no se inventa (ni 60 ni 144).
                    warnings.Add("No se pudo medir la tasa de refresco del monitor: no se escribirá fps_max. " +
                                 "Un tope inventado sería peor que no poner ninguno. Mide el refresco o usa un tope fijo.");
                    return null;
                }
                var factor = preferences.FpsCap.RefreshFactor ?? DefaultRefreshFactor;
                if (factor <= 0)
                {
                    warnings.Add("El factor del tope derivado del refresco no es mayor que 0: no se escribirá fps_max.");
                    return null;
                }
                var derived = (int)Math.Round(hz * factor, MidpointRounding.AwayFromZero);
                if (derived <= 0)
                {
                    warnings.Add("El tope derivado del refresco sale 0 o negativo: no se escribirá fps_max.");
                    return null;
                }
                return derived;

            default:
                return null;
        }
    }

    /// <summary>
    /// ¿Se puede incluir esta opción? Las <see cref="Cs2Evidence.Unverified"/> solo con permiso explícito;
    /// si no, se avisa de que se ha omitido (no se omite en silencio).
    /// </summary>
    private static bool IsAllowed(Cs2Option option, Cs2ProfilePreferences preferences, List<string> warnings)
    {
        if (option.Evidence == Cs2Evidence.Unverified && !preferences.IncludeUnverified)
        {
            warnings.Add($"'{option.Id}' no tiene verificación: se omite. Usa --include-unverified si aun así la quieres.");
            return false;
        }
        return true;
    }
}
