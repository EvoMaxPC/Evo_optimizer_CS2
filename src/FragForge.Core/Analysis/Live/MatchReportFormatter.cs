using System.Globalization;

namespace FragForge.Core.Analysis.Live;

public enum MatchReportKind { Partial, Final }

public sealed record MatchEnvironment
{
    public string WindowStatusText { get; init; } = "no se sabe";
    public int? ClientWidth { get; init; }
    public int? ClientHeight { get; init; }
    public int? MonitorWidth { get; init; }
    public int? MonitorHeight { get; init; }
    public bool? WindowCoversMonitor { get; init; }
    public string ResolutionComparisonText { get; init; } = "no se sabe";
    public IReadOnlyList<string> DisplayModes { get; init; } = [];
    public IReadOnlyList<string> WindowNotes { get; init; } = [];
    public bool? IsElevated { get; init; }
}

public sealed record MatchReportInput
{
    public MatchReportKind Kind { get; init; }
    public string Mode { get; init; } = "";
    public string SessionStateText { get; init; } = "";
    public string? FailureMessage { get; init; }
    public double? SecondsSinceLastFrame { get; init; }
    public LiveViewSnapshot? Total { get; init; }
    public LiveViewSnapshot? Last30 { get; init; }
    public LiveViewSnapshot? Segment { get; init; }
    public LiveIngestCounters? Counters { get; init; }
    public string? StderrTail { get; init; }
    public MatchConfigSheet? Sheet { get; init; }
    public MatchConfigSheet? SheetAtEnd { get; init; }
    public MatchEnvironment? Environment { get; init; }
    public IReadOnlyList<string> HotkeyNotes { get; init; } = [];
    public DateTimeOffset GeneratedAtLocal { get; init; }
}

public static class MatchReportFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(MatchReportInput input)
    {
        var lines = new List<string>
        {
            input.Kind == MatchReportKind.Partial ? "VISTA PARCIAL: la medición sigue" : "RESUMEN FINAL",
            "Fecha: " + input.GeneratedAtLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv),
            "Modo: " + input.Mode,
            "Estado: " + input.SessionStateText,
        };
        if (input.FailureMessage is not null) lines.Add("Fallo: " + input.FailureMessage);
        if (input.SecondsSinceLastFrame is double elapsed && elapsed > 0) lines.Add("Sin frames hace " + elapsed.ToString("0.##", Inv) + " s");
        View(lines, "Total", input.Total);
        View(lines, "Últimos 30 s", input.Last30);
        View(lines, "Segmento", input.Segment);
        if (input.Counters is { } c)
        {
            lines.Add("Contadores del parser:");
            lines.Add(string.Create(Inv, $"Filas parseadas: {c.FramesParsed} · líneas malformadas: {c.MalformedLines} · NA: {c.NaFrames} · filas sin tiempo: {c.RowsWithoutTime} · líneas demasiado largas: {c.OverlongLines} · líneas parciales descartadas: {c.DiscardedPartialLines} · encabezado visto: {YesNo(c.HeaderSeen)}"));
        }
        if (input.StderrTail is not null) lines.Add("stderr: " + input.StderrTail);
        if (input.Environment is { } env)
        {
            lines.Add("Entorno:");
            lines.Add("Ventana: " + env.WindowStatusText);
            lines.Add("Área cliente: " + Dimensions(env.ClientWidth, env.ClientHeight));
            lines.Add("Monitor: " + Dimensions(env.MonitorWidth, env.MonitorHeight));
            lines.Add("La ventana cubre el monitor: " + YesNo(env.WindowCoversMonitor));
            lines.Add("Comparación de resolución: " + env.ResolutionComparisonText);
            foreach (var mode in env.DisplayModes) lines.Add("Pantallas activas: " + mode);
            foreach (var note in env.WindowNotes) lines.Add(note);
            lines.Add("Administrador: " + YesNo(env.IsElevated));
        }
        if (input.Sheet is { } sheet)
        {
            lines.Add("Hoja de configuración:");
            lines.Add("Cuenta Steam activa: " + (sheet.ActiveSteamAccountId ?? "no se sabe"));
            lines.Add("Resolución configurada: " + Dimensions(sheet.ConfiguredWidth, sheet.ConfiguredHeight));
            lines.Add("Frecuencia configurada: " + (sheet.ConfiguredRefreshHz is int hz ? hz.ToString(Inv) + " Hz" : "no se sabe"));
            Pairs(lines, "VideoSettings", sheet.VideoSettings);
            Pairs(lines, "Convars guardados por el juego (pueden ser pisados por autoexec.cfg y las opciones de lanzamiento)", sheet.StoredConvars);
            Pairs(lines, "AutoexecConvars", sheet.AutoexecConvars);
            foreach (var conflict in sheet.Conflicts) lines.Add("Conflicto: " + conflict);
            lines.Add("Opciones de lanzamiento: " + (sheet.LaunchOptions is not null ? sheet.LaunchOptions : sheet.LaunchOptionsKnownAbsent ? "sin opciones configuradas" : "no se sabe"));
            foreach (var note in sheet.Notes) lines.Add(note);
            if (input.SheetAtEnd is { } end)
            {
                var differences = MatchConfigDiff.Compare(sheet, end);
                if (differences.Count > 0) { lines.Add("LA CONFIGURACIÓN CAMBIÓ DURANTE LA MEDICIÓN"); lines.AddRange(differences); }
            }
        }
        lines.AddRange(input.HotkeyNotes);
        return string.Join("\n", lines);
    }

    private static void View(List<string> lines, string label, LiveViewSnapshot? view)
    {
        if (view is null) return;
        lines.Add(label + ":");
        if (label == "Total") lines.Add("El total incluye los frames acumulados de la serie mostrada desde el inicio de la medición (puede incluir menús y carga de mapa); para jugar mirá el segmento o los últimos 30 s.");
        lines.Add("Frames: " + view.FrameCount.ToString(Inv) + " · duración: " + Number(view.SpanSeconds, "0.00") + " s");
        var stats = view.Statistics;
        lines.Add("FPS promedio: " + Fps(stats?.MeanMs));
        lines.Add("FPS mínimo: " + Fps(stats?.MaxMs));
        lines.Add("FPS máximo: " + Fps(stats?.MinMs) + " (el máximo puede estar inflado por un solo frame corto)");
        lines.Add("1% bajo: " + Metric(stats?.Low1PercentAverageFps, stats, nameof(FrameStatistics.Low1PercentAverageFps), " FPS", "0.0"));
        lines.Add("P99 frametime: " + Metric(stats?.P99FrameTimeMs, stats, nameof(FrameStatistics.P99FrameTimeMs), " ms", "0.00"));
        lines.Add("0.1% bajo: " + Metric(stats?.Low01PercentAverageFps, stats, nameof(FrameStatistics.Low01PercentAverageFps), " FPS", "0.0"));
        if (view.Hitches is { } h)
        {
            lines.Add("Tirones: " + h.HitchCount.ToString(Inv));
            lines.Add("Pausas: " + h.PauseCount.ToString(Inv));
            lines.Add("Frame más largo: " + Number(h.LongestFrameMs, "0.00") + " ms");
            lines.AddRange(h.Notes);
        }
        else lines.Add("tirones: no disponible");
        foreach (var note in view.Notes) lines.Add(FormatSeriesNote(note));
    }

    private static string FormatSeriesNote(string note)
    {
        const string prefix = "hay ";
        const string suffix = " series más";
        if (note.Length < prefix.Length + suffix.Length || !note.StartsWith(prefix, StringComparison.Ordinal) || !note.EndsWith(suffix, StringComparison.Ordinal)) return note;
        var number = note.AsSpan(prefix.Length, note.Length - prefix.Length - suffix.Length);
        if (!int.TryParse(number, NumberStyles.None, Inv, out var count) || count < 1 || !number.SequenceEqual(count.ToString(Inv).AsSpan())) return note;
        return "hay " + count.ToString(Inv) + (count == 1 ? " serie más" : " series más") + "; esta vista muestra solo la serie con más frames acumulados";
    }

    private static string Metric(double? value, FrameStatistics? stats, string key, string unit, string format)
    {
        if (value is double v && double.IsFinite(v)) return v.ToString(format, Inv) + unit;
        if (stats is not null && stats.UnavailableReasons.TryGetValue(key, out var reason)) return reason;
        return "no se sabe";
    }
    private static string Fps(double? ms) => ms is double v && double.IsFinite(v) && v > 0 ? (1000.0 / v).ToString("0.0", Inv) + " FPS" : "no se sabe";
    private static string Number(double? value, string format) => value is double v && double.IsFinite(v) ? v.ToString(format, Inv) : "no se sabe";
    private static string Dimensions(int? width, int? height) => width is int w && height is int h ? string.Create(Inv, $"{w}x{h}") : "no se sabe";
    private static string YesNo(bool? value) => value is true ? "sí" : value is false ? "no" : "no se sabe";
    private static void Pairs(List<string> lines, string section, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        lines.Add(section + ":");
        foreach (var pair in pairs) lines.Add(pair.Key + ": " + pair.Value);
    }
}
