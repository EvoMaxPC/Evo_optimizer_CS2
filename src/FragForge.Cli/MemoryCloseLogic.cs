using System.Globalization;
using System.Text;
using FragForge.Core.Models;

namespace FragForge.Cli;

// All operating-system and console access is supplied by the caller. A null identity result is unknown.
public sealed record CloseIdentity(string? Name, long? Pid, DateTimeOffset? StartTime);
public interface ICloseIdentityReader
{
    Task<IReadOnlyList<CloseIdentity>?> ReadAsync(CancellationToken cancellationToken);
}
public interface ICloseProcess : IDisposable
{
    string? Name { get; }
    long? Pid { get; }
    DateTimeOffset? StartTime { get; }
    bool CloseMainWindow();
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
    void Kill();
}
public interface ICloseProcessOpener { ICloseProcess Open(long pid); }
public interface ICloseConsole
{
    bool IsInputRedirected { get; }
    Task<string?> ReadLineAsync();
    Task WriteLineAsync(string text);
}
public sealed record MemoryCloseResult(int ExitCode, string Log);

public sealed class MemoryCloseLogic(
    Func<CancellationToken, Task<MemorySnapshot>> snapshot,
    ICloseIdentityReader identities,
    ICloseProcessOpener opener,
    Func<CancellationToken, Task<double?>> availableMemory,
    Func<TimeSpan, CancellationToken, Task> delay,
    Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<bool>> shutdownServers,
    ICloseConsole console,
    Func<DateTimeOffset> clock,
    long ownPid,
    Func<string, CancellationToken, Task>? beforeActions = null)
{
    private static readonly string[] BrowserNames = ["firefox", "chrome", "msedge", "brave", "opera"];
    private static readonly string[] ServerNames = ["VBCSCompiler", "MSBuild", "dotnet"];
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MeasureDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(30);
    private const string ShutdownOption = "apagar servidores de compilación de .NET";
    private const string Footer = "La memoria libre puede volver a bajar. Esto no es una promesa de FPS.";

    public async Task<MemoryCloseResult> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var log = new StringBuilder();
        void Record(string text) => log.AppendLine(text);
        async Task Say(string text) { Record(text); await console.WriteLineAsync(text); }
        MemoryCloseResult Result(int code) => new(code, log.ToString());
        if (!MemoryCommands.TryParseTop(args, out var top)) { await Say("Uso: fragforge memoria cerrar [--top N], N entre 1 y 200."); return Result(2); }
        if (console.IsInputRedirected) { await Say("Entrada redirigida: se requiere una consola interactiva."); return Result(2); }
        try
        {
            Record("Hora: " + clock().ToString("O", CultureInfo.InvariantCulture));
            var view = await snapshot(cancellationToken);
            var initial = await identities.ReadAsync(cancellationToken);
            if (initial is null) { await Say("Identidades desconocidas; no se puede seleccionar procesos."); return Result(1); }
            var ownNames = initial.Where(x => x.Pid == ownPid).Select(x => ProtectedProcesses.Normalize(x.Name)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ownNames.Count == 0) { await Say("Nombre del proceso propio desconocido; no se puede seleccionar procesos."); return Result(1); }
            var groups = view.Processes.Where(g => g.Classification != "protegido" &&
                ProtectedProcesses.Classify(g.Name, 5).Classification != "protegido" && !ownNames.Contains(ProtectedProcesses.Normalize(g.Name)))
                .OrderByDescending(g => g.WorkingSetMb.HasValue).ThenByDescending(g => g.WorkingSetMb)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Take(top).ToArray();
            await Say("El PID padre no está en el snapshot de M-1; solo se excluye el propio PID.");
            await Say("MB estimados: las páginas compartidas pueden contarse de más.");
            for (var i = 0; i < groups.Length; i++)
                await Say($"{i + 1}: {groups[i].Name} / {groups[i].Instances.ToString(CultureInfo.InvariantCulture)} instancias / {Format(groups[i].WorkingSetMb)} MB estimados");
            bool offerServers = view.Processes.Any(g => ServerNames.Contains(ProtectedProcesses.Normalize(g.Name), StringComparer.OrdinalIgnoreCase));
            if (offerServers) await Say($"{groups.Length + 1}: {ShutdownOption}");
            await Say("Números a cerrar (ej: 1,3) o Enter para salir:");
            var input = await console.ReadLineAsync();
            if (string.IsNullOrEmpty(input)) return Result(0);
            if (!ParseSelection(input, groups.Length + (offerServers ? 1 : 0), out var selected))
            { await Say("Selección inválida; no se cerró nada."); return Result(2); }
            var chosen = selected.Where(i => i <= groups.Length).Select(i => groups[i - 1]).ToArray();
            bool shutdown = offerServers && selected.Contains(groups.Length + 1);
            // Layer a: capture identities for the selected groups before confirmation.
            IReadOnlyList<CloseIdentity>? before;
            try { before = await identities.ReadAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { before = null; }
            var captured = new List<CloseIdentity>();
            var capturedPids = new HashSet<long>();
            foreach (var group in chosen)
            {
                var matches = before?.Where(x => SameName(x.Name, group.Name)).ToArray();
                if (matches is null || matches.Length == 0) { await Say($"{group.Name}: identidad desconocida; omitido."); continue; }
                foreach (var item in matches)
                {
                    if (!Allowed(item) || item.Pid == ownPid || !capturedPids.Add(item.Pid!.Value)) { await Say($"{group.Name}: identidad desconocida, duplicada o protegida; omitido."); continue; }
                    captured.Add(item);
                    Record($"Identidad seleccionada: {item.Name} PID {item.Pid} inicio {item.StartTime:O}");
                }
            }
            await Say("Selección: " + string.Join(", ", chosen.Select(g => g.Name).Concat(shutdown ? [ShutdownOption] : Array.Empty<string>())));
            if (chosen.Any(g => BrowserNames.Contains(ProtectedProcesses.Normalize(g.Name), StringComparer.OrdinalIgnoreCase)))
                await Say("Se pueden perder pestañas o formularios sin guardar.");
            await Say("¿Confirmar? [s/N]");
            if (!IsYes(await console.ReadLineAsync())) return Result(0);
            // The real command must persist its initial record before any process action.
            if (beforeActions is not null) await beforeActions(log.ToString(), cancellationToken);
            var memoryBefore = await availableMemory(cancellationToken);
            await Say("Disponible antes (MB): " + Format(memoryBefore));
            bool actionFailed = false;
            foreach (var group in chosen)
            {
                var pending = new List<(CloseIdentity Identity, ICloseProcess Process)>();
                try
                {
                    foreach (var id in captured.Where(x => SameName(x.Name, group.Name)))
                    {
                        ICloseProcess? process = null;
                        try
                        {
                            // Layer b: query again before opening anything.
                            var now = await identities.ReadAsync(cancellationToken);
                            var current = now?.SingleOrDefault(x => x.Pid == id.Pid);
                            if (!SameIdentity(id, current) || !Allowed(current) || current!.Pid == ownPid)
                            { await Say($"{group.Name} PID {id.Pid}: identidad cambiada o desconocida; omitido."); continue; }
                            process = opener.Open(id.Pid!.Value);
                            // Layer c: verify the identity from the obtained object before any action.
                            if (!MatchesHandle(id, process)) { await Say($"{group.Name} PID {id.Pid}: identidad del objeto distinta o protegida; omitido."); continue; }
                            try
                            {
                                if (process.CloseMainWindow() && await process.WaitForExitAsync(CloseTimeout, cancellationToken))
                                    await Say($"{group.Name} PID {id.Pid}: cierre amable completado.");
                                else
                                {
                                    await Say($"{group.Name} PID {id.Pid}: sin ventana o no cerró; pendiente de confirmación.");
                                    pending.Add((id, process));
                                    process = null;
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception) { actionFailed = true; await Say($"{group.Name} PID {id.Pid}: error en cierre amable."); }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        // Layer e: all verification/open/action exceptions fail closed.
                        catch (Exception) { await Say($"{group.Name} PID {id.Pid}: no se pudo verificar o cerrar; omitido."); }
                        finally { process?.Dispose(); }
                    }
                    if (pending.Count > 0)
                    {
                        await Say($"¿Forzar el cierre de {group.Name}? [s/N]");
                        if (IsYes(await console.ReadLineAsync()))
                            foreach (var (id, process) in pending)
                            {
                                try
                                {
                                    // Layer d: repeat object verification immediately before each Kill.
                                    if (!MatchesHandle(id, process)) { await Say($"{group.Name} PID {id.Pid}: identidad cambiada; omitido."); continue; }
                                    try { process.Kill(); await Say($"{group.Name} PID {id.Pid}: cierre forzado solicitado."); }
                                    catch (Exception) { actionFailed = true; await Say($"{group.Name} PID {id.Pid}: error en cierre forzado."); }
                                }
                                catch (Exception) { await Say($"{group.Name} PID {id.Pid}: no se pudo verificar o forzar; omitido."); }
                            }
                        else foreach (var (id, _) in pending) await Say($"{group.Name} PID {id.Pid}: cierre forzado no confirmado.");
                    }
                }
                finally { foreach (var item in pending) item.Process.Dispose(); }
            }
            bool serverFailed = false;
            if (shutdown)
            {
                var success = await shutdownServers(["build-server", "shutdown"], ServerTimeout, cancellationToken);
                serverFailed = !success;
                await Say(success ? "Servidores de compilación: apagado solicitado." : "Servidores de compilación: no se pudo completar el apagado.");
            }
            await delay(MeasureDelay, cancellationToken);
            var memoryAfter = await availableMemory(cancellationToken);
            await Say("Disponible después (MB): " + Format(memoryAfter));
            await Say("Delta disponible (MB): " + Format(memoryBefore.HasValue && memoryAfter.HasValue ? memoryAfter.Value - memoryBefore.Value : null));
            await Say(Footer);
            return Result(serverFailed || actionFailed ? 1 : 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { Record("Operación cancelada; el estado puede ser desconocido."); return Result(1); }
        catch (Exception) { await Say("Error: no se pudo completar la operación; el estado puede ser desconocido."); return Result(1); }
    }

    private static bool ParseSelection(string text, int max, out int[] selected)
    {
        var result = new List<int>();
        foreach (var part in text.Split(','))
        {
            if (part.Length == 0 || !part.All(c => c is >= '0' and <= '9') ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1 || value > max || result.Contains(value))
            { selected = []; return false; }
            result.Add(value);
        }
        selected = result.ToArray(); return true;
    }
    private static bool IsYes(string? answer) => string.Equals(answer, "s", StringComparison.OrdinalIgnoreCase);
    private static string Format(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "no se sabe";
    private static bool SameName(string? a, string? b) => string.Equals(ProtectedProcesses.Normalize(a), ProtectedProcesses.Normalize(b), StringComparison.OrdinalIgnoreCase);
    private static bool Allowed(CloseIdentity? id) => id is not null && id.StartTime.HasValue && id.Pid.HasValue &&
        ProtectedProcesses.Classify(id.Name, id.Pid).Classification != "protegido";
    private static bool SameIdentity(CloseIdentity? a, CloseIdentity? b) => Allowed(a) && Allowed(b) &&
        a!.Pid == b!.Pid && a.StartTime == b.StartTime && SameName(a.Name, b.Name);
    private static bool MatchesHandle(CloseIdentity id, ICloseProcess process) => SameIdentity(id, new CloseIdentity(process.Name, process.Pid, process.StartTime));
}
