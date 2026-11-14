using FragForge.Actions.Measurement;
using FragForge.Core.Experiments;
using FragForge.Core.Logging;
using Xunit;

namespace FragForge.Tests.Measurement;

/// <summary>
/// La puerta de validez distingue tres cosas que antes se confundían:
/// captura sin evidencia (Invalid), captura con evidencia dudosa (Suspect) y captura utilizable (Valid).
/// La fixture real de 2.6.0 es el caso Suspect por antonomasia: ~8,6 FPS, PresentMode=Other, display en NA.
/// </summary>
public sealed class CaptureValidityTests
{
    /// <summary>La captura real de referencia: DOS swapchains. No es un baseline válido.</summary>
    private static FrameSeries TwoSwapChains()
    {
        using var reader = new StringReader(PresentMonFixtures.TwoSwapChains);
        return PresentMonCsvParser.Parse(reader);
    }

    private static FrameSeries OnlyZero()
    {
        using var reader = new StringReader(PresentMonFixtures.OnlyZero);
        return PresentMonCsvParser.Parse(reader);
    }

    [Fact]
    public void Real260Capture_IsSuspect_NotValid_NotInvalid()
    {
        var report = CaptureValidityGate.Evaluate(TwoSwapChains());

        Assert.Equal(CaptureValidity.Suspect, report.Validity);
        Assert.True(report.IsSuspect);
        Assert.True(report.IsUsable, "Suspect no bloquea: se guarda y se informa.");
        Assert.NotEmpty(report.Reasons);
    }

    [Fact]
    public void TwoSwapChainCapture_IsSuspectBecauseOfTheChains_NotBecauseOfDisplayOrGpu()
    {
        // La cadena ELEGIDA es 0x0: tiene seguimiento de display y GPU real. Por eso los motivos NO
        // pueden ser "sin seguimiento de display" ni "GPU no disponible": esos aplican a 0x167400D5A60,
        // que es la DESCARTADA. Este test fija ese matiz.
        var report = CaptureValidityGate.Evaluate(TwoSwapChains());

        Assert.Contains(report.Reasons, reason => reason.Contains("varias swapchains en el proceso", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Reasons, reason => reason.Contains("sin seguimiento de display", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Reasons, reason => reason.Contains("GPU no disponible", StringComparison.Ordinal));
    }

    [Fact]
    public void DiscardedDxgiChain_WouldHaveBeenSuspectForDisplayAndGpu_ChosenOneIsNot()
    {
        // Prueba del contraste: la cadena DXGI por separado sí dispara "sin display" y "GPU no disponible".
        using var reader = new StringReader(PresentMonFixtures.OnlyDxgi);
        var dxgiOnly = PresentMonCsvParser.Parse(reader);
        var report = CaptureValidityGate.Evaluate(dxgiOnly);

        Assert.Contains(report.Reasons, reason => reason.Contains("sin seguimiento de display", StringComparison.Ordinal));
        Assert.Contains(report.Reasons, reason => reason.Contains("GPU no disponible", StringComparison.Ordinal));
        Assert.Contains(report.Reasons, reason => reason.Contains("PresentMode=Other", StringComparison.Ordinal));
    }

    [Fact]
    public void NonStandardLayout_IsSuspect_WithItsOwnReason()
    {
        var csv = PresentMonFixtures.V1MetricsHeader + "\n" +
                  "dwm.exe,100,0x1,DXGI,1,0,0,1.5,0.4,16.6,1,Hardware: Legacy Flip,1.0,16.7,16.8,0.5,1.0,2.0,NA\n";
        using var reader = new StringReader(csv);
        var series = PresentMonCsvParser.Parse(reader);

        var report = CaptureValidityGate.Evaluate(series);

        Assert.Equal(CaptureValidity.Suspect, report.Validity);
        Assert.Contains(report.Reasons, reason => reason.Contains("formato de CSV no estándar", StringComparison.Ordinal));
    }

    [Fact]
    public void NoFrames_IsInvalid()
    {
        var report = CaptureValidityGate.Evaluate(new FrameSeries());
        Assert.Equal(CaptureValidity.Invalid, report.Validity);
        Assert.False(report.IsUsable);
    }

    [Fact]
    public void MissingRequiredColumn_IsInvalid_AndNamesTheColumn()
    {
        using var reader = new StringReader("Application,PresentMode\ncs2.exe,Flip\n");
        var series = PresentMonCsvParser.Parse(reader);
        var report = CaptureValidityGate.Evaluate(series);

        Assert.Equal(CaptureValidity.Invalid, report.Validity);
        Assert.Contains(report.Reasons, reason => reason.Contains("MsBetweenPresents", StringComparison.Ordinal));
        Assert.Contains(report.Reasons, reason => reason.Contains("Application", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthyCapture_IsValid()
    {
        var frameTimes = Enumerable.Repeat(5.0, 400).ToArray(); // 200 FPS
        var series = new FrameSeries
        {
            FrameTimesMs = frameTimes,
            PresentModes = Enumerable.Repeat("Independent Flip", 400).ToArray(),
            DisplayChangeMs = Enumerable.Repeat<double?>(5.0, 400).ToArray(),
            UntilDisplayedMs = Enumerable.Repeat<double?>(5.1, 400).ToArray(),
            GpuBusyMs = Enumerable.Repeat<double?>(2.0, 400).ToArray(),
            SourceColumns = "frameTime=MsBetweenPresents;cpu=MsCPUBusy;gpu=MsGPUBusy;mode=PresentMode"
        };

        var report = CaptureValidityGate.Evaluate(series);

        Assert.Equal(CaptureValidity.Valid, report.Validity);
        Assert.Empty(report.Reasons);
    }

    [Fact]
    public void TooFewFramesVersusRefresh_IsSuspect()
    {
        var frameTimes = Enumerable.Repeat(5.0, 100).ToArray();
        var series = new FrameSeries
        {
            FrameTimesMs = frameTimes,
            PresentModes = Enumerable.Repeat("Independent Flip", 100).ToArray(),
            DisplayChangeMs = Enumerable.Repeat<double?>(5.0, 100).ToArray(),
            UntilDisplayedMs = Enumerable.Repeat<double?>(5.1, 100).ToArray(),
            GpuBusyMs = Enumerable.Repeat<double?>(2.0, 100).ToArray()
        };

        // 100 frames esperando 240 Hz durante 10 s = 2400 esperados; 100 << 50 %.
        var report = CaptureValidityGate.Evaluate(series, TimeSpan.FromSeconds(10), refreshHz: 240);

        Assert.Equal(CaptureValidity.Suspect, report.Validity);
        Assert.Contains(report.Reasons, reason => reason.Contains("frames de ~", StringComparison.Ordinal));
    }

    [Fact]
    public void Validity_IsAdditiveInJson_AndOldDocumentsStillLoad()
    {
        // Un session.json anterior a este cambio no trae "validity": debe leerse como Valid sin motivos.
        var oldJson = """
            {"runId":"r1","arm":"Baseline","index":0,"isValid":true,"durationSeconds":10}
            """;
        var run = System.Text.Json.JsonSerializer.Deserialize<RunResult>(oldJson, JsonDefaults.Options);

        Assert.NotNull(run);
        Assert.Equal(CaptureValidity.Valid, run!.Validity.Validity);
        Assert.Empty(run.Validity.Reasons);
    }

    [Fact]
    public void Validity_RoundTripsThroughJson()
    {
        var original = new RunResult
        {
            RunId = "r1",
            Arm = ArmKind.Candidate,
            Index = 1,
            IsValid = true,
            Validity = CaptureValidityReport.Suspect("mediana de FPS 8.6 < 30", "GPU no disponible")
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original, JsonDefaults.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<RunResult>(json, JsonDefaults.Options);

        Assert.NotNull(restored);
        Assert.Equal(CaptureValidity.Suspect, restored!.Validity.Validity);
        Assert.Equal(2, restored.Validity.Reasons.Count);
    }

    [Fact]
    public void MultipleSwapChains_IsSuspect()
    {
        var series = new FrameSeries
        {
            FrameTimesMs = Enumerable.Repeat(5.0, 400).ToArray(),
            PresentModes = Enumerable.Repeat("Independent Flip", 400).ToArray(),
            DisplayChangeMs = Enumerable.Repeat<double?>(5.0, 400).ToArray(),
            UntilDisplayedMs = Enumerable.Repeat<double?>(5.1, 400).ToArray(),
            GpuBusyMs = Enumerable.Repeat<double?>(2.0, 400).ToArray(),
            SwapChains =
            [
                new SwapChainSummary("0x0", 400, 5.0, true, Chosen: true),
                new SwapChainSummary("0x167400D5A60", 3, 117.7, false, Chosen: false)
            ]
        };

        var report = CaptureValidityGate.Evaluate(series);

        Assert.Equal(CaptureValidity.Suspect, report.Validity);
        Assert.Contains(report.Reasons, reason => reason.Contains("varias swapchains en el proceso", StringComparison.Ordinal));
    }
}
