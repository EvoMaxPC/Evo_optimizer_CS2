namespace FragForge.Core.Cs2Profile;

/// <summary>Por qué se rechazó una ruta. Cada valor da un mensaje distinto y accionable.</summary>
public enum PathRejection
{
    None,
    /// <summary>La ruta está dentro de <c>userdata</c>. Steam la gestiona; escribir ahí está prohibido.</summary>
    InsideUserData,
    /// <summary>Está dentro de la instalación de Steam pero NO en <c>game\csgo\cfg</c>.</summary>
    NotTheCfgFolder,
    /// <summary>El nombre del archivo no es uno de los dos permitidos.</summary>
    NotAnAllowedFileName,
    /// <summary>La ruta no es absoluta después de normalizar.</summary>
    NotAbsolute
}

/// <summary>Resultado de validar una ruta: aceptada con su forma normalizada, o rechazada con motivo.</summary>
public sealed record PathCheck(bool IsAllowed, string? NormalizedPath, PathRejection Rejection, string Reason)
{
    public static PathCheck Allowed(string path) => new(true, path, PathRejection.None, "");
    public static PathCheck Rejected(PathRejection rejection, string reason) => new(false, null, rejection, reason);
}

/// <summary>
/// Guardas de ruta del exportador. Todo lo que se escriba dentro de la instalación de CS2 pasa por aquí.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por qué no basta con <c>StartsWith</c>.</b> <c>C:\Steam</c> es prefijo de <c>C:\Steam2</c>, así que
/// comparar con <c>StartsWith</c> dejaría escribir en una carpeta que no es la de Steam. Aquí se compara
/// por SEGMENTOS: la ruta se parte y se exige que cada segmento coincida, no que el texto empiece igual.
/// </para>
/// <para>
/// <b>Qué se normaliza.</b> Rutas relativas (se rechazan: exigen una base), <c>..</c> y <c>.</c> (se
/// resuelven con <see cref="Path.GetFullPath(string)"/>), mayúsculas y minúsculas (Windows no distingue) y
/// separadores mezclados <c>/</c> y <c>\</c> (se unifican al del sistema).
/// </para>
/// </remarks>
public static class Cs2PathGuard
{
    /// <summary>Nombre de la carpeta que gestiona Steam y donde está PROHIBIDO escribir.</summary>
    public const string UserDataFolderName = "userdata";

    /// <summary>Subcarpeta, dentro de la instalación del juego, donde vive el cfg. Dos segmentos exactos.</summary>
    public static readonly string[] CfgRelativeSegments = ["game", "csgo", "cfg"];

    /// <summary>
    /// Valida el destino de un archivo DENTRO de la instalación del juego.
    /// </summary>
    /// <param name="candidatePath">Ruta pedida, absoluta o relativa a <paramref name="baseDirectory"/>.</param>
    /// <param name="gameExecutablePath">Ruta de <c>cs2.exe</c> de la instalación localizada.</param>
    /// <param name="steamRoot">Raíz de Steam, solo para poder decir "esto está en Steam pero no es el cfg".</param>
    /// <param name="baseDirectory">Base para rutas relativas. Normalmente el directorio de trabajo.</param>
    public static PathCheck CheckGameDestination(string candidatePath, string gameExecutablePath, string? steamRoot, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return PathCheck.Rejected(PathRejection.NotAbsolute, "La ruta está vacía.");

        var normalized = Normalize(candidatePath, baseDirectory);
        if (normalized is null)
            return PathCheck.Rejected(PathRejection.NotAbsolute, "La ruta no se puede convertir en una ruta absoluta válida.");

        // El nombre debe ser uno de los dos permitidos. Se comprueba antes que nada porque es lo más barato
        // de explicar al usuario.
        var fileName = Path.GetFileName(normalized);
        if (!IsAllowedFileName(fileName))
            return PathCheck.Rejected(PathRejection.NotAnAllowedFileName,
                $"Solo se puede escribir '{Cs2ProfileGenerator.GameCfgFileName}' o '{Cs2ProfileGenerator.AutoexecFileName}' en la carpeta del juego, no '{fileName}'.");

        // userdata: prohibido sin excepciones, y se comprueba ANTES que nada más.
        if (IsInsideUserData(normalized, steamRoot))
            return PathCheck.Rejected(PathRejection.InsideUserData,
                "El destino está dentro de userdata, que gestiona Steam. FragForge no escribe ahí.");

        // La carpeta del cfg se deriva del EJECUTABLE localizado, no de la ruta pedida: así el usuario no
        // puede apuntar a otra instalación ni colar una carpeta distinta.
        var expected = CfgDirectoryFor(gameExecutablePath);
        if (expected is null)
            return PathCheck.Rejected(PathRejection.NotTheCfgFolder, "No se pudo derivar la carpeta cfg del ejecutable de CS2 indicado.");

        var actualDirectory = Path.GetDirectoryName(normalized);
        if (actualDirectory is null || !AreSamePath(actualDirectory, expected))
            return PathCheck.Rejected(PathRejection.NotTheCfgFolder,
                $"El destino debe ser exactamente '{expected}'. Se rechazó '{actualDirectory}'.");

        return PathCheck.Allowed(normalized);
    }

    /// <summary>
    /// Carpeta <c>...\game\csgo\cfg</c> derivada de <c>...\game\bin\win64\cs2.exe</c>.
    /// Se sube tres niveles (win64 -> bin -> game) y se baja a <c>csgo\cfg</c>.
    /// </summary>
    public static string? CfgDirectoryFor(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath)) return null;
        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(gameExecutablePath));
        if (executableDirectory is null) return null;

        // ...\game\bin\win64 -> ...\game
        var gameDirectory = Path.GetDirectoryName(Path.GetDirectoryName(executableDirectory));
        if (gameDirectory is null) return null;
        var combined = Path.Combine(gameDirectory, "csgo", "cfg");
        return Path.GetFullPath(combined);
    }

    /// <summary>¿La ruta cae dentro de alguna carpeta <c>userdata</c> de la instalación de Steam?</summary>
    public static bool IsInsideUserData(string normalizedPath, string? steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return false;
        var userDataRoot = Path.GetFullPath(Path.Combine(steamRoot, UserDataFolderName));
        return IsSameOrChild(normalizedPath, userDataRoot);
    }

    /// <summary>Nombre permitido en la carpeta del juego. Exacto, sin distinguir mayúsculas (es Windows).</summary>
    public static bool IsAllowedFileName(string? fileName) =>
        fileName is not null &&
        (fileName.Equals(Cs2ProfileGenerator.GameCfgFileName, StringComparison.OrdinalIgnoreCase) ||
         fileName.Equals(Cs2ProfileGenerator.AutoexecFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Igualdad de rutas por SEGMENTOS, no por texto: evita el bypass <c>C:\Steam</c> vs <c>C:\Steam2</c> y
    /// absorbe mayúsculas y separadores mezclados. Tras normalizar con <see cref="Path.GetFullPath(string)"/>
    /// se comparan segmento a segmento con <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    public static bool AreSamePath(string left, string right)
    {
        var a = Segments(left);
        var b = Segments(right);
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool IsSameOrChild(string path, string root)
    {
        var child = Segments(path);
        var parent = Segments(root);
        if (child.Count < parent.Count) return false;
        for (var i = 0; i < parent.Count; i++)
            if (!child[i].Equals(parent[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static IReadOnlyList<string> Segments(string path) =>
        Path.GetFullPath(path)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Normaliza a ruta absoluta. Devuelve null si la entrada no se puede convertir. Una ruta relativa se
    /// resuelve contra <paramref name="baseDirectory"/> si se da; si no, se considera inválida.
    /// </summary>
    public static string? Normalize(string path, string? baseDirectory)
    {
        try
        {
            var unified = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(unified))
            {
                if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
                unified = Path.Combine(baseDirectory, unified);
            }
            return Path.GetFullPath(unified);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
