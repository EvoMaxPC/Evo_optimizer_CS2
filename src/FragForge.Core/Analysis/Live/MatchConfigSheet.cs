using FragForge.Core.Models;

namespace FragForge.Core.Analysis.Live;

public sealed record MatchConfigSheet
{
    public string? ActiveSteamAccountId { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> VideoSettings { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> StoredConvars { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> AutoexecConvars { get; init; } = [];
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public string? LaunchOptions { get; init; }
    public int? ConfiguredRefreshHz { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public int? ConfiguredWidth { get; init; }
    public int? ConfiguredHeight { get; init; }
}

/// <summary>Sin fuente verificada de las claves; ver L4.</summary>
public static class ConfiguredResolutionReader
{
    public static (int? Width, int? Height) TryRead(IReadOnlyDictionary<string, string> videoSettings) => (null, null);
}

public static class MatchConfigSheetBuilder
{
    public static MatchConfigSheet From(Cs2Inspection inspection)
    {
        var (width, height) = ConfiguredResolutionReader.TryRead(inspection.VideoSettings);
        return new MatchConfigSheet
        {
            ActiveSteamAccountId = inspection.ActiveSteamAccountId,
            VideoSettings = Sort(inspection.VideoSettings),
            StoredConvars = Sort(inspection.StoredConvars),
            AutoexecConvars = Sort(inspection.AutoexecConvars),
            Conflicts = inspection.Conflicts.ToArray(),
            LaunchOptions = inspection.LaunchOptions,
            ConfiguredRefreshHz = inspection.ConfiguredRefreshHz,
            Notes = inspection.Notes.ToArray(),
            ConfiguredWidth = width,
            ConfiguredHeight = height,
        };
    }

    private static KeyValuePair<string, string>[] Sort(IReadOnlyDictionary<string, string> source) =>
        source.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
}

public static class MatchConfigDiff
{
    public static IReadOnlyList<string> Compare(MatchConfigSheet atStart, MatchConfigSheet atEnd)
    {
        var result = new List<string>();
        CompareSection("AutoexecConvars", atStart.AutoexecConvars, atEnd.AutoexecConvars, result);
        if (!string.Equals(atStart.LaunchOptions, atEnd.LaunchOptions, StringComparison.Ordinal))
            result.Add($"LaunchOptions: cambió de {atStart.LaunchOptions ?? "no se sabe"} a {atEnd.LaunchOptions ?? "no se sabe"}");
        CompareSection("StoredConvars", atStart.StoredConvars, atEnd.StoredConvars, result);
        CompareSection("VideoSettings", atStart.VideoSettings, atEnd.VideoSettings, result);
        return result;
    }

    private static void CompareSection(string section, IReadOnlyList<KeyValuePair<string, string>> before,
        IReadOnlyList<KeyValuePair<string, string>> after, List<string> output)
    {
        var a = before.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var b = after.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        foreach (var key in a.Keys.Union(b.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var had = a.TryGetValue(key, out var oldValue);
            var has = b.TryGetValue(key, out var newValue);
            if (!had) output.Add($"{section}: {key} agregada: {newValue}");
            else if (!has) output.Add($"{section}: {key} quitada: {oldValue}");
            else if (!string.Equals(oldValue, newValue, StringComparison.Ordinal)) output.Add($"{section}: {key} cambió de {oldValue} a {newValue}");
        }
    }
}
