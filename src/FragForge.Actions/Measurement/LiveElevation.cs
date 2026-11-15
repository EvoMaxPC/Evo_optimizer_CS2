using System.Security.Principal;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Chequeo mínimo de elevación para la medición en vivo: PresentMon necesita permisos de administrador
/// (sesión ETW). Mismo patrón que el perfilador de hardware (<c>HardwareProfiler</c>): si no se puede
/// comprobar, vale <c>false</c> (no se finge elevación).
/// </summary>
public static class LiveElevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
