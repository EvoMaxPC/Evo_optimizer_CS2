using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using FragForge.Core.Experiments;
using FragForge.Core.Persistence;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Muestreo de sensores por contadores PDH (<c>PerformanceCounter</c>).
///
/// Todos los contadores se DESCUBREN en tiempo de ejecución: si un contador no existe en este equipo
/// se reporta como ausente y la métrica queda en null. Cero es un valor válido de medición; la ausencia no.
///
/// Limitaciones documentadas y aceptadas:
/// <list type="bullet">
/// <item>La temperatura del paquete de CPU no es legible sin un driver. PawnIO está presente en el equipo y
/// NO se usa. <c>cpuPackageTemp</c> será normalmente Unknown.</item>
/// <item>"Thermal Zone Information" es un proxy ACPI de confianza baja: se reporta como tal, nunca como temperatura de núcleo.</item>
/// <item>"Energy Meter" (RAPL) solo existe si Windows lo expone; en muchos equipos de escritorio no está.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PerformanceCounterSensorSampler : ISensorSampler
{
    private static readonly (string Category, string Counter, string Field)[] Candidates =
    [
        ("Processor Information", "% Processor Utility", "cpuUtility"),
        ("Processor Information", "% Processor Performance", "cpuPerformance"),
        ("Processor Information", "Processor Frequency", "cpuFrequency"),
        ("Processor Information", "% of Maximum Frequency", "cpuMaxFrequency"),
        ("Processor Information", "Performance Limit Flags", "performanceLimitFlags"),
        ("Processor Information", "% DPC Time", "dpcTime"),
        ("Processor Information", "% Interrupt Time", "interruptTime"),
        ("Processor Information", "DPCs Queued/sec", "dpcsQueued"),
        ("Processor Information", "Interrupts/sec", "interrupts"),
        ("GPU Engine", "Utilization Percentage", "gpuEngine"),
        ("GPU Adapter Memory", "Dedicated Usage", "gpuMemory"),
        ("Energy Meter", "Power", "packagePower"),
        ("Thermal Zone Information", "Temperature", "thermalZone"),
        ("System", "% Processor Time", "systemCpu")
    ];

    public Task<SensorAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        var available = new List<string>();
        var missing = new List<string>();
        foreach (var (category, counter, field) in Candidates)
        {
            try
            {
                using var performanceCounter = new PerformanceCounter(category, counter, "_Total", readOnly: true);
                _ = performanceCounter.CounterType;
                available.Add(field);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                missing.Add(field);
            }
        }
        return Task.FromResult(new SensorAvailability(available, missing, "windows-pdh"));
    }

    public async Task<SensorSeries> SampleAsync(SensorSamplingRequest request, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        var csvPath = Path.Combine(request.OutputDirectory, request.RunId + "-sensors.csv");
        var samples = new List<SensorSample>();
        var missing = new List<string>();
        var counters = new List<(PerformanceCounter Counter, string Field)>();
        foreach (var (category, counter, field) in Candidates)
        {
            try
            {
                var performanceCounter = new PerformanceCounter(category, counter, "_Total", readOnly: true);
                _ = performanceCounter.NextValue(); // primera lectura siempre 0 en PDH
                counters.Add((performanceCounter, field));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                missing.Add(field);
            }
        }

        var started = DateTimeOffset.UtcNow;
        var isPartial = false;
        try
        {
            while (DateTimeOffset.UtcNow - started < request.Duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(request.Interval, cancellationToken);
                var seconds = (DateTimeOffset.UtcNow - started).TotalSeconds;
                samples.Add(ReadSample(seconds, counters, missing));
            }
        }
        catch (OperationCanceledException) { isPartial = true; }
        finally { foreach (var (counter, _) in counters) counter.Dispose(); }

        await WriteCsvAsync(csvPath, samples, cancellationToken);
        return new SensorSeries { Samples = samples, MissingCounters = missing.Distinct().ToArray(), CsvPath = csvPath, IsPartial = isPartial };
    }

    private static SensorSample ReadSample(double seconds, List<(PerformanceCounter Counter, string Field)> counters, List<string> missing)
    {
        double? Get(string field)
        {
            var entry = counters.FirstOrDefault(c => c.Field == field);
            if (entry.Counter is null) return null;
            try { var value = entry.Counter.NextValue(); return double.IsNaN(value) || double.IsInfinity(value) ? null : value; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { missing.Add(field); return null; }
        }
        return new SensorSample
        {
            Seconds = seconds,
            CpuUtilityPercent = Get("cpuUtility"),
            CpuPerformancePercent = Get("cpuPerformance"),
            CpuFrequencyMhz = Get("cpuFrequency"),
            CpuMaxFrequencyPercent = Get("cpuMaxFrequency"),
            PerformanceLimitFlags = Get("performanceLimitFlags")?.ToString("0", CultureInfo.InvariantCulture),
            DpcTimePercent = Get("dpcTime"),
            InterruptTimePercent = Get("interruptTime"),
            InterruptsPerSecond = Get("interrupts"),
            GpuEnginePercent = Get("gpuEngine"),
            GpuSharedMemoryMb = Get("gpuMemory"),
            PackagePowerWatts = Get("packagePower"),
            ThermalZoneCelsius = Get("thermalZone"),
            ThermalZoneNote = Get("thermalZone") is null ? "Sin zona térmica expuesta por ACPI." : "Proxy ACPI de confianza baja; no es temperatura de núcleo.",
            SystemCpuPercent = Get("systemCpu")
        };
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<SensorSample> samples, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder("seconds,cpuUtilityPct,cpuPerformancePct,cpuFrequencyMhz,cpuMaxFrequencyPct,performanceLimitFlags,dpcTimePct,interruptTimePct,interruptsPerSec,gpuEnginePct,gpuSharedMemoryMb,packagePowerW,thermalZoneC\n");
        foreach (var sample in samples)
        {
            builder.Append(Format(sample.Seconds)).Append(',')
                .Append(Format(sample.CpuUtilityPercent)).Append(',')
                .Append(Format(sample.CpuPerformancePercent)).Append(',')
                .Append(Format(sample.CpuFrequencyMhz)).Append(',')
                .Append(Format(sample.CpuMaxFrequencyPercent)).Append(',')
                .Append(sample.PerformanceLimitFlags is null ? "" : "\"" + sample.PerformanceLimitFlags + "\"").Append(',')
                .Append(Format(sample.DpcTimePercent)).Append(',')
                .Append(Format(sample.InterruptTimePercent)).Append(',')
                .Append(Format(sample.InterruptsPerSecond)).Append(',')
                .Append(Format(sample.GpuEnginePercent)).Append(',')
                .Append(Format(sample.GpuSharedMemoryMb)).Append(',')
                .Append(Format(sample.PackagePowerWatts)).Append(',')
                .Append(Format(sample.ThermalZoneCelsius)).Append('\n');
        }
        await AtomicFile.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        static string Format(double? value) => value is null ? "" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public sealed class UnsupportedSensorSampler : ISensorSampler
{
    private static readonly string[] Candidates = ["cpuUtility", "cpuFrequency", "gpuEngine", "packagePower", "thermalZone"];
    public Task<SensorAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SensorAvailability([], Candidates, "unsupported"));
    public Task<SensorSeries> SampleAsync(SensorSamplingRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorSeries { MissingCounters = Candidates, IsPartial = false });
}
