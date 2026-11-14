using Xunit;

namespace FragForge.Tests.Fakes;

/// <summary>
/// Un <c>[Fact]</c> que solo tiene sentido en Windows: fuera de Windows se marca como SKIP, no se ejecuta.
/// </summary>
/// <remarks>
/// <para>
/// El CI corre los tests en <c>ubuntu-latest</c> y en <c>windows-latest</c>
/// (<c>.github/workflows/ci.yml</c>, jobs <c>linux-core</c> y <c>windows-full</c>). Fuera de Windows, un
/// assert sobre rutas <c>C:\...</c> no comprueba el producto: <see cref="System.IO.Path"/> usa <c>/</c>
/// como separador y <see cref="System.IO.Path.GetFullPath(string)"/> trata <c>C:\Steam</c> como una ruta
/// RELATIVA al directorio actual, devolviendo algo como <c>/home/runner/work/C:\Steam</c>. El test
/// fallaria por el entorno, no por el codigo.
/// </para>
/// <para>
/// Por que un atributo y no <c>if (!OperatingSystem.IsWindows()) return;</c>: una salida temprana deja el
/// test en VERDE sin haber comprobado nada, y nadie se entera de que en ese runner no se probo. Un skip
/// sale en el informe como omitido, con el motivo. En este repositorio conviven las dos formas
/// (ScheduledTaskScriptParsesTests.cs:59 lo hace con <c>return</c>); aqui se usa la que no miente.
/// </para>
/// <para>
/// No anade paquetes: hereda de <see cref="FactAttribute"/> y escribe <see cref="FactAttribute.Skip"/>, que
/// xunit 2.9.3 permite asignar desde el constructor del atributo.
/// </para>
/// </remarks>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    /// <summary>Motivo del skip, para que el informe diga por que no se ejecuto.</summary>
    public const string Reason = "Solo en Windows: el assert depende de las reglas de ruta de Windows.";

    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = Reason;
    }
}
