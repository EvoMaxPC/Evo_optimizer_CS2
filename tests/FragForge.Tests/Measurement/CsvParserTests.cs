using FragForge.Actions.Measurement;
using FragForge.Core.Experiments;
using Xunit;

namespace FragForge.Tests.Measurement;

/// <summary>
/// CSV de PresentMon contra fixtures REALES de 2.6.0 (ver <see cref="PresentMonFixtures"/>).
///
/// La captura de referencia tiene DOS swapchains del mismo ProcessID y NO es un baseline válido:
/// se usa solo para probar el parseo y la elección de swapchain. Estas pruebas no afirman nada sobre
/// los FPS del juego ni sobre la frecuencia de la pantalla.
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

    // ---------------------------------------------------------------- contrato 2.6.0 (una swapchain)

    [Fact]
    public void Real260_DxgiOnly_ParsesEveryRow_FromMsBetweenPresents()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.OnlyDxgi));

        Assert.Equal(3, series.FrameCount);
        Assert.Equal(115.63470000000001, series.FrameTimesMs[0], 12);
        Assert.Equal(117.71440000000001, series.FrameTimesMs[1], 12);
        Assert.Equal(119.76180000000001, series.FrameTimesMs[2], 12);
        Assert.Equal("0x167400D5A60", series.SwapChainAddress);
        // Una sola swapchain en el fichero -> un solo resumen, y es la elegida.
        var only = Assert.Single(series.SwapChains);
        Assert.True(only.Chosen);
    }

    [Fact]
    public void Real260_CpuComesFromMsCPUBusy_NotFromMsInPresentAPI()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.OnlyDxgi));

        Assert.Equal(113.7785, series.CpuBusyMs[0]!.Value, 6);
        // MsInPresentAPI = 1.6249 va a PresentApiMs, NO a CPU. Este es el error que se corrigió.
        Assert.Equal(1.6249, series.PresentApiMs[0]!.Value, 6);
        Assert.Contains("cpu=MsCPUBusy", series.SourceColumns);
        Assert.DoesNotContain("cpu=MsInPresentAPI", series.SourceColumns);
    }

    [Fact]
    public void Real260_NaCells_BecomeNull_NeverZero()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.OnlyDxgi));

        // Display en NA en toda la swapchain DXGI.
        Assert.All(series.DisplayChangeMs, value => Assert.Null(value));
        Assert.All(series.UntilDisplayedMs, value => Assert.Null(value));
        // MsCPUWait sí trae dato.
        Assert.Equal(1.6249, series.CpuWaitMs[0]!.Value, 6);
        // Un 0 es un dato real, no null: MsGPUTime/MsGPUBusy son 0.0000.
        Assert.Equal(0.0, series.GpuTimeMs[0]!.Value, 6);
        Assert.Equal(0.0, series.GpuBusyMs[0]!.Value, 6);
    }

    [Fact]
    public void Real260_GpuAllZeroInChosenChain_IsUnavailable()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.OnlyDxgi));

        Assert.True(series.GpuUnavailable);
        Assert.Contains(series.Warnings, warning => warning.Contains("GPU no disponible", StringComparison.Ordinal));
    }

    [Fact]
    public void Real260_ZeroSwapChain_GpuIsAvailable_AndPresentModeIsLegacyFlip()
    {
        // La adenda lo verifica: la iGPU Intel SÍ da MsGPUBusy con datos en la swapchain 0x0.
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.OnlyZero));

        Assert.Equal(3, series.FrameCount);
        Assert.Equal("0x0", series.SwapChainAddress);
        Assert.False(series.GpuUnavailable, "La swapchain 0x0 trae GPU real; 0x0 no es inválida por sí sola.");
        Assert.All(series.GpuBusyMs, value => Assert.NotNull(value));
        Assert.All(series.PresentModes, mode => Assert.Equal("Hardware: Legacy Flip", mode));
        Assert.All(series.AllowsTearing, value => Assert.True(value));
    }

    [Fact]
    public void Real260_InputToPhoton_IsNullable_AndClickToPhotonAllNa()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.OnlyZero));

        // MsAllInputToPhotonLatency: dato en 11 de 449 filas de la captura completa; aquí, NA en las 3.
        Assert.All(series.InputToPhotonMs, value => Assert.Null(value));
        Assert.All(series.ClickToPhotonMs, value => Assert.Null(value));
    }

    // ---------------------------------------------------------------- dos swapchains: elección

    [Fact]
    public void TwoSwapChains_ChoosesTheOneWithDisplayTracking_NotTheLargest()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.TwoSwapChains));

        // 0x0 tiene seguimiento de display, así que gana aunque el desempate por filas fuera otro.
        Assert.Equal("0x0", series.SwapChainAddress);
        Assert.Equal(3, series.FrameCount);
        // Las filas son las de 0x0 (~16.5 ms), no las de DXGI (~104-119 ms).
        Assert.Equal(16.34370000000000, series.FrameTimesMs[0], 12);
        Assert.Equal(16.91710000000000, series.FrameTimesMs[1], 12);
        Assert.Equal(16.72970000000000, series.FrameTimesMs[2], 12);
    }

    [Fact]
    public void TwoSwapChains_RecordsChosenReason_AndEveryDiscardedChainWithItsNumbers()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.TwoSwapChains));

        Assert.NotNull(series.SwapChainReason);
        Assert.Contains("seguimiento de display", series.SwapChainReason);

        Assert.Equal(2, series.SwapChains.Count);
        var chosen = Assert.Single(series.SwapChains.Where(c => c.Chosen));
        Assert.Equal("0x0", chosen.Address);
        Assert.True(chosen.HasDisplayTracking);

        var discarded = Assert.Single(series.SwapChains.Where(c => c.Discarded));
        Assert.Equal("0x167400D5A60", discarded.Address);
        Assert.Equal(3, discarded.FrameCount);
        Assert.False(discarded.HasDisplayTracking);
        // Mediana de la descartada calculada sobre SUS filas: 117.7144 (la del medio de 115.63/117.71/119.76).
        Assert.Equal(117.71440000000001, discarded.MedianFrameTimeMs!.Value, 9);
    }

    [Fact]
    public void TwoSwapChains_StatisticsComeFromOneChainOnly_AndMixingWouldDiffer()
    {
        // RED-FIRST. Con la regla vieja ("la de más filas") el desempate era entre 3 y 3, y si el
        // parser hubiera mezclado las 6 filas, la mediana habría caído entre los dos flujos.
        var parsed = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.TwoSwapChains));
        var onlyZero = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.OnlyZero));

        // 1) Solo entran las filas de UNA swapchain: 3, no 6.
        Assert.Equal(3, parsed.FrameCount);
        Assert.Equal(onlyZero.FrameTimesMs, parsed.FrameTimesMs);
        Assert.Equal(onlyZero.CpuBusyMs, parsed.CpuBusyMs);
        Assert.Equal(onlyZero.GpuBusyMs, parsed.GpuBusyMs);
        Assert.Equal(onlyZero.PresentModes, parsed.PresentModes);

        // 2) La mediana real de la cadena elegida es ~16.73 y la de la descartada ~117.71.
        //    Si se mezclaran las 6 filas, la mediana sería la media de las dos centrales: ~67, muy lejos
        //    de cualquiera de las dos. Se comprueba ese número para dejar constancia de la diferencia.
        var chosenMedian = Median(parsed.FrameTimesMs);
        var discarded = Assert.Single(parsed.SwapChains.Where(c => c.Discarded));
        var mixedMedian = Median([.. parsed.FrameTimesMs, 115.63470000000001, 117.71440000000001, 119.76180000000001]);

        Assert.Equal(chosenMedian, Median(onlyZero.FrameTimesMs));
        Assert.Equal(16.72970000000000, chosenMedian, 9);
        Assert.Equal(117.71440000000001, discarded.MedianFrameTimeMs!.Value, 9);
        Assert.True(Math.Abs(mixedMedian - chosenMedian) > 40, $"mezclar daría {mixedMedian}, muy distinto de {chosenMedian}");
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    [Fact]
    public void TwoSwapChains_IsNotABaseline_AndDoesNotClaimFps()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.TwoSwapChains));

        // No se afirma nada sobre FPS: solo se comprueba que la serie es de UNA swapchain y está marcada.
        Assert.Equal(2, series.SwapChains.Count);
        Assert.Contains(series.Warnings, w => w.Contains("varias swapchains", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoSwapChains_CrlfAndLf_ProduceIdenticalResults()
    {
        var lf = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.TwoSwapChains));
        var crlf = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.TwoSwapChainsCrlf));

        Assert.Equal(lf.FrameTimesMs, crlf.FrameTimesMs);
        Assert.Equal(lf.CpuBusyMs, crlf.CpuBusyMs);
        Assert.Equal(lf.SwapChainAddress, crlf.SwapChainAddress);
        Assert.Equal(lf.SwapChains.Count, crlf.SwapChains.Count);
    }

    [Fact]
    public void WithoutSwapChainColumn_AddressIsNull_NotAnEmptyString()
    {
        // RED-FIRST: si no hay columna SwapChainAddress, todas las filas caen en la misma clave
        // interna (""). Eso está bien para agrupar, pero reportar "" como dirección es afirmar una
        // dirección que no se conoce. R-L: se devuelve null.
        var csv = "Application,MsBetweenPresents,PresentMode,MsCPUBusy\n" +
                  "cs2.exe,16.7,Hardware: Legacy Flip,8.0\n" +
                  "cs2.exe,16.8,Hardware: Legacy Flip,8.1\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.Equal(2, series.FrameCount);
        Assert.Null(series.SwapChainAddress);
        // El resumen existe (hubo una sola agrupación) pero no inventa una dirección.
        var only = Assert.Single(series.SwapChains);
        Assert.Equal(string.Empty, only.Address);
        Assert.True(only.Chosen);
    }

    [Fact]
    public void RowsWithNaSwapChainAddress_AreGroupedSeparately_AndTheirAddressIsNull()
    {
        // Filas con dirección NA no se pueden atribuir a una cadena concreta: no se mezclan con la
        // cadena real ni se les inventa su dirección.
        var csv = PresentMonFixtures.Header260 + "\n" +
                  PresentMonFixtures.ZeroRow1 + "\n" + PresentMonFixtures.ZeroRow2 + "\n" +
                  PresentMonFixtures.ZeroRow3 + "\n" +
                  // La misma fila de 0x0 pero con SwapChainAddress=NA (columna 2, índice 2).
                  "cs2.exe,9556,NA,Other,0,0,1,Hardware: Legacy Flip,7500.0000,NA,16.50000000000000,16.40000000000000,0.00000000000000,1.20000000000000,16.2000,7483.1724,16.5000,16.5000,0.0000,1.2000,16.0000,15.8000,0.4000,0.4000,7483.1724,NA,NA,NA\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.Equal(2, series.SwapChains.Count);
        Assert.Equal("0x0", series.SwapChainAddress); // gana la que tiene dirección Y display, con 3 filas
        Assert.Equal(3, series.FrameCount);           // la fila NA NO se mezcla con las de 0x0
        Assert.Contains(series.SwapChains, c => c.Discarded && c.Address.Length == 0);
    }

    [Fact]
    public void SingleChain_WithMoreRows_IsStillTheOnlyOne_AndHasNoReason()
    {
        // Una sola swapchain repetida: no hay elección que justificar, aunque tenga muchas filas.
        var extra = PresentMonFixtures.OnlyDxgi.TrimEnd('\n') + "\n" + PresentMonFixtures.DxgiRow1 + "\n" + PresentMonFixtures.DxgiRow2 + "\n";
        var series = PresentMonCsvParser.Parse(new StringReader(extra));

        Assert.Equal("0x167400D5A60", series.SwapChainAddress);
        Assert.Equal(5, series.FrameCount);
        Assert.Single(series.SwapChains);
        Assert.Null(series.SwapChainReason); // una sola swapchain: no hay nada que justificar
        Assert.Empty(series.Warnings.Where(w => w.Contains("varias swapchains", StringComparison.Ordinal)));
    }

    [Fact]
    public void TwoChains_NoDisplayTrackingAnywhere_FallsBackToMostRows()
    {
        // Contraste con la captura real: si NINGUNA cadena tiene seguimiento de display, gana la de más
        // filas. Para forzar ese caso se sustituye la columna MsUntilDisplayed (índice 14) por NA en una
        // copia de las filas reales. Es una MUTACIÓN solo para ejercitar la rama: no describe el hardware.
        var withMoreDxgiRows = PresentMonFixtures.Header260 + "\n"
            + string.Join("\n", new[]
            {
                PresentMonFixtures.DxgiRow1, PresentMonFixtures.DxgiRow2,
                PresentMonFixtures.DxgiRow3, PresentMonFixtures.DxgiRow1,
                PresentMonFixtures.ZeroRow1, PresentMonFixtures.ZeroRow2, PresentMonFixtures.ZeroRow3
            }) + "\n";

        var noDisplay = WithoutColumn(withMoreDxgiRows, columnIndex: 14);
        var series = PresentMonCsvParser.Parse(new StringReader(noDisplay));

        // DXGI tiene 4 filas y 0x0 tiene 3: sin display en ninguna, gana DXGI por número de filas.
        Assert.Equal("0x167400D5A60", series.SwapChainAddress);
        Assert.Equal(4, series.FrameCount);
        Assert.Contains(series.Warnings, w => w.Contains("ninguna tiene seguimiento de display", StringComparison.Ordinal));
        Assert.Contains(series.Warnings, w => w.Contains("0x167400D5A60", StringComparison.Ordinal) && w.Contains("[elegida]", StringComparison.Ordinal));
        Assert.Contains(series.Warnings, w => w.Contains("0x0", StringComparison.Ordinal) && w.Contains("[descartada]", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoChains_DisplayTrackingWinsOverHavingMoreRows()
    {
        // La regla no es "la de más filas": con 4 filas DXGI sin display, gana 0x0 con 3 filas y display.
        var withMoreDxgiRows = PresentMonFixtures.Header260 + "\n"
            + string.Join("\n", new[]
            {
                PresentMonFixtures.DxgiRow1, PresentMonFixtures.DxgiRow2,
                PresentMonFixtures.DxgiRow3, PresentMonFixtures.DxgiRow1,
                PresentMonFixtures.ZeroRow1, PresentMonFixtures.ZeroRow2, PresentMonFixtures.ZeroRow3
            }) + "\n";

        var series = PresentMonCsvParser.Parse(new StringReader(withMoreDxgiRows));

        Assert.Equal("0x0", series.SwapChainAddress);
        Assert.Equal(3, series.FrameCount);
        var discarded = Assert.Single(series.SwapChains.Where(c => c.Discarded));
        Assert.Equal("0x167400D5A60", discarded.Address);
        Assert.Equal(4, discarded.FrameCount);
        Assert.False(discarded.HasDisplayTracking);
    }

    /// <summary>Sustituye una columna por <c>NA</c> en todas las filas de datos. Solo para pruebas.</summary>
    private static string WithoutColumn(string csv, int columnIndex)
    {
        var lines = csv.TrimEnd('\n').Split('\n');
        var output = new List<string> { lines[0] };
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitLine(line);
            if (columnIndex < cells.Count) cells[columnIndex] = "NA";
            output.Add(string.Join(",", cells));
        }
        return string.Join("\n", output) + "\n";
    }

    /// <summary>Split mínimo de las fixtures (sin comillas: los valores reales no las llevan).</summary>
    private static List<string> SplitLine(string line) => [.. line.Split(',')];

    // ---------------------------------------------------------------- formatos no estándar

    [Fact]
    public void V1MetricsLayout_IsAcceptedAsFallback_AndFlaggedNonStandard()
    {
        var csv = PresentMonFixtures.V1MetricsHeader + "\n" +
                  "dwm.exe,100,0x1,DXGI,1,0,0,1.5,0.4,16.6,1,Hardware: Legacy Flip,1.0,16.7,16.8,0.5,1.0,2.0,NA\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.True(series.FrameCount == 1, $"esperaba 1 fila, hay {series.FrameCount}; columnas: {string.Join(",", series.HeaderColumns)}");
        Assert.True(Math.Abs(series.FrameTimesMs[0] - 16.6) < 1e-6, $"frame time {series.FrameTimesMs[0]}, esperaba 16.6");
        Assert.True(
            series.IsNonStandardLayout,
            $"el layout --v1_metrics debe marcarse no estándar (trae Runtime+msBetweenPresents, no PresentRuntime). " +
            $"HeaderColumns: {string.Join(",", series.HeaderColumns)}; SourceColumns: {series.SourceColumns}");
        Assert.True(
            series.Warnings.Any(w => w.Contains("formato de CSV no estándar", StringComparison.Ordinal)),
            $"faltó el aviso de formato no estándar. Warnings: [{string.Join(" | ", series.Warnings)}]");
    }

    [Fact]
    public void V2MetricsLayout_IsAcceptedAsFallback_AndFlaggedNonStandard()
    {
        var csv = PresentMonFixtures.V2MetricsHeader + "\n" +
                  "dwm.exe,100,0x1,DXGI,1,0,0,Independent Flip,1.0,16.6,3.9,0.5,1.0,2.0,2.1,0.3,1.0,16.7,0.0,0.0,NA,9.0,NA\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.True(series.FrameCount == 1, $"esperaba 1 fila, hay {series.FrameCount}");
        Assert.True(Math.Abs(series.FrameTimesMs[0] - 16.6) < 1e-6, $"frame time {series.FrameTimesMs[0]}, esperaba 16.6 (columna FrameTime)");
        Assert.True(
            series.IsNonStandardLayout,
            $"el layout --v2_metrics debe marcarse no estándar. HeaderColumns: {string.Join(",", series.HeaderColumns)}");
        Assert.True(
            series.CpuBusyMs[0] is { } cpu && Math.Abs(cpu - 3.9) < 1e-6,
            $"CPUBusy {series.CpuBusyMs[0]?.ToString() ?? "null"}, esperaba 3.9");
    }

    [Fact]
    public void DefaultLayout_IsNotFlaggedNonStandard()
    {
        var series = PresentMonCsvParser.Parse(new StringReader(PresentMonFixtures.OnlyDxgi));
        Assert.False(series.IsNonStandardLayout);
        Assert.DoesNotContain(series.Warnings, w => w.Contains("no estándar", StringComparison.Ordinal));
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
    public void TruncatedRow_IsDiscarded_NotPartiallyUsed()
    {
        var csv = PresentMonFixtures.Header260 + "\n" + PresentMonFixtures.DxgiRow1 + "\n" +
                  "cs2.exe,9556,0x167400D5A60,DXGI,0,0,0,Other,283.2085,NA\n" + PresentMonFixtures.DxgiRow3 + "\n";
        var series = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.Equal(2, series.FrameCount);
        Assert.Equal(115.63470000000001, series.FrameTimesMs[0], 12);
        Assert.Equal(119.76180000000001, series.FrameTimesMs[1], 12);
    }

    [Fact]
    public void ParseFile_ReadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "fragforge-csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllText(path, PresentMonFixtures.TwoSwapChains);
            var series = PresentMonCsvParser.ParseFile(path);
            Assert.Equal(3, series.FrameCount);
            Assert.Equal(9556, series.ProcessId);
            Assert.Equal("0x0", series.SwapChainAddress);
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
