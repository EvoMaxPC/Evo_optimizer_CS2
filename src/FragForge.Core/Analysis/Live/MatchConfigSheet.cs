using System.Globalization;
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
    public bool LaunchOptionsKnownAbsent { get; init; }
    public int? ConfiguredRefreshHz { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public int? ConfiguredWidth { get; init; }
    public int? ConfiguredHeight { get; init; }
}

/// <summary>Lee las dimensiones configuradas, sin interpretar el modo de pantalla.</summary>
public static class ConfiguredResolutionReader
{
    // Observado en cs2_video.txt de la PC de desarrollo (4 cuentas); no es documentación de Valve.
    private const string WidthKey = "video.cfg.setting.defaultres";
    private const string HeightKey = "video.cfg.setting.defaultresheight";

    public static (int? Width, int? Height) TryRead(IReadOnlyDictionary<string, string> videoSettings)
    {
        var (width, height, _) = ReadPair(videoSettings);
        return (width, height);
    }

    public static (int? Width, int? Height, IReadOnlyList<string> Notes) Read(Cs2Inspection inspection)
    {
        if (inspection.ActiveSteamAccountId is not null)
        {
            var (width, height, error) = ReadPair(inspection.VideoSettings);
            return (width, height, error is null ? [] : [error]);
        }
        if (inspection.VideoSettingsByAccount.Count == 0)
            return (null, null, ["no se encontró cs2_video.txt"]);

        foreach (var account in inspection.VideoSettingsByAccount.OrderBy(x => x.Key, StringComparer.Ordinal))
            if (account.Value is null)
                return (null, null, [$"no se pudo leer la cuenta {account.Key}; no se sabe qué resolución usa la partida"]);

        int? commonWidth = null;
        int? commonHeight = null;
        var different = false;
        foreach (var account in inspection.VideoSettingsByAccount.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var (width, height, error) = ReadPair(account.Value!);
            if (error is not null) return (null, null, [$"cuenta {account.Key}: {error}"]);
            if (commonWidth is null) { commonWidth = width; commonHeight = height; }
            else if (commonWidth != width || commonHeight != height) different = true;
        }
        if (different) return (null, null, ["las cuentas de Steam tienen resoluciones distintas y no se sabe cuál usa la partida"]);
        return (commonWidth, commonHeight, [$"cuenta activa desconocida; las {inspection.VideoSettingsByAccount.Count.ToString(CultureInfo.InvariantCulture)} cuentas leídas coinciden"]);
    }

    private static (int? Width, int? Height, string? Error) ReadPair(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(WidthKey, out var widthText) || !int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width is <= 0 or > 16384)
            return (null, null, $"resolución configurada desconocida: falta o es inválida la clave {WidthKey}");
        if (!settings.TryGetValue(HeightKey, out var heightText) || !int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) || height is <= 0 or > 16384)
            return (null, null, $"resolución configurada desconocida: falta o es inválida la clave {HeightKey}");
        return (width, height, null);
    }
}

public static class MatchConfigSheetBuilder
{
    public static MatchConfigSheet From(Cs2Inspection inspection)
    {
        var (width, height, resolutionNotes) = ConfiguredResolutionReader.Read(inspection);
        return new MatchConfigSheet
        {
            ActiveSteamAccountId = inspection.ActiveSteamAccountId,
            VideoSettings = Sort(inspection.VideoSettings),
            StoredConvars = Sort(inspection.StoredConvars),
            AutoexecConvars = Sort(inspection.AutoexecConvars),
            Conflicts = inspection.Conflicts.ToArray(),
            LaunchOptions = inspection.LaunchOptions,
            LaunchOptionsKnownAbsent = inspection.LaunchOptionsKnownAbsent,
            ConfiguredRefreshHz = inspection.ConfiguredRefreshHz,
            Notes = inspection.Notes.Concat(resolutionNotes).ToArray(),
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
