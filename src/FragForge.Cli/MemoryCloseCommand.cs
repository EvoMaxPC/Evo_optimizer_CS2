using System.Runtime.Versioning;
using FragForge.Actions.Platform;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Cli;

public sealed class MemoryCloseCommand(
    Func<string[], Func<string, CancellationToken, Task>, CancellationToken, Task<MemoryCloseResult>> run,
    IMemoryCloseLog log,
    TextWriter error)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        MemoryCloseResult? result = null;
        int exit = 1;
        try
        {
            await log.ReserveAsync("", cancellationToken);
            result = await run(args, (line, token) => log.AppendAsync(line + Environment.NewLine, token), cancellationToken);
            exit = result.ExitCode;
        }
        catch (Exception) { await error.WriteLineAsync("No se pudo completar el cierre; el estado puede ser desconocido."); }
        finally
        {
            try
            {
                var text = result is null ? "Error inesperado; el estado puede ser desconocido." : "Resultado final: exit " + exit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // Final recording is deliberately not cancelled after an interrupted operation.
                if (log.Reserved) await log.AppendAsync(text + Environment.NewLine, CancellationToken.None);
                else await log.ReserveAsync(text + Environment.NewLine, CancellationToken.None);
            }
            catch (Exception) { await error.WriteLineAsync("Aviso: no se pudo guardar el registro de cierre."); exit = 1; }
        }
        return exit;
    }

    public static Task<int> RunPlatformAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!MemoryCommands.TryParseTop(args, out _)) { Console.Error.WriteLine("Uso: fragforge memoria cerrar [--top N], N entre 1 y 200."); return Task.FromResult(2); }
            if (Console.IsInputRedirected) { Console.Error.WriteLine("Entrada redirigida: se requiere una consola interactiva."); return Task.FromResult(2); }
            Console.Error.WriteLine("El comando memoria cerrar solo está disponible en Windows."); return Task.FromResult(1);
        }
        return RunWindowsAsync(args);
    }

    [SupportedOSPlatform("windows")]
    private static Task<int> RunWindowsAsync(string[] args)
    {
        var wmi = new WmiQuery();
        var memory = new ProcessMemoryReader(wmi);
        var identities = new WmiCloseIdentityReader(wmi);
        var console = new ConsoleCloseAdapter();
        var log = new MemoryCloseLog(System.IO.Path.Combine(new FragForgePaths().Root, "memoria"), () => DateTimeOffset.Now);
        async Task<MemoryCloseResult> Run(string[] arguments, Func<string, CancellationToken, Task> record, CancellationToken ct)
        {
            async Task<double?> Available(CancellationToken token) => (await memory.ReadAsync(token)).System?.AvailableMb;
            var logic = new MemoryCloseLogic(memory.ReadAsync, identities, new WindowsCloseProcessOpener(identities, Environment.ProcessId), Available,
                (duration, token) => Task.Delay(duration, token), (_, _, _) => Task.FromResult(false), console, () => DateTimeOffset.Now, Environment.ProcessId, record);
            return await logic.RunAsync(arguments, ct);
        }
        return new MemoryCloseCommand(Run, log, Console.Error).RunAsync(args);
    }
}
