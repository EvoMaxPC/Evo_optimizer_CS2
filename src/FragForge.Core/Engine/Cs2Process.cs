namespace FragForge.Core.Engine;

/// <summary>
/// Detección de CS2 en ejecución por NOMBRE de proceso.
///
/// Capa 8.7, reutilizada por la Capa 9 (modo sesión). Solo lee la lista de procesos y compara el
/// nombre: **no** abre handles, no lee módulos, no consulta hilos ni memoria, y no toca nada del
/// proceso. Eso la hace compatible con el anticheat y con R-M.
///
/// El nombre se compara sin extensión y sin distinguir mayúsculas. Windows no permite dos procesos con
/// el mismo nombre de imagen distinguidos por caso, así que la comparación ordinal-ignore-case es la
/// correcta.
///
/// <para>
/// El tipo no es Windows-only: <see cref="System.Diagnostics.Process"/> existe en <c>net10.0</c> y esta
/// clase no usa ninguna API de Windows. En plataformas donde no se pueden enumerar procesos, la función
/// devuelve <c>Unknown</c> en vez de mentir con <c>false</c> (R-L).
/// </para>
/// </summary>
public static class Cs2Process
{
    /// <summary>Nombre de imagen de CS2 sin extensión.</summary>
    public const string ProcessName = "cs2";

    /// <summary>
    /// ¿Hay un proceso <c>cs2.exe</c> en ejecución?
    ///
    /// Devuelve <c>null</c> (<see cref="Unknown"/>) si la enumeración de procesos falla: no poder
    /// comprobarlo NO es lo mismo que saber que no corre. Un <c>false</c> inventado haría que la
    /// recuperación del modo sesión revirtiera mientras el juego está abierto.
    /// </summary>
    public static bool? IsRunning(IEnumerable<string>? processNames)
    {
        if (processNames is null) return null;
        foreach (var name in processNames)
        {
            if (string.Equals(name, ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Enumeración real de nombres de proceso. Separada de <see cref="IsRunning"/> para poder probar la
    /// decisión sin tocar el sistema: los tests pasan una lista literal.
    ///
    /// No abre handles a los procesos: pide el nombre y los libera. Cualquier fallo de enumeración se
    /// propaga como <c>Unknown</c> para que el llamador decida, no se silencia.
    /// </summary>
    public static bool? IsRunningOnThisMachine()
    {
        try
        {
            var names = new List<string>();
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try { names.Add(process.ProcessName); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* proceso terminado entre la lista y la lectura */ }
                finally { process.Dispose(); }
            }
            return IsRunning(names);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
