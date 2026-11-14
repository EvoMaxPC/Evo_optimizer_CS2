using System.Text.Json;
using FragForge.Core.Engine;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Cli;

/// <summary>
/// <c>fragforge session start|stop|status</c> (Capa 9, 9.4).
///
/// No hay verbo de vigilancia en el CLI: el vigilante tiene que sobrevivir al comando, y un proceso de
/// consola que se queda 2 horas esperando no es la forma. El CLI deja la sesión preparada y la RECUPERA en el
/// siguiente arranque (9.3); la vigilancia continua es de la App, que sí vive mientras se juega.
///
/// Prohibido aquí (9.5): no se mata ni se prioriza ningún proceso, no se abre cs2.exe y no se toca Steam.
/// </summary>
public static class SessionCommands
{
    public static IReadOnlyList<string> Verbs { get; } = ["start", "stop", "status"];

    public static async Task<int> RunAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var verb = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (verb is null || !Verbs.Contains(verb))
            throw new ArgumentException($"session requiere un verbo de la lista cerrada: {string.Join(" | ", Verbs)}.");

        var sessions = services.GetRequiredService<GameSessionService>();

        switch (verb)
        {
            case "start":
            {
                var profile = ParseProfile(parsed.Value("profile"));
                var result = await sessions.StartAsync(profile, cancellationToken);
                if (parsed.Has("json")) Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
                else foreach (var message in result.Messages) Console.WriteLine(message);
                return result.Started ? ExitCodes.Success : ExitCodes.Preflight;
            }
            case "stop":
            {
                var result = await sessions.StopAsync(cancellationToken);
                if (parsed.Has("json")) Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
                else if (result.AlreadyReverted) Console.WriteLine("No había sesión que revertir.");
                else if (result.Reverted) Console.WriteLine("Sesión revertida y marcador eliminado.");
                else { Console.WriteLine("La reversión NO se completó; el marcador se conserva para reintentar:"); foreach (var error in result.Errors) Console.WriteLine($"  - {error}"); }
                return result.Reverted || result.AlreadyReverted ? ExitCodes.Success : ExitCodes.Failure;
            }
            default:
            {
                // status también informa del estado de recuperación: si quedó una sesión huérfana de una caída,
                // se dice explícitamente en vez de mostrarla como activa sin más.
                var status = await sessions.StatusAsync(cancellationToken);
                var running = Cs2Process.IsRunningOnThisMachine();
                if (parsed.Has("json"))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { Status = status, Cs2Running = running }, JsonDefaults.Options));
                    return ExitCodes.Success;
                }

                Console.WriteLine($"Modo sesión     : {Describe(status.State)}");
                if (status.Marker is { } marker)
                {
                    Console.WriteLine($"Operación       : {marker.OperationId}");
                    Console.WriteLine($"Snapshot        : {marker.SnapshotId ?? "(sin snapshot)"}");
                    Console.WriteLine($"Inicio          : {marker.StartedAt:u}");
                    Console.WriteLine($"Perfil          : {marker.Profile ?? "(no registrado)"}");
                    Console.WriteLine($"Tweaks          : {marker.TweakIds.Count}");
                }
                if (!string.IsNullOrWhiteSpace(status.Message)) Console.WriteLine($"Detalle         : {status.Message}");
                if (!string.IsNullOrWhiteSpace(status.Error)) Console.WriteLine($"Error           : {status.Error}");
                Console.WriteLine($"CS2 en ejecución: {running switch { true => "sí", false => "no", null => "desconocido" }}");
                return ExitCodes.Success;
            }
        }
    }

    private static string Describe(GameSessionState state) => state switch
    {
        GameSessionState.Active => "activo",
        GameSessionState.WaitingForGame => "esperando CS2",
        GameSessionState.Reverting => "revirtiendo",
        _ => "inactivo"
    };

    private static TweakProfile ParseProfile(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? TweakProfile.Competitive
            : Enum.TryParse<TweakProfile>(value, ignoreCase: true, out var profile)
                ? profile
                : throw new ArgumentException($"Perfil desconocido: '{value}'.");
}
