using FragForge.Actions.Measurement;
using Xunit;

namespace FragForge.Tests.Measurement.Live;

public sealed class Cs2WindowProbeTests
{
    private static NativeWindow Window(int handle, uint pid, int width, int height, WindowRectangle rect, bool visible = true, bool minimized = false, bool dpi = true) =>
        new((nint)handle, pid, visible, minimized, rect, width, height, (nint)1, new WindowRectangle(0, 0, 1920, 1080), dpi);

    private sealed class FakeApi(IReadOnlyList<NativeWindow> windows, bool fail = false) : INativeWindowApi
    {
        public IReadOnlyList<NativeWindow> Enumerate() => fail ? throw new InvalidOperationException("fallo") : windows;
    }

    [Fact]
    public void Selector_UsesClientAreaThenWindowAreaThenHandle()
    {
        var windows = new[] {
            Window(3, 10, 100, 100, new(0, 0, 150, 150)),
            Window(2, 10, 100, 100, new(0, 0, 200, 200)),
            Window(1, 10, 100, 100, new(0, 0, 200, 200)),
            Window(4, 11, 500, 500, new(0, 0, 500, 500)) };
        // Tres áreas cliente de 10000; área ventana 40000 gana a 22500; handle 1 gana a 2.
        var selected = Cs2WindowSelector.Select(windows, [10]);
        Assert.Equal(Cs2WindowStatus.Observed, selected.Status);
        Assert.Equal((nint)1, selected.Window!.Handle);
    }

    [Fact]
    public void Selector_ReportsAbsentHiddenAndMinimized()
    {
        var windows = new[] { Window(1, 10, 10, 10, new(0, 0, 10, 10), minimized: true),
            Window(2, 10, 10, 10, new(0, 0, 10, 10), visible: false) };
        // Sin PID: no corre; PID 11: ninguna ventana; PID 10: dos ventanas no disponibles.
        Assert.Equal(Cs2WindowStatus.NotRunning, Cs2WindowSelector.Select(windows, []).Status);
        Assert.Equal(Cs2WindowStatus.NoWindow, Cs2WindowSelector.Select(windows, [11]).Status);
        Assert.Equal(Cs2WindowStatus.MinimizedOrHidden, Cs2WindowSelector.Select(windows, [10]).Status);
    }

    [Fact]
    public void Probe_ReportsObservedDimensionsAndDpiNote()
    {
        var window = Window(1, 10, 1024, 768, new(0, 0, 1920, 1080), dpi: false);
        var probe = new Cs2WindowProbe(new FakeApi([window]), () => [10]);
        var observed = probe.Observe();
        // Cliente 1024x768; ventana y monitor comparten límites 1920x1080.
        Assert.Equal(Cs2WindowStatus.Observed, observed.Status);
        Assert.Equal(1024, observed.ClientWidth);
        Assert.Equal(768, observed.ClientHeight);
        Assert.Equal(1920, observed.WindowWidth);
        Assert.Equal(1080, observed.MonitorHeight);
        Assert.True(observed.WindowCoversMonitor);
        Assert.Contains("las medidas pueden estar escaladas por DPI (no se pudo fijar el contexto)", observed.Notes);
    }

    [Fact]
    public void Probe_ExceptionIsUnavailable()
    {
        var result = new Cs2WindowProbe(new FakeApi([], fail: true), () => [10]).Observe();
        Assert.Equal(Cs2WindowStatus.Unavailable, result.Status);
        Assert.Null(result.ClientWidth);
        Assert.Contains("fallo", result.Notes[0]);
    }

    [Fact]
    public void Compare_SameAndDifferent()
    {
        // 1024x768 es igual a sí misma y distinta de 1920x1080.
        Assert.Equal(ResolutionComparisonKind.Same, ResolutionComparison.Compare(1024, 768, 1024, 768).Kind);
        var different = ResolutionComparison.Compare(1024, 768, 1920, 1080);
        Assert.Equal(ResolutionComparisonKind.Different, different.Kind);
        Assert.Contains("configurada 1024x768, observada 1920x1080", different.Text);
    }

    [Theory]
    [InlineData(null, 768, 1920, 1080)]
    [InlineData(1024, null, 1920, 1080)]
    [InlineData(1024, 768, null, 1080)]
    [InlineData(1024, 768, 1920, null)]
    public void Compare_AnyUnknownIsUnknown(int? cw, int? ch, int? ow, int? oh)
    {
        // Falta uno de cuatro valores: no se puede comparar.
        var result = ResolutionComparison.Compare(cw, ch, ow, oh);
        Assert.Equal(ResolutionComparisonKind.Unknown, result.Kind);
        Assert.Equal("no se sabe", result.Text);
    }
}
