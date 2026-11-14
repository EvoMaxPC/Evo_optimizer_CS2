namespace FragForge.Core.Localization;

/// <summary>
/// Mensaje de estado que deja un escaneo, y la regla para decidir si el cambio de perfil debe
/// rehacerlo.
///
/// Ronda 6: el mensaje de un escaneo es TEXTO YA FORMADO que lleva DENTRO una copia del contador
/// ("Escaneo completado: 30 evaluados; Mostrando 3 de 30 tweaks en el perfil Safe."). Notificar la
/// propiedad del contador no rehace ese texto, así que al cambiar de perfil la barra de estado seguía
/// anunciando el perfil anterior mientras la tabla ya había cambiado. Esta clase fija las dos piezas
/// para poder probarlas sin WPF (los tests son net10.0 y no pueden referenciar la App).
/// </summary>
public static class ScanStatusText
{
    /// <summary>Mensaje de estado de un escaneo, con el contador que corresponda en ese momento.</summary>
    public static string Format(int evaluated, string countText) =>
        $"Escaneo completado: {evaluated} evaluados; {countText}";

    /// <summary>
    /// ¿El mensaje actual sigue siendo el que dejó el último escaneo? Solo entonces el cambio de perfil
    /// puede rehacerlo. Si otra acción lo reemplazó ("Aplicación completada. Snapshot: …", un error,
    /// "Listo"), esto devuelve false y el cambio de perfil NO debe pisarlo.
    /// </summary>
    public static bool IsStillTheScanMessage(string? current, string? lastScanStatus) =>
        lastScanStatus is not null && string.Equals(current, lastScanStatus, StringComparison.Ordinal);
}
