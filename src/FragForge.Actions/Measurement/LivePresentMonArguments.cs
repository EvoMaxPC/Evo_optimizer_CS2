namespace FragForge.Actions.Measurement;

/// <summary>
/// Construye los argumentos de PresentMon 2.6.0 para medición en vivo por stdout.
/// Función pura: no lanza nada. La duración la controla el acumulador (L1), no PresentMon:
/// por eso NUNCA se emiten <c>--timed</c> ni <c>--terminate_after_timed</c>.
/// </summary>
public static class LivePresentMonArguments
{
    /// <summary>
    /// Nombre único por corrida; FragForge no cierra sesiones ETW ajenas. Si matar PresentMon
    /// dejara viva su sesión ETW, quedaría huérfana (NO verificado).
    /// </summary>
    public const string SessionNamePrefix = "FragForgeLive-";

    /// <summary>Genera un nombre de sesión con ocho dígitos hexadecimales de un Guid.</summary>
    public static string NewSessionName() => SessionNamePrefix + Guid.NewGuid().ToString("N").Substring(0, 8);

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
            "--no_console_stats",
        ];
    }
}
