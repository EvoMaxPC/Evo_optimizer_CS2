using System.Diagnostics;
using FragForge.Core.Abstractions;
using FragForge.Core.Experiments;
using FragForge.Core.Models;

namespace FragForge.Actions.Measurement;

public sealed record MemoryBandwidthResult(double ReadGigabytesPerSecond, double MedianThreadGbs, double SpreadPercent, int Threads, double Seconds, string Method)
{
    /// <summary>
    /// Confianza del dato como evidencia de canal.
    /// Alta exige AMBAS cosas: medición estable (dispersión baja) y que SMBIOS muestre canales distintos con
    /// capacidad igual. Solo entonces se permite afirmar "Dual". Nunca se declara "Single" sin evidencia.
    /// </summary>
    public string Confidence { get; init; } = "Low";
    public string ChannelVerdict { get; init; } = "Unknown";
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Micro-benchmark de ancho de banda de lectura multihilo.
///
/// Qué mide: recorrido secuencial de un búfer de 256 MiB con 1/2/4 hilos, en GB/s de lectura efectiva.
/// Qué NO mide: latencia, ancho de banda de escritura, ni el modo de entrelazado del controlador de memoria.
///
/// Uso como evidencia: se combina con SMBIOS (DeviceLocator ChannelA/ChannelB y capacidades iguales).
/// El resultado se guarda en facts.json para que el profiler lo lea. NO se sobrescribe
/// <c>MemoryInfo.Channels</c> salvo confianza Alta con ambas señales coincidentes.
/// </summary>
public sealed class MemoryBandwidthExperiment
{
    private const int BufferBytes = 256 * 1024 * 1024;

    public Task<MemoryBandwidthResult> RunAsync(HardwareProfile hardware, CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var threads = new[] { 1, Math.Min(2, Environment.ProcessorCount), Math.Min(4, Environment.ProcessorCount) }.Distinct().ToArray();
        var results = new List<(int Threads, double Gbs)>();
        // Un solo búfer reutilizado: reservar 256 MiB por configuración forzaría al GC de forma innecesaria.
        var buffer = new byte[BufferBytes];
        Array.Fill(buffer, (byte)1);
        var totalSeconds = 0.0;
        foreach (var threadCount in threads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (gbs, seconds) = Measure(buffer, threadCount, cancellationToken);
            totalSeconds += seconds;
            results.Add((threadCount, gbs));
        }

        var spread = results.Count > 1
            ? (results.Max(r => r.Gbs) - results.Min(r => r.Gbs)) / results.Max(r => r.Gbs) * 100.0
            : 0;
        var smbiosChannels = SmbiosEvidence(hardware, notes);
        var stable = spread < 12.0;
        var confidence = smbiosChannels == ChannelSignal.Dual && stable ? "High" : smbiosChannels == ChannelSignal.Unknown ? "Low" : "Medium";
        var verdict = confidence == "High" ? "Dual" : "Unknown";
        if (verdict == "Unknown") notes.Add("No se declara canal: falta evidencia combinada de confianza alta. Un dato de ancho de banda por sí solo no distingue Dual de Single de forma fiable.");
        if (!stable) notes.Add($"Dispersión entre configuraciones de hilos: {spread:F1} % (ruido alto).");

        return Task.FromResult(new MemoryBandwidthResult(
            results.Max(r => r.Gbs),
            Statistics.Median(results.Select(r => r.Gbs).ToArray()),
            spread,
            results.OrderByDescending(r => r.Gbs).First().Threads,
            totalSeconds,
            "Lectura secuencial multihilo sobre 256 MiB; GB/s efectivos")
        {
            Confidence = confidence,
            ChannelVerdict = verdict,
            Notes = notes
        });
    }

    private enum ChannelSignal { Unknown, Single, Dual }

    private static ChannelSignal SmbiosEvidence(HardwareProfile hardware, List<string> notes)
    {
        var modules = hardware.Memory.Modules;
        if (modules.Count == 0)
        {
            notes.Add("SMBIOS no reporta módulos: sin evidencia de canal.");
            return ChannelSignal.Unknown;
        }
        var distinctLocators = modules.Select(m => m.DeviceLocator ?? "").Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sameCapacity = modules.Select(m => m.CapacityBytes).Distinct().Count() == 1;
        var channelsA = modules.Any(m => (m.DeviceLocator ?? "").Contains("ChannelA", StringComparison.OrdinalIgnoreCase));
        var channelsB = modules.Any(m => (m.DeviceLocator ?? "").Contains("ChannelB", StringComparison.OrdinalIgnoreCase));
        if (modules.Count >= 2 && sameCapacity && (channelsA && channelsB || distinctLocators.Length >= 2))
        {
            notes.Add($"SMBIOS: {modules.Count} módulos, capacidades iguales, localizadores distintos.");
            return ChannelSignal.Dual;
        }
        notes.Add($"SMBIOS: {modules.Count} módulos; no cumple la condición Dual (capacidad igual + localizadores distintos).");
        return ChannelSignal.Unknown;
    }

    private static (double Gbs, double Seconds) Measure(byte[] buffer, int threadCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chunk = BufferBytes / threadCount;
        var stopwatch = Stopwatch.StartNew();
        var workers = new Task[threadCount];
        var totals = new long[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var start = i * chunk;
            var end = i == threadCount - 1 ? BufferBytes : start + chunk;
            var slot = i;
            workers[i] = Task.Run(() =>
            {
                long sum = 0;
                for (var index = start; index < end; index++) sum += buffer[index];
                totals[slot] = sum;
            }, cancellationToken);
        }
        Task.WaitAll(workers, cancellationToken);
        stopwatch.Stop();
        var bytes = (double)BufferBytes;
        var seconds = stopwatch.Elapsed.TotalSeconds;
        // Evita que el JIT elimine la lectura.
        GC.KeepAlive(totals);
        return (seconds <= 0 ? 0 : bytes / seconds / 1024 / 1024 / 1024, seconds);
    }

    /// <summary>Persiste el hecho en facts.json con método y fecha, para que el profiler lo use como evidencia.</summary>
    public static async Task RecordAsync(IFactStore store, HardwareProfile hardware, MemoryBandwidthResult result, CancellationToken cancellationToken = default)
    {
        await store.WriteAsync(new MeasuredFact
        {
            Key = "memoryBandwidth",
            Value = FormattableString.Invariant($"{result.ReadGigabytesPerSecond:F1} GB/s"),
            Confidence = result.Confidence,
            Method = result.Method,
            Note = FormattableString.Invariant($"canal={result.ChannelVerdict}; hilos={result.Threads}; dispersión={result.SpreadPercent:F1}%; módulos={hardware.Memory.Modules.Count}; total={hardware.Memory.TotalBytes / (1024.0 * 1024.0 * 1024.0):F1} GiB")
        }, cancellationToken);
    }
}
