using System.Globalization;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Power;

public sealed class PowerPlanAction : ITweakAction
{
    private readonly IProcessRunner _runner;
    private readonly PowerPlanParameters _parameters;
    public string Kind => "PowerPlan";
    public PowerPlanAction(IProcessRunner runner, ActionSpec specification)
    {
        _runner = runner;
        _parameters = ActionParameters.Parse<PowerPlanParameters>(specification);
        Validate();
    }
    private Task<ProcessResult> RunPowerCfgAsync(IReadOnlyList<string> args, CancellationToken token) => _runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("powercfg.exe"), Arguments = args, Timeout = TimeSpan.FromSeconds(60) }, token);
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        var active = await GetActivePlanAsync(cancellationToken);
        var plans = await ListPlansAsync(cancellationToken);
        var existing = FindPlan(plans);
        var settings = new List<PowerSettingCapture>();
        string? export = null;
        if (existing is not null)
        {
            foreach (var setting in _parameters.Settings) settings.Add(await QuerySettingAsync(existing.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken));
            if (context.ArtifactDirectory is not null)
            {
                export = $"power-{existing.Value.Guid}.pow";
                await RunAsync(["/export", Path.Combine(context.ArtifactDirectory, export), existing.Value.Guid], cancellationToken);
            }
        }
        return new PowerPlanActionState { PlanName = _parameters.PlanName, PreviousActiveGuid = active, PlanExisted = existing is not null, Settings = settings, ExportArtifact = export };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        var plans = await ListPlansAsync(cancellationToken);
        var guid = FindPlan(plans)?.Guid;
        if (guid is null)
        {
            if (state.PlanExisted) throw new VerificationFailedException("El plan preexistente desapareció antes de aplicar.");
            var result = await RunAsync(["/duplicatescheme", _parameters.SourceSchemeGuid], cancellationToken);
            guid = ParseGuid(result, "duplicated power scheme");
        }
        foreach (var setting in _parameters.Settings)
        {
            await RunAsync(["/setacvalueindex", guid, setting.SubgroupGuid, setting.SettingGuid, setting.AcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            await RunAsync(["/setdcvalueindex", guid, setting.SubgroupGuid, setting.SettingGuid, setting.DcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        }
        var active = await GetActivePlanAsync(cancellationToken);
        if (_parameters.Activate || string.Equals(active, guid, StringComparison.OrdinalIgnoreCase)) await RunAsync(["/setactive", guid], cancellationToken);
        context.Report("apply", 1, $"Plan '{_parameters.PlanName}' configurado ({guid}).");
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (plan is null) return VerificationResult.Missing("El plan FragForge no existe.");
        foreach (var setting in _parameters.Settings)
        {
            var current = await QuerySettingAsync(plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken);
            if (current.AcIndex != setting.AcValue || current.DcIndex != setting.DcValue) return VerificationResult.Missing($"El ajuste {setting.SettingGuid} no coincide.");
        }
        if (_parameters.Activate)
        {
            var active = await GetActivePlanAsync(cancellationToken);
            if (!string.Equals(active, plan.Value.Guid, StringComparison.OrdinalIgnoreCase)) return VerificationResult.Missing("El plan FragForge no está activo.");
        }
        return VerificationResult.Match($"Plan '{_parameters.PlanName}' y ajustes verificados.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        if (string.IsNullOrWhiteSpace(state.PreviousActiveGuid)) throw new InvalidDataException("El snapshot no contiene el plan activo previo.");
        await RunAsync(["/setactive", state.PreviousActiveGuid], cancellationToken);
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (state.PlanExisted)
        {
            if (plan is null) throw new VerificationFailedException("El plan preexistente ya no existe.");
            foreach (var setting in state.Settings)
            {
                await RunAsync(["/setacvalueindex", plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, setting.AcIndex.ToString(CultureInfo.InvariantCulture)], cancellationToken);
                await RunAsync(["/setdcvalueindex", plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, setting.DcIndex.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            }
            await RunAsync(["/setactive", state.PreviousActiveGuid], cancellationToken);
        }
        else if (plan is not null) await RunAsync(["/delete", plan.Value.Guid], cancellationToken);
        context.Report("revert", 1, "Plan anterior reactivado; solo se elimina el plan creado por FragForge.");
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        var active = await GetActivePlanAsync(cancellationToken);
        if (!string.Equals(active, state.PreviousActiveGuid, StringComparison.OrdinalIgnoreCase)) return VerificationResult.Missing("El plan activo previo no fue restaurado.");
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (!state.PlanExisted) return plan is null ? VerificationResult.Match("Plan creado eliminado y plan previo activo.") : VerificationResult.Missing("El plan creado no fue eliminado.");
        if (plan is null) return VerificationResult.Missing("El plan preexistente fue eliminado incorrectamente.");
        foreach (var setting in state.Settings)
        {
            var current = await QuerySettingAsync(plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken);
            if (current != setting) return VerificationResult.Missing("Los ajustes AC/DC previos no fueron restaurados.");
        }
        return VerificationResult.Match("Plan preexistente restaurado sin eliminarlo.");
    }
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var existing = FindPlan(await ListPlansAsync(cancellationToken));
        var action = existing is null ? $"Duplicar esquema {_parameters.SourceSchemeGuid} como '{_parameters.PlanName}'" : $"Actualizar el plan existente '{_parameters.PlanName}' sin eliminarlo";
        var settings = string.Join(", ", _parameters.Settings.Select(s => $"{s.SettingGuid} AC={s.AcValue} DC={s.DcValue}"));
        return $"{action}; {settings}; activar={_parameters.Activate}. Revert reactiva el plan anterior y elimina solo el plan nuevo, o restaura los ajustes previos si ya existía.";
    }
    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(_parameters.PlanName) || _parameters.PlanName.Contains('(') || _parameters.PlanName.Contains(')') || _parameters.PlanName.Length > 80) throw new CatalogValidationException("Invalid power plan name.", [_parameters.PlanName]);
        if (!Guid.TryParseExact(_parameters.SourceSchemeGuid, "D", out _)) throw new CatalogValidationException("Invalid source scheme GUID.", [_parameters.SourceSchemeGuid]);
        if (_parameters.Settings.Count == 0) throw new CatalogValidationException("Power plan requires settings.", ["settings"]);
        foreach (var setting in _parameters.Settings)
        {
            if (!Guid.TryParseExact(setting.SubgroupGuid, "D", out _) || !Guid.TryParseExact(setting.SettingGuid, "D", out _) || setting.AcValue is < 0 or > 100 || setting.DcValue is < 0 or > 100) throw new CatalogValidationException("Invalid power setting GUID or percentage.", [setting.SettingGuid]);
        }
    }
    private async Task<IReadOnlyList<(string Guid, string Name)>> ListPlansAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/list"], cancellationToken);
        return Regex.Matches(result.StandardOutput, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]*)\)").Select(m => (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim())).ToArray();
    }
    private async Task<string> GetActivePlanAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/getactivescheme"], cancellationToken);
        var match = Regex.Match(result.StandardOutput, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((Active|Current|[^)]*)\)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : ParseGuid(result, "active power scheme");
    }
    private (string Guid, string Name)? FindPlan(IReadOnlyList<(string Guid, string Name)> plans)
    {
        var matches = plans.Where(p => p.Name.Equals(_parameters.PlanName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new FragForgeException("ambiguous_power_plan", $"Hay varios planes llamados '{_parameters.PlanName}'.");
        return matches.Length == 0 ? null : matches[0];
    }
    private async Task<PowerSettingCapture> QuerySettingAsync(string planGuid, string subgroupGuid, string settingGuid, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/query", planGuid, subgroupGuid, settingGuid], cancellationToken);
        var indices = Regex.Matches(result.StandardOutput, @"0x([0-9a-fA-F]+)\s*$", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        if (indices.Length < 2) throw new ActionNotApplicableException($"No se pudieron leer los índices AC/DC de {settingGuid}.");
        return new PowerSettingCapture(subgroupGuid, settingGuid, indices[^2], indices[^1]);
    }
    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunPowerCfgAsync(arguments, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powercfg.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError + result.StandardOutput);
        return result;
    }
    private static string ParseGuid(ProcessResult result, string context) => Regex.Match(result.StandardOutput, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value is var guid && Guid.TryParseExact(guid, "D", out _) ? guid.ToLowerInvariant() : throw new InvalidDataException($"No GUID found in {context} output.");
}
internal sealed record PowerPlanParameters
{
    public string PlanName { get; init; } = "FragForge Performance";
    public string SourceSchemeGuid { get; init; } = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public bool Activate { get; init; } = true;
    public IReadOnlyList<PowerPlanTarget> Settings { get; init; } = [];
}
public sealed record PowerPlanTarget
{
    public required string SubgroupGuid { get; init; }
    public required string SettingGuid { get; init; }
    public int AcValue { get; init; }
    public int DcValue { get; init; }
}
