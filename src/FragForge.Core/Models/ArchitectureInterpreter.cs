using System.Runtime.InteropServices;

namespace FragForge.Core.Models;

/// <summary>
/// Arquitectura del sistema como valor de enum, NUNCA como texto localizado.
///
/// Motivo (hotfix Capa 0): <c>Win32_OperatingSystem.OSArchitecture</c> devuelve texto traducido
/// ("64 bits" en es-MX, "64-bit" en en-US). La condición <c>windows</c> lo comparaba contra el
/// literal "X64" del catálogo y marcaba TODO el catálogo como "No aplicable" en un equipo x64.
///
/// Regla R-C: solo valores de enum o numéricos. R-L: si no se reconoce, <see cref="Unknown"/>;
/// jamás se asume x64 por defecto.
/// </summary>
public enum OsArchitectureKind
{
    /// <summary>No se pudo determinar. NUNCA se interpreta como x64.</summary>
    Unknown = 0,
    X86,
    Arm,
    X64,
    Arm64
}

public static class ArchitectureInterpreter
{
    /// <summary>
    /// Mapea el valor NUMÉRICO de <c>Win32_Processor.Architecture</c> a enum.
    ///
    /// Valores del MOF de Microsoft: 0=x86, 5=ARM, 9=x64, 12=ARM64. Los que no tienen equivalente
    /// en <see cref="OsArchitectureKind"/> (1=MIPS, 2=Alpha, 3=PowerPC, 4=IA64, 6=ia64, 7=Alpha64,
    /// 8=MIPS64) devuelven <see cref="OsArchitectureKind.Unknown"/>: no se pliega un valor raro a x64.
    /// </summary>
    public static OsArchitectureKind FromProcessorArchitectureNumber(int? value) => value switch
    {
        0 => OsArchitectureKind.X86,
        5 => OsArchitectureKind.Arm,
        9 => OsArchitectureKind.X64,
        12 => OsArchitectureKind.Arm64,
        _ => OsArchitectureKind.Unknown
    };

    /// <summary>Arquitectura según <see cref="RuntimeInformation.OSArchitecture"/>: fuente preferida,
    /// no depende de WMI ni del idioma del sistema.</summary>
    public static OsArchitectureKind FromRuntime(Architecture architecture) => architecture switch
    {
        Architecture.X86 => OsArchitectureKind.X86,
        Architecture.X64 => OsArchitectureKind.X64,
        Architecture.Arm => OsArchitectureKind.Arm,
        Architecture.Arm64 => OsArchitectureKind.Arm64,
        _ => OsArchitectureKind.Unknown
    };

    /// <summary>Arquitectura del sistema en el que corre ESTE proceso.</summary>
    public static OsArchitectureKind FromCurrentRuntime() => FromRuntime(RuntimeInformation.OSArchitecture);

    /// <summary>
    /// Texto canónico del enum ("X64", "Arm64"...). Es lo que se compara contra el catálogo y lo que
    /// aparece en el motivo mostrado: nunca un texto localizado del sistema.
    /// </summary>
    public static string ToWireValue(OsArchitectureKind kind) => kind.ToString();

    /// <summary>
    /// Interpreta un texto de arquitectura ya almacenado (p. ej. leído de un perfil JSON).
    ///
    /// Acepta los valores de enum y, por compatibilidad con perfiles escritos por versiones
    /// anteriores, el texto de WMI en sus formas conocidas ("64 bits" de es-MX, "64-bit"/"64-bit" de
    /// en-US). Cualquier otra cosa es <see cref="OsArchitectureKind.Unknown"/>.
    /// </summary>
    public static OsArchitectureKind Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return OsArchitectureKind.Unknown;
        var normalized = text.Trim();

        // 1) Valor de enum canónico, que es lo que escribe esta versión.
        if (Enum.TryParse<OsArchitectureKind>(normalized, ignoreCase: true, out var parsed))
            return parsed;

        // 2) Compatibilidad con perfiles viejos que guardaron el texto de WMI.
        //    Se normaliza el texto a mano (sin depender de la cultura del hilo).
        var simplified = normalized.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return simplified switch
        {
            "64bits" or "64-bit" or "64bit" => OsArchitectureKind.X64,
            "32bits" or "32-bit" or "32bit" => OsArchitectureKind.X86,
            _ => OsArchitectureKind.Unknown
        };
    }
}
