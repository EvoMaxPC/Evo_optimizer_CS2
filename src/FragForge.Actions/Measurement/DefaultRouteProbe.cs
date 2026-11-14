using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Pregunta al stack IP cuál es la mejor interfaz para salir hacia una dirección externa
/// (<c>GetBestInterface</c>, <c>Iphlpapi.dll</c>).
///
/// <para>
/// Es exactamente la decisión que toma Windows al enrutar, y por eso es la forma correcta de saber cuál es el
/// adaptador de la ruta por defecto. La alternativa que se había usado antes —"el único adaptador con puerta de
/// enlace"— es incorrecta en una PC con Ethernet y Wi-Fi conectados: <b>los dos</b> tienen puerta de enlace, y
/// ese criterio no elegía ninguno.
/// </para>
///
/// <para>
/// La dirección de destino es una constante documentada de documentación (TEST-NET-1, RFC 5737): nunca se
/// envía un paquete, solo se consulta la tabla de rutas. No hay tráfico de red ni DNS.
/// </para>
///
/// <para>
/// <c>DefaultDllImportSearchPaths(System32)</c> es el mismo patrón que usan <c>DisplayProbe</c> y
/// <c>MouseRateProbe</c> en este repo: la DLL nunca se resuelve desde el directorio de trabajo.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class DefaultRouteProbe
{
    /// <summary>
    /// Destino de la consulta: <c>192.0.2.1</c> (TEST-NET-1, RFC 5737), reservado para documentación. No se
    /// contacta: <c>GetBestInterface</c> solo mira la tabla de rutas.
    /// </summary>
    public const string ProbeDestination = "192.0.2.1";

    /// <summary>
    /// Índice de la interfaz por la que saldría el tráfico hacia <see cref="ProbeDestination"/>. Devuelve
    /// <c>null</c> si la llamada falla: no se adivina una interfaz (R-L).
    /// </summary>
    public static int? GetBestInterfaceIndex()
    {
        if (!OperatingSystem.IsWindows()) return null;

        // GetBestInterface recibe la dirección en orden de red (big-endian) como uint.
        if (!IPAddress.TryParse(ProbeDestination, out var address)) return null;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return null;
        var networkOrder = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);

        var result = GetBestInterface(networkOrder, out var index);
        // 0 = NO_ERROR. Cualquier otra cosa (sin ruta, sin adaptador) es "no se sabe".
        return result == 0 ? (int)index : null;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);
}
