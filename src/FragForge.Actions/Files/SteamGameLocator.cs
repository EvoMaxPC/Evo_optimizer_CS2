using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Files;

public sealed class SteamGameLocator(IRegistry registry) : IGameLocator
{
    private const string RelativeExecutable = @"steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe";
    public async Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default)
    {
        var steamRoots = new List<string>();
        foreach (var key in new[] { @"HKCU\Software\Valve\Steam", @"HKLM\SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            foreach (var name in new[] { "SteamPath", "InstallPath", "SteamExe" })
            {
                var value = await registry.ReadValueAsync(key, name, cancellationToken);
                if (value.ValueExists && value.Data is string path)
                {
                    var expanded = Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));
                    if (name == "SteamExe") expanded = Path.GetDirectoryName(expanded) ?? expanded;
                    if (Directory.Exists(expanded)) steamRoots.Add(Path.GetFullPath(expanded));
                }
            }
        }
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(defaultRoot)) steamRoots.Add(defaultRoot);
        var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            VdfNode node;
            try { node = ValveDataFormat.Parse(await File.ReadAllTextAsync(libraryFile, cancellationToken)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }
            if (!node.Children.TryGetValue("libraryfolders", out var folders)) continue;
            foreach (var folder in folders.Children.Values)
            {
                if (folder.Children.TryGetValue("path", out var pathNode) && pathNode.Value is not null)
                {
                    var path = Path.GetFullPath(pathNode.Value.Replace(@"\\", "\\").Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(path)) libraries.Add(path);
                }
            }
        }
        var candidates = libraries.Select(l => Path.Combine(l, RelativeExecutable)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new GameInfo { Cs2ExecutablePath = candidates.Length == 1 ? candidates[0] : null, Cs2DetectionSource = candidates.Length == 1 ? "Steam libraryfolders.vdf" : candidates.Length > 1 ? "Ambiguous; manual selection required" : "Not found", Cs2Candidates = candidates, Cs2Ambiguous = candidates.Length > 1 };
    }
}
