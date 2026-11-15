using FragForge.Core.Analysis.Live;
using FragForge.Tests.Measurement;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

/// <summary>
/// Ingesta en vivo: splitter + parser + acumulador. Todos los valores esperados están calculados
/// a mano en el comentario de cada caso.
/// </summary>
public sealed class LiveFrameIngestTests
{
    private const double Tolerance = 1e-9;

    /// <summary>Fila de 28 columnas con los índices que lee el parser (Application=0, SwapChain=2, TimeInMs=8, MsBetweenPresents=10).</summary>
    private static string Row(string application, string swapChain, string timeInMs, string msBetweenPresents)
    {
        var cells = new string[28];
        for (var i = 0; i < cells.Length; i++) cells[i] = "0";
        cells[0] = application;
        cells[2] = swapChain;
        cells[8] = timeInMs;
        cells[10] = msBetweenPresents;
        return string.Join(",", cells);
    }

    private static string Csv(params string[] rows) =>
        PresentMonFixtures.Header260 + "\r\n" + string.Join("\r\n", rows) + "\r\n";

    [Fact]
    public void Push_EncabezadoMasTresFilas_ContadoresYMedia()
    {
        var ingest = new LiveFrameIngest();
        // Frametimes 10, 20, 30 -> media (10+20+30)/3 = 20. Tiempos 0, 100, 200.
        ingest.Push(Csv(
            Row("cs2.exe", "0x0", "0.0", "10.0"),
            Row("cs2.exe", "0x0", "100.0", "20.0"),
            Row("cs2.exe", "0x0", "200.0", "30.0")));

        var counters = ingest.Counters;
        Assert.True(counters.HeaderSeen);
        Assert.True(counters.HasFrameTimeColumn);
        Assert.True(counters.HasTimeColumn);
        Assert.Equal(3L, counters.FramesParsed);
        Assert.Equal(0L, counters.MalformedLines);
        Assert.Equal(0L, counters.NaFrames);

        var quick = ingest.Accumulator.GetTotalQuick();
        Assert.Equal(3L, quick.FrameCount);
        Assert.Equal(20.0, quick.Statistics!.MeanMs!.Value, Tolerance);
    }

    [Theory]
    [InlineData(0)]  // texto entero
    [InlineData(7)]  // trozos de 7 caracteres
    [InlineData(1)]  // trozos de 1 carácter
    public void Push_MismoTextoEnTrozosDistintos_ResultadosIdenticos(int chunkSize)
    {
        var text = Csv(
            Row("cs2.exe", "0x0", "0.0", "10.0"),
            Row("cs2.exe", "0x0", "100.0", "20.0"),
            Row("cs2.exe", "0x0", "200.0", "30.0"));

        var ingest = new LiveFrameIngest();
        if (chunkSize == 0)
        {
            ingest.Push(text);
        }
        else
        {
            for (var i = 0; i < text.Length; i += chunkSize)
                ingest.Push(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
        }

        // Propiedad clave: el troceado no cambia nada. Valores de referencia calculados a mano:
        // 3 frames, media 20, span (200-0)/1000 = 0.2 s.
        var counters = ingest.Counters;
        Assert.Equal(3L, counters.FramesParsed);
        Assert.Equal(0L, counters.MalformedLines);
        Assert.Equal(0L, counters.NaFrames);
        Assert.Equal(0L, counters.RowsWithoutTime);
        Assert.Equal(0L, counters.OverlongLines);

        var quick = ingest.Accumulator.GetTotalQuick();
        Assert.Equal(3L, quick.FrameCount);
        Assert.Equal(20.0, quick.Statistics!.MeanMs!.Value, Tolerance);
        Assert.Equal(0.2, quick.SpanSeconds!.Value, Tolerance);
    }

    [Fact]
    public void Push_FilaNA_NoEntraALaSerie_YNaFramesDeLaVistaSigueEnCero()
    {
        var ingest = new LiveFrameIngest();
        ingest.Push(Csv(
            Row("cs2.exe", "0x0", "0.0", "10.0"),
            Row("cs2.exe", "0x0", "100.0", "NA"),
            Row("cs2.exe", "0x0", "200.0", "30.0")));

        var counters = ingest.Counters;
        Assert.Equal(1L, counters.NaFrames);
        Assert.Equal(2L, counters.FramesParsed);

        var total = ingest.Accumulator.GetTotal();
        Assert.Equal(2L, total.FrameCount);
        // La fila NA nunca entra a una serie: FrameStatistics.NaFrames de la vista vale 0.
        // Por eso existen los contadores de ingesta: L3 muestra NaFrames desde acá, no desde la vista.
        Assert.Equal(0, total.Statistics!.NaFrames);
    }

    [Fact]
    public void Push_FilaTruncadaAlFinal_NoEntraYSeDescartaAlCompletar()
    {
        var ingest = new LiveFrameIngest();
        var good = Row("cs2.exe", "0x0", "0.0", "10.0");
        // Fila cortada a mitad de número ("16." sin terminar, sin '\n'): si se entregara al parser,
        // "16." parsearía como 16.0 y entraría como frame válido. No debe entrar.
        var truncated = "cs2.exe,9556,0x0,Other,0,0,0,Other,100.0,NA,16.";
        ingest.Push(PresentMonFixtures.Header260 + "\r\n" + good + "\r\n" + truncated);
        ingest.Complete();

        var counters = ingest.Counters;
        Assert.Equal(1L, counters.FramesParsed);
        Assert.Equal(1L, counters.DiscardedPartialLines);
        Assert.Equal(1L, ingest.Accumulator.GetTotalQuick().FrameCount);
    }

    [Fact]
    public void Push_EncabezadoSinMsBetweenPresents_FilasMalformadas()
    {
        var headerCells = PresentMonFixtures.Header260.Split(',');
        var withoutPrimary = string.Join(",", headerCells.Where(c => c != "MsBetweenPresents"));
        var ingest = new LiveFrameIngest();
        ingest.Push(withoutPrimary + "\r\n"
            + Row("cs2.exe", "0x0", "0.0", "10.0") + "\r\n"
            + Row("cs2.exe", "0x0", "100.0", "20.0") + "\r\n");

        var counters = ingest.Counters;
        Assert.True(counters.HeaderSeen);
        Assert.False(counters.HasFrameTimeColumn);
        Assert.Equal(2L, counters.MalformedLines);
        Assert.Equal(0L, counters.FramesParsed);
    }

    [Fact]
    public void Push_DuracionFija1s_AceptaCuatroYCompleta()
    {
        var ingest = new LiveFrameIngest(targetDurationSeconds: 1.0);
        // Frames de 250 ms con TimeInMs 0, 250, 500, 750, 1000, 1250 (6 frames).
        // Se acepta mientras (t - t0) < 1000: 0, 250, 500, 750 -> 4 frames.
        // t=1000: 1000 < 1000 es falso -> completa y se cuenta en IgnoredAfterCompletion.
        // t=1250: ya completa -> IgnoredAfterCompletion. Total ignorados: 6 - 4 = 2.
        ingest.Push(PresentMonFixtures.Header260 + "\r\n");
        foreach (var t in new[] { 0.0, 250.0, 500.0, 750.0, 1000.0, 1250.0 })
            ingest.Push(Row("cs2.exe", "0x0", t.ToString(System.Globalization.CultureInfo.InvariantCulture), "250.0") + "\r\n");

        Assert.True(ingest.Accumulator.IsComplete);
        Assert.Equal(4L, ingest.Accumulator.GetTotalQuick().FrameCount);
        Assert.Equal(2L, ingest.Accumulator.IgnoredAfterCompletion);
    }

    [Fact]
    public void Push_TrasComplete_SeIgnora()
    {
        var ingest = new LiveFrameIngest();
        ingest.Push(Csv(Row("cs2.exe", "0x0", "0.0", "10.0")));
        ingest.Complete();
        ingest.Push(Row("cs2.exe", "0x0", "100.0", "20.0") + "\r\n");

        var counters = ingest.Counters;
        Assert.Equal(1L, counters.FramesParsed);
        Assert.Equal(1L, ingest.Accumulator.GetTotalQuick().FrameCount);
    }
}
