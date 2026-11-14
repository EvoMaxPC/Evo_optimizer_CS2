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
    private static FrameSeries FromReal260()
    {
        using var reader = new StringReader(CsvParserTests.Real260);
        return PresentMonCsvParser.Parse(reader);
    }

    [Fact]
    public void Real260Capture_IsSuspect_NotValid_NotInvalid()
    {
        var report = CaptureValidityGate.Evaluate(FromReal260());

        Assert.Equal(CaptureValidity.Suspect, report.Validity);
        Assert.True(report.IsSuspect);
        Assert.True(report.IsUsable, "Suspect no bloquea: se guarda y se informa.");
        Assert.NotEmpty(report.Reasons);
    }

    [Fact]
    public void Real260Capture_ReportsTheConcreteReasons()
    {
        var report = CaptureValidityGate.Evaluate(FromReal260());

        Assert.Contains(report.Reasons, reason => reason.Contains("PresentMode=Other", StringComparison.Ordinal));
        Assert.Contains(report.Reasons, reason => reason.Contains("sin seguimiento de display", StringComparison.Ordinal));
        Assert.Contains(report.Reasons, reason => reason.Contains("GPU no disponible", StringComparison.Ordinal));
        Assert.Contains(report.Reasons, reason => reason.Contains("mediana de FPS", StringComparison.Ordinal));
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
            Warnings = ["varias swapchains: 0xA=3, 0xB=1; se analiza 0xA"]
        };

        var report = CaptureValidityGate.Evaluate(series);

        Assert.Equal(CaptureValidity.Suspect, report.Validity);
        Assert.Contains(report.Reasons, reason => reason.Contains("swapchain", StringComparison.Ordinal));
    }
}
