using FragForge.Core.Analysis;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Analysis;

/// <summary>
/// El lector se prueba con la CABECERA REAL de PresentMon 2.6.0 (28 columnas, flags por defecto) copiada de
/// <c>PresentMonFixtures.cs</c>. Nada aquí lanza PresentMon ni toca la máquina: son cadenas de texto.
/// </summary>
public sealed class FrameReaderTests
{
    // Encabezado real, verbatim de tests/FragForge.Tests/Measurement/PresentMonFixtures.cs:18-22.
    internal const string Header260 =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    /// <summary>Construye una fila con TODAS las columnas en NA salvo las que se pasan. Evita contar comas a mano.</summary>
    internal static string Row(string app, string chain, string msBetweenPresents, string msBetweenDisplayChange = "NA")
    {
        var cells = Header260.Split(',');
        var values = cells.Select(c => "NA").ToArray();
        void Set(string name, string value)
        {
            var index = Array.IndexOf(cells, name);
            Assert.True(index >= 0, $"El encabezado de pruebas no tiene '{name}'.");
            values[index] = value;
        }
        Set("Application", app);
        Set("SwapChainAddress", chain);
        Set("MsBetweenPresents", msBetweenPresents);
        Set("MsBetweenDisplayChange", msBetweenDisplayChange);
        return string.Join(',', values);
    }

    private static FrameStream Parse(params string[] lines) => FrameReader.Read(new StringReader(string.Join('\n', lines)));

    [Fact]
    public void NonFiniteAndInvalidNumbers_AreMalformed_WhileNaRemainsMissing()
    {
        const string csv = "Application,TimeInMs,MsBetweenPresents,MsBetweenDisplayChange\n" +
            "cs2.exe,NaN,10,2\ncs2.exe,Infinity,10,2\ncs2.exe,-Infinity,10,2\n" +
            "cs2.exe,1,NaN,2\ncs2.exe,1,Infinity,2\ncs2.exe,1,-Infinity,2\n" +
            "cs2.exe,bad,10,2\ncs2.exe,1,bad,2\ncs2.exe,1,NA,2\n" +
            "cs2.exe,NA,12.5,NA\ncs2.exe,1e2,1.5e1,3\n";
        var stream = FrameReader.Read(new StringReader(csv));
        var series = Assert.Single(stream.Series);
        // 6 no finitas + 2 texto inválido = 8 malformadas; 1 NA principal; 2 frames: 12.5 y 15.
        Assert.Equal(8, stream.MalformedLines);
        Assert.Equal(1, stream.NaFrames);
        Assert.Equal([12.5, 15.0], series.Primary);
        Assert.Equal([null, 3.0], series.Secondary);
    }

    [Fact]
    public void ReadsPrimaryColumnByName_NotByPosition()
    {
        // Mismo dato, columnas del encabezado en OTRO orden: si se leyera por posición saldría otra cosa.
        var reordered = "MsBetweenDisplayChange,SwapChainAddress,Application,MsBetweenPresents,PresentRuntime";
        var stream = FrameReader.Read(new StringReader(reordered + "\nNA,0xABC,juego.exe,16.7,NA\n"));

        var series = Assert.Single(stream.Series);
        Assert.Equal("juego.exe", series.Application);
        Assert.Equal("0xABC", series.SwapChain);
        // Cálculo a mano: una sola fila con 16.7 -> un frame de 16.7.
        Assert.Equal([16.7], series.Primary);
        Assert.Equal("MsBetweenPresents", stream.PrimaryColumn);
        Assert.Equal("MsBetweenDisplayChange", stream.SecondaryColumn);
    }

    [Fact]
    public void NaInPrimaryColumn_IsCountedAndExcluded_NeverZero()
    {
        var stream = Parse(Header260,
            Row("game.exe", "0x1", "10"),
            Row("game.exe", "0x1", "NA"),
            Row("game.exe", "0x1", "20"));

        var series = Assert.Single(stream.Series);
        // Cálculo a mano: 3 filas, la del medio es NA -> 2 frames válidos, 1 NA. Media = (10+20)/2 = 15.
        Assert.Equal([10.0, 20.0], series.Primary);
        Assert.Equal(1, stream.NaFrames);
        Assert.Equal(0, stream.MalformedLines);
        Assert.Equal(15.0, series.Primary.Average(), 10);
        Assert.DoesNotContain(0.0, series.Primary);
    }

    [Fact]
    public void TwoSwapChains_ProduceTwoSeries_NoSilentPick()
    {
        var stream = Parse(Header260,
            Row("game.exe", "0x1", "10"),
            Row("game.exe", "0x2", "20"),
            Row("game.exe", "0x1", "30"));

        // Cálculo a mano: 0x1 tiene 10 y 30 (2 frames); 0x2 tiene 20 (1 frame). Total 3, ninguno perdido.
        Assert.Equal(2, stream.Series.Count);
        var first = stream.Series.Single(s => s.SwapChain == "0x1");
        var second = stream.Series.Single(s => s.SwapChain == "0x2");
        Assert.Equal([10.0, 30.0], first.Primary);
        Assert.Equal([20.0], second.Primary);
        Assert.Equal(3, stream.Series.Sum(s => s.Primary.Count));
    }

    [Fact]
    public void TwoApplications_AreKeptApart()
    {
        var stream = Parse(Header260,
            Row("game.exe", "0x1", "10"),
            Row("overlay.exe", "0x1", "50"),
            Row("game.exe", "0x1", "12"));

        // Cálculo a mano: mezclar daría [10,12,50]; separado, game.exe = [10,12] y overlay.exe = [50].
        Assert.Equal(2, stream.Series.Count);
        Assert.Equal([10.0, 12.0], stream.Series.Single(s => s.Application == "game.exe").Primary);
        Assert.Equal([50.0], stream.Series.Single(s => s.Application == "overlay.exe").Primary);
        // Media de game.exe = (10 + 12) / 2 = 22 / 2 = 11.
        Assert.Equal(11.0, stream.Series.Single(s => s.Application == "game.exe").Primary.Average(), 10);
    }

    [Fact]
    public void MissingPrimaryColumn_IsUnavailableWithReason_NotAnException()
    {
        var stream = FrameReader.Read(new StringReader("Application,SwapChainAddress,MsBetweenDisplayChange\ngame.exe,0x1,16.7\n"));

        Assert.Empty(stream.Series);
        Assert.False(stream.HasData);
        Assert.NotNull(stream.UnavailableReason);
        Assert.Contains("MsBetweenPresents", stream.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSecondaryColumn_IsNull_NotEmptyList()
    {
        var stream = FrameReader.Read(new StringReader("Application,SwapChainAddress,MsBetweenPresents\ngame.exe,0x1,16.7\n"));

        var series = Assert.Single(stream.Series);
        // La secundaria no está en el encabezado: null. Distinto de "estaba y todas eran NA".
        Assert.Null(series.Secondary);
        Assert.Null(stream.SecondaryColumn);
        Assert.Equal([16.7], series.Primary);
    }

    [Fact]
    public void SecondaryColumn_WhenPresent_IsAlignedWithPrimary()
    {
        var stream = Parse(Header260,
            Row("game.exe", "0x1", "10", "11"),
            Row("game.exe", "0x1", "20", "NA"),
            Row("game.exe", "0x1", "30", "33"));

        var series = Assert.Single(stream.Series);
        // Alineación por índice: [10,20,30] con [11, NA, 33]. El NA de la secundaria NO se convierte en 0.
        Assert.Equal([10.0, 20.0, 30.0], series.Primary);
        Assert.Equal([11.0, null, 33.0], series.Secondary);
    }

    [Fact]
    public void MalformedLine_IsCountedAndReported_NotAnAbort()
    {
        var stream = Parse(Header260,
            Row("game.exe", "0x1", "10"),
            "game.exe,0x1",                      // truncada: no llega a la columna principal
            Row("game.exe", "0x1", "definitivamente-no-un-numero"),
            Row("game.exe", "0x1", "20"));

        var series = Assert.Single(stream.Series);
        // Cálculo a mano: 4 líneas -> 1 truncada + 1 no numérica = 2 malformadas, 0 NA y 2 válidas [10, 20].
        Assert.Equal([10.0, 20.0], series.Primary);
        Assert.Equal(2, stream.MalformedLines);
        Assert.Equal(0, stream.NaFrames);
    }

    [Fact]
    public void HandlesBomAndCrLf()
    {
        var bom = "\uFEFF";
        var text = bom + Header260 + "\r\n" + Row("game.exe", "0x1", "10") + "\r\n" + Row("game.exe", "0x1", "30") + "\r\n";
        var stream = FrameReader.Read(new StringReader(text));

        // Cálculo a mano: (10 + 30) / 2 = 20. Si el BOM se colara en el nombre de la primera columna, esto
        // no encontraría 'Application' y la etiqueta sería "(sin aplicación)".
        var series = Assert.Single(stream.Series);
        Assert.Equal([10.0, 30.0], series.Primary);
        Assert.Equal(20.0, series.Primary.Average(), 10);
        Assert.Equal("game.exe", series.Application);
        Assert.Null(stream.UnavailableReason);
    }

    [Fact]
    public void EmptyCsv_IsUnavailableWithReason()
    {
        var stream = FrameReader.Read(new StringReader(""));
        Assert.Empty(stream.Series);
        Assert.False(stream.HasData);
        Assert.NotNull(stream.UnavailableReason);
    }

    [Fact]
    public void AllNa_IsUnavailableWithCounts()
    {
        var stream = Parse(Header260, Row("game.exe", "0x1", "NA"), Row("game.exe", "0x1", "NA"));

        // Cálculo a mano: 2 filas, ambas NA -> 0 frames y 2 NA.
        Assert.Equal(2, stream.NaFrames);
        Assert.False(stream.HasData);
        Assert.NotNull(stream.UnavailableReason);
        Assert.Empty(stream.Series.Single().Primary);
    }

    [Fact]
    public void ReadFile_StreamsFromDisk_WithRealHeader()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "capture.csv");
        File.WriteAllText(path, Header260 + "\n" + Row("game.exe", "0x1", "6.9") + "\n" + Row("game.exe", "0x1", "10.0") + "\n");

        var stream = FrameReader.ReadFile(path);

        // Cálculo a mano: (6.9 + 10.0) / 2 = 8.45.
        var series = Assert.Single(stream.Series);
        Assert.Equal([6.9, 10.0], series.Primary);
        Assert.Equal(8.45, series.Primary.Average(), 10);
        // El encabezado real de PresentMon 2.6.0 tiene 28 columnas; si el lector se comiera alguna, esto falla.
        Assert.Equal(28, stream.HeaderColumns.Count);
    }

    [Fact]
    public void NumbersUseInvariantDecimalPoint()
    {
        // Bajo cultura es-AR el separador decimal es la coma: si el parseo no fuera invariante, "10.5" sería
        // 105 (separador de miles) o fallaría, y el frame se perdería como NA.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-AR");
            var stream = Parse(Header260, Row("game.exe", "0x1", "10.5"), Row("game.exe", "0x1", "20.25"));
            // Cálculo a mano: (10.5 + 20.25) / 2 = 15.375.
            var series = Assert.Single(stream.Series);
            Assert.Equal([10.5, 20.25], series.Primary);
            Assert.Equal(15.375, series.Primary.Average(), 10);
            Assert.Equal(0, stream.NaFrames);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
