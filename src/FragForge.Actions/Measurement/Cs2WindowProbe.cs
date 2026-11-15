using System.Diagnostics;
using System.Globalization;
using FragForge.Core.Engine;

namespace FragForge.Actions.Measurement;

public readonly record struct WindowRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed record NativeWindow(nint Handle, uint ProcessId, bool Visible, bool Minimized,
    WindowRectangle Window, int ClientWidth, int ClientHeight, nint Monitor, WindowRectangle MonitorBounds,
    bool DpiContextApplied);

public interface INativeWindowApi
{
    IReadOnlyList<NativeWindow> Enumerate();
}

public enum Cs2WindowStatus { Observed, NotRunning, NoWindow, MinimizedOrHidden, Unavailable }

public sealed record Cs2WindowObservation(Cs2WindowStatus Status, IReadOnlyList<int> ProcessIds,
    int? ClientWidth, int? ClientHeight, int? WindowWidth, int? WindowHeight,
    int? MonitorWidth, int? MonitorHeight, bool? WindowCoversMonitor, IReadOnlyList<string> Notes);

public static class Cs2WindowSelector
{
    public static (Cs2WindowStatus Status, NativeWindow? Window) Select(IReadOnlyList<NativeWindow> windows, IReadOnlyList<int> processIds)
    {
        if (processIds.Count == 0) return (Cs2WindowStatus.NotRunning, null);
        var candidates = windows.Where(w => processIds.Any(id => id >= 0 && (uint)id == w.ProcessId)).ToArray();
        if (candidates.Length == 0) return (Cs2WindowStatus.NoWindow, null);
        var selected = candidates.Where(w => w.Visible && !w.Minimized)
            .OrderByDescending(w => (long)w.ClientWidth * w.ClientHeight)
            .ThenByDescending(w => (long)w.Window.Width * w.Window.Height)
            .ThenBy(w => w.Handle.ToInt64()).FirstOrDefault();
        return selected is null ? (Cs2WindowStatus.MinimizedOrHidden, null) : (Cs2WindowStatus.Observed, selected);
    }
}

public sealed class Cs2WindowProbe
{
    private readonly INativeWindowApi _api;
    private readonly Func<IReadOnlyList<int>> _getProcessIds;

    public Cs2WindowProbe(INativeWindowApi api) : this(api, GetProcessIds) { }
    internal Cs2WindowProbe(INativeWindowApi api, Func<IReadOnlyList<int>> getProcessIds)
    {
        _api = api;
        _getProcessIds = getProcessIds;
    }

    public Cs2WindowObservation Observe()
    {
        IReadOnlyList<int> ids = [];
        try
        {
            ids = _getProcessIds();
            var (status, window) = Cs2WindowSelector.Select(ids.Count == 0 ? [] : _api.Enumerate(), ids);
            if (window is null)
            {
                var note = status switch
                {
                    Cs2WindowStatus.NotRunning => "cs2 no está corriendo",
                    Cs2WindowStatus.NoWindow => "no se encontró ventana de cs2",
                    _ => "ventana no disponible (minimizada u oculta)"
                };
                return new(status, ids, null, null, null, null, null, null, null, [note]);
            }
            var notes = new List<string>();
            if (!window.DpiContextApplied) notes.Add("las medidas pueden estar escaladas por DPI (no se pudo fijar el contexto)");
            return new(status, ids, window.ClientWidth, window.ClientHeight, window.Window.Width, window.Window.Height,
                window.MonitorBounds.Width, window.MonitorBounds.Height, window.Window == window.MonitorBounds, notes);
        }
        catch (Exception ex)
        {
            return new(Cs2WindowStatus.Unavailable, ids, null, null, null, null, null, null, null,
                ["ventana no disponible: " + ex.Message]);
        }
    }

    private static IReadOnlyList<int> GetProcessIds()
    {
        var processes = Process.GetProcessesByName(Cs2Process.ProcessName);
        try { return processes.Select(p => p.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

public enum ResolutionComparisonKind { Same, Different, Unknown }
public sealed record ResolutionComparisonResult(ResolutionComparisonKind Kind, string Text);

/// <summary>La resolución observada es el área cliente; no prueba el modo de pantalla ni la resolución interna de render.</summary>
public static class ResolutionComparison
{
    public static ResolutionComparisonResult Compare(int? configuredWidth, int? configuredHeight, int? observedWidth, int? observedHeight)
    {
        if (configuredWidth is null || configuredHeight is null || observedWidth is null || observedHeight is null)
            return new(ResolutionComparisonKind.Unknown, "no se sabe");
        var text = string.Create(CultureInfo.InvariantCulture,
            $"configurada {configuredWidth}x{configuredHeight}, observada {observedWidth}x{observedHeight} (área cliente; no prueba el modo de pantalla ni la resolución interna de render)");
        return new(configuredWidth == observedWidth && configuredHeight == observedHeight ? ResolutionComparisonKind.Same : ResolutionComparisonKind.Different, text);
    }
}
