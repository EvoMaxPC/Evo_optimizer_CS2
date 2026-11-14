using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Experiments;
using FragForge.Core.Models;

namespace FragForge.Actions.Inventory;

/// <summary>
/// Valores por defecto de la huella. Vive FUERA de <see cref="FootprintProbe"/> a propósito:
/// <c>FootprintProbe</c> está anotada <c>[SupportedOSPlatform("windows")]</c>, así que TODOS sus
/// miembros —una constante incluida— heredan esa restricción. <see cref="UnsupportedFootprintProbe"/>
/// es justamente la variante para plataformas SIN Windows, y usaba
/// <c>FootprintProbe.DefaultSeconds</c> como valor por defecto: eso es CA1416 en el sitio de llamada.
/// Este contenedor no tiene restricción, así que ambas variantes pueden usarlo.
/// </summary>
public static class FootprintDefaults
{
    /// <summary>Duración por defecto de la medición, en segundos.</summary>
    public const double Seconds = 60.0;
}

public sealed record NamedProcess(string Name, int ProcessId);

public sealed record FootprintSample
{
    public double Seconds { get; init; }
    public int ProcessCount { get; init; }
    public long TotalWorkingSetBytes { get; init; }
    public double? CpuPercent { get; init; }
    public double? DpcPercent { get; init; }
    public double? InterruptsPerSecond { get; init; }
    /// <summary>Top por CPU del proceso que MÁS CPU consumió desde la muestra anterior. Solo nombre y PID.</summary>
    public IReadOnlyList<NamedProcess> TopByCpu { get; init; } = [];
    public IReadOnlyList<NamedProcess> TopByWorkingSet { get; init; } = [];
}

/// <summary>
/// Huella del sistema EN REPOSO.
///
/// Mide durante <paramref name="seconds"/> (60 por defecto) a 1 Hz: número de procesos, working set
/// total, CPU %, DPC % e interrupciones/s desde PDH, más el top 10 por CPU y por working set.
///
/// Privacidad: de cada proceso solo se guarda NOMBRE y PID. Nunca línea de comandos, ruta ni usuario.
/// Ausencia de un contador PDH se reporta como nota: no se rellena con 0.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FootprintProbe(ISensorSampler sampler)
{
    public async Task<BenchmarkSession> RunAsync(string label = "footprint", double seconds = FootprintDefaults.Seconds, CancellationToken cancellationToken = default)
    {
        var duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 600));
        var started = DateTimeOffset.UtcNow;
        var samples = new List<FootprintSample>();
        var availability = await sampler.CheckAsync(cancellationToken);

        Dictionary<int, TimeSpan> previousCpu = SnapshotCpu();
        var previousAt = Stopwatch.GetTimestamp();

        while (DateTimeOffset.UtcNow - started < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(previousAt, now).TotalSeconds;
            var currentCpu = SnapshotCpu();

            var cpuByPid = new List<(string Name, int Pid, double Percent)>();
            var workingSetByPid = new List<(string Name, int Pid, long Bytes)>();
            var processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    workingSetByPid.Add((process.ProcessName, process.Id, process.WorkingSet64));
                    if (previousCpu.TryGetValue(process.Id, out var beforeCpu) && currentCpu.TryGetValue(process.Id, out var afterCpu) && elapsed > 0)
                    {
                        var delta = (afterCpu - beforeCpu).TotalMilliseconds;
                        if (delta >= 0)
                            cpuByPid.Add((process.ProcessName, process.Id, delta / (elapsed * 1000.0) / Math.Max(1, Environment.ProcessorCount) * 100.0));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }

            previousCpu = currentCpu;
            previousAt = now;

            var sensor = await sampler.SampleAsync(new SensorSamplingRequest
            {
                OutputDirectory = Path.GetTempPath(),
                RunId = "footprint-" + Guid.NewGuid().ToString("N"),
                Duration = TimeSpan.FromSeconds(1.2),
                Interval = TimeSpan.FromSeconds(1)
            }, cancellationToken);
            var single = sensor.Samples.LastOrDefault();

            samples.Add(new FootprintSample
            {
                Seconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
                ProcessCount = processes.Length,
                TotalWorkingSetBytes = workingSetByPid.Sum(p => p.Bytes),
                CpuPercent = single?.CpuUtilityPercent,
                DpcPercent = single?.DpcTimePercent,
                InterruptsPerSecond = single?.InterruptsPerSecond,
                TopByCpu = cpuByPid.OrderByDescending(p => p.Percent).Take(10).Select(p => new NamedProcess(p.Name, p.Pid)).ToArray(),
                TopByWorkingSet = workingSetByPid.OrderByDescending(p => p.Bytes).Take(10).Select(p => new NamedProcess(p.Name, p.Pid)).ToArray()
            });
        }

        return new BenchmarkSession(
            label + "-" + started.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            label,
            BuildMeasurements(samples, availability),
            []);
    }

    private static Dictionary<int, TimeSpan> SnapshotCpu()
    {
        var snapshot = new Dictionary<int, TimeSpan>();
        foreach (var process in Process.GetProcesses())
        {
            try { snapshot[process.Id] = process.TotalProcessorTime; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally { process.Dispose(); }
        }
        return snapshot;
    }

    private static IReadOnlyList<BenchmarkMeasurement> BuildMeasurements(IReadOnlyList<FootprintSample> samples, SensorAvailability availability)
    {
        if (samples.Count == 0) return [];
        var measurements = new List<BenchmarkMeasurement>();
        void Add(string metric, double? value, string unit, IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (value is null || double.IsNaN(value.Value)) return;
            measurements.Add(new BenchmarkMeasurement(metric, value.Value, unit, metadata ?? new Dictionary<string, string>()));
        }

        Add("processCount", samples.Average(s => (double)s.ProcessCount), "procesos");
        Add("workingSetTotalMib", samples.Average(s => (double)s.TotalWorkingSetBytes) / (1024 * 1024), "MiB");
        Add("cpuPercent", samples.Average(s => s.CpuPercent), "%");
        Add("dpcPercent", samples.Average(s => s.DpcPercent), "%");
        Add("interruptsPerSecond", samples.Average(s => s.InterruptsPerSecond), "1/s");
        Add("sampleCount", samples.Count, "muestras", new Dictionary<string, string> { ["seconds"] = samples[^1].Seconds.ToString("0", CultureInfo.InvariantCulture) });

        var topCpu = samples.SelectMany(s => s.TopByCpu).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key).ToArray();
        if (topCpu.Length > 0)
            measurements.Add(new BenchmarkMeasurement("topCpuProcessNames", 0, "", new Dictionary<string, string> { ["names"] = string.Join(",", topCpu) }));

        var topMemory = samples.SelectMany(s => s.TopByWorkingSet).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key).ToArray();
        if (topMemory.Length > 0)
            measurements.Add(new BenchmarkMeasurement("topMemoryProcessNames", 0, "", new Dictionary<string, string> { ["names"] = string.Join(",", topMemory) }));

        if (samples.All(s => s.CpuPercent is null))
            measurements.Add(new BenchmarkMeasurement("cpuUnavailable", 0, "", new Dictionary<string, string>
            {
                ["note"] = "Contadores PDH de CPU/DPC no disponibles en este equipo; no se inventa un valor.",
                ["availableCounters"] = string.Join(",", availability.AvailableCounters)
            }));
        return measurements;
    }
}

/// <summary>Huella en plataformas sin Windows: sesión vacía con nota, nunca ceros.</summary>
public sealed class UnsupportedFootprintProbe
{
    public Task<BenchmarkSession> RunAsync(string label = "footprint", double seconds = FootprintDefaults.Seconds, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BenchmarkSession(label + "-unsupported", label, [], []));
}
