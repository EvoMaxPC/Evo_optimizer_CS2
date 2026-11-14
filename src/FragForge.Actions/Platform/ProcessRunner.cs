using System.Diagnostics;
using System.Text;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Platform;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(request), "Process timeout must be between 0 and 30 minutes.");
        var start = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }
        var output = await stdout.WaitAsync(CancellationToken.None);
        var error = await stderr.WaitAsync(CancellationToken.None);
        if (timedOut) return new ProcessResult(-1, output, error, true);
        return new ProcessResult(process.ExitCode, output, error, false);
    }
    public static string SystemExecutable(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
}
