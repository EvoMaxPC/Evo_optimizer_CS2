using System.Globalization;
using System.Runtime.Versioning;
using FragForge.Actions.Platform;
using FragForge.Core.Models;

namespace FragForge.Cli;

public sealed class MemoryCommands(Func<CancellationToken, Task<MemorySnapshot>> read, TextWriter output, TextWriter error)
{
    public const string Footer = "Esto es el working set actual, no una promesa de cuánta memoria se libera al cerrar. Esta versión no cierra nada.";
    public const string Usage = "Uso: fragforge memoria [--top N], donde N es un entero entre 1 y 200.";

    public static bool TryParseTop(string[] args, out int top)
    {
        top = 20;
        if (args.Length == 0) return true;
        if (args.Length != 2 || !args[0].Equals("--top", StringComparison.OrdinalIgnoreCase)) return false;
        var value = args[1];
        return value.Length > 0 && value.All(c => c is >= '0' and <= '9') &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out top) && top is >= 1 and <= 200;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!TryParseTop(args, out var top)) { await error.WriteLineAsync(Usage); return 2; }
        try
        {
            var snapshot = await read(cancellationToken).ConfigureAwait(false);
            var system = snapshot.System;
            await output.WriteLineAsync("Memoria disponible (MB): " + Format(system?.AvailableMb));
            await output.WriteLineAsync("Memoria comprometida (MB): " + Format(system?.CommittedMb));
            await output.WriteLineAsync("Compromiso en uso (%): " + Format(system?.PercentCommittedBytesInUse));
            await output.WriteLineAsync("Nombre / Instancias / MB / Clasificación");
            foreach (var row in snapshot.Processes.Take(top))
                await output.WriteLineAsync($"{row.Name} / {row.Instances.ToString(CultureInfo.InvariantCulture)} / {Format(row.WorkingSetMb)} / {row.Classification} ({row.Reason})");
            await output.WriteLineAsync(Footer);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { await error.WriteLineAsync("No se pudo consultar la memoria mediante WMI; no se sabe el estado actual."); return 1; }
    }

    private static string Format(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "no se sabe";

    public static Task<int> RunPlatformAsync(string[] args)
    {
        if (!TryParseTop(args, out _)) { Console.Error.WriteLine(Usage); return Task.FromResult(2); }
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("El comando memoria solo está disponible en Windows."); return Task.FromResult(1); }
        return RunWindowsAsync(args);
    }

    [SupportedOSPlatform("windows")]
    private static Task<int> RunWindowsAsync(string[] args) =>
        new MemoryCommands(new ProcessMemoryReader(new WmiQuery()).ReadAsync, Console.Out, Console.Error).RunAsync(args);
}
