using FragForge.Core.Experiments;
using Xunit;

namespace FragForge.Tests.Experiments;

public sealed class SourceHygieneTests
{
    private static readonly string[] ForbiddenProcessApis =
    [
        "OpenProcess(",
        "Process.GetProcessById",
        ".MainModule",
        ".Modules[",
        "ReadProcessMemory",
        "WriteProcessMemory",
        "CreateRemoteThread",
        "VirtualAllocEx"
    ];

    [Fact]
    public void NoProductCodeTouchesCs2ProcessMemory()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var token in ForbiddenProcessApis)
                if (text.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(root, file)} contiene '{token}'");
        }
        Assert.True(offenders.Count == 0, "Acceso a procesos prohibido por anti-cheat: " + string.Join("; ", offenders));
    }

    [Fact]
    public void NoProductCodeBuildsCommandLineStrings()
    {
        var root = FindRepositoryRoot();
        var offenders = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("Arguments = \"", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();
        Assert.True(offenders.Length == 0, "Los argumentos se pasan con ArgumentList, nunca concatenados: " + string.Join(", ", offenders));
    }

    [Fact]
    public void PresentMonIsNeverInvokedWithStopExistingSession()
    {
        var root = FindRepositoryRoot();
        var offenders = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("stop_existing_session", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();
        Assert.True(offenders.Length == 0, "PresentMon no debe cerrar sesiones ajenas: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ProductCodeHasNoDownloadCalls()
    {
        var root = FindRepositoryRoot();
        var forbidden = new[] { "WebClient", "HttpClient", "DownloadFile", "DownloadStringAsync", "curl ", "Invoke-WebRequest" };
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
                if (text.Contains(token, StringComparison.Ordinal)) offenders.Add($"{Path.GetRelativePath(root, file)}: {token}");
        }
        Assert.True(offenders.Count == 0, "FragForge no descarga binarios: " + string.Join("; ", offenders));
    }

    [Fact]
    public void MeasurementCodeIsBehindInterfaces_AndHasUnsupportedFallbacks()
    {
        var root = FindRepositoryRoot();
        var measurement = Path.Combine(root, "src", "FragForge.Actions", "Measurement");
        var files = Directory.GetFiles(measurement, "*.cs");
        Assert.NotEmpty(files);
        var text = string.Concat(files.Select(File.ReadAllText));
        Assert.Contains("UnsupportedFrameCapture", text, StringComparison.Ordinal);
        Assert.Contains("UnsupportedSensorSampler", text, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FragForge.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}

public sealed class BottleneckAndThrottleTests
{
    [Fact]
    public void Bottleneck_WithoutCpuOrGpuColumns_IsUnknown()
    {
        var assessment = BottleneckClassifier.Classify(new RunMetrics { AvgFps = 200, FrameTimeP99 = 6 });
        Assert.Equal(BottleneckKind.Unknown, assessment.Kind);
        Assert.Contains("no se puede clasificar", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bottleneck_HighCpu_IsCpuBound_WithSharedPowerNote()
    {
        var assessment = BottleneckClassifier.Classify(new RunMetrics { CpuBusyP95 = 95, GpuBusyP95 = 40, AvgFps = 120 });
        Assert.Equal(BottleneckKind.CpuBound, assessment.Kind);
        Assert.Contains("28 W", assessment.Reason);
    }

    [Fact]
    public void Bottleneck_BothHigh_IsMixed()
    {
        Assert.Equal(BottleneckKind.Mixed, BottleneckClassifier.Classify(new RunMetrics { CpuBusyP95 = 90, GpuBusyP95 = 92 }).Kind);
    }

    [Fact]
    public void Bottleneck_CpuOnly_StaysUnknownBecauseGpuIsNotDiscarded()
    {
        var assessment = BottleneckClassifier.Classify(new RunMetrics { CpuBusyP95 = 40 });
        Assert.Equal(BottleneckKind.Unknown, assessment.Kind);
        Assert.Equal("Low", assessment.Confidence);
    }

    [Fact]
    public void Throttle_FrequencyDeficitAndDrift_AreBothReported()
    {
        var sensors = new SensorSeries
        {
            Samples = Enumerable.Range(0, 20).Select(i => new SensorSample
            {
                CpuFrequencyMhz = 1800,
                CpuMaxFrequencyPercent = 60,
                PerformanceLimitFlags = i > 10 ? "Thermal" : "0"
            }).ToArray()
        };
        var frameTimes = Enumerable.Repeat(5.0, 600).Concat(Enumerable.Repeat(7.0, 600)).ToArray();
        var assessment = ThrottleDetector.Detect(new FrameSeries { FrameTimesMs = frameTimes }, sensors);
        Assert.True(assessment.Suspected);
        Assert.NotNull(assessment.FrequencyDeficitPct);
        Assert.True(assessment.FrequencyDeficitPct > 15);
        Assert.NotNull(assessment.FrameTimeDriftPct);
        Assert.True(assessment.FrameTimeDriftPct > 8);
        Assert.Contains(assessment.Evidence, e => e.Contains("Thermal", StringComparison.Ordinal));
    }

    [Fact]
    public void Throttle_StableSystem_IsNotSuspected()
    {
        var sensors = new SensorSeries
        {
            Samples = Enumerable.Range(0, 20).Select(_ => new SensorSample { CpuFrequencyMhz = 3000, CpuMaxFrequencyPercent = 100, PerformanceLimitFlags = "0" }).ToArray()
        };
        var assessment = ThrottleDetector.Detect(new FrameSeries { FrameTimesMs = Enumerable.Repeat(5.0, 900).ToArray() }, sensors);
        Assert.False(assessment.Suspected);
    }

    [Fact]
    public void Throttle_WithoutSensors_ReportsWhatItCannotSee()
    {
        var assessment = ThrottleDetector.Detect(new FrameSeries { FrameTimesMs = Enumerable.Repeat(5.0, 900).ToArray() }, null);
        Assert.Contains(assessment.Evidence, e => e.Contains("Sin datos de sensores", StringComparison.Ordinal));
        Assert.Null(assessment.FrequencyDeficitPct);
    }
}
