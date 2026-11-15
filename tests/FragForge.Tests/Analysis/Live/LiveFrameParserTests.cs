using System.Globalization;
using FragForge.Core.Analysis.Live;
using FragForge.Tests.Measurement;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

/// <summary>
/// Parser en vivo: encabezado real de PresentMon 2.6.0 y filas reales medidas en la PC, más casos borde.
/// Todos los valores esperados están calculados a mano en el comentario de cada caso.
/// </summary>
public sealed class LiveFrameParserTests
{
    private const double Tolerance = 1e-9;

    // Filas reales medidas en la PC (PresentMon 2.6.0, stdout por pipe), verbatim.
    private const string FirefoxRow =
        "firefox.exe,8500,0x1AB2150BA90,DXGI,0,0,0,Composed: Flip,304.7300,NA,120.70080000000000,122.23230000000001,0.14330000000000,0.73020000000000,4.7107,184.1669,120.7064,120.5631,0.1433,0.6391,120.6542,0.9629,119.6913,NA,184.1669,NA,NA,NA";

    private const string DwmRow1 =
        "dwm.exe,1480,0x0,Other,-1,0,0,Hardware: Legacy Flip,306.1034,NA,120.69880000000001,122.23230000000001,0.00000000000000,0.35240000000000,3.3373,185.4046,120.6988,120.6988,0.0000,120.7145,0.3367,0.3367,0.0000,NA,185.4046,NA,NA,NA";

    private const string DwmRow2 =
        "dwm.exe,1480,0x0,Other,-1,0,0,Hardware: Legacy Flip,424.3585,NA,118.25510000000000,116.65940000000001,0.00000000000000,0.35770000000000,1.7416,306.1034,118.2551,118.2551,0.0000,118.2907,0.3221,0.3221,0.0000,4.0394,306.1034,NA,NA,NA";

    [Fact]
    public void Feed_EncabezadoRealYFilasReales_ParseaTiempoYFrametime()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-MX");
        try
        {
            var parser = new LiveFrameParser();

            // El CSV real termina en CRLF: las líneas llegan con \r\n.
            Assert.Equal(LiveLineKind.Header, parser.Feed(PresentMonFixtures.Header260 + "\r\n").Kind);

            var firefox = parser.Feed(FirefoxRow + "\r\n");
            Assert.Equal(LiveLineKind.Frame, firefox.Kind);
            var firefoxFrame = firefox.Frame!;
            Assert.Equal("firefox.exe", firefoxFrame.Application);
            Assert.Equal("0x1AB2150BA90", firefoxFrame.SwapChain);
            Assert.Equal(304.73, firefoxFrame.TimeMs!.Value, Tolerance);
            Assert.Equal(120.7008, firefoxFrame.FrameTimeMs, Tolerance);

            var dwm1 = parser.Feed(DwmRow1 + "\r\n");
            Assert.Equal(LiveLineKind.Frame, dwm1.Kind);
            var dwm1Frame = dwm1.Frame!;
            Assert.Equal("dwm.exe", dwm1Frame.Application);
            Assert.Equal("0x0", dwm1Frame.SwapChain);
            Assert.Equal(306.1034, dwm1Frame.TimeMs!.Value, Tolerance);
            Assert.Equal(120.6988, dwm1Frame.FrameTimeMs, Tolerance);

            var dwm2 = parser.Feed(DwmRow2 + "\r\n");
            Assert.Equal(LiveLineKind.Frame, dwm2.Kind);
            var dwm2Frame = dwm2.Frame!;
            Assert.Equal(424.3585, dwm2Frame.TimeMs!.Value, Tolerance);
            Assert.Equal(118.2551, dwm2Frame.FrameTimeMs, Tolerance);

            Assert.Equal(0L, parser.MalformedLines);
            Assert.Equal(0L, parser.NaFrames);
            Assert.Equal(0L, parser.RowsWithoutTime);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Feed_FilaNA_Truncada_SinTiempo_LineaVacia()
    {
        var parser = new LiveFrameParser();
        Assert.Equal(LiveLineKind.Header, parser.Feed(PresentMonFixtures.Header260).Kind);

        // NA en MsBetweenPresents (índice 10): no es frame, no es 0.
        var na = parser.Feed(MakeRow("cs2.exe", "0x0", "100.0", "NA"));
        Assert.Equal(LiveLineKind.Na, na.Kind);
        Assert.Equal(1L, parser.NaFrames);

        // Línea truncada: no llega a la columna principal (índice 10).
        var truncated = parser.Feed("cs2.exe,9556,0x0");
        Assert.Equal(LiveLineKind.Malformed, truncated.Kind);
        Assert.Equal(1L, parser.MalformedLines);

        // TimeInMs NA (índice 8) con frametime válido: frame sin tiempo.
        var noTime = parser.Feed(MakeRow("cs2.exe", "0x0", "NA", "16.5"));
        Assert.Equal(LiveLineKind.Frame, noTime.Kind);
        Assert.Null(noTime.Frame!.TimeMs);
        Assert.Equal(1L, parser.RowsWithoutTime);

        // Líneas vacías o en blanco (con \n solo): se ignoran.
        Assert.Equal(LiveLineKind.Ignored, parser.Feed("").Kind);
        Assert.Equal(LiveLineKind.Ignored, parser.Feed("\n").Kind);
        Assert.Equal(LiveLineKind.Ignored, parser.Feed("   ").Kind);
    }

    [Fact]
    public void Feed_SoloTimeInSeconds_ConvierteASegundosPorMil()
    {
        var parser = new LiveFrameParser();
        var headerCells = PresentMonFixtures.Header260.Split(',');
        for (var i = 0; i < headerCells.Length; i++)
            if (headerCells[i] == "TimeInMs") headerCells[i] = "TimeInSeconds";
        Assert.Equal(LiveLineKind.Header, parser.Feed(string.Join(",", headerCells)).Kind);

        // 0.30473 s * 1000 = 304.73 ms (el mismo valor que la fila real en TimeInMs).
        var frame = parser.Feed(MakeRow("firefox.exe", "0x1AB2150BA90", "0.30473", "120.7008"));
        Assert.Equal(LiveLineKind.Frame, frame.Kind);
        Assert.Equal(304.73, frame.Frame!.TimeMs!.Value, Tolerance);
        Assert.Equal(120.7008, frame.Frame.FrameTimeMs, Tolerance);
    }

    [Fact]
    public void Feed_AmbasColumnasDeTiempo_GanaTimeInMs()
    {
        var parser = new LiveFrameParser();
        // TimeInSeconds como columna extra al final (índice 28). TimeInMs (índice 8) debe ganar.
        Assert.Equal(LiveLineKind.Header, parser.Feed(PresentMonFixtures.Header260 + ",TimeInSeconds").Kind);

        var cells = new string[29];
        for (var i = 0; i < cells.Length; i++) cells[i] = "0";
        cells[0] = "cs2.exe";
        cells[2] = "0x0";
        cells[8] = "304.7300";   // TimeInMs
        cells[10] = "120.7008";  // MsBetweenPresents
        cells[28] = "999.0";     // TimeInSeconds (debe ignorarse)
        var frame = parser.Feed(string.Join(",", cells));

        Assert.Equal(LiveLineKind.Frame, frame.Kind);
        Assert.Equal(304.73, frame.Frame!.TimeMs!.Value, Tolerance);
    }

    /// <summary>Fila de 28 columnas con los índices que lee el parser (0, 2, 8, 10).</summary>
    private static string MakeRow(string application, string swapChain, string timeInMs, string msBetweenPresents)
    {
        var cells = new string[28];
        for (var i = 0; i < cells.Length; i++) cells[i] = "0";
        cells[0] = application;
        cells[2] = swapChain;
        cells[8] = timeInMs;
        cells[10] = msBetweenPresents;
        return string.Join(",", cells);
    }
}
