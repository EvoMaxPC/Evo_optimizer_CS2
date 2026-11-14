using System.Globalization;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

/// <summary>
/// Lee y escribe propiedades avanzadas de un adaptador POR <c>RegistryKeyword</c> (Capa 5.2, R-C), nunca por
/// <c>DisplayName</c>.
///
/// <para>
/// El mapeo entre lo que se escribe (<c>RegistryValue</c>) y lo que el usuario ve (<c>DisplayValue</c>) NO se
/// hace con textos: <c>Get-NetAdapterAdvancedProperty</c> devuelve, por cada propiedad,
/// <c>ValidRegistryValues</c> y <c>ValidDisplayValues</c> como DOS listas paralelas. La posición es el mapeo.
/// Eso es lo que hace que funcione igual en es-MX y en en-US sin parsear ninguna etiqueta.
/// </para>
///
/// <para>
/// Solo se escribe un valor que esté en <c>ValidRegistryValues</c> (R-L): si el adaptador no lo enumera, la
/// escritura no se intenta. Si la propiedad no existe, el estado es <see cref="NetworkAdvancedProperty.Exists"/>
/// = <c>false</c> y el tweak queda No aplicable.
/// </para>
///
/// <para>
/// La salida de PowerShell se pide en JSON y se parsea por PROPIEDAD DEL OBJETO, nunca por texto de consola.
/// El objeto se serializa con <c>ConvertTo-Json</c> y las listas con <c>-Depth</c> suficiente para que no se
/// aplanen.
/// </para>
/// </summary>
public class NetworkAdvancedPropertyReader(IProcessRunner runner)
{
    /// <summary>Prefijo del comando. <c>-RegistryKeyword</c> exige el asterisco incluido (p. ej. <c>*EEE</c>).</summary>
    public const string Verb = "Get-NetAdapterAdvancedProperty";

    /// <summary>
    /// Lee una propiedad por keyword. <c>interfaceGuid</c> viene de
    /// <see cref="NetworkAdapterIdentity.InterfaceGuid"/> (R-I); se pasa a PowerShell por su GUID.
    /// </summary>
    // virtual a propósito: los tests de la ACCIÓN sustituyen esta lectura para no depender de PowerShell.
    // La lógica de parseo y de R-L se prueba aparte, contra el lector real.
    public virtual async Task<NetworkAdvancedProperty> ReadAsync(string interfaceGuid, string keyword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceGuid)) throw new ArgumentException("Se requiere InterfaceGuid.", nameof(interfaceGuid));
        if (string.IsNullOrWhiteSpace(keyword)) throw new ArgumentException("Se requiere RegistryKeyword.", nameof(keyword));

        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " +
            $"$p=Get-NetAdapterAdvancedProperty -InterfaceGuid '{Escape(interfaceGuid)}' -RegistryKeyword '{Escape(keyword)}' -ErrorAction SilentlyContinue; " +
            "if ($null -eq $p) { '{\"exists\":false}' } else { [pscustomobject]@{ " +
            "exists=$true; keyword=[string]$p.RegistryKeyword; registry=[string]$p.RegistryValue; " +
            "display=[string]$p.DisplayValue; validRegistry=@($p.ValidRegistryValues); validDisplay=@($p.ValidDisplayValues) " +
            "} | ConvertTo-Json -Compress -Depth 5 }";

        var result = await RunAsync(script, cancellationToken);
        return Parse(result.StandardOutput, interfaceGuid, keyword);
    }

    /// <summary>
    /// Escribe un valor <c>RegistryValue</c> solo si está enumerado por el adaptador. Devuelve el valor que
    /// quedó escrito.
    /// </summary>
    /// <exception cref="ActionNotApplicableException">La propiedad no existe o el valor no está enumerado.</exception>
    public virtual async Task<NetworkAdvancedProperty> WriteAsync(string interfaceGuid, string keyword, string registryValue, CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(interfaceGuid, keyword, cancellationToken);
        if (!current.Exists)
            throw new ActionNotApplicableException($"El adaptador {interfaceGuid} no expone '{keyword}'.");
        if (!current.ValidRegistryValues.Contains(registryValue, StringComparer.Ordinal))
            // R-L: no se escribe un valor que el adaptador no enumere. No se "prueba a ver si cuela".
            throw new ActionNotApplicableException($"'{keyword}' no enumera el valor '{registryValue}' en este adaptador. Valores válidos: {string.Join(", ", current.ValidRegistryValues)}.");

        var script = "$ErrorActionPreference='Stop'; " +
            $"Set-NetAdapterAdvancedProperty -InterfaceGuid '{Escape(interfaceGuid)}' -RegistryKeyword '{Escape(keyword)}' -RegistryValue @('{Escape(registryValue)}') -Confirm:$false -ErrorAction Stop";

        await RunAsync(script, cancellationToken);
        return await ReadAsync(interfaceGuid, keyword, cancellationToken);
    }

    /// <summary>
    /// Parsea la salida JSON del objeto que devuelve PowerShell. Separado para poder probarlo sin proceso.
    ///
    /// Acepta objeto único y array de un elemento: <c>Get-NetAdapterAdvancedProperty</c> devuelve un objeto
    /// cuando hay una coincidencia y <c>$null</c> cuando no la hay.
    /// </summary>
    internal static NetworkAdvancedProperty Parse(string json, string interfaceGuid, string keyword)
    {
        if (string.IsNullOrWhiteSpace(json)) return NetworkAdvancedProperty.Missing(interfaceGuid, keyword);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF'));
        }
        catch (JsonException)
        {
            // Una salida que no es JSON no se interpreta como "existe": R-L.
            return NetworkAdvancedProperty.Missing(interfaceGuid, keyword);
        }

        using (document)
        {
            var element = document.RootElement;
            // Si viniera como array, se toma el primer elemento; con más de uno no se elige: se ignora.
            if (element.ValueKind == JsonValueKind.Array)
            {
                if (element.GetArrayLength() != 1) return NetworkAdvancedProperty.Missing(interfaceGuid, keyword);
                element = element[0];
            }
            if (element.ValueKind != JsonValueKind.Object) return NetworkAdvancedProperty.Missing(interfaceGuid, keyword);
            if (!element.TryGetProperty("exists", out var exists) || exists.ValueKind != JsonValueKind.True) return NetworkAdvancedProperty.Missing(interfaceGuid, keyword);

            return new NetworkAdvancedProperty
            {
                InterfaceGuid = interfaceGuid,
                // El keyword que devuelve el adaptador manda: puede normalizar mayúsculas (p. ej. *eee).
                Keyword = OptionString(element, "keyword") ?? keyword,
                Exists = true,
                RegistryValue = OptionString(element, "registry") ?? "",
                DisplayValue = OptionString(element, "display"),
                // Posición a posición: es el ÚNICO mapeo válido sin leer etiquetas localizadas.
                ValidRegistryValues = OptionStrings(element, "validRegistry"),
                ValidDisplayValues = OptionStrings(element, "validDisplay")
            };
        }
    }

    private async Task<ProcessResult> RunAsync(string script, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest
        {
            FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            Timeout = TimeSpan.FromSeconds(60)
        }, cancellationToken);

        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }

    /// <summary>Comillas simples duplicadas: es como PowerShell escapa dentro de una cadena literal.</summary>
    private static string Escape(string value) => value.Replace("'", "''");

    private static string? OptionString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Lee una lista de cadenas. PowerShell colapsa un array de un elemento a un escalar en <c>ConvertTo-Json</c>
    /// cuando el <c>-Depth</c> no basta, así que se aceptan las dos formas.
    /// </summary>
    private static IReadOnlyList<string> OptionStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ValueKind == JsonValueKind.Number ? x.ToString() : null)
                .Where(x => x is not null).Select(x => x!).ToArray(),
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Number => [value.ToString()],
            _ => []
        };
    }
}

/// <summary>
/// Estado de una propiedad avanzada. <see cref="Exists"/> = <c>false</c> significa "el adaptador no la
/// expone", que es No aplicable, NO un valor por defecto.
/// </summary>
public sealed record NetworkAdvancedProperty
{
    public required string InterfaceGuid { get; init; }
    public required string Keyword { get; init; }
    public bool Exists { get; init; }
    /// <summary>Valor actual en forma de registro, p. ej. <c>0</c> o <c>Disabled</c>.</summary>
    public string RegistryValue { get; init; } = "";
    /// <summary>El mismo valor como lo muestra el sistema. Informativo: NUNCA se compara contra él.</summary>
    public string? DisplayValue { get; init; }
    /// <summary>Valores de registro admitidos por ESTE adaptador, en el orden que da el driver.</summary>
    public IReadOnlyList<string> ValidRegistryValues { get; init; } = [];
    /// <summary>Su pareja visible, misma posición que <see cref="ValidRegistryValues"/>.</summary>
    public IReadOnlyList<string> ValidDisplayValues { get; init; } = [];

    public static NetworkAdvancedProperty Missing(string interfaceGuid, string keyword) =>
        new() { InterfaceGuid = interfaceGuid, Keyword = keyword, Exists = false };

    /// <summary>
    /// Traduce un valor de registro a su texto visible usando la posición. Devuelve <c>null</c> si no está
    /// enumerado, en vez de inventar una etiqueta.
    /// </summary>
    public string? DisplayFor(string registryValue)
    {
        var index = -1;
        for (var i = 0; i < ValidRegistryValues.Count; i++)
        {
            if (string.Equals(ValidRegistryValues[i], registryValue, StringComparison.Ordinal)) { index = i; break; }
        }
        return index >= 0 && index < ValidDisplayValues.Count ? ValidDisplayValues[index] : null;
    }

    /// <summary>¿El adaptador enumera este valor? Es la condición previa a escribir (R-L).</summary>
    public bool Allows(string registryValue) => ValidRegistryValues.Contains(registryValue, StringComparer.Ordinal);
}
