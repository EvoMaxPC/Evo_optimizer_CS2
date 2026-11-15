namespace FragForge.Core.Models;

/// <summary>
/// Instantánea de SOLO LECTURA de la configuración de CS2 (Capa 8.6).
///
/// Todo lo que hay aquí se leyó del disco. Nada se escribe: el inspector no abre archivos de CS2 en modo
/// escritura, no toca <c>localconfig.vdf</c> ni lanza Steam. Si en el futuro se escribiera, tendría que
/// ser con Steam y CS2 cerrados y con copia de seguridad; no es esta ronda.
/// </summary>
public sealed record Cs2Inspection
{
    /// <summary>Ruta de cs2.exe si se resolvió; <c>null</c> si no (y entonces el resto no se puede leer).</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Cuenta de Steam activa según <c>ActiveProcess\ActiveUser</c>, o <c>null</c> si no se pudo leer.</summary>
    public string? ActiveSteamAccountId { get; init; }

    /// <summary>
    /// Carpeta de <c>userdata</c> correspondiente a la cuenta activa, o <c>null</c>. Steam nombra las
    /// carpetas con el <c>SteamID3</c> (la cuenta en formato 32 bits), no con el id de 64 bits: la
    /// conversión se hace explícitamente, no por coincidencia de texto.
    /// </summary>
    public string? ActiveUserDataDirectory { get; init; }

    /// <summary>Todas las carpetas de <c>userdata</c> encontradas, para poder decir "hay 5 cuentas".</summary>
    public IReadOnlyList<string> UserDataDirectories { get; init; } = [];

    /// <summary>Valores leídos de <c>cs2_video.txt</c> (clave → valor), ya normalizados.</summary>
    public IReadOnlyDictionary<string, string> VideoSettings { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ajustes por cuenta cuando la activa se desconoce; null indica lectura o parseo fallido.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>?> VideoSettingsByAccount { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.Ordinal);

    /// <summary>Convars guardadas en los archivos locales de la cuenta activa, clave → valor.</summary>
    public IReadOnlyDictionary<string, string> StoredConvars { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>False si no se pudo verificar completamente la lectura de convars.</summary>
    public bool StoredConvarsKnown { get; init; }

    /// <summary>Convars del <c>autoexec.cfg</c> del jugador, clave → valor. Solo lectura.</summary>
    public IReadOnlyDictionary<string, string> AutoexecConvars { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Conflictos detectados: una convar con valores distintos entre lo guardado y el <c>autoexec</c>, o una
    /// clave que aparece en ambos con distinto valor. Cada entrada es una frase lista para mostrar.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>Opciones de lanzamiento de 730 leídas del <c>localconfig.vdf</c>, o <c>null</c>. Solo lectura.</summary>
    public string? LaunchOptions { get; init; }

    /// <summary>Archivo válido sin clave de opciones para la app 730; false no indica ausencia.</summary>
    public bool LaunchOptionsKnownAbsent { get; init; }

    /// <summary>Texto recomendado de opciones de lanzamiento. Se muestra; NO se escribe en Steam.</summary>
    public string RecommendedLaunchOptions { get; init; } = "";

    /// <summary>Refresco configurado según <c>cs2_video.txt</c>, si está.</summary>
    public int? ConfiguredRefreshHz { get; init; }

    /// <summary>Motivos por los que algo no se pudo leer. Vacío = todo se leyó.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool HasAnyData => VideoSettings.Count > 0 || StoredConvars.Count > 0 || AutoexecConvars.Count > 0;
}
