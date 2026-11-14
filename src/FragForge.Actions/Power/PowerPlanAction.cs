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
    /// <summary>
    /// Preflight. NO escribe nada. Comprueba:
    /// <list type="number">
    /// <item>El plan de origen (<c>sourceSchemeGuid</c>) existe en <c>/list</c>; si no, NotApplicable
    /// con mensaje claro en vez de dejar que <c>/duplicatescheme</c> falle de forma críptica.</item>
    /// <item>Cada ajuste declarado está ENUMERADO por el sistema y su valor AC cae dentro de lo que
    /// el sistema admite (rango o lista), leído con <c>/qh</c> del plan de origen o del activo —nunca
    /// del plan FragForge, que puede no existir todavía—. Regla R-L: si no está enumerado, NotApplicable.</item>
    /// </list>
    /// El modo <c>settingsOnly</c> solo toca valores de un plan existente: su origen es el plan destino.
    /// </summary>
    public async Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        var plans = await ListPlansAsync(cancellationToken);

        // Plan del que se leen los valores AC antes de escribir:
        //  - modo normal: el plan de ORIGEN (el FragForge puede no existir todavía);
        //  - modo settingsOnly: el plan destino, que tiene que existir (no se duplica nada).
        var queryGuid = _parameters.Mode.Equals("settingsOnly", StringComparison.OrdinalIgnoreCase)
            ? (FindPlan(plans) ?? throw new ActionNotApplicableException($"El plan '{_parameters.PlanName}' no existe en este equipo; settingsOnly no crea planes.")).Guid
            : (FindSourcePlan(plans) ?? throw new ActionNotApplicableException($"El plan base {_parameters.SourceSchemeGuid} no existe en este equipo; no se puede duplicar.")).Guid;

        var queried = await QuerySettingsAsync(queryGuid, cancellationToken);
        foreach (var setting in _parameters.Settings)
        {
            var found = queried.FirstOrDefault(q =>
                string.Equals(q.SubgroupGuid, setting.SubgroupGuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(q.SettingGuid, setting.SettingGuid, StringComparison.OrdinalIgnoreCase));
            if (found is null) throw new ActionNotApplicableException($"El sistema no enumera el ajuste {setting.SettingGuid} en este equipo; no se asume nada.");
            if (!found.Allows(setting.AcValue)) throw new ActionNotApplicableException($"El valor AC {setting.AcValue} de {setting.SettingGuid} no está en {found.Describe()}: lo enumera el sistema para este equipo.");
        }
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        var active = await GetActivePlanAsync(cancellationToken);
        var plans = await ListPlansAsync(cancellationToken);
        var existing = FindPlan(plans);

        // ¿De qué plan se capturan los valores previos?
        //  - Si el plan FragForge YA existe: de él (es el que se va a modificar).
        //  - Si no existe todavía (primera aplicación): del plan de ORIGEN, que es la copia de la que
        //    nace el plan nuevo. Así el estado capturado refleja los valores que el tweak va a cambiar,
        //    y el revert tiene algo real que restaurar.
        //  - settingsOnly: SOLO del plan destino; si no existe, no hay nada que capturar (Preflight ya
        //    lo habrá marcado NotApplicable).
        var capturePlan = _parameters.Mode.Equals("settingsOnly", StringComparison.OrdinalIgnoreCase)
            ? existing
            : existing ?? FindSourcePlan(plans);

        var settings = new List<PowerSettingCapture>();
        string? export = null;
        if (capturePlan is not null)
        {
            foreach (var setting in _parameters.Settings) settings.Add(await QuerySettingAsync(capturePlan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken));
            if (context.ArtifactDirectory is not null)
            {
                export = $"power-{capturePlan.Value.Guid}.pow";
                await RunAsync(["/export", Path.Combine(context.ArtifactDirectory, export), capturePlan.Value.Guid], cancellationToken);
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
            // Punto 7: el plan base tiene que existir; si no, /duplicatescheme falla con un mensaje
            // críptico de powercfg. Mejor decirlo y no tocar nada.
            if (FindSourcePlan(plans) is null) throw new ActionNotApplicableException($"El plan base {_parameters.SourceSchemeGuid} no existe en este equipo; no se duplica.");
            var result = await RunAsync(["/duplicatescheme", _parameters.SourceSchemeGuid], cancellationToken);
            guid = ParseGuid(result, "duplicated power scheme");
        }
        // Revalidación JUSTO ANTES de escribir: el Preflight corrió en la fase de planificación, antes
        // del snapshot, y el estado pudo cambiar. Se relee /qh del plan destino y se comprueba el valor.
        var enumerated = await QuerySettingsAsync(guid, cancellationToken);
        foreach (var setting in _parameters.Settings)
        {
            var found = enumerated.FirstOrDefault(q =>
                string.Equals(q.SubgroupGuid, setting.SubgroupGuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(q.SettingGuid, setting.SettingGuid, StringComparison.OrdinalIgnoreCase));
            if (found is null) throw new ActionNotApplicableException($"El sistema dejó de enumerar el ajuste {setting.SettingGuid}; no se escribe a ciegas.");
            if (!found.Allows(setting.AcValue)) throw new ActionNotApplicableException($"El valor AC {setting.AcValue} de {setting.SettingGuid} quedó fuera de {found.Describe()} antes de escribir.");

            await RunAsync(["/setacvalueindex", guid, setting.SubgroupGuid, setting.SettingGuid, setting.AcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            // Regla 1: solo AC. DC se escribe únicamente si el equipo NO tiene batería (y el dato es
            // conocido). Con batería o con dato desconocido no se escribe DC y se informa.
            if (WriteDc(context)) await RunAsync(["/setdcvalueindex", guid, setting.SubgroupGuid, setting.SettingGuid, setting.DcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        }
        if (!WriteDc(context)) context.Report("apply", 0.9, "Equipo con batería o dato de batería desconocido: no se escribió ningún valor de CC.");
        var active = await GetActivePlanAsync(cancellationToken);
        if (_parameters.Activate || string.Equals(active, guid, StringComparison.OrdinalIgnoreCase)) await RunAsync(["/setactive", guid], cancellationToken);
        context.Report("apply", 1, $"Plan '{_parameters.PlanName}' configurado ({guid}).");
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (plan is null) return VerificationResult.Missing("El plan FragForge no existe.");
        var settingsOnly = _parameters.Mode.Equals("settingsOnly", StringComparison.OrdinalIgnoreCase);
        foreach (var setting in _parameters.Settings)
        {
            var current = await QuerySettingAsync(plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken);
            if (current.AcIndex != setting.AcValue) return VerificationResult.Missing($"El ajuste {setting.SettingGuid} no coincide en AC.");
            // DC solo se escribió si el equipo no tiene batería: no se exige cuando no se escribió.
            if (!settingsOnly && WriteDc(context) && current.DcIndex != setting.DcValue) return VerificationResult.Missing($"El ajuste {setting.SettingGuid} no coincide en DC.");
        }
        if (settingsOnly) return VerificationResult.Match($"Ajustes del plan '{_parameters.PlanName}' verificados (settingsOnly).");
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

        // ---- settingsOnly: revierte SOLO sus valores. Nunca borra el plan, nunca cambia el activo. ----
        if (_parameters.Mode.Equals("settingsOnly", StringComparison.OrdinalIgnoreCase))
        {
            var target = FindPlan(await ListPlansAsync(cancellationToken));
            if (target is null)
            {
                // El plan ya no existe: no hay nada que revertir y NO es un error.
                context.Report("revert", 1, $"Plan '{_parameters.PlanName}' ausente: nada que revertir.");
                return;
            }
            foreach (var setting in state.Settings)
            {
                await RunAsync(["/setacvalueindex", target.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, setting.AcIndex.ToString(CultureInfo.InvariantCulture)], cancellationToken);
                if (WriteDc(context)) await RunAsync(["/setdcvalueindex", target.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, setting.DcIndex.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            }
            // Regla 5: si el plan tocado es el ACTIVO, repetir /setactive <mismo GUID> para que el
            // sistema recalcule. Si no lo es, no se activa nada.
            var current = await GetActivePlanAsync(cancellationToken);
            if (string.Equals(current, target.Value.Guid, StringComparison.OrdinalIgnoreCase)) await RunAsync(["/setactive", target.Value.Guid], cancellationToken);
            context.Report("revert", 1, "Valores previos restaurados; el plan no se elimina y el plan activo no cambia.");
            return;
        }

        // ---- modo normal (dueño del plan): reactiva el previo y elimina SOLO el plan creado. ----
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

        if (_parameters.Mode.Equals("settingsOnly", StringComparison.OrdinalIgnoreCase))
        {
            var target = FindPlan(await ListPlansAsync(cancellationToken));
            // Plan ausente = nada que revertir: éxito con nota, no fallo.
            if (target is null) return VerificationResult.Match("Plan ausente: nada que revertir.");
            foreach (var setting in state.Settings)
            {
                var current = await QuerySettingAsync(target.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken);
                if (current.AcIndex != setting.AcIndex) return VerificationResult.Missing($"El ajuste {setting.SettingGuid} no volvió a su AC previo.");
            }
            return VerificationResult.Match("Valores previos restaurados (settingsOnly); el plan sigue existiendo.");
        }

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
        if (_parameters.Mode is not null && !_parameters.Mode.Equals("settingsOnly", StringComparison.OrdinalIgnoreCase))
            throw new CatalogValidationException("Modo de PowerPlan desconocido; solo se admite 'settingsOnly'.", [_parameters.Mode]);
        foreach (var setting in _parameters.Settings)
        {
            // B1: aquí solo se exige "entero >= 0". El rango o la lista válida NO es fija: la enumera
            // el sistema para ESE equipo y se comprueba en Preflight contra `powercfg /qh` (R-L).
            if (!Guid.TryParseExact(setting.SubgroupGuid, "D", out _) || !Guid.TryParseExact(setting.SettingGuid, "D", out _) || setting.AcValue < 0 || setting.DcValue < 0)
                throw new CatalogValidationException("GUID de ajuste inválido o valor negativo.", [setting.SettingGuid]);
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
    /// <summary>El plan de origen que se duplicará, resuelto por GUID (nunca por nombre: es localizable).</summary>
    private (string Guid, string Name)? FindSourcePlan(IReadOnlyList<(string Guid, string Name)> plans) =>
        plans.Where(p => p.Guid.Equals(_parameters.SourceSchemeGuid, StringComparison.OrdinalIgnoreCase)).Select(p => ((string Guid, string Name)?)p).FirstOrDefault();

    /// <summary>
    /// ¿Se escribe CC? Solo si el equipo NO tiene batería y el dato es conocido. Con batería o con
    /// dato desconocido (<c>IsPortable</c> nulo) no se escribe CC: escribir CC en un equipo con
    /// batería cambia el comportamiento en batería sin que el usuario lo haya pedido.
    /// </summary>
    private static bool WriteDc(ActionExecutionContext context) => !context.Hardware.FormFactor.HasBattery;

    /// <summary>Lee todos los settings enumerados de un plan con <c>/qh</c> y los parsea sin etiquetas.</summary>
    private async Task<IReadOnlyList<PowerSettingQuery>> QuerySettingsAsync(string planGuid, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/qh", planGuid], cancellationToken);
        return PowerCfgQueryParser.Parse(result.StandardOutput);
    }

    private async Task<PowerSettingCapture> QuerySettingAsync(string planGuid, string subgroupGuid, string settingGuid, CancellationToken cancellationToken)
    {
        // Un solo parser para todo (R-C): /query y /qh comparten la forma de bloque. Se acepta la
        // lectura por el parser y, si el bloque no trae dos índices, se dice en vez de adivinar.
        var result = await RunAsync(["/query", planGuid, subgroupGuid, settingGuid], cancellationToken);
        var parsed = PowerCfgQueryParser.Find(result.StandardOutput, subgroupGuid, settingGuid);
        if (parsed is null) throw new ActionNotApplicableException($"No se pudieron leer los índices AC/DC de {settingGuid} en el plan {planGuid}.");
        return new PowerSettingCapture(subgroupGuid, settingGuid, parsed.AcIndex, parsed.DcIndex);
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
    /// <summary>
    /// Modo de operación. <c>null</c>/ausente = comportamiento histórico (duplica el plan de origen,
    /// escribe los ajustes y activa). <c>settingsOnly</c> = solo escribe valores en un plan que YA
    /// existe: no duplica, no borra y no cambia el plan activo (lo usan los tweaks de settings).
    /// </summary>
    public string? Mode { get; init; }
    public IReadOnlyList<PowerPlanTarget> Settings { get; init; } = [];
}
public sealed record PowerPlanTarget
{
    public required string SubgroupGuid { get; init; }
    public required string SettingGuid { get; init; }
    public int AcValue { get; init; }
    public int DcValue { get; init; }
}
