using FragForge.Actions.Measurement;
using FragForge.Core.Experiments;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Measurement;

/// <summary>
/// PresentMon con un lanzador falso: prueba la CONSTRUCCIÓN de la invocación y el manejo del CSV,
/// sin ejecutar nada. La captura real no está validada en el PN64 (ver informe, sección "NO validado").
/// </summary>
public sealed class PresentMonCaptureTests
{
    private sealed class FakeLauncher : IToolProcessLauncher
    {
        public List<ToolProcessRequest> Requests { get; } = [];
        public ToolProcessResult Result { get; set; } = new(0, false, false, "", "");
        public bool Cancelled { get; set; }
        public string? CsvToWrite { get; set; }

        public Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (CsvToWrite is not null && request.ExpectedOutputFile is not null) File.WriteAllText(request.ExpectedOutputFile, CsvToWrite);
            if (Cancelled) throw new OperationCanceledException();
            return Task.FromResult(Result);
        }
    }

    private static (PresentMonFrameCapture Capture, FakeLauncher Launcher, TempDirectory Tools) Create(string? approvedHash = null)
    {
        var tools = new TempDirectory();
        var path = Path.Combine(tools.Path, "PresentMon.exe");
        File.WriteAllText(path, "fake-binary");
        var sha = approvedHash ?? ToolVerifier.ComputeSha256(path);
        var allowlist = new ToolAllowlist { Tools = [new AllowedTool { Key = ToolVerifier.PresentMonKey, FileName = "PresentMon.exe", Sha256 = sha }] };
        var launcher = new FakeLauncher();
        return (new PresentMonFrameCapture(launcher, new ToolVerifier(tools.Path, allowlist)), launcher, tools);
    }

    private static CaptureRequest Request(string runId = "run-01") => new()
    {
        ProcessName = "cs2",
        OutputDirectory = Path.Combine(Path.GetTempPath(), "fragforge-capture-" + Guid.NewGuid().ToString("N")),
        RunId = runId,
        Duration = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public async Task NotApproved_NeverLaunchesTheTool()
    {
        var (capture, launcher, tools) = Create(approvedHash: new string('a', 64));
        using var _ = tools;
        var request = Request();
        var availability = await capture.CheckAvailabilityAsync();
        Assert.False(availability.IsAvailable);
        var series = await capture.CaptureAsync(request);
        Assert.True(series.IsPartial);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public async Task Arguments_ArePassedIndividually_WithOwnSessionName_AndNoStopExistingSession()
    {
        var (capture, launcher, tools) = Create();
        using var _ = tools;
        var request = Request("abc-123");
        launcher.CsvToWrite = "FrameTime,PresentMode\n5,Flip\n";
        await capture.CaptureAsync(request);
        var sent = Assert.Single(launcher.Requests).Arguments;
        Assert.Contains("--process_name", sent);
        Assert.Contains("cs2.exe", sent);
        var sessionIndex = sent.ToList().IndexOf("--session_name");
        Assert.True(sessionIndex >= 0);
        Assert.Equal("FragForge-abc123", sent[sessionIndex + 1]);
        Assert.DoesNotContain(sent, argument => argument.Contains("stop_existing_session", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--terminate_after_timed", sent);
        // Cada argumento llega por separado: ninguna opción viaja pegada a su valor ("--timed 30").
        Assert.DoesNotContain(sent, argument => argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains(' '));
        Assert.Contains("30", sent);
        if (Directory.Exists(request.OutputDirectory)) Directory.Delete(request.OutputDirectory, true);
    }

    [Fact]
    public void SessionName_IsUniquePerRun_AndSanitized()
    {
        var first = PresentMonFrameCapture.SessionName("run-01");
        var second = PresentMonFrameCapture.SessionName("run-02");
        Assert.NotEqual(first, second);
        Assert.StartsWith("FragForge-", first, StringComparison.Ordinal);
        Assert.All(first, character => Assert.True(char.IsLetterOrDigit(character) || character == '-'));
        Assert.True(PresentMonFrameCapture.SessionName(new string('x', 200)).Length <= 58);
    }

    [Fact]
    public async Task Cancellation_KeepsPartialCsv_AndMarksRunInvalid()
    {
        var (capture, launcher, tools) = Create();
        using var _ = tools;
        var request = Request();
        launcher.Cancelled = true;
        var series = await capture.CaptureAsync(request);
        Assert.True(series.IsPartial);
        if (Directory.Exists(request.OutputDirectory)) Directory.Delete(request.OutputDirectory, true);
    }

    [Fact]
    public async Task TooFewFrames_IsReportedInSourceColumns_NotSilentlyAccepted()
    {
        var (capture, launcher, tools) = Create();
        using var _ = tools;
        var request = Request();
        launcher.CsvToWrite = "FrameTime\n5\n5.1\n";
        var series = await capture.CaptureAsync(request);
        Assert.Equal(2, series.FrameCount);
        Assert.Contains("frames=2<min300", series.SourceColumns ?? string.Empty);
        if (Directory.Exists(request.OutputDirectory)) Directory.Delete(request.OutputDirectory, true);
    }

    [Fact]
    public async Task SuccessfulCapture_ParsesTheCsvItProduced()
    {
        var (capture, launcher, tools) = Create();
        using var _ = tools;
        var request = Request();
        launcher.CsvToWrite = string.Join("\n", new[] { "FrameTime,PresentMode" }.Concat(Enumerable.Repeat("5.0,Independent Flip", 400)));
        var series = await capture.CaptureAsync(request);
        Assert.Equal(400, series.FrameCount);
        Assert.False(series.IsPartial);
        if (Directory.Exists(request.OutputDirectory)) Directory.Delete(request.OutputDirectory, true);
    }
}
