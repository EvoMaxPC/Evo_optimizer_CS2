using System.Globalization;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Cli;

/// <summary>
/// <c>fragforge security status</c> (Capa 10.1). **SOLO LECTURA**: no hay verbo de escritura.
///
/// <para>
/// Lista cerrada de verbos, igual que <c>net</c> y <c>maintenance</c>: un verbo desconocido es error de uso.
/// El comando no necesita admin para leer: si algo no se puede leer, se imprime Unknown con el motivo.
/// </para>
/// </summary>
public static class SecurityCommands
{
    public static async Task<int> RunAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var verb = parsed.Positionals.FirstOrDefault();
        if (!string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Uso: fragforge security status [--json]");
            return ExitCodes.Usage;
        }

        var posture = await services.GetRequiredService<ISecurityPostureService>().ReadAsync(cancellationToken);
        if (parsed.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(posture, JsonDefaults.Options));
            return ExitCodes.Success;
        }

        Console.WriteLine("Postura de seguridad (solo lectura). Unknown = no se pudo leer; NO es \"desactivado\".");
        Console.WriteLine($"  VBS                    : {posture.Vbs}");
        Console.WriteLine($"  HVCI (Integridad mem.) : {posture.Hvci}   [{posture.HvciSource}]");
        Console.WriteLine($"  Secure Boot            : {posture.SecureBoot}");
        Console.WriteLine($"  TPM                    : {posture.Tpm}{(posture.TpmVersion is null ? "" : " (" + posture.TpmVersion + ")")}");
        Console.WriteLine($"  BitLocker (solo estado): {posture.BitLocker}");
        Console.WriteLine($"  Lista drivers vulnerab.: {posture.VulnerableDriverBlocklist}");
        Console.WriteLine($"  Driver 3.º (PawnIO)    : {(posture.ThirdPartyKernelDriver is null ? "Unknown (no se pudo comprobar)" : posture.ThirdPartyKernelDriver == true ? "Presente: " + (posture.ThirdPartyKernelDriverName ?? "sin nombre") : "No detectado")}");

        if (posture.Observations.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Observaciones:");
            foreach (var observation in posture.Observations)
                Console.WriteLine($"  - {observation.Key}: {observation.Value} ({observation.Source}){(observation.Note is null ? "" : " — " + observation.Note)}");
        }

        Console.WriteLine();
        Console.WriteLine("Contrato: " + posture.AnticheatContract);
        return ExitCodes.Success;
    }
}
