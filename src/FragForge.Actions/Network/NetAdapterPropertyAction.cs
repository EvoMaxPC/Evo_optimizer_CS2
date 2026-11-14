using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

/// <summary>
/// Propiedades avanzadas de un adaptador de red (Capa 5.1/5.2).
///
/// <para>
/// <b>La identificación por nombre visible está ELIMINADA de esta ruta.</b> Antes, el parámetro
/// <c>adapterName</c> aceptaba <c>{AdapterDescription:I225-V}</c> y se resolvía con
/// <c>Description.Contains(...)</c> sobre el perfil: eso falla en cuanto Intel cambia el texto del driver o el
/// usuario renombra la conexión, y puede coincidir con dos adaptadores. Ahora se exige IDENTIDAD DE HARDWARE
/// (R-I): <c>hardwareIdContains</c> y/o <c>interfaceGuid</c>, resueltos por <see cref="INetworkAdapterResolver"/>.
/// </para>
///
/// <para>
/// Cada propiedad se escribe por <see cref="NetworkAdvancedPropertyReader"/> (<c>RegistryKeyword</c>) y solo
/// si el adaptador la enumera en <c>ValidRegistryValues</c> (R-L). El snapshot captura TODAS las propiedades
/// tocadas antes de escribir (5.6) y el revert las restaura en orden inverso.
/// </para>
/// </summary>
public sealed class NetAdapterPropertyAction(
    INetworkAdapterResolver resolver,
    NetworkAdvancedPropertyReader reader,
    IProtectionPolicy policy,
    ActionSpec specification) : ITweakAction
{
    private readonly NetworkParameters _parameters = ActionParameters.Parse<NetworkParameters>(specification);

    public string Kind => "NetAdapterProperty";

    public async Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (_parameters.Properties.Count == 0) throw new CatalogValidationException("Network action requires properties.", ["properties"]);

        var adapter = await RequireAdapterAsync(cancellationToken);
        foreach (var property in _parameters.Properties)
        {
            policy.EnsureNetworkPropertyAllowed(adapter.Name, property.Keyword);
            var current = await reader.ReadAsync(adapter.InterfaceGuid!, property.Keyword, cancellationToken);
            if (!current.Exists) throw new ActionNotApplicableException($"El adaptador {adapter.InterfaceGuid} no expone '{property.Keyword}'.");
            // R-L: si el valor pedido no está enumerado, el tweak no se aplica. No se "prueba a ver".
            foreach (var value in property.Value)
            {
                if (!current.Allows(value))
                    throw new ActionNotApplicableException($"'{property.Keyword}' no enumera '{value}' en este adaptador. Valores válidos: {string.Join(", ", current.ValidRegistryValues)}.");
            }
        }
    }

    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = await RequireAdapterAsync(cancellationToken);
        var captures = new List<NetworkPropertyCapture>();
        foreach (var property in _parameters.Properties)
        {
            var current = await reader.ReadAsync(adapter.InterfaceGuid!, property.Keyword, cancellationToken);
            captures.Add(new NetworkPropertyCapture
            {
                AdapterName = adapter.InterfaceGuid!,
                Keyword = current.Keyword,
                Exists = current.Exists,
                // Un valor único se guarda como lista de uno: es lo que acepta la escritura.
                RegistryValue = current.Exists ? [current.RegistryValue] : [],
                DisplayValue = current.DisplayValue
            });
        }
        return new NetworkPropertyActionState { Properties = captures };
    }

    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = await RequireAdapterAsync(cancellationToken);
        foreach (var property in _parameters.Properties)
        {
            policy.EnsureNetworkPropertyAllowed(adapter.Name, property.Keyword);
            foreach (var value in property.Value)
                await reader.WriteAsync(adapter.InterfaceGuid!, property.Keyword, value, cancellationToken);
        }
    }

    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = await RequireAdapterAsync(cancellationToken);
        foreach (var property in _parameters.Properties)
        {
            var current = await reader.ReadAsync(adapter.InterfaceGuid!, property.Keyword, cancellationToken);
            var expected = property.Value.Count > 0 ? property.Value[0] : "";
            if (!current.Exists || !string.Equals(current.RegistryValue, expected, StringComparison.Ordinal))
                return VerificationResult.Missing($"{adapter.InterfaceGuid}:{property.Keyword} no coincide ({current.RegistryValue} != {expected}).");
        }
        return VerificationResult.Match("Propiedades avanzadas del adaptador verificadas.");
    }

    /// <summary>
    /// Restaura lo capturado. Usa el GUID guardado en el snapshot, no el del parámetro: si el adaptador
    /// cambió de índice o de nombre, el revert sigue apuntando al mismo dispositivo.
    /// </summary>
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        foreach (var property in ((NetworkPropertyActionState)before).Properties.Reverse())
        {
            policy.EnsureNetworkPropertyAllowed(property.AdapterName, property.Keyword);
            if (!property.Exists) throw new InvalidOperationException("Cannot remove an advanced property; capture required an existing property.");
            // Se reescribe el valor que HABÍA. Si ya no está enumerado, WriteAsync lanza y el motor marca
            // RollbackFailed: no se oculta (5.6).
            await reader.WriteAsync(property.AdapterName, property.Keyword, property.RegistryValue[0], cancellationToken);
        }
    }

    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) =>
        Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken))
            ? VerificationResult.Match("Propiedades de red restauradas.")
            : VerificationResult.Missing("Las propiedades de red no volvieron al estado capturado.");

    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = await RequireAdapterAsync(cancellationToken);
        var lines = new List<string>
        {
            // El aviso de la desconexión va ANTES de la lista: es lo que el usuario tiene que saber (5.3).
            "AVISO: cambiar una propiedad avanzada del adaptador puede reiniciar el enlace y cortar la conexión unos segundos."
        };
        foreach (var property in _parameters.Properties)
        {
            var current = await reader.ReadAsync(adapter.InterfaceGuid!, property.Keyword, cancellationToken);
            var target = property.Value.Count > 0 ? property.Value[0] : "";
            var display = current.DisplayFor(target);
            lines.Add($"{adapter.InterfaceGuid}:{property.Keyword}: [{current.RegistryValue}] -> [{target}]{(display is null ? "" : $" ({display})")}. Revert restaura el valor previo.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Resuelve el adaptador por hardware. Si no hay una coincidencia ÚNICA, no aplica: no se cae al nombre.
    /// </summary>
    private async Task<NetworkAdapterIdentity> RequireAdapterAsync(CancellationToken cancellationToken)
    {
        var selector = new NetworkAdapterSelector
        {
            InterfaceGuid = _parameters.InterfaceGuid,
            HardwareIdContains = _parameters.HardwareIdContains
        };
        if (!selector.HasHardwareIdentity)
            throw new CatalogValidationException("Network action requires a hardware identity (interfaceGuid or hardwareIdContains).", ["adapter"]);

        var adapter = await resolver.ResolveAsync(selector, cancellationToken);
        if (adapter is null)
            throw new ActionNotApplicableException($"No hay un adaptador físico único para interfaceGuid={_parameters.InterfaceGuid ?? "-"} hardwareId~={_parameters.HardwareIdContains ?? "-"}.");
        return adapter;
    }
}

/// <summary>Parámetros de la acción. La identidad es de hardware; NO existe un campo de nombre visible.</summary>
internal sealed record NetworkParameters
{
    /// <summary>GUID de interfaz exacto. Alternativa a <see cref="HardwareIdContains"/>.</summary>
    public string? InterfaceGuid { get; init; }
    /// <summary>Fragmento del hardware ID (<c>PnPDeviceID</c>), p. ej. <c>DEV_15F3</c>.</summary>
    public string? HardwareIdContains { get; init; }
    public IReadOnlyList<NetworkPropertyTarget> Properties { get; init; } = [];
}

internal sealed record NetworkPropertyTarget
{
    public required string Keyword { get; init; }
    public IReadOnlyList<string> Value { get; init; } = [];
}
