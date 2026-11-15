using FragForge.Core.Analysis;
using FragForge.Core.Analysis.Live;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

/// <summary>
/// Acumulador en vivo: vistas total/ventana/segmento, duración fija, tope y concurrencia.
/// Todos los valores esperados están calculados a mano en el comentario de cada caso.
/// t_ms = i * 15.625 es exacto en binario (15.625 = 125/8), así que no hay deriva de coma flotante.
/// </summary>
public sealed class LiveFrameAccumulatorTests
{
    private const double Tolerance = 1e-9;
    private const double FrameMs = 15.625; // 64 FPS exacto en binario.

    private static void FeedUniform(LiveFrameAccumulator acc, int count, double frameMs, double firstTimeMs,
        string application = "cs2.exe", string swapChain = "0x0")
    {
        for (var i = 0; i < count; i++)
            acc.Add(new LiveFrame(application, swapChain, frameMs, firstTimeMs + i * frameMs));
    }

    [Fact]
    public void Total_Uniforme6400_Media64FpsYSpan()
    {
        var acc = new LiveFrameAccumulator();
        FeedUniform(acc, 6400, FrameMs, 0.0);

        // 6400 frames a 15.625 ms: media 15.625 -> 1000/15.625 = 64 FPS.
        // Span = (6399*15.625 - 0)/1000 = 99984.375/1000 = 99.984375 = 6399/64.
        var quick = acc.GetTotalQuick();
        Assert.Equal(6400L, quick.FrameCount);
        Assert.Equal(FrameMs, quick.Statistics!.MeanMs!.Value, Tolerance);
        Assert.Equal(64.0, 1000.0 / quick.Statistics.MeanMs.Value, Tolerance);
        Assert.Equal(99.984375, quick.SpanSeconds!.Value, Tolerance);

        var total = acc.GetTotal();
        Assert.Equal(6400L, total.FrameCount);
        Assert.Equal(FrameMs, total.Statistics!.MeanMs!.Value, Tolerance);
        Assert.Equal(99.984375, total.SpanSeconds!.Value, Tolerance);
    }

    [Fact]
    public void Ventana30s_Uniforme6400_Quedan1920Frames()
    {
        var acc = new LiveFrameAccumulator();
        FeedUniform(acc, 6400, FrameMs, 0.0);

        // Último t = 6399*15.625 = 99984.375. Corte = 99984.375 - 30000 = 69984.375.
        // i=4479 -> t=69984.375 (NO es > corte). i=4480 -> t=70000 (sí). Quedan 6400-4480 = 1920.
        // Span = (99984.375 - 70000)/1000 = 29.984375 = 1919/64.
        var window = acc.GetLastWindow();
        Assert.Equal(1920L, window.FrameCount);
        Assert.Equal(FrameMs, window.Statistics!.MeanMs!.Value, Tolerance);
        Assert.Equal(29.984375, window.SpanSeconds!.Value, Tolerance);
        Assert.Equal("últimos 30 s", window.Label);
    }

    [Fact]
    public void Segmento_MediaDelTramoYTotal()
    {
        var acc = new LiveFrameAccumulator();
        var t = 0.0;
        for (var i = 0; i < 1000; i++) { acc.Add(new LiveFrame("cs2.exe", "0x0", 15.625, t)); t += 15.625; }
        acc.MarkStart();
        for (var i = 0; i < 1000; i++) { acc.Add(new LiveFrame("cs2.exe", "0x0", 31.25, t)); t += 31.25; }
        acc.MarkEnd();
        for (var i = 0; i < 1000; i++) { acc.Add(new LiveFrame("cs2.exe", "0x0", 15.625, t)); t += 15.625; }

        // Segmento = tramo del medio: 1000 frames a 31.25 ms -> media 31.25 -> 32 FPS.
        var segment = acc.GetSegment();
        Assert.Equal(1000L, segment.FrameCount);
        Assert.Equal(31.25, segment.Statistics!.MeanMs!.Value, Tolerance);

        // Total: 3000 frames. Media = (1000*15.625 + 1000*31.25 + 1000*15.625)/3000
        //   = (15625 + 31250 + 15625)/3000 = 62500/3000 = 20.8333...
        var total = acc.GetTotal();
        Assert.Equal(3000L, total.FrameCount);
        Assert.Equal(62500.0 / 3000.0, total.Statistics!.MeanMs!.Value, Tolerance);
    }

    [Fact]
    public void DuracionFija30s_Acepta1920YCompleta()
    {
        var acc = new LiveFrameAccumulator(targetDurationSeconds: 30.0);
        FeedUniform(acc, 6400, FrameMs, 0.0);

        // Se acepta i mientras i*15.625 < 30000 -> i < 1920. i=1920 -> t=30000 (no < 30000):
        // marca IsComplete. El resto (6400-1920 = 4480) va a IgnoredAfterCompletion.
        Assert.True(acc.IsComplete);
        Assert.Equal(4480L, acc.IgnoredAfterCompletion);
        Assert.Equal(1920L, acc.GetTotalQuick().FrameCount);
    }

    [Fact]
    public void Total_900Frames_Low1Insuficiente()
    {
        var acc = new LiveFrameAccumulator();
        FeedUniform(acc, 900, FrameMs, 0.0);

        // 900 < MinFramesForLow1Percent (1000): Low1 es null con el texto del calculator.
        var stats = acc.GetTotal().Statistics!;
        Assert.Null(stats.Low1PercentAverageFps);
        var reason = stats.UnavailableReasons[nameof(FrameStatistics.Low1PercentAverageFps)];
        Assert.Contains("insuficiente: 900 frames, se necesitan 1000", reason);
    }

    [Fact]
    public void Total_Pausa5000ms_PauseCount1YMinimo02Fps()
    {
        var acc = new LiveFrameAccumulator();
        FeedUniform(acc, 100, 10.0, 0.0);
        acc.Add(new LiveFrame("cs2.exe", "0x0", 5000.0, 1000.0));

        var total = acc.GetTotal();
        // Un frame de 5000 ms > PauseThresholdMs (1000): PauseCount 1.
        Assert.Equal(1, total.Hitches!.PauseCount);
        // El frame más lento es la pausa: 1000/5000 = 0.2 FPS mínimo.
        Assert.Equal(5000.0, total.Statistics!.MaxMs!.Value, Tolerance);
        Assert.Equal(0.2, 1000.0 / total.Statistics.MaxMs.Value, Tolerance);
    }

    [Fact]
    public void DosSeries_MuestraLaMayorSinSumar()
    {
        var acc = new LiveFrameAccumulator();
        FeedUniform(acc, 100, FrameMs, 0.0, "cs2.exe", "0x0");
        FeedUniform(acc, 50, FrameMs, 0.0, "cs2.exe", "0x1234");

        var total = acc.GetTotalQuick();
        // La serie de 100 frames gana; nunca se suman (no 150).
        Assert.Equal(100L, total.FrameCount);
        Assert.Equal("0x0", total.SeriesKey!.SwapChain);
        Assert.Contains("hay 1 series más", total.Notes);
    }

    [Fact]
    public void SinColumnaDeTiempo_VistasPorTiempoNoDisponibles()
    {
        var acc = new LiveFrameAccumulator();
        for (var i = 0; i < 100; i++)
            acc.Add(new LiveFrame("cs2.exe", "0x0", FrameMs, null));

        const string expected = "no disponible: el CSV no tiene columna de tiempo (TimeInMs ni TimeInSeconds)";

        var window = acc.GetLastWindow();
        Assert.Null(window.Statistics);
        Assert.Null(window.SpanSeconds);
        Assert.Contains(expected, window.Notes);

        var quick = acc.GetTotalQuick();
        Assert.Null(quick.SpanSeconds);
        Assert.Contains(expected, quick.Notes);
        // Pero el conteo y la media no necesitan tiempo:
        Assert.Equal(100L, quick.FrameCount);
        Assert.Equal(FrameMs, quick.Statistics!.MeanMs!.Value, Tolerance);
    }

    [Fact]
    public void TopeMaxFrames_DejaDeAcumularConEstadoVisible()
    {
        var acc = new LiveFrameAccumulator(maxFrames: 100);
        FeedUniform(acc, 150, FrameMs, 0.0);

        var total = acc.GetTotalQuick();
        Assert.Equal(100L, total.FrameCount);
        Assert.True(acc.ReachedMaxFrames);
        Assert.Contains(total.Notes, n => n.Contains("tope de 100 frames"));
    }

    [Fact]
    public void FueraDeOrden_SeCuentaConNota()
    {
        var acc = new LiveFrameAccumulator();
        acc.Add(new LiveFrame("cs2.exe", "0x0", FrameMs, 0.0));
        acc.Add(new LiveFrame("cs2.exe", "0x0", FrameMs, 15.625));
        acc.Add(new LiveFrame("cs2.exe", "0x0", FrameMs, 31.25));
        acc.Add(new LiveFrame("cs2.exe", "0x0", FrameMs, 10.0)); // < 31.25: fuera de orden.
        acc.Add(new LiveFrame("cs2.exe", "0x0", FrameMs, 46.875));

        Assert.Equal(1L, acc.OutOfOrderRows);
        var total = acc.GetTotalQuick();
        Assert.Equal(5L, total.FrameCount); // se agregan igual.
        Assert.Contains(total.Notes, n => n.Contains("fuera de orden"));
    }

    [Fact]
    public void Concurrencia_FrameCountNuncaDecrece()
    {
        var acc = new LiveFrameAccumulator();
        const int frames = 10000;
        using var done = new ManualResetEventSlim(false);
        Exception? readerError = null;
        var monotonic = true;
        var last = 0L;

        var reader = new Thread(() =>
        {
            try
            {
                while (!done.IsSet)
                {
                    var count = acc.GetTotalQuick().FrameCount;
                    if (count < last) monotonic = false;
                    last = count;
                }
            }
            catch (Exception ex)
            {
                readerError = ex;
            }
        });
        reader.Start();

        FeedUniform(acc, frames, FrameMs, 0.0);
        done.Set();
        reader.Join();

        Assert.Null(readerError);
        Assert.True(monotonic);
        Assert.Equal((long)frames, acc.GetTotalQuick().FrameCount);
    }
}
