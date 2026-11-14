using FragForge.Core.Localization;

namespace FragForge.Core.Models;

/// <summary>
/// Texto de aviso anticheat (Capa 10.2). El catálogo define la salvedad concreta por tweak; cuando no la
/// define, aquí está el contrato común, que es lo que la app muestra en vez de una celda vacía.
///
/// <para>
/// El texto por defecto NO es un "no comprobado": es el contrato del motor. FragForge no inyecta, no abre
/// handles a <c>cs2.exe</c>, no modifica archivos del juego y no toca Steam ni procesos de anticheat. Lo que
/// el aviso NO puede decir es "seguro con X": eso no lo respalda ninguna fuente.
/// </para>
/// </summary>
public static class AnticheatNote
{
    public const string DefaultEsMx =
        "No modifica el proceso ni los archivos del juego. No toca Steam ni anticheat. Revisa los requisitos de tu proveedor.";

    public const string DefaultEnUs =
        "Does not modify the game process or files. Does not touch Steam or anticheat. Check your provider's requirements.";

    /// <summary>Texto a mostrar: el del catálogo si existe, si no el contrato común.</summary>
    public static string Resolve(LocalizedText? note, string culture) =>
        note?.Resolve(culture) is { Length: > 0 } text
            ? text
            : culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? DefaultEnUs : DefaultEsMx;
}
