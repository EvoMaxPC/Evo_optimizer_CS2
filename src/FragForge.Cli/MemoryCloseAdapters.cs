using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using FragForge.Actions.Platform;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Cli;

public sealed class WmiCloseIdentityReader(IWmiQuery wmi) : ICloseIdentityReader
{
    public IReadOnlyList<CloseIdentity> Latest { get; private set; } = [];
    public async Task<IReadOnlyList<CloseIdentity>?> ReadAsync(CancellationToken cancellationToken)
    {
        Latest = [];
        var rows = await wmi.QueryAsync(@"root\cimv2", "Win32_Process", ["Name", "ProcessId", "CreationDate", "ParentProcessId"], cancellationToken: cancellationToken);
        Latest = rows.Select(row =>
        {
            var id = WmiValues.UInt64(row, "ProcessId");
            var parent = WmiValues.UInt64(row, "ParentProcessId");
            return new CloseIdentity(WmiValues.String(row, "Name"), id is <= long.MaxValue ? (long)id.Value : null, ParseCreationDate(WmiValues.String(row, "CreationDate")), parent is <= long.MaxValue ? (long)parent.Value : null);
        }).ToArray();
        return Latest;
    }

    public static DateTimeOffset? ParseCreationDate(string? value)
    {
        if (value is null || value.Length != 25 || value[14] != '.' || (value[21] != '+' && value[21] != '-')) return null;
        if (!DateTime.TryParseExact(value[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
            !int.TryParse(value[22..], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes > 840) return null;
        try { return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Unspecified), TimeSpan.FromMinutes(value[21] == '-' ? -minutes : minutes)).ToUniversalTime(); }
        catch (ArgumentException) { return null; }
    }
}

public sealed class ConsoleCloseAdapter : ICloseConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected;
    public Task<string?> ReadLineAsync() => Console.In.ReadLineAsync();
    public Task WriteLineAsync(string text) => Console.Out.WriteLineAsync(text);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCloseProcessOpener(WmiCloseIdentityReader identities, long ownPid) : ICloseProcessOpener
{
    public ICloseProcess Open(long pid)
    {
        var identity = identities.Latest.SingleOrDefault(x => x.Pid == pid);
        if (identity?.StartTime is null || pid == ownPid || ProtectedProcesses.Classify(identity.Name, pid).Classification == "protegido")
            throw new InvalidOperationException("Identidad desconocida o protegida.");
        var name = ProtectedProcesses.Normalize(identity.Name);
        var candidates = Process.GetProcessesByName(name);
        Process? retained = null;
        try
        {
            var chosen = candidates.SingleOrDefault(p => p.Id == pid);
            if (chosen is null) throw new InvalidOperationException("Proceso no disponible.");
            // Hold this object's process handle for its lifetime so subsequent operations use the same object.
            _ = chosen.SafeHandle;
            var result = new WindowsCloseProcess(chosen);
            retained = chosen;
            return result;
        }
        finally
        {
            foreach (var candidate in candidates)
                if (!ReferenceEquals(candidate, retained)) candidate.Dispose();
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCloseProcess(Process process) : ICloseProcess
{
    public string? Name
    {
        get
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException("Proceso terminado.");
            return process.ProcessName;
        }
    }
    public long? Pid => process.Id;
    // Win32_Process.CreationDate has microsecond precision.
    public DateTimeOffset? StartTime
    {
        get
        {
            var utc = process.StartTime.ToUniversalTime();
            return new DateTimeOffset(utc.AddTicks(-(utc.Ticks % 10)));
        }
    }
    public bool HasExited { get { process.Refresh(); return process.HasExited; } }
    public bool CloseMainWindow() => process.CloseMainWindow();
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); return true; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
    public void Kill() { try { process.Kill(entireProcessTree: false); } catch { throw; } }
    public void Dispose() => process.Dispose();
}

// Deshabilitado: el apagado no permite excluir los ancestros.
public static class BuildServerShutdown
{
    public static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        if (!arguments.SequenceEqual(new[] { "build-server", "shutdown" }, StringComparer.Ordinal)) throw new ArgumentException("Argumentos inválidos.", nameof(arguments));
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    public static async Task<bool> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Process.Start(CreateStartInfo(arguments));
        if (process is null) return false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); return process.ExitCode == 0; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
}
