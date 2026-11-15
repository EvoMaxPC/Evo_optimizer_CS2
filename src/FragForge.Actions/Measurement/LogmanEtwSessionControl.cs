using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FragForge.Actions.Measurement;

public static class EtwSessionNames
{
    private static readonly Regex OwnName = new(@"\AFragForgeLive-[0-9a-f]{8}\z", RegexOptions.CultureInvariant);
    public static bool IsOwn(string? name) => name is not null && OwnName.IsMatch(name);
}

public sealed record EtwCloseResult(bool Closed, string Message);
public sealed record EtwProcessResult(int ExitCode, string StdOut, bool TimedOut, bool OutputTruncated = false,
    bool Canceled = false, bool KillFailed = false);

public interface IEtwSessionControl
{
    Task<IReadOnlyList<string>> ListOwnSessionsAsync(CancellationToken ct);
    Task<EtwCloseResult> CloseAsync(string sessionName, CancellationToken ct);
}

/// <summary>Solo consulta y cierra sesiones de nombre propio; no utiliza un intérprete de comandos.</summary>
public sealed class LogmanEtwSessionControl : IEtwSessionControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<EtwProcessResult>> _run;

    public LogmanEtwSessionControl() : this(RunLogmanAsync) { }
    public LogmanEtwSessionControl(Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<EtwProcessResult>> run) => _run = run;

    public async Task<IReadOnlyList<string>> ListOwnSessionsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await _run(["query", "-ets"], Timeout, ct).ConfigureAwait(false);
        if (result.Canceled)
            throw new OperationCanceledException("Consulta cancelada." + (result.KillFailed ? " No se pudo terminar logman." : ""), ct);
        ct.ThrowIfCancellationRequested();
        if (result.TimedOut || result.ExitCode != 0 || result.OutputTruncated)
            throw new InvalidOperationException("No se pudo obtener una lista completa de sesiones ETW." + (result.KillFailed ? " No se pudo terminar logman." : ""));
        return result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(EtwSessionNames.IsOwn).Select(name => name!).Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task<EtwCloseResult> CloseAsync(string sessionName, CancellationToken ct)
    {
        if (!EtwSessionNames.IsOwn(sessionName)) return new(false, "Nombre de sesión ETW no válido; no se ejecutó ningún comando.");
        var manual = $"En PowerShell como administrador: logman stop {sessionName} -ets";
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await _run(["stop", sessionName, "-ets"], Timeout, ct).ConfigureAwait(false);
            if (result.TimedOut || result.Canceled || ct.IsCancellationRequested)
                return new(false, "Tiempo agotado o cancelación; el estado de la sesión ETW es desconocido." +
                    (result.KillFailed ? " No se pudo terminar logman." : "") + " " + manual);
            if (result.ExitCode != 0) return new(false, "No se pudo cerrar la sesión ETW. " + manual);
            var remaining = await ListOwnSessionsAsync(ct).ConfigureAwait(false);
            return remaining.Contains(sessionName, StringComparer.Ordinal)
                ? new(false, "La sesión ETW sigue listada. " + manual)
                : new(true, "Sesión ETW cerrada y ya no listada.");
        }
        catch (OperationCanceledException ex) { return new(false, "Operación cancelada; el estado de la sesión ETW es desconocido. " + ex.Message + " " + manual); }
        catch (Exception ex) { return new(false, "No se pudo confirmar el cierre; el estado de la sesión ETW es desconocido: " + ex.Message + ". " + manual); }
    }

    private static async Task<EtwProcessResult> RunLogmanAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var allowed = args.Count == 2 && args[0] == "query" && args[1] == "-ets"
            || args.Count == 3 && args[0] == "stop" && EtwSessionNames.IsOwn(args[1]) && args[2] == "-ets";
        if (!allowed) throw new ArgumentException("Comando de logman no permitido.");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Path.IsPathFullyQualified(system)) throw new InvalidOperationException("No se pudo localizar el directorio de sistema.");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(system, "logman.exe"), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("No se pudo iniciar logman.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var stdout = DrainAsync(process.StandardOutput, deadline.Token);
        var stderr = DrainAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return new(process.ExitCode, output.Text, false, output.Truncated);
        }
        catch (OperationCanceledException)
        {
            var killFailed = false;
            try { process.Kill(); }
            catch (Exception) { killFailed = true; }
            return new(-1, "", !ct.IsCancellationRequested, Canceled: ct.IsCancellationRequested, KillFailed: killFailed);
        }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 32768; // hasta 64 KiB de caracteres UTF-16 almacenados
        var result = new StringBuilder();
        var buffer = new char[1024];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var take = Math.Min(count, limit - result.Length);
            if (take > 0) result.Append(buffer, 0, take);
            if (take < count) truncated = true;
        }
        return (result.ToString(), truncated);
    }
}
