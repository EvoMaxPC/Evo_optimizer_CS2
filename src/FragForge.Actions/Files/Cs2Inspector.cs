using System.Globalization;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Files;

/// <summary>
/// Inspector de CS2 de SOLO LECTURA (Capa 8.6).
///
/// Lee, en este orden:
/// <list type="number">
/// <item>cs2.exe con <see cref="ICs2Resolver"/> (Steam → appmanifest).</item>
/// <item>La cuenta activa con <c>HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser</c>.</item>
/// <item>La carpeta <c>userdata\&lt;cuenta&gt;</c> correspondiente.</item>
/// <item>De esa cuenta: <c>730\local\cfg\cs2_video.txt</c>, los dos archivos locales de convars y el
///       <c>autoexec.cfg</c> (este último en la carpeta del juego o junto a los cfg).</item>
/// <item>Las opciones de lanzamiento del <c>localconfig.vdf</c>, SOLO para mostrarlas.</item>
/// </list>
///
/// <para>
/// Nada de esto escribe. No se abre ningún archivo con <see cref="FileMode.Open"/> en modo escritura, no se
/// lanza Steam y no se toca <c>localconfig.vdf</c>. Si el usuario quiere cambiar algo, el CLI le muestra el
/// texto recomendado y lo cambia él.
/// </para>
///
/// <para>
/// Un archivo que no se puede leer NO hace fallar la inspección: se anota en <c>Notes</c> con el motivo y
/// se devuelve el resto. Es lo contrario de adivinar (R-L).
/// </para>
/// </summary>
public sealed class Cs2Inspector(ICs2Resolver resolver, IRegistry registry)
{
    private const string SteamUserKey = @"HKCU\Software\Valve\Steam\ActiveProcess";

    /// <summary>Opciones recomendadas: se MUESTRAN, no se escriben en Steam.</summary>
    public const string RecommendedLaunchOptions = "-novid -nojoy -console";

    public async Task<Cs2Inspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var resolution = await resolver.ResolveAsync(cancellationToken);
        if (!resolution.Found)
        {
            notes.Add(resolution.Source);
            return new Cs2Inspection { Notes = notes, RecommendedLaunchOptions = RecommendedLaunchOptions };
        }

        var executable = resolution.ExecutablePath;
        var libraryRoot = FindLibraryRoot(executable);
        // userdata está en la raíz de Steam. Si CS2 está en una biblioteca secundaria, la raíz es otra:
        // se prefiere la que tenga userdata y, si no, la de la biblioteca.
        var steamRoot = FindSteamRootWithUserData(libraryRoot, resolution);
        if (steamRoot is null) notes.Add("No se pudo localizar la carpeta de Steam con userdata.");

        var inspection = new Cs2Inspection { ExecutablePath = executable, Notes = notes, RecommendedLaunchOptions = RecommendedLaunchOptions };

        // --- Cuenta activa y su userdata ---
        var userDataRoots = steamRoot is null ? [] : ListUserData(steamRoot);
        var accountId = await ReadActiveAccountAsync(notes, cancellationToken);
        string? activeUserData = null;
        if (accountId is not null && steamRoot is not null)
        {
            // Steam nombra las carpetas de userdata con el id de cuenta (SteamID3) de 32 bits, que es
            // exactamente lo que guarda ActiveUser. Se compara como número, no como texto.
            var candidate = Path.Combine(steamRoot, "userdata", accountId.Value.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(candidate)) activeUserData = candidate;
            else notes.Add($"La cuenta activa es {accountId.Value}, pero no existe su carpeta userdata.");
        }
        else if (accountId is null) notes.Add("No se pudo leer la cuenta de Steam activa (ActiveProcess\\ActiveUser).");

        inspection = inspection with
        {
            ActiveSteamAccountId = accountId?.ToString(CultureInfo.InvariantCulture),
            ActiveUserDataDirectory = activeUserData,
            UserDataDirectories = userDataRoots
        };

        if (accountId is null)
        {
            var accounts = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.Ordinal);
            foreach (var directory in userDataRoots.OrderBy(path => path, StringComparer.Ordinal))
            {
                var path = Path.Combine(directory, "730", "local", "cfg", "cs2_video.txt");
                var id = Path.GetFileName(directory);
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) continue;
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    accounts[id] = null;
                    notes.Add($"Cuenta {id}: no se pudo comprobar cs2_video.txt: {ex.Message}");
                    continue;
                }
                var accountNotes = new List<string>();
                var text = await ReadTextAsync(path, accountNotes, cancellationToken);
                IReadOnlyDictionary<string, string>? settings = null;
                if (text is not null)
                {
                    var parsed = Safe(() => Cs2ConfigParser.ParseVideoSettings(text), accountNotes, "cs2_video.txt");
                    if (accountNotes.Count == 0) settings = parsed;
                }
                if (settings is null)
                {
                    if (accountNotes.Count == 0) accountNotes.Add("no se pudo leer o verificar el archivo");
                    foreach (var note in accountNotes) notes.Add($"Cuenta {id}: {note}");
                }
                accounts[id] = settings;
            }
            return inspection with { VideoSettingsByAccount = accounts };
        }

        if (activeUserData is null) return inspection;

        // --- Configuración de la cuenta activa ---
        var cfg = Path.Combine(activeUserData, "730", "local", "cfg");
        var video = await ReadTextAsync(Path.Combine(cfg, "cs2_video.txt"), notes, cancellationToken);
        var saved = await StoredConvarReader.ReadAsync(cfg, notes, cancellationToken);
        var autoexec = await ReadFirstOfAsync(notes, cancellationToken,
            Path.Combine(cfg, "autoexec.cfg"),
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable))) ?? cfg, "csgo", "cfg", "autoexec.cfg"));

        var videoSettings = video is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : Safe(() => Cs2ConfigParser.ParseVideoSettings(video), notes, "cs2_video.txt");
        var stored = saved.Values;
        var autoexecValues = autoexec is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : Safe(() => Cs2ConfigParser.ParseAutoexec(autoexec), notes, "autoexec.cfg");

        // --- Opciones de lanzamiento (solo lectura) ---
        var launch = await ReadLaunchOptionsAsync(steamRoot, accountId, notes, cancellationToken);

        return inspection with
        {
            VideoSettings = videoSettings,
            StoredConvars = stored,
            StoredConvarsKnown = saved.Known,
            AutoexecConvars = autoexecValues,
            Conflicts = autoexec is null ? [] : Cs2ConfigParser.FindConvarConflicts(stored, autoexecValues),
            LaunchOptions = launch.Value,
            LaunchOptionsKnownAbsent = launch.Success && !launch.Found,
            ConfiguredRefreshHz = Cs2ConfigParser.ParseRefreshHz(videoSettings)
        };
    }

    // ------------------------------------------------------------------ piezas

    /// <summary>
    /// <c>...\steamapps\common\&lt;installdir&gt;\game\bin\win64\cs2.exe</c> → la carpeta que contiene
    /// <c>steamapps</c> (la raíz de la biblioteca donde está instalado CS2).
    ///
    /// OJO: esa NO es necesariamente la raíz de Steam, y <c>userdata</c> vive en la raíz de Steam. Para el
    /// inspector se necesita la raíz (la que tiene <c>userdata</c>), así que se devuelven las dos cosas por
    /// separado y el llamador usa la raíz para cuentas y la biblioteca para el ejecutable.
    /// </summary>
    private static string? FindLibraryRoot(string? executable)
    {
        if (executable is null) return null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
        while (directory is not null)
        {
            if (directory.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return directory.Parent?.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>
    /// La carpeta de Steam que TIENE <c>userdata</c> (cuentas). Se prueban las rutas que el resolver ya
    /// sondeó y, como último recurso, la de la biblioteca del ejecutable. Si ninguna la tiene, se devuelve
    /// la biblioteca para no perder el resto de la inspección; el llamador lo anota.
    /// </summary>
    private static string? FindSteamRootWithUserData(string? libraryRoot, Cs2Resolution resolution)
    {
        var candidates = new List<string>();
        foreach (var probed in resolution.ProbedPaths)
        {
            // ProbedPaths incluye "…\steamapps\libraryfolders.vdf": su raíz es la de Steam.
            if (probed.EndsWith("libraryfolders.vdf", StringComparison.OrdinalIgnoreCase) && Directory.GetParent(probed)?.Parent is { } parent)
                candidates.Add(parent.FullName);
        }
        if (libraryRoot is not null) candidates.Add(libraryRoot);
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(Path.Combine(candidate, "userdata"))) return candidate;
        }
        return candidates.FirstOrDefault();
    }

    private static IReadOnlyList<string> ListUserData(string steamRoot)
    {
        var root = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(root)) return [];
        try { return Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private async Task<uint?> ReadActiveAccountAsync(List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            var value = await registry.ReadValueAsync(SteamUserKey, "ActiveUser", cancellationToken);
            if (!value.ValueExists || value.Data is null) return null;
            // El registro puede devolver int (DWord con signo) o uint; se aceptan ambos y se normaliza a uint.
            return value.Data switch
            {
                int signed => unchecked((uint)signed),
                uint unsigned => unsigned,
                long wide when wide is >= 0 and <= uint.MaxValue => (uint)wide,
                string text when uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"No se pudo leer {SteamUserKey}\\ActiveUser: {ex.Message}");
            return null;
        }
    }

    private static async Task<LocalConfigLaunchOptionsReader.Result> ReadLaunchOptionsAsync(string? steamRoot, uint? accountId, List<string> notes, CancellationToken cancellationToken)
    {
        if (steamRoot is null || accountId is null)
        {
            notes.Add("No se pudo determinar la cuenta de Steam para leer localconfig.vdf.");
            return new(false, false, null, null);
        }
        var path = Path.Combine(steamRoot, "userdata", accountId.Value.ToString(CultureInfo.InvariantCulture), "config", "localconfig.vdf");
        var result = await LocalConfigLaunchOptionsReader.ReadAsync(path, cancellationToken);
        if (result.Error is not null) notes.Add(result.Error);
        return result;
    }

    private static async Task<string?> ReadTextAsync(string path, List<string> notes, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try { return await File.ReadAllTextAsync(path, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"No se pudo leer '{path}': {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> ReadFirstOfAsync(List<string> notes, CancellationToken cancellationToken, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var text = await ReadTextAsync(candidate, notes, cancellationToken);
            if (text is not null) return text;
        }
        return null;
    }

    private static Dictionary<string, string> Safe(Func<IReadOnlyDictionary<string, string>> parse, List<string> notes, string name)
    {
        try { return new Dictionary<string, string>(parse(), StringComparer.OrdinalIgnoreCase); }
        catch (InvalidDataException ex)
        {
            notes.Add($"'{name}' no se pudo interpretar: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

}
