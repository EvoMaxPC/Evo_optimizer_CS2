using System.Globalization;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

/// <summary>
/// Resolutor de adaptadores de red por IDENTIDAD DE HARDWARE (R-I, Capa 5.1).
///
/// Dos fuentes, ninguna de ellas el nombre visible:
/// <list type="bullet">
/// <item><c>root\StandardCimv2:MSFT_NetAdapter</c> — <c>InterfaceGuid</c>, <c>PnPDeviceID</c>,
///       <c>NdisPhysicalMedium</c>, <c>Speed</c>, <c>HardwareInterface</c>, <c>Virtual</c>, <c>State</c>.</item>
/// <item><c>HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\&lt;NNNN&gt;</c>
///       — <c>NetCfgInstanceId</c>. Es la misma identidad que <c>InterfaceGuid</c>, pero en el registro, y
///       es la que se usa para escribir <c>NetAdapterAdvancedProperty</c> sin depender del nombre.</item>
/// </list>
///
/// La lista de propiedades de <c>MSFT_NetAdapter</c> NO se inventa aquí: se reutiliza
/// <see cref="Platform.WindowsHardwareProfiler.NetAdapterProperties"/>, que está verificada contra la clase
/// real por <c>NetAdapterPropertiesTests</c>. Solo se añaden las cuatro que el perfil no pedía
/// (<c>PnPDeviceID</c>, <c>InterfaceIndex</c>, <c>MaxSpeed</c>, <c>MediaConnectState</c>), y también están en
/// el fixture medido.
///
/// Si una consulta falla, la lista sale vacía y NO se adivina nada (R-L). Un adaptador sin
/// <c>InterfaceGuid</c> no se devuelve: sin identidad no hay ruta de apply.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsNetworkAdapterResolver(IWmiQuery wmi, IRegistry registry) : INetworkAdapterResolver
{
    /// <summary>Namespace de <c>MSFT_NetAdapter</c>. Igual que en el perfil de hardware.</summary>
    public const string Scope = @"root\StandardCimv2";

    /// <summary>Clase de la que salen las identidades.</summary>
    public const string ClassName = "MSFT_NetAdapter";

    /// <summary>Clave de la clase de red del registro, donde vive <c>NetCfgInstanceId</c>.</summary>
    public const string NetworkClassKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Propiedades que se piden. Las diez primeras son exactamente las del perfil (verificadas contra la
    /// clase real); las cuatro últimas también pertenecen a la clase según el fixture medido.
    /// </summary>
    public static readonly string[] Properties =
    [
        .. Platform.WindowsHardwareProfiler.NetAdapterProperties,
        "PnPDeviceID",
        "InterfaceIndex",
        "MaxSpeed",
        "MediaConnectState"
    ];

    public async Task<IReadOnlyList<NetworkAdapterIdentity>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = await wmi.QueryAsync(Scope, ClassName, Properties, where: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin consulta no hay identidades. No se rellena con nada.
            return [];
        }

        var netCfg = await ReadNetCfgInstanceIdsAsync(cancellationToken);
        var result = new List<NetworkAdapterIdentity>();
        foreach (var row in rows)
        {
            var interfaceGuid = NormalizeGuid(WmiString(row, "InterfaceGuid"));
            // R-I: sin InterfaceGuid no hay identidad de hardware, así que no entra en la ruta de apply.
            if (interfaceGuid is null) continue;

            var hardwareId = WmiString(row, "PnPDeviceID");
            var medium = WmiInt32(row, "NdisPhysicalMedium");
            result.Add(new NetworkAdapterIdentity
            {
                Name = WmiString(row, "Name") ?? "Unknown",
                Description = WmiString(row, "InterfaceDescription") ?? "Unknown",
                InterfaceGuid = interfaceGuid,
                NetCfgInstanceId = netCfg.TryGetValue(interfaceGuid, out var id) ? id : null,
                HardwareId = hardwareId,
                Kind = Platform.WindowsHardwareProfiler.MapPhysicalMedium(medium),
                // Speed es la velocidad negociada; MaxSpeed es la capacidad. Se prefiere Speed y se cae a
                // MaxSpeed solo si Speed falta, sin fusionarlos.
                LinkSpeedBitsPerSecond = WmiUInt64(row, "Speed") ?? WmiUInt64(row, "MaxSpeed"),
                NdisPhysicalMedium = medium,
                IsPhysical = WmiBool(row, "HardwareInterface") == true && WmiBool(row, "Virtual") != true,
                State = WmiString(row, "State") ?? "Unknown"
            });
        }
        return result;
    }

    /// <summary>
    /// Resuelve por InterfaceGuid, NetCfgInstanceId o hardware ID. Devuelve <c>null</c> si no hay una
    /// coincidencia ÚNICA: con dos candidatos no se elige uno al azar.
    /// </summary>
    public async Task<NetworkAdapterIdentity?> ResolveAsync(NetworkAdapterSelector selector, CancellationToken cancellationToken = default)
    {
        // Un selector sin identidad de hardware no resuelve nada: es la garantía de que no se vuelve a
        // identificar un adaptador por su nombre visible.
        if (!selector.HasHardwareIdentity) return null;

        var adapters = await ListAsync(cancellationToken);
        var wantedGuid = NormalizeGuid(selector.InterfaceGuid);
        var wantedNetCfg = NormalizeGuid(selector.NetCfgInstanceId);
        var wantedHardware = selector.HardwareIdContains?.Trim();

        var matches = adapters.Where(a =>
            (wantedGuid is null || string.Equals(a.InterfaceGuid, wantedGuid, StringComparison.OrdinalIgnoreCase)) &&
            (wantedNetCfg is null || string.Equals(a.NetCfgInstanceId, wantedNetCfg, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(wantedHardware) || (a.HardwareId?.Contains(wantedHardware, StringComparison.OrdinalIgnoreCase) ?? false))
        ).ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Lee <c>NetCfgInstanceId</c> de cada subclave <c>NNNN</c> de la clase de red y lo indexa por GUID
    /// normalizado. Las subclaves se recorren por su nombre de cuatro dígitos, no por su descripción.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadNetCfgInstanceIdsAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var subKeys = await registry.GetSubKeyNamesAsync(NetworkClassKey, cancellationToken);
            foreach (var subKey in subKeys)
            {
                // Solo las instancias: 0000..9999. Cualquier otra subclave (Properties, etc.) se ignora.
                if (subKey.Length != 4 || !subKey.All(char.IsAsciiDigit)) continue;
                var read = await registry.ReadValueAsync(NetworkClassKey + "\\" + subKey, "NetCfgInstanceId", cancellationToken);
                if (!read.ValueExists || read.Data is not string raw) continue;
                // Se indexa por la forma normalizada pero se conserva el GUID tal como lo devolvió el
                // registro, que es lo que se muestra y se guarda en el snapshot.
                var normalized = NormalizeGuid(raw);
                if (normalized is not null) map[normalized] = raw.Trim();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin registro no hay NetCfgInstanceId; la identidad por InterfaceGuid sigue siendo válida.
            return map;
        }
        return map;
    }

    /// <summary>
    /// Normaliza un GUID a minúsculas y con llaves, para comparar siempre igual. Devuelve <c>null</c> si no
    /// es un GUID: un texto que no parsea no se usa como identidad.
    /// </summary>
    public static string? NormalizeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value.Trim(), out var guid) ? guid.ToString("B").ToLowerInvariant() : null;
    }

    private static string? WmiString(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static int? WmiInt32(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;

    private static ulong? WmiUInt64(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : null;

    private static bool? WmiBool(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? Convert.ToBoolean(value, CultureInfo.InvariantCulture) : null;
}
