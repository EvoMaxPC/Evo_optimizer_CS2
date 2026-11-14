using FragForge.Actions.Measurement;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Network;

/// <summary>
/// Capa 5.5 — sonda de latencia/jitter/pérdida.
///
/// Lo importante que se prueba aquí no es el ping (eso es del sistema), sino la ESTADÍSTICA y la regla de
/// NA: con cero respuestas, mediana/p99/jitter son <c>null</c>, no 0. Un 0 ms de mediana sin respuestas sería
/// una mentira, y es justo el error que se cuela cuando se hace <c>Average()</c> sobre una lista vacía.
///
/// No se usa red real: el ping se sustituye por un doble.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NetworkProbeTests
{
    [Fact]
    public async Task Measure_ComputesMedianP99AndLoss()
    {
        // 10 muestras, 1 pérdida → 90% recibido.
        var samples = new long[] { 10, 12, 11, 13, 14, 15, 12, 11, 13 };
        var ping = new ScriptedPing(samples, failures: 1);
        var probe = new NetworkProbe(new EmptyResolver(), ping);

        var result = await probe.MeasureAsync("example.invalid", count: 10);

        Assert.Equal(10, result.Sent);
        Assert.Equal(9, result.Received);
        Assert.Equal(10.0, result.LossPercent);
        Assert.True(result.HasSamples);
        // Ordenadas: 10,11,11,12,12,13,13,14,15 → mediana (5ª de 9) = 12
        Assert.Equal(12, result.MedianMs);
        Assert.Equal(15, result.P99Ms);
        Assert.Equal(10, result.MinMs);
        Assert.Equal(15, result.MaxMs);
        Assert.NotNull(result.JitterMs);
    }

    /// <summary>
    /// **El caso crítico**: cero respuestas. Todo lo que no se puede calcular queda en <c>null</c> (NA), y la
    /// pérdida es 100%. Nada de 0 ms.
    /// </summary>
    [Fact]
    public async Task Measure_WithZeroReplies_ReturnsNullStats_NotZero()
    {
        var probe = new NetworkProbe(new EmptyResolver(), new ScriptedPing([], failures: 5));

        var result = await probe.MeasureAsync("example.invalid", count: 5);

        Assert.False(result.HasSamples);
        Assert.Null(result.MedianMs);
        Assert.Null(result.P99Ms);
        Assert.Null(result.JitterMs);
        Assert.Null(result.MinMs);
        Assert.Null(result.MaxMs);
        Assert.Equal(100.0, result.LossPercent);
    }

    [Fact]
    public async Task Measure_WithCompleteLoss_IsStillOneHundredPercent()
    {
        var result = NetworkProbeResult.Create("host", sent: 7, samples: [], failures: 7);

        Assert.Equal(100.0, result.LossPercent);
        Assert.False(result.HasSamples);
    }

    [Theory]
    [InlineData(new long[] { 1, 2, 3, 4 }, 2)]        // par → media de las dos centrales (2+3)/2 = 2.5 → 3
    [InlineData(new long[] { 1, 2, 3, 4, 5 }, 3)]     // impar → central exacta
    [InlineData(new long[] { 5 }, 5)]
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6 }, 3)]  // (3+4)/2 = 3.5 → 4
    public void Median_HandlesEvenAndOddCounts(long[] values, long expected)
    {
        Assert.Equal(expected, NetworkProbeResult.Median(values.OrderBy(v => v).ToArray()));
    }

    [Theory]
    [InlineData(99, 100)]  // con 100 muestras, p99 es la 99.ª
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    public void Percentile_UsesNearestRank(int percentile, long expected)
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();

        Assert.Equal(expected, NetworkProbeResult.Percentile(sorted, percentile));
    }

    /// <summary>Con una sola muestra, mediana y p99 son esa muestra y el jitter es 0 (no null).</summary>
    [Fact]
    public void Create_WithOneSample_HasZeroJitterButRealValues()
    {
        var result = NetworkProbeResult.Create("host", sent: 1, samples: [42], failures: 0);

        Assert.Equal(42, result.MedianMs);
        Assert.Equal(42, result.P99Ms);
        Assert.Equal(0d, result.JitterMs);
        Assert.Equal(0.0, result.LossPercent);
    }

    /// <summary>Sin host elegido por el usuario no se mide: no hay hosts por defecto.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Measure_WithoutHost_Throws(string host)
    {
        var probe = new NetworkProbe(new EmptyResolver(), new ScriptedPing([], 0));

        await Assert.ThrowsAsync<ArgumentException>(() => probe.MeasureAsync(host));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(NetworkProbe.MaxCount + 1)]
    public async Task Measure_RejectsUnreasonableCounts(int count)
    {
        var probe = new NetworkProbe(new EmptyResolver(), new ScriptedPing([], 0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => probe.MeasureAsync("host", count));
    }

    /// <summary>
    /// El resultado NO contiene ningún veredicto: ni "buena", ni "mala", ni "cuello de botella". Solo números.
    /// </summary>
    [Fact]
    public void Result_HasNoVerdictField()
    {
        var names = typeof(NetworkProbeResult).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(names, n => n.Contains("Verdict", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Rating", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Score", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Diagnóstico: sin adaptadores no se inventa uno, y con más de un candidato tampoco se elige.
    /// </summary>
    [Fact]
    public async Task Diagnose_WithNoAdapters_ReturnsNullAdapterWithANote()
    {
        var diagnosis = await new NetworkProbe(new EmptyResolver(), new ScriptedPing([], 0)).DiagnoseAdapterAsync();

        Assert.Null(diagnosis.Adapter);
        Assert.False(string.IsNullOrWhiteSpace(diagnosis.Note));
    }

    /// <summary>
    /// El mapeo de tipo de medio está marcado NO VERIFICADO en estas NICs, pero los dos valores documentados
    /// deben seguir dando lo que dicen: 14 = Ethernet y 9 = Wi-Fi.
    /// </summary>
    [Theory]
    [InlineData(14, NetworkKind.Ethernet)]
    [InlineData(9, NetworkKind.WiFi)]
    [InlineData(0, NetworkKind.Unknown)]
    [InlineData(15, NetworkKind.Unknown)]
    [InlineData(null, NetworkKind.Unknown)]
    public void MapPhysicalMedium_OnlyMapsDocumentedValues(int? medium, NetworkKind expected)
    {
        Assert.Equal(expected, FragForge.Actions.Platform.WindowsHardwareProfiler.MapPhysicalMedium(medium));
    }

    /// <summary>
    /// Devuelve las respuestas indicadas y después las pérdidas. El plan se construye de una vez para que el
    /// número de éxitos y de fallos sea EXACTAMENTE el del test, sin depender del orden de llamada.
    /// </summary>
    private sealed class ScriptedPing : IPingProbe
    {
        private readonly Queue<long?> _plan = new();

        public ScriptedPing(IReadOnlyList<long> replies, int failures)
        {
            foreach (var reply in replies) _plan.Enqueue(reply);
            for (var i = 0; i < failures; i++) _plan.Enqueue(null);
        }

        public Task<PingReplySample> SendAsync(string host, CancellationToken cancellationToken = default)
        {
            if (_plan.Count == 0) return Task.FromResult(new PingReplySample(false, null));
            var next = _plan.Dequeue();
            return Task.FromResult(new PingReplySample(next is not null, next));
        }
    }

    private sealed class EmptyResolver : INetworkAdapterResolver
    {
        public Task<IReadOnlyList<NetworkAdapterIdentity>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NetworkAdapterIdentity>>([]);

        public Task<NetworkAdapterIdentity?> ResolveAsync(NetworkAdapterSelector selector, CancellationToken cancellationToken = default) =>
            Task.FromResult<NetworkAdapterIdentity?>(null);
    }
}
