using System.Runtime.Versioning;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkAdapterController(IProcessRunner runner) : INetworkAdapterController
{
    public async Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default)
    {
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " +
            $"$p=Get-NetAdapterAdvancedProperty -Name '{Escape(adapterName)}' -RegistryKeyword '{Escape(keyword)}' -ErrorAction SilentlyContinue; " +
            "if ($null -eq $p) { '{\"exists\":false}' } else { [pscustomobject]@{exists=$true; keyword=[string]$p.RegistryKeyword; value=@($p.RegistryValueData); display=[string]$p.DisplayValue} | ConvertTo-Json -Compress -Depth 4 }";
        var result = await RunAsync(script, null, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var root = document.RootElement;
        if (!root.GetProperty("exists").GetBoolean()) return new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false };
        return new NetworkPropertyCapture { AdapterName = adapterName, Keyword = root.GetProperty("keyword").GetString() ?? keyword, Exists = true, RegistryValue = root.GetProperty("value").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(), DisplayValue = root.GetProperty("display").GetString() };
    }
    public async Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default)
    {
        var array = "@(" + string.Join(",", value.Select(v => "'" + Escape(v) + "'")) + ")";
        var script = $"$ErrorActionPreference='Stop'; Set-NetAdapterAdvancedProperty -Name '{Escape(adapterName)}' -RegistryKeyword '{Escape(keyword)}' -RegistryValue {array} -Confirm:$false";
        await RunAsync(script, null, cancellationToken);
    }
    private async Task<ProcessResult> RunAsync(string script, string? input, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], StandardInput = input, Timeout = TimeSpan.FromSeconds(90) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
    private static string Escape(string value) => value.Replace("'", "''");
}
