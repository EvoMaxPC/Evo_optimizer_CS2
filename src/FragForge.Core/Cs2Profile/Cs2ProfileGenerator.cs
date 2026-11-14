using System.Globalization;
using System.Text;
using FragForge.Core.Models;

namespace FragForge.Core.Cs2Profile;

/// <summary>Texto generado: el .cfg, las opciones de lanzamiento y las advertencias. Nada más.</summary>
public sealed record GeneratedProfile
{
    /// <summary>Contenido exacto del .cfg, con finales de línea <c>\n</c>.</summary>
    public string CfgText { get; init; } = "";

    /// <summary>Argumentos para pegar en Steam (vacío si no hay ninguno).</summary>
    public string LaunchOptions { get; init; } = "";

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Genera el texto del perfil. PURO y DETERMINISTA: la fecha y la versión entran como parámetros, no se leen
/// del reloj ni del ensamblado en tiempo de ejecución. Dos llamadas con la misma entrada dan el mismo texto,
/// byte a byte, lo que es lo mínimo para poder compararlo en un diff o en un test.
/// </summary>
/// <remarks>
/// El texto NO promete FPS ni resultados. Es una lista de comandos con un encabezado que dice quién lo
/// generó, cuándo y con qué versión. Un comentario no cambia el juego: quien lo lea debe poder repetir o
/// deshacer lo hecho.
/// </remarks>
public static class Cs2ProfileGenerator
{
    /// <summary>Marca de apertura del bloque. El exportador usa exactamente esta cadena.</summary>
    public const string BlockStart = "// >>> FragForge (no editar a mano)";

    /// <summary>Marca de cierre del bloque.</summary>
    public const string BlockEnd = "// <<< FragForge";

    /// <summary>Nombre del archivo nuevo en la carpeta cfg del juego.</summary>
    public const string GameCfgFileName = "fragforge-cs2.cfg";

    /// <summary>Nombre del autoexec del juego.</summary>
    public const string AutoexecFileName = "autoexec.cfg";

    /// <summary>Prefijo del backup del autoexec, antes de la fecha.</summary>
    public const string BackupPrefix = "autoexec.cfg.fragforge-backup-";

    /// <summary>Comando que el usuario debe ejecutar para activar el perfil cuando va en archivo aparte.</summary>
    public const string ExecHint = "exec fragforge-cs2";

    /// <summary>Formato de la fecha del encabezado. Invariante y ordenable: no depende de la cultura.</summary>
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Genera el perfil completo.
    /// </summary>
    /// <param name="preferences">Preferencias del usuario.</param>
    /// <param name="catalog">Catálogo de opciones.</param>
    /// <param name="generatedAtUtc">Fecha del encabezado. OBLIGATORIA para que la salida sea reproducible.</param>
    /// <param name="version">Versión de FragForge. Null = se lee del ensamblado.</param>
    public static GeneratedProfile Generate(
        Cs2ProfilePreferences preferences,
        Cs2OptionCatalog catalog,
        DateTimeOffset generatedAtUtc,
        string? version = null)
    {
        var plan = Cs2ProfilePlanner.Plan(preferences, catalog);
        var text = new StringBuilder();

        // Encabezado. Los números y la fecha van con cultura invariante (R-E): el mismo perfil en un equipo
        // en español y en uno en inglés debe producir el mismo archivo.
        text.Append("// FragForge — perfil de arranque de CS2 generado por FragForge\n");
        text.Append("// Versión: ").Append(string.IsNullOrWhiteSpace(version) ? AssemblyInfoText.Version() : version).Append('\n');
        text.Append("// Generado: ").Append(generatedAtUtc.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)).Append(" UTC\n");
        text.Append("// Objetivo declarado: ").Append(preferences.Objective.ToString()).Append('\n');
        text.Append("//\n");
        text.Append("// Este archivo no promete FPS ni mejora medible: fija valores de configuración que el\n");
        text.Append("// usuario eligió. Si algo no encaja, cambia el valor o borra la línea.\n");
        text.Append('\n');

        // Cuerpo: una línea por opción, sin duplicados (el planificador ya garantiza uno por id).
        foreach (var option in plan.CfgOptions)
        {
            if (option.Value is null) text.Append(option.Text).Append('\n');
            else text.Append(option.Text).Append(' ').Append(option.Value).Append('\n');
        }

        if (plan.CfgOptions.Count == 0) text.Append("// (sin opciones de .cfg seleccionadas)\n");

        var warnings = new List<string>(plan.Warnings);
        if (plan.LaunchOptions.Count > 0)
        {
            // Los argumentos NO van en el .cfg: Steam los pasa por línea de comandos y no son convars.
            text.Append('\n');
            text.Append("// Opciones de lanzamiento (NO van en el .cfg; se pegan en Steam):\n");
            foreach (var option in plan.LaunchOptions) text.Append("//   ").Append(option.Text).Append('\n');
        }

        return new GeneratedProfile
        {
            CfgText = text.ToString(),
            LaunchOptions = string.Join(' ', plan.LaunchOptions.Select(o => o.Text)),
            Warnings = warnings
        };
    }

    /// <summary>
    /// Envuelve el contenido en las marcas del bloque FragForge. El bloque va al FINAL del autoexec.
    /// </summary>
    public static string WrapInBlock(string content) =>
        BlockStart + "\n" + content.TrimEnd('\n') + "\n" + BlockEnd + "\n";
}
