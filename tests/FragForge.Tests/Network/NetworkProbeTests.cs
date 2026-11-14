using FragForge.Actions.Measurement;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
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

    /// <summary>
    /// El caso que el compilador destapo (CS1503): una respuesta con Success=true pero RoundtripTimeMs=null.
    /// PingReplySample declara el RTT como long? a proposito —null = no se sabe—, asi que esa muestra NO
    /// puede entrar en la estadistica convertida a 0: contaria como un ping de 0 ms y falsearia mediana, p99
    /// y jitter. Tiene que contarse como fallo (NA), nunca como 0 (R-L).
    /// </summary>
    [Fact]
    public async Task Measure_WithSuccessButUnknownRoundtrip_CountsItAsFailure_NotAsZero()
    {
        var probe = new NetworkProbe(new EmptyResolver(), new SuccessWithoutRoundtripPing());

        var result = await probe.MeasureAsync("example.invalid", count: 3);

        Assert.False(result.HasSamples);
        // Received cuenta MUESTRAS UTILES (samples.Count, NetworkProbe.cs:237), no respuestas ICMP. Un reply
        // con exito pero sin RTT no es una muestra: entra por la rama de fallo. Si aqui apareciera un 3,
        // significaria que el RTT nulo se convirtio en 0.
        Assert.Equal(0, result.Received);
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

    // Los valores esperados que estaban escritos NO coincidian con el comentario de al lado ni con la
    // implementacion: Median redondea con MidpointRounding.AwayFromZero.
    //
    // DEFINICION (la que implementa NetworkProbe.cs:261-266):
    //   - numero IMPAR de muestras: el elemento central, sorted[n/2].
    //   - numero PAR: el PROMEDIO de las dos centrales, redondeado a long con AwayFromZero
    //     (2.5 -> 3, 3.5 -> 4). No se elige "el inferior": se promedia y se redondea al alza en .5.
    //   - devuelve `long`, nunca null. Con 0 muestras NO se llama: lanzaria (ver el test de abajo).
    [Theory]
    [InlineData(new long[] { 7 }, 7)]                 // impar de 1: el propio valor
    [InlineData(new long[] { 1, 2, 3 }, 2)]           // impar: central exacta
    [InlineData(new long[] { 1, 2, 3, 4, 5 }, 3)]     // impar: central exacta
    [InlineData(new long[] { 1, 2, 3, 4 }, 3)]        // par → (2+3)/2 = 2.5 → 3  (AwayFromZero)
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6 }, 4)]  // par → (3+4)/2 = 3.5 → 4
    [InlineData(new long[] { 10, 20 }, 15)]           // par → (10+20)/2 = 15 exacto
    public void Median_HandlesEvenAndOddCounts(long[] values, long expected)
    {
        Assert.Equal(expected, NetworkProbeResult.Median(values.OrderBy(v => v).ToArray()));
    }

    /// <summary>
    /// Mediana sin muestras: NO hay valor, y no se inventa 0 (R-L). Se exige igual que Percentile:
    /// el contrato es que el llamador no la invoque con una lista vacía, y si lo hace, es un error
    /// de programacion, no un dato.
    /// </summary>
    [Fact]
    public void Median_WithNoSamples_ThrowsInsteadOfInventingZero()
    {
        Assert.Throws<ArgumentException>(() => NetworkProbeResult.Median([]));
    }

    // DEFINICION (NetworkProbe.cs:268-278): nearest-rank.
    //   rank = ceil(p/100 * n), 1-based  ->  indice = clamp(rank-1, 0, n-1)
    //   Es "el valor minimo que deja al menos el p% de las muestras por debajo": no interpola.
    [Theory]
    [InlineData(0, 1)]      // p0   -> rank 0 -> clamp a indice 0 -> el minimo
    [InlineData(50, 50)]    // p50  -> rank 50 -> indice 49
    [InlineData(99, 99)]    // p99  -> rank 99 -> indice 98 -> valor 99
    [InlineData(100, 100)]  // p100 -> rank 100 -> indice 99 -> el maximo
    public void Percentile_UsesNearestRank(int percentile, long expected)
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();

        Assert.Equal(expected, NetworkProbeResult.Percentile(sorted, percentile));
    }

    /// <summary>
    /// Los mismos percentiles sobre una lista corta, para que el rank se vea a mano:
    /// con n=3, p50 -> ceil(1.5)=2 -> indice 1 -> 2; p99 -> ceil(2.97)=3 -> indice 2 -> 3.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 2)]
    [InlineData(99, 3)]
    [InlineData(100, 3)]
    public void Percentile_OnThreeSamples_MatchesTheHandComputedRank(int percentile, long expected)
    {
        Assert.Equal(expected, NetworkProbeResult.Percentile([1, 2, 3], percentile));
    }

    /// <summary>Sin muestras no hay percentil: lanza, no devuelve 0 (R-L).</summary>
    [Fact]
    public void Percentile_WithNoSamples_Throws()
    {
        Assert.Throws<ArgumentException>(() => NetworkProbeResult.Percentile([], 99));
    }

    /// <summary>
    /// Y el conjunto: sin muestras recibidas el resultado NO tiene valores de latencia (null), no ceros.
    /// Es el camino real por el que se llega a "sin muestras": Create retorna antes de calcular nada.
    /// </summary>
    [Fact]
    public void Create_WithNoReceivedSamples_LeavesLatencyNull()
    {
        var result = NetworkProbeResult.Create("host", sent: 4, samples: [], failures: 4);

        Assert.False(result.HasSamples);
        Assert.Null(result.MedianMs);
        Assert.Null(result.P99Ms);
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
    /// 5.5: el estado EEE sale del adaptador elegido. Si la propiedad no existe, queda <c>null</c>, que en el
    /// CLI se imprime como NA — nunca como "desactivado".
    /// </summary>
    [Fact]
    public async Task Diagnose_ReadsTheCurrentEeeState_WhenThePropertyExists()
    {
        var reader = new ScriptedEeeReader("1");
        var resolver = new OneAdapterResolver();

        var diagnosis = await new NetworkProbe(resolver, new ScriptedPing([], 0), reader, StubRoute, StubIndex).DiagnoseAdapterAsync();

        Assert.Equal("1", diagnosis.Adapter?.Eee);
        Assert.Equal("*EEE", reader.LastKeyword);
    }

    /// <summary>Sin la propiedad, `Eee` es null (NA) y el diagnóstico NO falla por ello.</summary>
    [Fact]
    public async Task Diagnose_WithoutTheEeeProperty_ReportsNullNotDisabled()
    {
        var diagnosis = await new NetworkProbe(new OneAdapterResolver(), new ScriptedPing([], 0), new ScriptedEeeReader(null), StubRoute, StubIndex).DiagnoseAdapterAsync();

        Assert.Null(diagnosis.Adapter?.Eee);
    }

    /// <summary>Si el lector lanza (PowerShell ausente, permisos), el diagnóstico sigue y EEE queda NA.</summary>
    [Fact]
    public async Task Diagnose_WhenReadingEeeFails_StillReturnsTheAdapter()
    {
        var diagnosis = await new NetworkProbe(new OneAdapterResolver(), new ScriptedPing([], 0), new ThrowingEeeReader(), StubRoute, StubIndex).DiagnoseAdapterAsync();

        Assert.NotNull(diagnosis.Adapter);
        Assert.Null(diagnosis.Adapter!.Eee);
    }

    /// <summary>Sin lector (parámetro opcional omitido) el diagnóstico funciona igual, con EEE en NA.</summary>
    [Fact]
    public async Task Diagnose_WithoutAPropertyReader_LeavesEeeUnknown()
    {
        var diagnosis = await new NetworkProbe(new OneAdapterResolver(), new ScriptedPing([], 0), null, StubRoute, StubIndex).DiagnoseAdapterAsync();

        Assert.NotNull(diagnosis.Adapter);
        Assert.Null(diagnosis.Adapter!.Eee);
    }

    /// <summary>Ruta por defecto falsa: el índice 7 es el de <see cref="OneAdapterResolver"/>.</summary>
    private static int? StubRoute() => 7;

    /// <summary>Todas las interfaces del doble resuelven al índice 7, sin tocar el stack real.</summary>
    private static int StubIndex(string _) => 7;

    private sealed class OneAdapterResolver : INetworkAdapterResolver
    {
        public Task<IReadOnlyList<NetworkAdapterIdentity>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NetworkAdapterIdentity>>(
            [
                new NetworkAdapterIdentity
                {
                    Name = "Ethernet",
                    Description = "Intel(R) Ethernet Controller I225-V",
                    InterfaceGuid = "{d72a2249-535c-4478-9fdd-9b3a439f6395}",
                    HardwareId = @"PCI\VEN_8086&DEV_15F3",
                    Kind = NetworkKind.Ethernet,
                    NdisPhysicalMedium = 14,
                    IsPhysical = true,
                    State = "2"
                }
            ]);

        public Task<NetworkAdapterIdentity?> ResolveAsync(NetworkAdapterSelector selector, CancellationToken cancellationToken = default) =>
            Task.FromResult<NetworkAdapterIdentity?>(null);
    }

    private sealed class ScriptedEeeReader(string? value) : FragForge.Actions.Network.NetworkAdvancedPropertyReader(new FakeProcessRunner())
    {
        public string? LastKeyword { get; private set; }

        public override Task<FragForge.Actions.Network.NetworkAdvancedProperty> ReadAsync(string interfaceGuid, string keyword, CancellationToken cancellationToken = default)
        {
            LastKeyword = keyword;
            return Task.FromResult(value is null
                ? FragForge.Actions.Network.NetworkAdvancedProperty.Missing(interfaceGuid, keyword)
                : new FragForge.Actions.Network.NetworkAdvancedProperty { InterfaceGuid = interfaceGuid, Keyword = keyword, Exists = true, RegistryValue = value, ValidRegistryValues = ["0", "1"] });
        }
    }

    private sealed class ThrowingEeeReader() : FragForge.Actions.Network.NetworkAdvancedPropertyReader(new FakeProcessRunner())
    {
        public override Task<FragForge.Actions.Network.NetworkAdvancedProperty> ReadAsync(string interfaceGuid, string keyword, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PowerShell no disponible (fake).");
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

    /// <summary>
    /// Devuelve exito SIN tiempo de ida y vuelta. ScriptedPing no puede expresarlo: deriva Success de si el
    /// valor es null. Este doble existe solo para el caso que el compilador destapo.
    /// </summary>
    private sealed class SuccessWithoutRoundtripPing : IPingProbe
    {
        public Task<PingReplySample> SendAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PingReplySample(true, null));
    }

    private sealed class EmptyResolver : INetworkAdapterResolver
    {
        public Task<IReadOnlyList<NetworkAdapterIdentity>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NetworkAdapterIdentity>>([]);

        public Task<NetworkAdapterIdentity?> ResolveAsync(NetworkAdapterSelector selector, CancellationToken cancellationToken = default) =>
            Task.FromResult<NetworkAdapterIdentity?>(null);
    }
}
