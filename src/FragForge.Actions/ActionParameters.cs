using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions;

internal static class ActionParameters
{
    public static T Parse<T>(ActionSpec specification)
    {
        var json = specification.Parameters.ValueKind == JsonValueKind.Undefined ? "{}" : specification.Parameters.GetRawText();
        try { return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? throw new JsonException("Null action parameters."); }
        catch (JsonException ex) { throw new CatalogValidationException($"Parámetros inválidos para la acción '{specification.Kind}'.", [ex.Message]); }
    }
    public static string ResolveToken(string value, ActionExecutionContext context) => value switch
    {
        "{Cs2Executable}" => context.Hardware.Games.Cs2ExecutablePath ?? throw new ActionNotApplicableException("No se detectó cs2.exe; usa la ruta manual."),
        "{Cs2Directory}" => Path.GetDirectoryName(context.Hardware.Games.Cs2ExecutablePath ?? throw new ActionNotApplicableException("No se detectó CS2."))!,
        _ => value
    };
}
internal sealed record RegistryActionParameters { public IReadOnlyList<RegistryValueTarget> Values { get; init; } = []; }
internal sealed record RegistryValueTarget
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public JsonElement Value { get; init; }
    public string Mode { get; init; } = "Set";
}
