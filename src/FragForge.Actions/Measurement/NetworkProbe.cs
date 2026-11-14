using System.Globalization;
using System.Net.NetworkInformation;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Diagnóstico de red de SOLO LECTURA (Capa 5.5).
///
/// Dos partes, ninguna de ellas escribe nada:
/// <list type="number">
/// <item><b>Adaptador de la ruta por defecto</b>, resuelto por <see cref="INetworkAdapterResolver"/> y
///       cruzado con las interfaces activas de <c>NetworkInterface</c> para saber cuál tiene la puerta de
///       enlace. El tipo de medio sale de <c>NdisPhysicalMedium</c> con el mapeo que ya existe
///       (<c>14 = Ethernet</c>, <c>9 = Wi-Fi</c>), marcado NO VERIFICADO en estas NICs concretas.</item>
/// <item><b>Latencia / jitter / pérdida</b> contra un host que ELIGE el usuario. No hay hosts
///       hardcodeados: sin <c>--host</c> no se mide nada.</item>
/// </list>
///
/// <para>
/// Las estadísticas son mediana y p99, más la pérdida. Con <b>cero respuestas</b> devuelve
/// <see cref="NetworkProbeResult.HasSamples"/> = <c>false</c> y todos los números en <c>null</c>: NA no es 0
/// (R-L). No se emite ningún veredicto de "buena" o "mala" red.
/// </para>
/// </summary>
// Se marca Windows porque el diagnóstico del adaptador depende del resolutor, que lee MSFT_NetAdapter y la
// clase de red del registro. Marcar la clase entera evita CA1416 en los llamadores (R-N).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NetworkProbe(
    INetworkAdapterResolver resolver,
    IPingProbe ping,
    NetworkAdvancedPropertyReader? properties = null,
    Func<int?>? bestInterfaceIndex = null,
    Func<string, int>? interfaceIndexOfOverride = null)
{
    /// <summary>Cuántas muestras se piden por defecto. El usuario puede cambiarlo con <c>--count</c>.</summary>
    public const int DefaultCount = 20;

    /// <summary>Tope defensivo: no se lanzan miles de pings por accidente.</summary>
    public const int MaxCount = 200;

    /// <summary>
    /// Diagnóstico del adaptador. Solo lectura: no escribe propiedades ni reinicia el enlace.
    /// </summary>
    public async Task<NetworkAdapterDiagnosis> DiagnoseAdapterAsync(CancellationToken cancellationToken = default)
    {
        var adapters = await resolver.ListAsync(cancellationToken);
        var active = adapters.Where(a => a.IsPhysical && IsUp(a.State)).ToArray();

        if (active.Length == 0)
        {
            return new NetworkAdapterDiagnosis
            {
                Adapter = null,
                Note = adapters.Count == 0
                    ? "No se pudo enumerar ningún adaptador con InterfaceGuid."
                    : $"Hay {adapters.Count} adaptador(es) físico(s) pero ninguno está Up; no se elige uno."
            };
        }

        // La ruta por defecto NO se puede leer de MSFT_NetAdapter. Se pregunta al sistema por la mejor
        // interfaz hacia una dirección IPv4 externa (GetBestInterface): es la misma decisión que toma el
        // stack al enrutar, sin enviar tráfico a ningún sitio.
        //
        // Antes se usaba "el único adaptador con puerta de enlace", y eso está MAL en esta PC: el I225-V y
        // la AX210 pueden tener ambos puerta de enlace configurada, así que ese criterio devolvía null para
        // siempre. La puerta de enlace indica que el adaptador PUEDE enrutar, no que el sistema lo haga.
        // La resolución del índice se puede inyectar: en los tests no hay NICs reales y sin esto el
        // diagnóstico siempre devolvería null (los tests no podrían distinguir "no hay ruta" de "no se leyó").
        var preferredIndex = (bestInterfaceIndex ?? DefaultRouteProbe.GetBestInterfaceIndex)();
        var chosen = preferredIndex is null ? null : active.FirstOrDefault(a => interfaceIndexOf(a.Name) == preferredIndex);

        // Candidatos: los que tienen puerta de enlace (pueden enrutar). Se informa siempre, aunque haya uno
        // elegido, porque el usuario puede querer comparar.
        var withGateway = active.Where(a => HasGateway(a.Name)).ToArray();

        var resolved = chosen is null ? null : chosen with { IsDefaultRoute = true };
        // 5.5 pide el ESTADO EEE actual, y es de solo lectura: se consulta la propiedad *EEE del adaptador
        // elegido. Si no existe, queda null (Unknown), no "desactivado".
        var eee = resolved is null || properties is null ? null : await ReadEeeAsync(resolved, cancellationToken);

        return new NetworkAdapterDiagnosis
        {
            Adapter = resolved is null ? null : resolved with { Eee = eee },
            Candidates = withGateway.Select(a => a.Name).ToArray(),
            Note = chosen is not null
                ? $"Ruta por defecto según GetBestInterface (índice {preferredIndex})."
                : preferredIndex is null
                    ? "GetBestInterface no devolvió una interfaz: no se puede decir cuál es la ruta por defecto."
                    : $"GetBestInterface devolvió el índice {preferredIndex}, que no corresponde a ningún adaptador físico activo."
        };
    }

    /// <summary>
    /// Mide latencia, jitter y pérdida contra <paramref name="host"/>. El host lo elige el usuario.
    /// </summary>
    public async Task<NetworkProbeResult> MeasureAsync(string host, int count = DefaultCount, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Se requiere un host elegido por el usuario; no hay hosts por defecto.", nameof(host));
        if (count < 1 || count > MaxCount) throw new ArgumentOutOfRangeException(nameof(count), count, $"El número de muestras debe estar entre 1 y {MaxCount}.");

        var samples = new List<long>(count);
        var failures = 0;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await ping.SendAsync(host, cancellationToken);
            if (reply.Success && reply.RoundtripTimeMs is >= 0) samples.Add(reply.RoundtripTimeMs);
            else failures++;
        }

        return NetworkProbeResult.Create(host, count, samples, failures);
    }

    /// <summary>
    /// Lee <c>*EEE</c> del adaptador. Solo lectura. Devuelve <c>null</c> si la propiedad no existe o no se
    /// pudo leer: <c>null</c> es "no se sabe", no "desactivado".
    /// </summary>
    private async Task<string?> ReadEeeAsync(NetworkAdapterIdentity adapter, CancellationToken cancellationToken)
    {
        if (adapter.InterfaceGuid is null) return null;
        try
        {
            var property = await properties!.ReadAsync(adapter.InterfaceGuid, "*EEE", cancellationToken);
            return property.Exists ? property.RegistryValue : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // PowerShell no disponible, adaptador sin la propiedad, permisos: es Unknown, no un error que
            // tumbe el diagnóstico.
            return null;
        }
    }

    /// <summary>
    /// Índice de interfaz IPv4 del adaptador, por su nombre. 0 si no se encuentra: 0 no es un índice válido
    /// de interfaz, así que no puede casar con nada por accidente.
    /// </summary>
    /// <summary>
    /// Índice IPv4 de una interfaz por nombre. Inyectable en el ctor para los tests; en producción es el real.
    /// </summary>
    private readonly Func<string, int> interfaceIndexOf = interfaceIndexOfOverride ?? InterfaceIndex;

    private static int InterfaceIndex(string name)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))?
                .GetIPProperties().GetIPv4Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException) { return 0; }
    }

    private static bool IsUp(string state) =>
        state.Equals("2", StringComparison.Ordinal) || state.Contains("Up", StringComparison.OrdinalIgnoreCase);

    private static bool HasGateway(string interfaceName)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                string.Equals(n.Name, interfaceName, StringComparison.OrdinalIgnoreCase) &&
                n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().GatewayAddresses.Count > 0);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}

/// <summary>Un ping enviado. <c>RoundtripTimeMs</c> es <c>null</c> cuando no hubo respuesta.</summary>
public sealed record PingReplySample(bool Success, long? RoundtripTimeMs);

/// <summary>Envía un ping. Se abstrae para poder probar la estadística sin red (y sin Windows).</summary>
public interface IPingProbe
{
    Task<PingReplySample> SendAsync(string host, CancellationToken cancellationToken = default);
}

/// <summary>Implementación con <see cref="System.Net.NetworkInformation.Ping"/>. Solo envía ICMP; no abre puertos.</summary>
public sealed class SystemPingProbe(int timeoutMs = 2000) : IPingProbe
{
    public async Task<PingReplySample> SendAsync(string host, CancellationToken cancellationToken = default)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(cancellationToken);
            return new PingReplySample(reply.Status == IPStatus.Success, reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
        }
        catch (PingException)
        {
            // Host inexistente o ICMP bloqueado: es un fallo de muestra, no una excepción que aborte la serie.
            return new PingReplySample(false, null);
        }
    }
}

/// <summary>
/// Resultado de la sonda. <see cref="HasSamples"/> = <c>false</c> significa NA: mediana, p99 y jitter son
/// <c>null</c>, no 0.
/// </summary>
public sealed record NetworkProbeResult
{
    public required string Host { get; init; }
    /// <summary>Pings enviados.</summary>
    public int Sent { get; init; }
    /// <summary>Pings con respuesta.</summary>
    public int Received { get; init; }
    /// <summary>Pérdida en porcentaje, con un decimal. Siempre calculable: si no hubo respuestas es 100.</summary>
    public double LossPercent { get; init; }
    public long? MedianMs { get; init; }
    public long? P99Ms { get; init; }
    /// <summary>Jitter como desviación media absoluta respecto a la mediana. <c>null</c> sin muestras.</summary>
    public double? JitterMs { get; init; }
    public long? MinMs { get; init; }
    public long? MaxMs { get; init; }

    public bool HasSamples => Received > 0;

    /// <summary>
    /// Construye el resultado. Separado para probar la estadística sin red: es donde de verdad se puede
    /// equivocar uno (percentiles, NA frente a 0).
    /// </summary>
    public static NetworkProbeResult Create(string host, int sent, IReadOnlyList<long> samples, int failures)
    {
        if (sent <= 0) throw new ArgumentOutOfRangeException(nameof(sent), sent, "Se requiere al menos una muestra enviada.");
        var received = samples.Count;
        var loss = Math.Round((sent - received) * 100.0 / sent, 1);
        if (received == 0)
        {
            return new NetworkProbeResult { Host = host, Sent = sent, Received = 0, LossPercent = loss };
        }

        var sorted = samples.OrderBy(x => x).ToArray();
        var median = Median(sorted);
        return new NetworkProbeResult
        {
            Host = host,
            Sent = sent,
            Received = received,
            LossPercent = loss,
            MedianMs = median,
            P99Ms = Percentile(sorted, 99),
            // Jitter = media de |x - mediana|. Es una definición explícita, no la "variación de RTD".
            JitterMs = Math.Round(samples.Average(x => Math.Abs(x - median)), 2),
            MinMs = sorted[0],
            MaxMs = sorted[^1]
        };
    }

    /// <summary>Mediana. Con número par de muestras promedia las dos centrales. Devuelve entero por redondeo.</summary>
    internal static long Median(IReadOnlyList<long> sorted)
    {
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (long)Math.Round((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Percentil por rango más cercano (nearest-rank): el valor mínimo que deja al menos el p% de las muestras
    /// por debajo. Es el método que no inventa interpolaciones entre pings.
    /// </summary>
    internal static long Percentile(IReadOnlyList<long> sorted, int percentile)
    {
        if (sorted.Count == 0) throw new ArgumentException("Sin muestras.", nameof(sorted));
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}

/// <summary>Diagnóstico del adaptador. <see cref="Adapter"/> es <c>null</c> si no se pudo determinar sin adivinar.</summary>
public sealed record NetworkAdapterDiagnosis
{
    public NetworkAdapterIdentity? Adapter { get; init; }
    /// <summary>Adaptadores activos con puerta de enlace. Con más de uno no se elige (R-L).</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];
    public string Note { get; init; } = "";
}
