using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Files;

/// <summary>
/// Resuelve cs2.exe por la cadena declarativa de Steam (Capa 8.2):
///
/// <list type="number">
/// <item><c>HKCU\Software\Valve\Steam</c> → <c>SteamPath</c>. (También <c>HKLM\SOFTWARE\WOW6432Node\Valve\Steam</c>
///       → <c>InstallPath</c>, y la carpeta por omisión de Program Files (x86).)</item>
/// <item><c>&lt;Steam&gt;\steamapps\libraryfolders.vdf</c> → todas las bibliotecas (<c>path</c>).</item>
/// <item>En cada biblioteca, <c>steamapps\appmanifest_730.acf</c> → <c>installdir</c>.</item>
/// <item><c>&lt;biblioteca&gt;\steamapps\common\&lt;installdir&gt;\game\bin\win64\cs2.exe</c>.</item>
/// </list>
///
/// Si algo falta, devuelve <c>Null</c> con el motivo escrito: nunca una ruta adivinada. Si hay más de
/// una candidata válida, devuelve <c>null</c> y la lista en <see cref="Cs2Resolution.Candidates"/>.
///
/// No usa <see cref="IGameLocator"/> (que exige la carpeta fija <c>Counter-Strike Global Offensive</c>):
/// aquí la carpeta sale del <c>installdir</c> del manifiesto, así que funciona con instalaciones
/// renombradas o movidas.
/// </summary>
public sealed class SteamCs2Resolver(IRegistry registry, string? defaultSteamRoot = null) : ICs2Resolver
{
    /// <summary>App id de CS2 en Steam.</summary>
    public const string AppId = "730";

    private const string RegistryUserPath = @"HKCU\Software\Valve\Steam";
    private const string RegistryMachinePath = @"HKLM\SOFTWARE\WOW6432Node\Valve\Steam";
    private const string RelativeExecutable = @"game\bin\win64\cs2.exe";

    public async Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var probed = new List<string>();
        var roots = await FindSteamRootsAsync(probed, cancellationToken);
        if (roots.Count == 0)
            return new Cs2Resolution { Source = "No se encontró la carpeta de Steam (registro ni ubicación por omisión).", ProbedPaths = probed };

        var libraries = new List<string>(roots);
        foreach (var root in roots)
        {
            var manifest = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            probed.Add(manifest);
            if (!File.Exists(manifest)) continue;
            IReadOnlyList<string> paths;
            try { paths = ParseLibraryFolders(await File.ReadAllTextAsync(manifest, cancellationToken)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in paths)
            {
                if (Directory.Exists(path) && !libraries.Contains(path, StringComparer.OrdinalIgnoreCase)) libraries.Add(path);
            }
        }

        var candidates = new List<string>();
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var acf = Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
            probed.Add(acf);
            if (!File.Exists(acf)) continue;
            string? installDir;
            try { installDir = ParseInstallDir(await File.ReadAllTextAsync(acf, cancellationToken)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }
            if (string.IsNullOrWhiteSpace(installDir)) continue;

            var executable = Path.Combine(library, "steamapps", "common", installDir, RelativeExecutable);
            probed.Add(executable);
            if (File.Exists(executable) && !candidates.Contains(executable, StringComparer.OrdinalIgnoreCase)) candidates.Add(executable);
        }

        if (candidates.Count == 1)
            return new Cs2Resolution { ExecutablePath = candidates[0], Source = "Steam: appmanifest_730.acf (installdir)", ProbedPaths = probed, Candidates = candidates };

        return new Cs2Resolution
        {
            ExecutablePath = null,
            Source = candidates.Count > 1
                ? $"Hay {candidates.Count} instalaciones de CS2; selecciona una manualmente."
                : "No se encontró appmanifest_730.acf con un cs2.exe existente en ninguna biblioteca de Steam.",
            ProbedPaths = probed,
            Candidates = candidates
        };
    }

    private async Task<List<string>> FindSteamRootsAsync(List<string> probed, CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        foreach (var (key, name) in new[] { (RegistryUserPath, "SteamPath"), (RegistryMachinePath, "InstallPath") })
        {
            probed.Add($"{key}\\{name}");
            try
            {
                var value = await registry.ReadValueAsync(key, name, cancellationToken);
                // Solo se acepta el valor si existe y es texto; un tipo inesperado no se convierte a ciegas.
                if (!value.ValueExists || value.Data is not string raw || string.IsNullOrWhiteSpace(raw)) continue;
                var path = NormalizeDirectory(raw);
                if (path is not null && Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase)) roots.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* se prueba la siguiente fuente */ }
        }

        // Raiz por omision. Se puede FIJAR desde fuera (defaultSteamRoot) para que un test no dependa de si
        // esta maquina tiene Steam instalado de verdad: sin poder fijarla, un equipo real con Steam rompe
        // cualquier prueba que simule "sin Steam" o "una sola instalacion". En produccion va null y se usa
        // ProgramFilesX86, que es el comportamiento de siempre.
        var fallback = defaultSteamRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        probed.Add(fallback);
        if (Directory.Exists(fallback) && !roots.Contains(Path.GetFullPath(fallback), StringComparer.OrdinalIgnoreCase))
            roots.Add(Path.GetFullPath(fallback));
        return roots;
    }

    /// <summary>
    /// Normaliza una ruta de VDF/registro: barras de Windows, barras dobles y barras finales.
    ///
    /// El VDF escapa la barra invertida (<c>C:\\Program Files (x86)\\Steam</c>), y el parser de
    /// <c>ValveDataFormat</c> ya la desescapa, pero el registro de Steam a veces trae la ruta con barras
    /// normales o con barras dobles. Aquí se acepta cualquiera de las dos y se colapsan las repeticiones.
    /// </summary>
    internal static string? NormalizeDirectory(string raw)
    {
        var value = raw.Trim().Trim('"');
        if (value.Length == 0) return null;
        value = value.Replace('/', Path.DirectorySeparatorChar);
        // Colapsa barras repetidas sin tocar el prefijo UNC (\\servidor\recurso).
        var unc = value.StartsWith(@"\\", StringComparison.Ordinal);
        while (value.Contains(@"\\", StringComparison.Ordinal)) value = value.Replace(@"\\", @"\", StringComparison.Ordinal);
        if (unc) value = @"\" + value;
        // Barras FINALES: el doc de este metodo las anuncia ("barras dobles y barras finales") y el test lo
        // exige, pero Path.GetFullPath NO las quita: solo resuelve "." y "..". Se quitan aqui, conservando la
        // raiz ("C:\") y la raiz UNC ("\\srv\share"), que sin su barra dejarian de serlo.
        while (value.Length > 3 && value.EndsWith(@"\", StringComparison.Ordinal) && !value.EndsWith(@":\", StringComparison.Ordinal))
            value = value[..^1];
        return Path.GetFullPath(value);
    }

    /// <summary>
    /// <c>libraryfolders.vdf</c> → rutas de las bibliotecas, en el orden del archivo.
    ///
    /// Se usa el parser existente (<see cref="ValveDataFormat"/>): clave/valor entre comillas y llaves
    /// anidadas. <c>libraryfolders</c> → <c>&lt;índice&gt;</c> → <c>path</c>. El índice puede ser
    /// <c>"0"</c>, <c>"1"</c>… o cualquier clave: se itera a los hijos, no se busca un número fijo.
    /// </summary>
    internal static IReadOnlyList<string> ParseLibraryFolders(string vdf)
    {
        var node = ValveDataFormat.Parse(vdf);
        if (!node.Children.TryGetValue("libraryfolders", out var folders)) throw new InvalidDataException("VDF sin 'libraryfolders'.");
        var paths = new List<string>();
        foreach (var folder in folders.Children.Values)
        {
            if (folder.Value is not null) continue;
            if (!folder.Children.TryGetValue("path", out var pathNode) || pathNode.Value is not { } value) continue;
            var normalized = NormalizeDirectory(value);
            if (normalized is not null) paths.Add(normalized);
        }
        return paths;
    }

    /// <summary>
    /// <c>appmanifest_730.acf</c> → <c>AppState\installdir</c>.
    ///
    /// No se asume un nombre de carpeta: sale del manifiesto. Se valida que sea un NOMBRE de carpeta
    /// (sin separadores ni <c>..</c>) para que un manifiesto manipulado no pueda escapar de <c>common</c>.
    /// </summary>
    internal static string? ParseInstallDir(string acf)
    {
        var node = ValveDataFormat.Parse(acf);
        if (!node.Children.TryGetValue("AppState", out var state)) throw new InvalidDataException("ACF sin 'AppState'.");
        if (!state.Children.TryGetValue("installdir", out var dir) || dir.Value is not { } installDir) return null;
        var trimmed = installDir.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.Contains("..", StringComparison.Ordinal) || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"'installdir' no es un nombre de carpeta válido: '{installDir}'.");
        return trimmed;
    }
}
