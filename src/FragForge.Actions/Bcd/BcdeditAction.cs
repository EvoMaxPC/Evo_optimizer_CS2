using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Bcd;

public sealed class BcdeditAction(IProcessRunner runner, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly BcdParameters _parameters = ActionParameters.Parse<BcdParameters>(specification);
    public string Kind => "Bcdedit";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureBcdAllowed(_parameters.Element);
        if (_parameters.Element != _parameters.Element.ToLowerInvariant() || Regex.IsMatch(_parameters.Element, "[^a-z0-9]")) throw new CatalogValidationException("Invalid BCD element.", [_parameters.Element]);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureBcdAllowed(_parameters.Element);
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("bcdedit.exe"), Arguments = ["/enum", "{current}", "/v"], Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (result.TimedOut) throw new ProcessFailedException("bcdedit.exe", result.ExitCode, "timeout");
        var match = Regex.Match(result.StandardOutput, @"^\s*" + Regex.Escape(_parameters.Element) + @"\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        string? export = null;
        if (context.ArtifactDirectory is not null)
        {
            export = $"bcd-{_parameters.Element}.bcd";
            var target = Path.Combine(context.ArtifactDirectory, export);
            if (File.Exists(target)) throw new InvalidOperationException($"Ya existe la exportación BCD '{target}'; no se sobrescribe una copia de recuperación.");
            await RunAsync(["/export", target], cancellationToken);
            if (!File.Exists(target)) throw new InvalidOperationException("bcdedit /export no produjo el archivo de recuperación; no se aplicará el cambio BCD.");
        }
        return new BcdActionState { Element = _parameters.Element, Existed = match.Success, PreviousValue = match.Success ? match.Groups[1].Value : null, ExportArtifact = export ?? "" };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        if (string.IsNullOrEmpty(state.ExportArtifact)) throw new InvalidOperationException("BCD changes require a persisted bcdedit /export backup.");
        policy.EnsureBcdAllowed(_parameters.Element);
        await RunAsync(["/set", "{current}", _parameters.Element, _parameters.Value], cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await CaptureAsync(context with { ArtifactDirectory = null }, cancellationToken);
        var state = (BcdActionState)current;
        return state.Existed && string.Equals(state.PreviousValue, _parameters.Value, StringComparison.OrdinalIgnoreCase) ? VerificationResult.Match($"BCD {_parameters.Element}={_parameters.Value}.") : VerificationResult.Missing("BCD no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        policy.EnsureBcdAllowed(state.Element);
        if (state.Existed) await RunAsync(["/set", "{current}", state.Element, state.PreviousValue ?? ""], cancellationToken);
        else await RunAsync(["/deletevalue", "{current}", state.Element], cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        var current = (BcdActionState)await CaptureAsync(context with { ArtifactDirectory = null }, cancellationToken);
        return current.Existed == state.Existed && string.Equals(current.PreviousValue, state.PreviousValue, StringComparison.OrdinalIgnoreCase) ? VerificationResult.Match("BCD restaurado.") : VerificationResult.Missing("BCD no volvió al estado capturado.");
    }
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"BCD {{current}} {_parameters.Element}={_parameters.Value}; exportación completa previa; revert restaura o elimina el elemento. Requiere Advanced y doble confirmación.");
    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("bcdedit.exe"), Arguments = arguments, Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("bcdedit.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError + result.StandardOutput);
        return result;
    }
}
internal sealed record BcdParameters { public required string Element { get; init; } public required string Value { get; init; } }
