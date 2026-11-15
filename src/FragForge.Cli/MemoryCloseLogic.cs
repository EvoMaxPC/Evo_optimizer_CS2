using System.Globalization;
using System.Text;
using FragForge.Core.Models;

namespace FragForge.Cli;

public sealed record CloseIdentity(string? Name, long? Pid, DateTimeOffset? StartTime, long? ParentPid = null);
public interface ICloseIdentityReader { Task<IReadOnlyList<CloseIdentity>?> ReadAsync(CancellationToken cancellationToken); }
public interface ICloseProcess : IDisposable
{
    string? Name { get; }
    long? Pid { get; }
    DateTimeOffset? StartTime { get; }
    bool HasExited { get; }
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
    Func<string, CancellationToken, Task> writeLine)
{
    private static readonly string[] Browsers = ["firefox", "chrome", "msedge", "brave", "opera"];
    private static readonly string[] Terminals = ["dotnet", "powershell", "pwsh", "conhost", "WindowsTerminal", "cmd"];
    private sealed class LogFailure(Exception inner) : Exception("No se pudo escribir el registro.", inner);
    private sealed class AncestorFailure : Exception { }

    // Incluye el padre aunque haya desaparecido; al agotar el límite se falla de forma conservadora.
    public static bool TryAncestors(IReadOnlyList<CloseIdentity>? rows, long ownPid, out IReadOnlyDictionary<long, string> result)
    {
        var found = new Dictionary<long, string>();
        result = found;
        if (rows is null || rows.Count(x => x.Pid == ownPid) != 1) return false;
        long pid = ownPid;
        for (var depth = 0; depth <= 32; depth++)
        {
            if (found.ContainsKey(pid)) return true;
            var matches = rows.Where(x => x.Pid == pid).ToArray();
            if (matches.Length > 1) return false;
            found[pid] = matches.Length == 0 ? "no se sabe" : matches[0].Name ?? "no se sabe";
            if (matches.Length == 0) return true;
            if (matches[0].ParentPid is not long parent || parent < 0) return false;
            if (parent == 0 || found.ContainsKey(parent)) return true;
            if (depth == 32) return false;
            pid = parent;
        }
        return false;
    }

    public async Task<MemoryCloseResult> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        _ = shutdownServers; // La opción no puede excluir los ancestros.
        var log = new StringBuilder();
        async Task Record(string text)
        {
            log.AppendLine(text);
            try { await writeLine(text, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new LogFailure(ex); }
        }
        async Task Say(string text) { await Record(text); await console.WriteLineAsync(text); }
        async Task<string?> Answer(string label)
        {
            var answer = await console.ReadLineAsync();
            await Record(label + ": " + (answer ?? "(fin de entrada)"));
            return answer;
        }
        MemoryCloseResult Result(int code) => new(code, log.ToString());
        var excluded = new Dictionary<long, string>();
        async Task<IReadOnlyList<CloseIdentity>> RefreshAncestors()
        {
            IReadOnlyList<CloseIdentity>? rows;
            try { rows = await identities.ReadAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { throw new AncestorFailure(); }
            if (!TryAncestors(rows, ownPid, out var chain)) throw new AncestorFailure();
            foreach (var pair in chain) excluded[pair.Key] = pair.Value;
            return rows!;
        }
        async Task<bool> Excluded(long pid) { await RefreshAncestors(); return excluded.ContainsKey(pid); }
        try
        {
            await Record("Hora: " + clock().ToString("O", CultureInfo.InvariantCulture));
            if (!MemoryCommands.TryParseTop(args, out var top)) { await Say("Uso: fragforge memoria cerrar [--top N], N entre 1 y 200."); return Result(2); }
            if (console.IsInputRedirected) { await Say("Entrada redirigida: se requiere una consola interactiva."); return Result(2); }
            var view = await snapshot(cancellationToken);
            await RefreshAncestors();
            if (view.ProcessRows is null) throw new AncestorFailure();
            await Say("PID propios y ancestros excluidos: " + string.Join(", ", excluded.Select(x => x.Key.ToString(CultureInfo.InvariantCulture) + " (" + x.Value + ")")));
            var eligible = view.ProcessRows.Where(row => row.ProcessId.HasValue && !excluded.ContainsKey(row.ProcessId.Value) && ProtectedProcesses.Classify(row.Name, row.ProcessId).Classification != "protegido").ToArray();
            var groups = ProcessMemoryGrouping.Group(eligible).Where(g => g.Classification != "protegido").Take(top).ToArray();
            await Say("MB estimados: las páginas compartidas pueden contarse de más.");
            for (var i = 0; i < groups.Length; i++)
                await Say($"{i + 1}: {groups[i].Name} / {groups[i].Instances.ToString(CultureInfo.InvariantCulture)} instancias / {Format(groups[i].WorkingSetMb)} MB estimados");
            await Say("Números a cerrar (ej: 1,3) o Enter para salir:");
            var input = await Answer("Selección ingresada");
            if (string.IsNullOrEmpty(input)) return Result(0);
            if (!ParseSelection(input, groups.Length, out var selection)) { await Say("Selección inválida; no se cerró nada."); return Result(2); }
            var chosen = selection.Select(i => groups[i - 1]).ToArray();
            var before = await RefreshAncestors();
            var captured = new List<CloseIdentity>();
            foreach (var group in chosen)
            {
                foreach (var row in eligible.Where(x => SameName(x.Name, group.Name)))
                {
                    var id = before.SingleOrDefault(x => x.Pid == row.ProcessId);
                    if (excluded.ContainsKey(row.ProcessId!.Value)) { await Say($"{group.Name} PID {row.ProcessId}: ancestro excluido; omitido."); continue; }
                    if (id is null || !Allowed(id) || !SameName(id.Name, row.Name))
                    { await Say($"{group.Name}: identidad desconocida, duplicada o protegida; omitido."); continue; }
                    captured.Add(id);
                    await Say($"Identidad seleccionada: {id.Name} PID {id.Pid} inicio {id.StartTime:O} / {Format(row.WorkingSetMb)} MB estimados");
                }
            }
            await Say("Selección: " + string.Join(", ", chosen.Select(g => g.Name)));
            if (chosen.Any(g => Browsers.Contains(ProtectedProcesses.Normalize(g.Name), StringComparer.OrdinalIgnoreCase))) await Say("Se pueden perder pestañas o formularios sin guardar.");
            if (chosen.Any(g => Terminals.Contains(ProtectedProcesses.Normalize(g.Name), StringComparer.OrdinalIgnoreCase))) await Say("Puede cerrar terminales, compilaciones o programas .NET que estés usando.");
            await Say("¿Confirmar? [s/N]");
            if (!IsYes(await Answer("Respuesta de confirmación"))) return Result(0);
            var beforeMemory = await availableMemory(cancellationToken);
            await Say("Disponible antes (MB): " + Format(beforeMemory));
            bool failed = false;
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
                            var now = await RefreshAncestors();
                            var current = now.SingleOrDefault(x => x.Pid == id.Pid);
                            if (excluded.ContainsKey(id.Pid!.Value)) { await Say($"{group.Name} PID {id.Pid}: ancestro excluido; omitido."); continue; }
                            if (!SameIdentity(id, current)) { await Say($"{group.Name} PID {id.Pid}: identidad cambiada o desconocida; omitido."); continue; }
                            process = opener.Open(id.Pid.Value);
                            if (await Excluded(id.Pid.Value) || !MatchesHandle(id, process)) { await Say($"{group.Name} PID {id.Pid}: identidad del objeto distinta o protegida; omitido."); continue; }
                            await Record($"Intención cierre amable: {id.Name} PID {id.Pid} inicio {id.StartTime:O}");
                            if (await Excluded(id.Pid.Value) || !MatchesHandle(id, process)) { await Say($"{group.Name} PID {id.Pid}: identidad cambiada o ancestro; omitido."); continue; }
                            bool closed;
                            try { closed = process.CloseMainWindow() && await process.WaitForExitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception) { failed = true; await Say($"{group.Name} PID {id.Pid}: error en cierre amable."); continue; }
                            if (closed) await Say($"{group.Name} PID {id.Pid}: cierre amable completado.");
                            else
                            {
                                await Say($"{group.Name} PID {id.Pid}: sin ventana o no cerró; pendiente de confirmación.");
                                pending.Add((id, process)); process = null;
                            }
                        }
                        catch (LogFailure) { throw; }
                        catch (AncestorFailure) { throw; }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception) { await Say($"{group.Name} PID {id.Pid}: no se pudo verificar o cerrar; omitido."); }
                        finally { process?.Dispose(); }
                    }
                    var alive = new List<(CloseIdentity Identity, ICloseProcess Process)>();
                    foreach (var item in pending)
                    {
                        try
                        {
                            if (item.Process.HasExited) { await Say($"{group.Name} PID {item.Identity.Pid}: ya terminó."); continue; }
                            if (await Excluded(item.Identity.Pid!.Value)) { await Say($"{group.Name} PID {item.Identity.Pid}: ancestro excluido; omitido."); continue; }
                            if (!MatchesHandle(item.Identity, item.Process)) { await Say($"{group.Name} PID {item.Identity.Pid}: identidad cambiada; omitido."); continue; }
                            alive.Add(item); await Say($"Pendiente: {item.Identity.Name} PID {item.Identity.Pid}");
                        }
                        catch (LogFailure) { throw; }
                        catch (AncestorFailure) { throw; }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception) { await Say($"{group.Name} PID {item.Identity.Pid}: no se pudo verificar; omitido."); }
                    }
                    if (alive.Count == 0) continue;
                    await Say($"¿Forzar el cierre de {alive.Count.ToString(CultureInfo.InvariantCulture)} proceso(s) de {group.Name}? [s/N]");
                    if (!IsYes(await Answer("Respuesta de cierre forzado"))) { foreach (var item in alive) await Say($"{group.Name} PID {item.Identity.Pid}: cierre forzado no confirmado."); continue; }
                    foreach (var (id, process) in alive)
                    {
                        try
                        {
                            if (await Excluded(id.Pid!.Value) || !MatchesHandle(id, process) || process.HasExited) { await Say($"{group.Name} PID {id.Pid}: identidad cambiada o ancestro; omitido."); continue; }
                            await Record($"Intención cierre forzado: {id.Name} PID {id.Pid} inicio {id.StartTime:O}");
                            if (await Excluded(id.Pid.Value) || !MatchesHandle(id, process) || process.HasExited) { await Say($"{group.Name} PID {id.Pid}: identidad cambiada o ancestro; omitido."); continue; }
                            process.Kill(); await Say($"{group.Name} PID {id.Pid}: cierre forzado solicitado.");
                        }
                        catch (LogFailure) { throw; }
                        catch (AncestorFailure) { throw; }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception) { failed = true; await Say($"{group.Name} PID {id.Pid}: no se pudo verificar o forzar; omitido."); }
                    }
                }
                finally { foreach (var item in pending) item.Process.Dispose(); }
            }
            await delay(TimeSpan.FromSeconds(3), cancellationToken);
            var after = await availableMemory(cancellationToken);
            await Say("Disponible después (MB): " + Format(after));
            await Say("Delta disponible (MB): " + Format(beforeMemory.HasValue && after.HasValue ? after.Value - beforeMemory.Value : null));
            await Say("La memoria libre puede volver a bajar. Esto no es una promesa de FPS.");
            return Result(failed ? 1 : 0);
        }
        catch (LogFailure) { await console.WriteLineAsync("Aviso: no se pudo escribir el registro; no se realizó la siguiente acción."); return Result(1); }
        catch (AncestorFailure) { await console.WriteLineAsync("No se pudo resolver la cadena de ancestros; no se ofrecerán más cierres."); try { await Record("No se pudo resolver la cadena de ancestros; no se ofrecerán más cierres."); } catch (LogFailure) { } return Result(1); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { log.AppendLine("Operación cancelada; el estado puede ser desconocido."); return Result(1); }
        catch (Exception) { try { await Say("No se pudo completar la operación; no se sabe el estado."); } catch (LogFailure) { await console.WriteLineAsync("Aviso: no se pudo escribir el registro."); } return Result(1); }
    }

    private static bool ParseSelection(string text, int max, out int[] selected)
    {
        var result = new List<int>();
        foreach (var part in text.Split(','))
        {
            if (part.Length == 0 || !part.All(c => c is >= '0' and <= '9') || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1 || value > max || result.Contains(value)) { selected = []; return false; }
            result.Add(value);
        }
        selected = result.ToArray(); return true;
    }
    private static bool IsYes(string? answer) => string.Equals(answer, "s", StringComparison.OrdinalIgnoreCase);
    private static string Format(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "no se sabe";
    private static bool SameName(string? a, string? b) => string.Equals(ProtectedProcesses.Normalize(a), ProtectedProcesses.Normalize(b), StringComparison.OrdinalIgnoreCase);
    private static bool Allowed(CloseIdentity? id) => id is not null && id.StartTime.HasValue && id.Pid.HasValue && ProtectedProcesses.Classify(id.Name, id.Pid).Classification != "protegido";
    private static bool SameIdentity(CloseIdentity? a, CloseIdentity? b) => Allowed(a) && Allowed(b) && a!.Pid == b!.Pid && a.StartTime == b.StartTime && SameName(a.Name, b.Name);
    private static bool MatchesHandle(CloseIdentity id, ICloseProcess process) => SameIdentity(id, new CloseIdentity(process.Name, process.Pid, process.StartTime));
}
