namespace FragForge.UI.Core;

/// <summary>Adapta al contexto gráfico los textos de preflight que el motor comparte con la CLI.</summary>
public static class PreflightPresentation
{
    private const string CliRemediation = "Habilítala en Protección del sistema o usa --enable-system-protection; --no-restore-point solo omite el punto, no el snapshot/journal. Si querés aplicar igual, repetí el comando con --acepto-sin-punto-de-restauracion";
    private const string UiRemediation = "Habilitá la protección del sistema en Windows o desde Mantenimiento. Si corresponde, usá la confirmación de la tarjeta para aplicar sin punto de restauración.";
    private const string CliRecheck = "La protección del sistema no pudo confirmarse; usa --no-restore-point si aceptas solo snapshot y journal.";
    private const string UiRecheck = "La protección del sistema no pudo confirmarse. Revisá las opciones de protección y punto de restauración en Mantenimiento.";

    public static string ForApp(string message)
    {
        var result = message.Replace(CliRemediation, UiRemediation, StringComparison.Ordinal)
            .Replace(CliRecheck, UiRecheck, StringComparison.Ordinal);
        // Si aparece una instrucción CLI nueva que no conocemos, no la mostramos como guía de la App.
        return result.Contains("--", StringComparison.Ordinal)
            ? "El preflight no se pudo completar. Ejecutá Doctor para revisar los motivos."
            : result;
    }
}
