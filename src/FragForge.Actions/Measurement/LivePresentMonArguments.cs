namespace FragForge.Actions.Measurement;

/// <summary>
/// Construye los argumentos de PresentMon 2.6.0 para medición en vivo por stdout.
/// Función pura: no lanza nada. La duración la controla el acumulador (L1), no PresentMon:
/// por eso NUNCA se emiten <c>--timed</c> ni <c>--terminate_after_timed</c>.
/// </summary>
public static class LivePresentMonArguments
{
    /// <summary>
    /// Nombre de sesión ETW FIJO. Con <c>--stop_existing_session</c>, un FragForge nuevo limpia una
    /// sesión ETW que haya quedado de un corte (PresentMon puede dejarla abierta si se lo mata de golpe).
    /// Implica una sola instancia de medición en vivo a la vez.
    /// </summary>
    public const string DefaultSessionName = "FragForgeLive";

    /// <summary>
    /// Argumentos para <c>--output_stdout</c> midiendo solo <paramref name="processName"/>.
    /// El nombre se normaliza: si no termina en <c>.exe</c> (OrdinalIgnoreCase) se agrega UNA vez;
    /// <c>"cs2"</c> y <c>"cs2.exe"</c> dan <c>"cs2.exe"</c>. (PresentMon además canoniza el nombre:
    /// quita directorio y extensión y compara en minúsculas.)
    /// </summary>
    /// <exception cref="ArgumentException">Si <paramref name="processName"/> está vacío o en blanco.</exception>
    public static IReadOnlyList<string> Build(string processName, string sessionName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            throw new ArgumentException("El nombre del proceso no puede estar vacío.", nameof(processName));

        var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : processName + ".exe";

        return
        [
            "--output_stdout",
            "--process_name", exeName,
            "--session_name", sessionName,
            "--stop_existing_session",
            "--no_console_stats",
        ];
    }
}
