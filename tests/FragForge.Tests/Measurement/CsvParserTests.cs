using FragForge.Actions.Measurement;
using FragForge.Core.Experiments;
using Xunit;

namespace FragForge.Tests.Measurement;

/// <summary>
/// CSV de PresentMon. Las fixtures <c>Real260*</c> son una captura REAL de PresentMon 2.6.0 en la PC
/// objetivo (10 s sobre cs2.exe, flags por defecto, sin <c>--v1_metrics</c>/<c>--v2_metrics</c>),
/// copiada verbatim: 28 columnas, faltantes como <c>NA</c> y hasta 14 decimales.
///
/// OJO: esa captura fue ~8,6 FPS con <c>PresentMode=Other</c>, display en NA y GPU=0. NO es un
/// baseline válido (probable ventana sin foco o pantalla de carga; causa NO confirmada). Se usa
/// únicamente como fixture de "captura sospechosa" y para validar el parseo.
///
/// Las fixtures <c>V1Csv</c>/<c>V2Csv</c> son SINTÉTICAS y cubren formas de columna de otras versiones.
/// </summary>
public sealed class CsvParserTests
{
    /// <summary>Encabezado real de PresentMon 2.6.0, en su orden exacto (28 columnas).</summary>
    internal const string Header260 =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode," +
        "TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency," +
        "MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait," +
        "MsAnimationError,AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    private const string Row1 =
        "cs2.exe,9556,0x167400D5A60,DXGI,0,0,0,Other,283.2085,NA,115.63470000000001,NA,1.62490000000000,0.00000000000000,NA,169.4300,115.4034,113.7785,1.6249,0.0000,0.0000,0.0000,0.0000,NA,NA,NA,NA,NA";

    private const string Row2 =
        "cs2.exe,9556,0x167400D5A60,DXGI,0,0,0,Other,400.9229,NA,117.71440000000001,NA,1.87450000000000,0.00000000000000,NA,284.8334,117.9640,116.0895,1.8745,0.0000,0.0000,0.0000,0.0000,NA,NA,NA,NA,NA";

    private const string Row3 =
        "cs2.exe,9556,0x167400D5A60,DXGI,0,0,0,Other,520.6847,NA,119.76180000000001,NA,1.66020000000000,0.00000000000000,NA,402.7974,119.5475,117.8873,1.6602,0.0000,0.0000,0.0000,0.0000,NA,NA,NA,NA,NA";

    /// <summary>Captura real de 2.6.0: LF.</summary>
    internal static string Real260 => Header260 + "\n" + Row1 + "\n" + Row2 + "\n" + Row3 + "\n";

    /// <summary>La misma captura con CRLF, para probar ambos finales de línea.</summary>
    internal static string Real260Crlf => Header260 + "\r\n" + Row1 + "\r\n" + Row2 + "\r\n" + Row3 + "\r\n";

    private const string V1Csv = """
        Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsUntilRenderComplete,MsUntilDisplayed,TimeInSeconds,Dropped
        cs2.exe,4242,0x1,DXGI,1,0,1,Composed: Flip,5.000,5.100,0.400,4.000,5.000,0.005,0
        cs2.exe,4242,0x1,DXGI,1,0,1,Composed: Flip,5.200,5.300,0.410,4.100,5.200,0.0102,0
        cs2.exe,4242,0x1,DXGI,1,0,1,Independent Flip,5.100,5.200,0.390,4.050,5.100,0.0153,0
        """;

    private const string V2Csv = """
        Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameTime,CPUBusy,GPUBusy,DisplayLatency,DisplayedTime,AnimationError,AllInputToPhotonLatency
        cs2.exe,4242,0x1,DXGI,1,0,1,Independent Flip,4.900,3.900,2.100,1.000,4.950,0.000,9.000
        cs2.exe,4242,0x1,DXGI,1,0,1,Independent Flip,5.000,3.950,2.200,1.000,5.050,0.000,9.100
        """;

    // ---------------------------------------------------------------- contrato 2.6.0

    [Fact]
    public void Real260_ParsesEveryRow_AndReadsFrameTimeFromMsBetweenPresents()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(Real260));

        Assert.Equal(3, series.FrameCount);
        Assert.Equal(115.63470000000001, series.FrameTimesMs[0], 12);
        Assert.Equal(117.71440000000001, series.FrameTimesMs[1], 12);
        Assert.Equal(119.76180000000001, series.FrameTimesMs[2], 12);
    }

    [Fact]
    public void Real260_CpuComesFromMsCPUBusy_NotFromMsInPresentAPI()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(Real260));

        // MsCPUBusy = 113.7785 (la columna 18 del contrato real).
        Assert.Equal(113.7785, series.CpuBusyMs[0]!.Value, 6);
        // MsInPresentAPI = 1.6249 va a PresentApiMs, NO a CPU. Este es el error que se corrigió.
        Assert.Equal(1.6249, series.PresentApiMs[0]!.Value, 6);
        Assert.Contains("cpu=MsCPUBusy", series.SourceColumns);
        Assert.DoesNotContain("cpu=MsInPresentAPI", series.SourceColumns);
    }

    [Fact]
    public void Real260_NaCells_BecomeNull_AndNeverZero()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(Real260));

        // MsBetweenDisplayChange y MsUntilDisplayed son NA en toda la captura.
        Assert.All(series.DisplayChangeMs, value => Assert.Null(value));
        Assert.All(series.UntilDisplayedMs, value => Assert.Null(value));
        // MsCPUWait sí trae dato (1.6249).
        Assert.Equal(1.6249, series.CpuWaitMs[0]!.Value, 6);
        // MsGPUTime y MsGPUBusy son 0.0000; 0 es un dato, no null.
        Assert.Equal(0.0, series.GpuTimeMs[0]!.Value, 6);
        Assert.Equal(0.0, series.GpuBusyMs[0]!.Value, 6);
    }

    [Fact]
    public void Real260_GpuAllZero_IsReportedUnavailable_NotAsZeroPerformance()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(Real260));

        Assert.True(series.GpuUnavailable);
        Assert.Contains(series.Warnings, warning => warning.Contains("GPU no disponible", StringComparison.Ordinal));
    }

    [Fact]
    public void Real260_ReadsIdentityColumns_AndPresentMode()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(Real260));

        Assert.Equal("cs2.exe", series.ApplicationName);
        Assert.Equal(9556, series.ProcessId);
        Assert.Equal("0x167400D5A60", series.SwapChainAddress);
        Assert.Equal(283.2085, series.TimesMs[0]!.Value, 6);
        Assert.All(series.PresentModes, mode => Assert.Equal("Other", mode));
        Assert.All(series.SyncIntervals, value => Assert.Equal(0, value));
        Assert.All(series.AllowsTearing, value => Assert.False(value));
    }

    [Fact]
    public void Real260_ColumnOrderCanBePermuted_AndStillParses()
    {
        var permuted = PermuteColumns(Real260);
        var series = PresentMonCsvParser.Parse(new StringReader(permuted));

        Assert.Equal(3, series.FrameCount);
        Assert.Equal(115.63470000000001, series.FrameTimesMs[0], 12);
        Assert.Equal(113.7785, series.CpuBusyMs[0]!.Value, 6);
        Assert.Equal(9556, series.ProcessId);
    }

    [Fact]
    public void Real260_CrlfAndLf_ProduceIdenticalResults()
    {
        var lf = PresentMonCsvParser.Parse(new StringReader(Real260));
        var crlf = PresentMonCsvParser.Parse(new StringReader(Real260Crlf));

        Assert.Equal(lf.FrameTimesMs, crlf.FrameTimesMs);
        Assert.Equal(lf.CpuBusyMs, crlf.CpuBusyMs);
        Assert.Equal(lf.ProcessId, crlf.ProcessId);
    }

    [Fact]
    public void TruncatedRow_IsDiscarded_NotPartiallyUsed()
    {
        // Fila cortada justo antes de la columna de frametime.
        var csv = Header260 + "\n" + Row1 + "\n" + "cs2.exe,9556,0x167400D5A60,DXGI,0,0,0,Other,283.2085,NA\n" + Row3 + "\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.Equal(2, series.FrameCount);
        Assert.Equal(115.63470000000001, series.FrameTimesMs[0], 12);
        Assert.Equal(119.76180000000001, series.FrameTimesMs[1], 12);
    }

    [Fact]
    public void MultipleSwapChains_AnalysesDominant_AndWarns()
    {
        var otherRow = Row2.Replace("0x167400D5A60", "0xDEADBEEF000", StringComparison.Ordinal);
        var csv = Header260 + "\n" + Row1 + "\n" + Row2 + "\n" + Row3 + "\n" + otherRow + "\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.Equal("0x167400D5A60", series.SwapChainAddress);
        Assert.Contains(series.Warnings, w => w.Contains("varias swapchains", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRequiredColumn_ListsTheColumnsFound()
    {
        var csv = "Application,PresentMode\ncs2.exe,Composed: Flip\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.True(series.IsEmpty);
        Assert.StartsWith("missing-frame-time", series.SourceColumns ?? string.Empty);
        Assert.Contains("Application", series.SourceColumns);
        Assert.Contains("PresentMode", series.SourceColumns);
        Assert.Equal(["Application", "PresentMode"], series.HeaderColumns);
    }

    // ---------------------------------------------------------------- versiones previas (sintéticas)

    [Fact]
    public void ParsesV1Columns_ByName()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(V1Csv));
        Assert.Equal([5.0, 5.2, 5.1], series.FrameTimesMs);
        Assert.Equal(["Composed: Flip", "Composed: Flip", "Independent Flip"], series.PresentModes);
        Assert.Contains("MsBetweenPresents", series.SourceColumns);
    }

    [Fact]
    public void V1CpuColumnIsAbsent_SoCpuIsNull_NotTakenFromMsInPresentAPI()
    {
        // La fixture V1 no tiene MsCPUBusy ni CPUBusy: CPU debe quedar Unknown.
        // Antes se tomaba MsInPresentAPI como CPU, que es un dato distinto.
        var series = PresentMonCsvParser.Parse(new StringReader(V1Csv));
        Assert.All(series.CpuBusyMs, value => Assert.Null(value));
        Assert.Equal(0.4, series.PresentApiMs[0]!.Value, 6);
    }

    [Fact]
    public void ParsesV2Columns_ByColumnName_NotPosition()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(V2Csv));
        Assert.Equal([4.9, 5.0], series.FrameTimesMs);
        Assert.Equal([3.9, 3.95], series.CpuBusyMs.Select(v => v!.Value));
        Assert.Equal([2.1, 2.2], series.GpuBusyMs.Select(v => v!.Value));
        Assert.All(series.PresentModes, mode => Assert.Equal("Independent Flip", mode));
    }

    // ---------------------------------------------------------------- borde

    [Fact]
    public void MissingCpuColumn_YieldsNullsNotZeros()
    {
        var csv = "Application,FrameTime,PresentMode\ncs2.exe,5.0,Composed: Flip\ncs2.exe,5.1,Composed: Flip\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));
        Assert.Equal(2, series.FrameCount);
        Assert.All(series.CpuBusyMs, value => Assert.Null(value));
        Assert.All(series.GpuBusyMs, value => Assert.Null(value));
        var metrics = FrameMetricsCalculator.Compute(series, 0);
        Assert.Null(metrics.CpuBusyP95);
        Assert.Null(metrics.GpuBusyP95);
        Assert.Contains("cpuBusyP95", metrics.MissingMetrics);
    }

    [Fact]
    public void MissingFrameTimeColumn_ReturnsEmptySeriesMarked()
    {
        var csv = "Application,PresentMode\ncs2.exe,Composed: Flip\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));
        Assert.True(series.IsEmpty);
        Assert.StartsWith("missing-frame-time", series.SourceColumns ?? string.Empty);
    }

    [Fact]
    public void ZeroAndNegativeFrameTimes_AreDiscarded()
    {
        var csv = "FrameTime,PresentMode\n0,Flip\n-1,Flip\n5.0,Flip\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));
        Assert.Equal([5.0], series.FrameTimesMs);
    }

    [Fact]
    public void EmptyInput_IsEmptySeries_HdrOnly()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(""));
        Assert.True(series.IsEmpty);
        Assert.Equal("empty", series.SourceColumns);
    }

    [Fact]
    public void ParseFile_ReadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "fragforge-csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllText(path, Real260);
            var series = PresentMonCsvParser.ParseFile(path);
            Assert.Equal(3, series.FrameCount);
            Assert.Equal(9556, series.ProcessId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void QuotedFieldsWithCommasAndEscapedQuotes_AreParsed()
    {
        var csv = "FrameTime,PresentMode\n5.0,\"Composed: Flip, \"\"special\"\"\"\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));
        Assert.Equal("Composed: Flip, \"special\"", Assert.Single(series.PresentModes));
    }

    [Fact]
    public void InvariantCulture_IsUsedForDecimalPointRegardlessOfCurrentCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-MX");
            var series = PresentMonCsvParser.Parse(new StringReader("FrameTime\n5.25\n"));
            Assert.Equal(5.25, Assert.Single(series.FrameTimesMs));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ParsedSeries_FlowsIntoMetricsWithoutNaN()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(V2Csv));
        var repeated = Enumerable.Repeat(5.0, 400).ToArray();
        var metrics = FrameMetricsCalculator.Compute(series with
        {
            FrameTimesMs = repeated,
            CpuBusyMs = Enumerable.Repeat<double?>(3.0, 400).ToArray(),
            GpuBusyMs = Enumerable.Repeat<double?>(2.0, 400).ToArray(),
            PresentModes = Enumerable.Repeat("Independent Flip", 400).ToArray()
        }, 0);
        Assert.Equal(200.0, metrics.AvgFps!.Value, 6);
        Assert.NotNull(metrics.CpuBusyP95);
    }

    /// <summary>Invierte el orden de las columnas conservando las filas alineadas.</summary>
    private static string PermuteColumns(string csv)
    {
        var lines = csv.TrimEnd('\n').Split('\n');
        var header = lines[0].Split(',');
        var order = Enumerable.Range(0, header.Length).Reverse().ToArray();
        var output = new List<string> { string.Join(",", order.Select(i => header[i])) };
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            output.Add(string.Join(",", order.Select(i => i < cells.Length ? cells[i] : "NA")));
        }
        return string.Join("\n", output) + "\n";
    }
}
