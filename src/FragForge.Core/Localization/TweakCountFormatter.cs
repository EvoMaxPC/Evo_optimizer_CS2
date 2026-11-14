using FragForge.Core.Models;

namespace FragForge.Core.Localization;

/// <summary>
/// Texto del contador de tweaks ("Mostrando 7 de 30 tweaks en el perfil Balanced.").
///
/// Ronda 6: esto vivía como propiedad calculada en el ViewModel de WPF, que los tests
/// (<c>net10.0</c>) no pueden referenciar. Se extrae aquí como función PURA para poder probarla, y
/// sobre todo para fijar el contrato con el catálogo REAL: el conteo mostrado no puede decrecer al
/// subir de perfil y <c>Safe</c> tiene que ser estrictamente menor que <c>Advanced</c>.
///
/// Es una función de formato: no consulta el catálogo ni conoce WPF. Los dos conteos se pasan ya
/// calculados (<c>Tweaks.Count</c> y <c>_catalog.Definitions.Count</c>), así que el texto depende
/// solo de sus argumentos.
///
/// El perfil se muestra con <see cref="CultureInfo.InvariantCulture"/> del propio enum
/// (<c>Safe</c>, <c>Balanced</c>, <c>Competitive</c>, <c>Advanced</c>): los nombres de perfil no se
/// traducen, igual que antes del cambio.
/// </summary>
public static class TweakCountFormatter
{
    /// <summary>
    /// Texto del contador. <paramref name="shown"/> es lo que la tabla está mostrando y
    /// <paramref name="total"/> el tamaño del catálogo.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Si algún conteo es negativo: un contador negativo es un error de programación, no algo que
    /// deba mostrarse en la UI.
    /// </exception>
    public static string Format(TweakProfile profile, int shown, int total)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shown);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        // Interpolación simple: los dos números son enteros y el nombre del perfil es el del enum, así
        // que la salida es la misma en es-MX y en en-US. El texto NO se traduce (igual que antes).
        return $"Mostrando {shown} de {total} tweaks en el perfil {profile}.";
    }
}
