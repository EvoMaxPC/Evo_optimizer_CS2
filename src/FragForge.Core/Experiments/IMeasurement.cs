using FragForge.Core.Models;

namespace FragForge.Core.Experiments;

/// <summary>
/// Fuente de series de frames (típicamente PresentMon). La implementación Windows vive en
/// <c>FragForge.Actions/Measurement</c>; en otras plataformas existe una versión Unsupported.
/// </summary>
public interface IFrameCapture
{
    string Name { get; }
    /// <summary>Indica si la captura está disponible (herramienta presente, hash válido, plataforma soportada).</summary>
    Task<CaptureAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Captura una corrida. Escribe el CSV crudo en <paramref name="outputDirectory"/> y devuelve la serie.
    /// Si se cancela, la serie vuelve con <see cref="FrameSeries.IsPartial"/> = true.
    /// </summary>
    Task<FrameSeries> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default);
    /// <summary>Parsea un CSV ya existente por NOMBRE de columna, tolerante a PresentMon 1.x y 2.x.</summary>
    FrameSeries ParseCsv(string path);
}

public sealed record CaptureRequest
{
    public required string ProcessName { get; init; }
    public required string OutputDirectory { get; init; }
    public required string RunId { get; init; }
    public required TimeSpan Duration { get; init; }
    public IProgress<EngineProgress>? Progress { get; init; }
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MinimumFrames { get; init; } = 300;
}

public sealed record CaptureAvailability(bool IsAvailable, string Reason, string? ToolPath = null, string? Sha256 = null, bool IsPlaceholderHash = false)
{
    public static CaptureAvailability Unsupported(string reason) => new(false, reason);
}

/// <summary>
/// Muestreador de sensores del sistema (contadores PDH). Devuelve Unknown con nota cuando un contador no existe;
/// nunca devuelve 0 como sustituto de un dato ausente.
/// </summary>
public interface ISensorSampler
{
    Task<SensorAvailability> CheckAsync(CancellationToken cancellationToken = default);
    Task<SensorSeries> SampleAsync(SensorSamplingRequest request, CancellationToken cancellationToken = default);
}

public sealed record SensorSamplingRequest
{
    public required string OutputDirectory { get; init; }
    public required string RunId { get; init; }
    public required TimeSpan Duration { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    public IProgress<EngineProgress>? Progress { get; init; }
}

public sealed record SensorAvailability(IReadOnlyList<string> AvailableCounters, IReadOnlyList<string> MissingCounters, string Platform)
{
    public bool AnyAvailable => AvailableCounters.Count > 0;
}

/// <summary>Muestra de un instante. Todos los campos son nullable: ausente ≠ cero.</summary>
public sealed record SensorSample
{
    public double Seconds { get; init; }
    public double? CpuUtilityPercent { get; init; }
    public double? CpuPerformancePercent { get; init; }
    public double? CpuFrequencyMhz { get; init; }
    public double? CpuMaxFrequencyPercent { get; init; }
    public string? PerformanceLimitFlags { get; init; }
    public double? DpcTimePercent { get; init; }
    public double? InterruptTimePercent { get; init; }
    public double? InterruptsPerSecond { get; init; }
    public double? GpuEnginePercent { get; init; }
    public double? GpuSharedMemoryMb { get; init; }
    public double? PackagePowerWatts { get; init; }
    public double? ThermalZoneCelsius { get; init; }
    public string? ThermalZoneNote { get; init; }
    public double? SystemCpuPercent { get; init; }
}

public sealed record SensorSeries
{
    public IReadOnlyList<SensorSample> Samples { get; init; } = [];
    public IReadOnlyList<string> MissingCounters { get; init; } = [];
    public string? CsvPath { get; init; }
    public bool IsPartial { get; init; }
    public double? MedianCpuFrequencyMhz => Median(s => s.CpuFrequencyMhz);
    public double? MedianPackagePowerWatts => Median(s => s.PackagePowerWatts);
    public double? MedianCpuUtilityPercent => Median(s => s.CpuUtilityPercent);

    private double? Median(Func<SensorSample, double?> selector)
    {
        var values = Samples.Select(selector).Where(v => v is not null).Select(v => v!.Value).ToArray();
        return values.Length == 0 ? null : Statistics.Median(values);
    }
}

/// <summary>Resultado del clasificador de cuello de botella.</summary>
public enum BottleneckKind { Unknown, CpuBound, GpuBound, Mixed, PowerOrThermalLimited }

public sealed record BottleneckAssessment(BottleneckKind Kind, string Confidence, string Reason, IReadOnlyDictionary<string, double> Evidence)
{
    public static BottleneckAssessment Unknown(string reason) => new(BottleneckKind.Unknown, "Unknown", reason, new Dictionary<string, double>());
}

/// <summary>Resultado del detector de throttling.</summary>
public sealed record ThrottleAssessment(bool Suspected, string Confidence, IReadOnlyList<string> Evidence, double? FrameTimeDriftPct, double? FrequencyDeficitPct);

/// <summary>
/// Lectura de un hecho medido en esta máquina. Lo usan <c>mem-bench</c> y cualquier medición que deba
/// persistirse en facts.json para que el profiler la consuma como evidencia.
/// </summary>
public interface IFactStore
{
    Task<FactsDocument> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(MeasuredFact fact, CancellationToken cancellationToken = default);
}
