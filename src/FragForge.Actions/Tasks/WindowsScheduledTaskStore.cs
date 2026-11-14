using System.Runtime.Versioning;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Tasks;

[SupportedOSPlatform("windows")]
public sealed class WindowsScheduledTaskStore(IProcessRunner runner) : IScheduledTaskStore
{
    public async Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var (folder, name) = Split(path);
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " +
            $"$t=Get-ScheduledTask -TaskPath '{Escape(folder)}' -TaskName '{Escape(name)}' -ErrorAction SilentlyContinue; " +
            "if ($null -eq $t) { '{\"exists\":false}' } else { [pscustomobject]@{exists=$true; enabled=[bool]$t.Settings.Enabled; xml=(Export-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName)} | ConvertTo-Json -Compress -Depth 4 }";
        var result = await RunAsync(script, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var root = document.RootElement;
        return new ScheduledTaskActionState { Path = path, Exists = root.GetProperty("exists").GetBoolean(), Enabled = root.TryGetProperty("enabled", out var e) && e.GetBoolean(), XmlDefinition = root.TryGetProperty("xml", out var x) ? x.GetString() : null };
    }
    public async Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
    {
        var (folder, name) = Split(path);
        var cmdlet = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        await RunAsync($"$ErrorActionPreference='Stop'; {cmdlet} -TaskPath '{Escape(folder)}' -TaskName '{Escape(name)}' -Confirm:$false | Out-Null", cancellationToken);
    }
    private async Task<ProcessResult> RunAsync(string script, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
    private static (string Folder, string Name) Split(string path)
    {
        var normalized = path.Replace('/', '\\');
        var index = normalized.LastIndexOf('\\');
        if (index <= 0 || index == normalized.Length - 1) throw new ArgumentException("Invalid scheduled task path.");
        return (normalized[..(index + 1)], normalized[(index + 1)..]);
    }
    private static string Escape(string value) => value.Replace("'", "''");
}
