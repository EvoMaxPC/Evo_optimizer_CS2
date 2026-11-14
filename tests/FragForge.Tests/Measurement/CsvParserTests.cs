using FragForge.Actions.Measurement;
using FragForge.Core.Experiments;
using Xunit;

namespace FragForge.Tests.Measurement;

/// <summary>
/// Los CSV de este archivo son SINTÉTICOS: reproducen la forma de columna de PresentMon 1.x y 2.x,
/// no son capturas reales. Sirven para probar el parser, no para validar métricas del juego.
/// </summary>
public sealed class CsvParserTests
{
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

    [Fact]
    public void ParsesV1Columns_ByName()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(V1Csv));
        Assert.Equal([5.0, 5.2, 5.1], series.FrameTimesMs);
        Assert.Equal(["Composed: Flip", "Composed: Flip", "Independent Flip"], series.PresentModes);
        Assert.Contains("MsBetweenPresents", series.SourceColumns);
        Assert.Contains("msInPresentAPI", series.SourceColumns);
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
            File.WriteAllText(path, "FrameTime,PresentMode\n5.0,Flip\n5.1,Flip\n");
            var series = PresentMonCsvParser.ParseFile(path);
            Assert.Equal(2, series.FrameCount);
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
}
