using System.Text.Json;
using FragForge.Actions.Platform;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Cli;

/// <summary>
/// <c>fragforge maintenance &lt;verbo&gt;</c>: tareas de mantenimiento del sistema operativo con una lista
/// CERRADA de verbos. No es un ejecutor genérico y no acepta argumentos del usuario más allá del verbo:
/// cada verbo tiene sus argumentos fijos, así que no hay forma de colar un comando distinto.
///
/// Reglas (Capa 3):
///  - <c>retrim</c> → <c>defrag.exe &lt;unidad del sistema&gt; /L</c> (retrim de volúmenes delgados).
///  - <c>component-cleanup</c> → <c>DISM /Online /Cleanup-Image /StartComponentCleanup</c>. <b>Nunca /ResetBase</b>:
///    borra la capacidad de desinstalar actualizaciones y no es reversible.
///  - Confirmación explícita obligatoria (<c>--confirm</c> con el verbo). Sin ella no se ejecuta nada.
///  - Bloqueado si cs2.exe está en ejecución o si hay un modo sesión activo.
///  - Se registra en el journal el comando exacto y el código de salida.
/// </summary>
public static class MaintenanceCommands
{
    /// <summary>Lista cerrada de verbos. Cualquier otra cosa es un error de uso, no una adivinanza.</summary>
    public static IReadOnlyList<string> Verbs { get; } = ["retrim", "component-cleanup"];

    public static async Task<int> RunAsync(IServiceProvider services, ParsedArgs parsed)
    {
        var verb = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (verb is null || !Verbs.Contains(verb))
            throw new ArgumentException($"maintenance requiere un verbo de la lista cerrada: {string.Join(" | ", Verbs)}.");

        var runner = services.GetRequiredService<IProcessRunner>();
        var journal = services.GetRequiredService<IJournal>();
        var policy = services.GetRequiredService<IProtectionPolicy>();

        // --- Bloqueos previos: nada se ejecuta con el juego abierto ni en modo sesión. ---
        var blocker = FindBlocker(parsed);
        if (blocker is not null)
        {
            Console.WriteLine($"Mantenimiento '{verb}' bloqueado: {blocker}");
            return ExitCodes.Preflight;
        }

        // --- Confirmación explícita. El valor de --confirm debe coincidir con el verbo. ---
        var confirmation = parsed.Value("confirm");
        if (!string.Equals(confirmation, verb, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Falta la confirmación: repite 'fragforge maintenance {verb} --confirm {verb}' para ejecutarlo.");
            Console.WriteLine(Describe(verb));
            return ExitCodes.Usage;
        }

        // --- Protección: qué Ensure aplica a cada verbo (R-D: antes de ejecutar). ---
        // retrim escribe sobre el sistema de archivos del volumen del sistema ⇒ EnsureFileAllowed sobre
        // la ruta que se va a retrimar. component-cleanup reescribe el almacén de componentes de WinSxS,
        // que el modelo de protección no expone: se documenta y NO se simula una comprobación falsa.
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                          ?? throw new InvalidOperationException("No se pudo determinar la unidad del sistema.");
        if (verb == "retrim")
        {
            try { policy.EnsureFileAllowed(systemDrive); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"Política de protección rechazó el retrim de {systemDrive}: {ex.Message}");
                await AppendJournalAsync(journal, verb, "--", -1, ex.Message);
                return ExitCodes.Preflight;
            }
        }

        var (fileName, arguments) = BuildCommand(verb, systemDrive);
        var commandLine = $"{fileName} {string.Join(' ', arguments)}";

        if (parsed.Has("dry-run"))
        {
            Console.WriteLine($"[dry-run] {commandLine}");
            Console.WriteLine(Describe(verb));
            return ExitCodes.Success;
        }

        Console.WriteLine($"Ejecutando: {commandLine}");
        var result = await runner.RunAsync(new ProcessRequest
        {
            FileName = fileName,
            Arguments = arguments,
            Timeout = TimeSpan.FromMinutes(verb == "component-cleanup" ? 120 : 30)
        });

        await AppendJournalAsync(journal, verb, commandLine, result.ExitCode, result.TimedOut ? "timeout" : null);

        if (!result.IsSuccess)
        {
            Console.WriteLine($"Falló (código {result.ExitCode}). {result.StandardError.Trim()}");
            return ExitCodes.Failure;
        }

        Console.WriteLine($"Completado: {verb} (código {result.ExitCode}).");
        return ExitCodes.Success;
    }

    /// <summary>Construye el comando con argumentos FIJOS. La unidad del sistema no viene del usuario.</summary>
    public static (string FileName, IReadOnlyList<string> Arguments) BuildCommand(string verb, string systemDrive) => verb switch
    {
        "retrim" => (ProcessRunner.SystemExecutable("defrag.exe"), [systemDrive, "/L"]),
        // PROHIBIDO /ResetBase: se construye la lista a mano, sin nada configurable.
        "component-cleanup" => (ProcessRunner.SystemExecutable("Dism.exe"), ["/Online", "/Cleanup-Image", "/StartComponentCleanup"]),
        _ => throw new ArgumentException($"Verbo de mantenimiento desconocido: {verb}.")
    };

    public static string Describe(string verb) => verb switch
    {
        "retrim" => "retrim: pide al almacenamiento que vuelva a marcar como libres los bloques que el sistema de archivos ya no usa (defrag /L). Puede tardar; no borra datos. No optimiza el arranque ni el rendimiento del juego.",
        "component-cleanup" => "component-cleanup: limpia versiones antiguas del almacén de componentes de Windows (DISM /StartComponentCleanup). No usa /ResetBase, así que las actualizaciones siguen siendo desinstalables. Puede tardar bastante y no se puede interrumpir sin riesgo.",
        _ => throw new ArgumentException($"Verbo de mantenimiento desconocido: {verb}.")
    };

    private static string? FindBlocker(ParsedArgs parsed)
    {
        if (string.Equals(parsed.Value("mode"), "session", StringComparison.OrdinalIgnoreCase))
            return "hay un modo sesión activo (--mode session).";
        foreach (var name in new[] { "cs2" })
        {
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0)
                    return $"{name}.exe está en ejecución; ciérralo antes de mantener el sistema.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No poder comprobarlo no autoriza a ejecutar: se informa y se detiene.
                return $"no se pudo comprobar si {name}.exe está en ejecución ({ex.Message}); no se ejecuta a ciegas.";
            }
        }
        return null;
    }

    private static async Task AppendJournalAsync(IJournal journal, string verb, string outcome, int exitCode, string? note)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            Verb = verb,
            Command = outcome,
            ExitCode = exitCode,
            Note = note
        });
        await journal.AppendAsync(new JournalEntry
        {
            EntryId = Guid.NewGuid().ToString("N"),
            OperationId = "maintenance-" + Guid.NewGuid().ToString("N"),
            EventType = JournalEventType.OperationCompleted,
            TweakId = null,
            Message = exitCode == 0
                ? $"maintenance {verb}: comando ejecutado y completado."
                : $"maintenance {verb}: comando ejecutado, código {exitCode}.",
            Data = data
        });
    }
}
