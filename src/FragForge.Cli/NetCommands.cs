using System.Globalization;
using System.Text.Json;
using FragForge.Actions.Measurement;
using FragForge.Actions.Network;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Cli;

/// <summary>
/// <c>fragforge net &lt;verbo&gt;</c> con una lista CERRADA de verbos (Capa 5.5).
///
/// Los dos verbos son de SOLO LECTURA: <c>diagnose</c> enumera el adaptador y su estado, y <c>probe</c> mide
/// latencia contra un host que elige el usuario. No hay verbo de escritura: los cambios de adaptador se hacen
/// con <c>apply</c> y su id, que es donde viven el snapshot y el revert.
///
/// <c>probe</c> exige <c>--host</c>: no hay hosts hardcodeados, ni siquiera uno "de referencia". Sin host, la
/// respuesta es un error de uso, no una medición a un servidor que elija FragForge.
/// </summary>
public static class NetCommands
{
    public static IReadOnlyList<string> Verbs { get; } = ["probe", "diagnose"];

    public static async Task<int> RunAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var verb = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (verb is null || !Verbs.Contains(verb))
            throw new ArgumentException($"net requiere un verbo de la lista cerrada: {string.Join(" | ", Verbs)}.");

        // Misma guarda que usan los demás comandos de medición del CLI (mouse-test): se pregunta por la
        // plataforma en el comando, en vez de marcar la clase entera como Windows-only. Atajo temprano: el
        // mensaje y el codigo de salida son los de NotWindows().
        if (!OperatingSystem.IsWindows()) return NotWindows();

        // CA1416: NetworkProbe esta marcada Windows-only. El analizador NO ve una guarda que este en OTRO
        // metodo, asi que DiagnoseAsync/ProbeAsync comprueban la plataforma ellos mismos antes de construirlo,
        // igual que hace FootprintAsync (InventoryCommands.cs:195).
        return verb switch
        {
            "diagnose" => await DiagnoseAsync(services, parsed, cancellationToken),
            _ => await ProbeAsync(services, parsed, cancellationToken)
        };
    }

    private static async Task<int> DiagnoseAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return NotWindows();
        var probe = CreateProbe(services);
        var diagnosis = await probe.DiagnoseAdapterAsync(cancellationToken);
        if (parsed.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(diagnosis, JsonDefaults.Options));
            return ExitCodes.Success;
        }

        if (diagnosis.Adapter is null)
        {
            Console.WriteLine("Adaptador por defecto: no se pudo determinar.");
            Console.WriteLine("  " + diagnosis.Note);
            if (diagnosis.Candidates.Count > 0) Console.WriteLine("  Candidatos: " + string.Join(", ", diagnosis.Candidates));
            return ExitCodes.Success;
        }

        var adapter = diagnosis.Adapter;
        Console.WriteLine($"Adaptador: {adapter.Name} ({adapter.Description})");
        Console.WriteLine($"  InterfaceGuid: {adapter.InterfaceGuid ?? "Unknown"}");
        Console.WriteLine($"  NetCfgInstanceId: {adapter.NetCfgInstanceId ?? "Unknown"}");
        Console.WriteLine($"  Hardware ID: {adapter.HardwareId ?? "Unknown"}");
        // El veredicto sobre el tipo de medio NO se emite: el mapeo está sin verificar en estas NICs.
        Console.WriteLine($"  Tipo de medio: {adapter.Kind} (NdisPhysicalMedium={adapter.NdisPhysicalMedium?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}; mapeo NO VERIFICADO en esta NIC)");
        Console.WriteLine($"  Velocidad de enlace: {(adapter.LinkSpeedBitsPerSecond is null ? "Unknown" : (adapter.LinkSpeedBitsPerSecond.Value / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps")}");
        Console.WriteLine($"  Estado: {adapter.State}");
        Console.WriteLine("  " + diagnosis.Note);
        // 5.5: estado EEE actual. Es de solo lectura; si no se pudo leer, se dice NA, no "desactivado".
        Console.WriteLine($"  EEE actual (*EEE): {adapter.Eee ?? "NA (no expuesto o ilegible)"}");
        // 5.4: la recomendación se muestra SIEMPRE, no solo cuando hay Wi-Fi.
        Console.WriteLine("  Recomendación: para jugar, usa Ethernet. El Wi-Fi queda como respaldo.");
        return ExitCodes.Success;
    }

    private static async Task<int> ProbeAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return NotWindows();
        var probe = CreateProbe(services);
        var host = parsed.Value("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            Console.Error.WriteLine("net probe requiere --host. No hay hosts por defecto: elige tú el destino de la medición.");
            return ExitCodes.Usage;
        }

        var count = ParseCount(parsed.Value("count"));
        if (count is null)
        {
            // MaxCount es Windows-only: aqui ya se paso la guarda de arriba, mismo metodo.
            Console.Error.WriteLine($"--count debe ser un entero entre 1 y {NetworkProbe.MaxCount}.");
            return ExitCodes.Usage;
        }

        var result = await probe.MeasureAsync(host, count.Value, cancellationToken);
        if (parsed.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
            return ExitCodes.Success;
        }

        Console.WriteLine($"Host: {result.Host}  (enviados {result.Sent}, recibidos {result.Received})");
        Console.WriteLine($"  Pérdida: {result.LossPercent.ToString("0.0", CultureInfo.InvariantCulture)} %");
        if (!result.HasSamples)
        {
            // NA no es 0: se dice con palabras, no se imprimen ceros.
            Console.WriteLine("  Mediana: NA (sin respuestas)");
            Console.WriteLine("  p99: NA (sin respuestas)");
            Console.WriteLine("  Jitter: NA (sin respuestas)");
            return ExitCodes.Success;
        }
        Console.WriteLine($"  Mediana: {result.MedianMs} ms");
        Console.WriteLine($"  p99: {result.P99Ms} ms");
        Console.WriteLine($"  Jitter (desv. media respecto a la mediana): {result.JitterMs?.ToString("0.00", CultureInfo.InvariantCulture)} ms");
        Console.WriteLine($"  Mín/Máx: {result.MinMs}/{result.MaxMs} ms");
        // Sin veredicto: no se dice si la red es "buena" o "mala".
        return ExitCodes.Success;
    }

    /// <summary>
    /// Construye el probe. Solo se llama DESPUES de comprobar la plataforma, en el mismo metodo que lo usa,
    /// para que CA1416 lo vea. Centralizado para no repetir las tres dependencias en dos sitios.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static NetworkProbe CreateProbe(IServiceProvider services)
    {
        var ping = services.GetService<IPingProbe>() ?? new SystemPingProbe();
        // El lector de propiedades es opcional en el ctor del probe: si falta, el diagnostico sigue
        // funcionando y EEE sale NA.
        return new NetworkProbe(
            services.GetRequiredService<Core.Abstractions.INetworkAdapterResolver>(),
            ping,
            services.GetService<NetworkAdvancedPropertyReader>());
    }

    /// <summary>Mismo mensaje y mismo codigo de salida que la guarda de RunAsync, sin duplicar el texto.</summary>
    private static int NotWindows()
    {
        Console.Error.WriteLine("net requiere Windows (MSFT_NetAdapter y la clase de red del registro).");
        return ExitCodes.MeasurementInvalid;
    }

    private static int? ParseCount(string? raw)
    {
        // NetworkProbe.DefaultCount y NetworkProbe.MaxCount son Windows-only por estar en la clase marcada:
        // la guarda va AQUI, en el mismo metodo, o CA1416 no la ve.
        if (!OperatingSystem.IsWindows()) return null;
        if (string.IsNullOrWhiteSpace(raw)) return NetworkProbe.DefaultCount;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
        return value is >= 1 and <= NetworkProbe.MaxCount ? value : null;
    }
}
