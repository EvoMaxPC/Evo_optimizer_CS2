using System.Globalization;
using FragForge.Actions.Files;
using FragForge.Core.Cs2Profile;
using FragForge.Core.Engine;
using FragForge.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Cli;

/// <summary>
/// <c>fragforge cs2 export|import|restore-autoexec</c>. Sin admin, sin servicios y sin escribir en Steam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Qué escribe y qué no.</b> Solo dos archivos, y solo dentro de la carpeta cfg del juego:
/// <c>fragforge-cs2.cfg</c> y <c>autoexec.cfg</c>. Nunca <c>userdata</c>, nunca <c>localconfig.vdf</c>.
/// Las opciones de lanzamiento se IMPRIMEN para que el usuario las pegue en Steam: no se tocan.
/// </para>
/// <para>
/// <b>Vista previa y confirmación.</b> Siempre se muestra el texto exacto antes de escribir. En la carpeta
/// del juego se pide confirmación explícita; <c>--yes</c> la omite (para tests y scripts).
/// </para>
/// </remarks>
public static class Cs2ProfileCommands
{
    public static IReadOnlyList<string> Verbs { get; } = ["export", "import", "restore-autoexec"];

    public static async Task<int> RunAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var prompt = services.GetService<IConsolePrompt>() ?? new ConsolePrompt();
        var verb = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (verb is null || !Verbs.Contains(verb))
        {
            prompt.WriteWarning($"cs2 requiere un verbo de la lista cerrada: {string.Join(" | ", Verbs)}.");
            return ExitCodes.Usage;
        }

        return verb switch
        {
            "export" => await ExportAsync(services, parsed, prompt, cancellationToken),
            "import" => await ImportAsync(services, parsed, prompt, cancellationToken),
            _ => await RestoreAutoexecAsync(services, parsed, prompt, cancellationToken)
        };
    }

    // ------------------------------------------------------------------ export

    private static async Task<int> ExportAsync(IServiceProvider services, ParsedArgs parsed, IConsolePrompt prompt, CancellationToken cancellationToken)
    {
        var catalog = Cs2OptionCatalog.LoadEmbedded();
        var inspection = await services.GetRequiredService<Cs2Inspector>().InspectAsync(cancellationToken);

        // 1) Tasa de refresco: es ENTRADA. Si no se pudo leer, null; nunca se inventa (R-L).
        var refresh = inspection.ConfiguredRefreshHz;
        if (refresh is null)
            prompt.WriteWarning("No se pudo leer la tasa de refresco del monitor: el tope derivado del refresco no estará disponible.");

        var preferences = BuildPreferences(parsed, catalog, refresh);
        var generated = Cs2ProfileGenerator.Generate(preferences, catalog, DateTimeOffset.UtcNow);

        // 2) Vista previa SIEMPRE, antes de escribir nada.
        prompt.WriteLine("");
        prompt.WriteLine("=== Vista previa del archivo .cfg (" + generated.CfgText.Split('\n').Length + " líneas) ===");
        foreach (var line in generated.CfgText.Split('\n')) prompt.WriteLine(line);
        prompt.WriteLine("=== fin de la vista previa ===");
        if (generated.LaunchOptions.Length > 0)
        {
            prompt.WriteLine("");
            prompt.WriteLine("Opciones de lanzamiento para pegar en Steam (Propiedades > Opciones de lanzamiento):");
            prompt.WriteLine("  " + generated.LaunchOptions);
        }
        foreach (var warning in generated.Warnings) prompt.WriteWarning("Aviso: " + warning);

        // 3) Comparación informativa con lo que ya hay (solo lectura).
        if (inspection.HasAnyData)
        {
            var comparison = Cs2ProfileComparer.Compare(preferences, catalog, inspection.AutoexecConvars, inspection.StoredConvars, inspection.LaunchOptions);
            prompt.WriteLine("");
            prompt.WriteLine("Comparación con lo que ya hay en este equipo:");
            foreach (var row in comparison.Options)
                prompt.WriteLine($"  {row.Text,-45} en archivos: {row.CurrentValue ?? "(sin dato)",-10} perfil: {row.ProfileValue ?? "(sin valor)",-10} -> {row.Relation}");
            foreach (var warning in comparison.Warnings) prompt.WriteWarning("Aviso: " + warning);
        }

        // 4) Destino: de la opción o preguntado.
        var destination = ResolveDestination(parsed, prompt);
        if (destination is null) return ExitCodes.Usage;

        var gameExe = inspection.ExecutablePath;
        if (destination.Value.Kind != ExportDestinationKind.Folder && gameExe is null)
        {
            prompt.WriteWarning("No se localizó la instalación de CS2: no se puede escribir en la carpeta del juego.");
            prompt.WriteWarning("Usa --dest carpeta:<ruta> para escribir el archivo donde tú elijas.");
            return ExitCodes.Preflight;
        }

        // 5) Aviso si el juego corre: los cambios se leen al reiniciar.
        var processProbe = services.GetService<Func<bool?>>() ?? Cs2Process.IsRunningOnThisMachine;
        if (processProbe() is true)
            prompt.WriteWarning("CS2 está en ejecución: los cambios se leen al iniciar el juego. Ciérralo y vuelve a abrirlo para que se apliquen.");

        // 6) Confirmación en la carpeta del juego (--yes la omite).
        if (destination.Value.Kind != ExportDestinationKind.Folder && !parsed.Has("yes"))
        {
            var confirmed = prompt.Confirm("¿Escribir en la carpeta cfg del juego?");
            if (confirmed != true)
            {
                prompt.WriteLine("No se ha escrito nada.");
                return ExitCodes.Success;
            }
        }

        // 7) Escribir.
        var request = new ExportRequest
        {
            Kind = destination.Value.Kind,
            FolderPath = destination.Value.Folder,
            GameExecutablePath = gameExe,
            SteamRoot = SteamRootFor(gameExe),
            CfgText = generated.CfgText,
            Timestamp = DateTimeOffset.UtcNow,
            Overwrite = parsed.Has("overwrite") || parsed.Has("yes")
        };

        var result = await Cs2ProfileExporter.ExportAsync(request, cancellationToken);

        if (!result.Written)
        {
            // Los fallos van por el canal de avisos (stderr en la consola real), no mezclados con la
            // salida normal: el usuario tiene que poder ver que NO se escribió.
            foreach (var message in result.Messages) prompt.WriteWarning(message);
            return ExitCodes.Failure;
        }

        foreach (var message in result.Messages) prompt.WriteLine(message);

        prompt.WriteLine("");
        prompt.WriteLine("Cómo activarlo:");
        prompt.WriteLine("  1. Si escribiste en una carpeta cualquiera, copia fragforge-cs2.cfg a la carpeta cfg del juego.");
        prompt.WriteLine("  2. Añade '" + Cs2ProfileGenerator.ExecHint + "' a tu autoexec.cfg, o escríbelo en la consola del juego.");
        if (generated.LaunchOptions.Length > 0)
            prompt.WriteLine("  3. Pega en Steam esta línea de opciones de lanzamiento: " + generated.LaunchOptions);
        return ExitCodes.Success;
    }

    private static Cs2ProfilePreferences BuildPreferences(ParsedArgs parsed, Cs2OptionCatalog catalog, int? refresh)
    {
        var objective = Enum.TryParse<Cs2ProfileObjective>(parsed.Value("objective"), ignoreCase: true, out var parsedObjective)
            ? parsedObjective
            : Cs2ProfileObjective.Equilibrado;

        var cap = ParseFpsCap(parsed.Value("fps-cap"));

        return new Cs2ProfilePreferences
        {
            Objective = objective,
            FpsCap = cap,
            MeasuredRefreshHz = refresh,
            IncludeUnverified = parsed.Has("include-unverified"),
            Options = []
        };
    }

    /// <summary>
    /// Interpreta <c>--fps-cap</c>: <c>sin-tope</c>, un entero (<c>240</c>) o <c>refresco</c> / <c>refresco:0.5</c>.
    /// </summary>
    public static FpsCap ParseFpsCap(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return FpsCap.None;
        var value = raw.Trim();
        if (value.Equals("sin-tope", StringComparison.OrdinalIgnoreCase)) return FpsCap.None;

        if (value.StartsWith("refresco", StringComparison.OrdinalIgnoreCase))
        {
            var separator = value.IndexOf(':');
            if (separator < 0) return FpsCap.DerivedFromRefresh(1.0);
            return double.TryParse(value[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) && factor > 0
                ? FpsCap.DerivedFromRefresh(factor)
                : throw new ArgumentException("--fps-cap refresco:<factor> necesita un factor mayor que 0 (por ejemplo refresco:0.5).");
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps) && fps > 0
            ? FpsCap.Fixed(fps)
            : throw new ArgumentException("--fps-cap acepta 'sin-tope', un número entero mayor que 0, o 'refresco[:factor]'.");
    }

    private static (ExportDestinationKind Kind, string? Folder)? ResolveDestination(ParsedArgs parsed, IConsolePrompt prompt)
    {
        // --dest es una opción de UN valor (ValueOptions), así que el parser ya la resolvió. No se usa SplitAt:
        // SplitAt es para opciones multi-valor como --versus y aquí confundiría el valor con posicionales.
        var raw = parsed.Value("dest");

        if (raw is null)
        {
            // Sin --dest se pregunta. La consola se inyecta, así que esto se puede testear.
            prompt.WriteLine("");
            prompt.WriteLine("¿Dónde quieres escribir el perfil?");
            prompt.WriteLine("  [1] Una carpeta que elija yo (por ejemplo el Escritorio). No toca Steam.");
            prompt.WriteLine("  [2] La carpeta cfg del juego, archivo NUEVO fragforge-cs2.cfg.");
            prompt.WriteLine("  [3] La carpeta cfg del juego, bloque FragForge dentro de autoexec.cfg (con copia de seguridad).");
            var answer = prompt.Ask("Elige 1, 2 o 3: ")?.Trim();
            return answer switch
            {
                "1" => PromptForFolder(prompt),
                "2" => (ExportDestinationKind.GameNewFile, null),
                "3" => (ExportDestinationKind.GameAutoexecBlock, null),
                _ => null
            };
        }

        if (raw.StartsWith("carpeta:", StringComparison.OrdinalIgnoreCase))
        {
            var folder = raw["carpeta:".Length..];
            return string.IsNullOrWhiteSpace(folder) ? null : (ExportDestinationKind.Folder, folder);
        }
        if (raw.Equals("juego-nuevo", StringComparison.OrdinalIgnoreCase)) return (ExportDestinationKind.GameNewFile, null);
        if (raw.Equals("juego-autoexec", StringComparison.OrdinalIgnoreCase)) return (ExportDestinationKind.GameAutoexecBlock, null);

        prompt.WriteWarning("--dest no reconocido. Usa carpeta:<ruta>, juego-nuevo o juego-autoexec.");
        return null;
    }

    private static (ExportDestinationKind Kind, string? Folder)? PromptForFolder(IConsolePrompt prompt)
    {
        var path = prompt.Ask("Ruta de la carpeta destino: ")?.Trim();
        return string.IsNullOrWhiteSpace(path) ? null : (ExportDestinationKind.Folder, path);
    }

    /// <summary>
    /// Raíz de Steam a partir del ejecutable: sube hasta el nivel que contiene <c>steamapps</c>.
    /// </summary>
    public static string? SteamRootFor(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath)) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(gameExecutablePath));
        while (directory is not null)
        {
            if (Path.GetFileName(directory).Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(directory);
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    // ------------------------------------------------------------------ import

    private static async Task<int> ImportAsync(IServiceProvider services, ParsedArgs parsed, IConsolePrompt prompt, CancellationToken cancellationToken)
    {
        var path = parsed.Positionals.Skip(1).FirstOrDefault();
        if (path is null)
        {
            prompt.WriteWarning("Falta el archivo del perfil: fragforge cs2 import <perfil.json>");
            return ExitCodes.Usage;
        }
        if (!File.Exists(path))
        {
            prompt.WriteWarning("No existe el archivo: " + path);
            return ExitCodes.Failure;
        }

        Cs2ProfilePreferences preferences;
        try
        {
            preferences = Cs2ProfileSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (Cs2ProfileImportException ex)
        {
            prompt.WriteWarning(ex.Message);
            return ExitCodes.Failure;
        }

        var catalog = Cs2OptionCatalog.LoadEmbedded();
        var generated = Cs2ProfileGenerator.Generate(preferences, catalog, DateTimeOffset.UtcNow);
        prompt.WriteLine("Perfil importado: " + path);
        foreach (var line in generated.CfgText.Split('\n')) prompt.WriteLine(line);
        if (generated.LaunchOptions.Length > 0) prompt.WriteLine("Opciones de lanzamiento: " + generated.LaunchOptions);
        foreach (var warning in generated.Warnings) prompt.WriteWarning("Aviso: " + warning);
        return ExitCodes.Success;
    }

    // ------------------------------------------------------------------ restore

    private static async Task<int> RestoreAutoexecAsync(IServiceProvider services, ParsedArgs parsed, IConsolePrompt prompt, CancellationToken cancellationToken)
    {
        var inspection = await services.GetRequiredService<Cs2Inspector>().InspectAsync(cancellationToken);
        var cfgDirectory = Cs2PathGuard.CfgDirectoryFor(inspection.ExecutablePath);
        if (cfgDirectory is null)
        {
            prompt.WriteWarning("No se localizó la instalación de CS2: no hay nada que restaurar.");
            return ExitCodes.Preflight;
        }
        if (!Directory.Exists(cfgDirectory))
        {
            prompt.WriteWarning("No existe la carpeta cfg del juego: " + cfgDirectory);
            return ExitCodes.Preflight;
        }

        // Último backup por nombre: la fecha va en el nombre y ordena lexicográficamente.
        var backups = Directory.GetFiles(cfgDirectory, Cs2ProfileGenerator.BackupPrefix + "*")
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (backups.Length == 0)
        {
            prompt.WriteWarning("No hay ninguna copia de seguridad de autoexec.cfg de FragForge en " + cfgDirectory);
            return ExitCodes.Failure;
        }

        var backup = backups[0];
        var target = Path.Combine(cfgDirectory, Cs2ProfileGenerator.AutoexecFileName);

        byte[] backupBytes;
        byte[] currentBytes;
        try
        {
            backupBytes = await File.ReadAllBytesAsync(backup, cancellationToken);
            currentBytes = File.Exists(target) ? await File.ReadAllBytesAsync(target, cancellationToken) : [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            prompt.WriteWarning($"No se pudo leer la copia o el autoexec actual: {ex.Message} No se ha modificado nada.");
            return ExitCodes.Failure;
        }

        // El backup se verifica antes de restaurar: si no se puede leer o está vacío, no se toca nada.
        if (backupBytes.Length == 0)
        {
            prompt.WriteWarning("La copia de seguridad está vacía: no se restaura nada. Archivo: " + backup);
            return ExitCodes.Failure;
        }

        if (!parsed.Has("yes") && prompt.Confirm($"¿Restaurar '{backup}' sobre autoexec.cfg? Se perderá el contenido actual.") != true)
        {
            prompt.WriteLine("No se ha restaurado nada.");
            return ExitCodes.Success;
        }

        try
        {
            await Core.Persistence.AtomicFile.WriteAllBytesAsync(target, backupBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var cause = ex is UnauthorizedAccessException
                ? "no hay permisos para escribir ahí (si el juego está abierto, ciérralo; si la carpeta es del sistema, ejecuta FragForge como administrador)"
                : "el sistema no pudo completar la escritura (puede que el archivo esté en uso o que falte espacio)";
            // No se puede afirmar que el autoexec siga igual: AtomicFile escribe un temporal y luego lo
            // mueve, y el fallo puede haber ocurrido en cualquiera de las dos etapas.
            prompt.WriteWarning($"No se pudo completar la restauración de '{target}': {cause}. Detalle: {ex.Message} " +
                                $"VERIFICA '{target}' antes de usarlo; la copia desde la que se restauró sigue en '{backup}'.");
            return ExitCodes.Failure;
        }

        prompt.WriteLine($"autoexec.cfg restaurado desde '{backup}'.");
        prompt.WriteLine($"SHA-256 del backup restaurado: {Cs2ProfileExporter.Sha256(backupBytes)}");
        prompt.WriteLine($"El autoexec que había antes ({currentBytes.Length} bytes) se ha sobrescrito; no se generó otra copia.");
        return ExitCodes.Success;
    }
}
