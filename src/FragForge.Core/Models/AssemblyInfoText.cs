using System.Reflection;

namespace FragForge.Core.Models;

/// <summary>
/// Nombre y versión para el título de la ventana (Capa 11.1), leídos del ensamblado en vez de escritos a mano.
///
/// <para>
/// Antes el título decía "FragForge — Capa 0": un nombre de fase de desarrollo que se quedaba congelado y no
/// decía la versión que el usuario estaba ejecutando. Ahora sale de <see cref="AssemblyInformationalVersionAttribute"/>
/// (que incluye el sufijo <c>+hash</c> de la compilación) y cae a <see cref="AssemblyName.Version"/> si falta.
/// </para>
///
/// <para>
/// Vive en Core y no en la App para que sea testeable sin WPF. En un test, <paramref name="assembly"/> permite
/// pasar un ensamblado conocido en vez de depender del de pruebas.
/// </para>
/// </summary>
public static class AssemblyInfoText
{
    /// <summary>Nombre del producto. Constante: no se localiza, es una marca.</summary>
    public const string ProductName = "FragForge";

    /// <summary>
    /// Texto del título, p. ej. <c>FragForge 0.1.0</c>. Nunca incluye el sufijo de compilación <c>+abc123</c>:
    /// eso va en <see cref="VersionWithCommit"/>, porque en el título es ruido.
    /// </summary>
    public static string WindowTitle(Assembly? assembly = null)
    {
        var version = Version(assembly);
        return string.IsNullOrWhiteSpace(version) ? ProductName : $"{ProductName} {version}";
    }

    /// <summary>Versión informativa sin el sufijo <c>+hash</c>. Vacío si no se puede leer.</summary>
    public static string Version(Assembly? assembly = null) => Clean(Informational(assembly) ?? (assembly ?? typeof(AssemblyInfoText).Assembly).GetName().Version?.ToString(3) ?? "");

    /// <summary>Versión informativa COMPLETA, con el commit si el build lo incluyó. Para diagnóstico.</summary>
    public static string VersionWithCommit(Assembly? assembly = null) => Informational(assembly) ?? (assembly ?? typeof(AssemblyInfoText).Assembly).GetName().Version?.ToString(3) ?? "";

    private static string? Informational(Assembly? assembly)
    {
        var attribute = (assembly ?? typeof(AssemblyInfoText).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return string.IsNullOrWhiteSpace(attribute?.InformationalVersion) ? null : attribute.InformationalVersion;
    }

    private static string Clean(string value)
    {
        var plus = value.IndexOf('+');
        return (plus >= 0 ? value[..plus] : value).Trim();
    }
}
