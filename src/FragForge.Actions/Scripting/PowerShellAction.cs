using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Actions.Scripting;

public sealed class PowerShellAction(IProcessRunner runner, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly PowerShellParameters _parameters = ActionParameters.Parse<PowerShellParameters>(specification);
    public string Kind => "PowerShell";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.AllowPowerShellActions) throw new ProtectedTargetException("PowerShell", "deshabilitado por defecto; requiere AllowPowerShellActions y doble confirmación Advanced");
        Validate();
        foreach (var script in Scripts()) policy.EnsurePowerShellAllowed(script);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var result = await RunAsync(_parameters.CaptureScript, null, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("CaptureScript must return a JSON object or array.");
        return new ScriptActionState { ScriptSha256 = HashScripts(), CapturedJson = document.RootElement.Clone() };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        EnsureHash(state);
        await RunAsync(_parameters.ApplyScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        var result = await RunAsync(_parameters.VerifyScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var matches = document.RootElement.GetProperty("matches").GetBoolean();
        var message = document.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "Script verification.";
        return matches ? VerificationResult.Match(message) : VerificationResult.Missing(message);
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        EnsureHash(state);
        await RunAsync(_parameters.RevertScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var after = await CaptureAsync(context, cancellationToken);
        return Core.Engine.ActionStateEquivalence.Equivalent(before, after) ? VerificationResult.Match("El script de captura volvió al JSON original.") : VerificationResult.Missing("El script de captura no reproduce el estado original.");
    }
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        return Task.FromResult($"PowerShell local explícito (Experimental/Advanced), SHA256={HashScripts()}. Captura/aplica/verifica/revertir con JSON por stdin. No se descarga código.\nAplicación:\n{_parameters.ApplyScript}\nReversión:\n{_parameters.RevertScript}");
    }
    private void EnsureAllowed(ActionExecutionContext context)
    {
        if (!context.AllowPowerShellActions) throw new ProtectedTargetException("PowerShell", "acción no habilitada");
        Validate();
        foreach (var script in Scripts()) policy.EnsurePowerShellAllowed(script);
    }
    private void EnsureHash(ScriptActionState state) { if (state.ScriptSha256 != HashScripts()) throw new FragForgeException("script_changed", "Los scripts difieren de la captura persistida."); }
    private void Validate()
    {
        if (Scripts().Any(string.IsNullOrWhiteSpace)) throw new CatalogValidationException("PowerShell requires capture/apply/verify/revert scripts.", ["scripts"]);
        if (_parameters.TimeoutSeconds is <= 0 or > 300) throw new CatalogValidationException("PowerShell timeout must be 1..300 seconds.", [_parameters.TimeoutSeconds.ToString()]);
    }
    private IEnumerable<string> Scripts() => [_parameters.CaptureScript, _parameters.ApplyScript, _parameters.VerifyScript, _parameters.RevertScript];
    private string HashScripts() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Scripts().ToArray(), JsonDefaults.Create(false))))).ToLowerInvariant();
    private async Task<ProcessResult> RunAsync(string script, string? input, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " + script], StandardInput = input, Timeout = TimeSpan.FromSeconds(_parameters.TimeoutSeconds) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
}
internal sealed record PowerShellParameters
{
    public string CaptureScript { get; init; } = "";
    public string ApplyScript { get; init; } = "";
    public string VerifyScript { get; init; } = "";
    public string RevertScript { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;
}
