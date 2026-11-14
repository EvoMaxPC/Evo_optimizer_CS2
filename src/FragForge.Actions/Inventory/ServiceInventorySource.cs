using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Inventory;

namespace FragForge.Actions.Inventory;

/// <summary>
/// Inventario de servicios por WMI (<c>Win32_Service</c>) más el registro para
/// <c>DelayedAutostart</c>. Solo lectura.
///
/// Servicios por usuario: Windows crea instancias con sufijo variable
/// (<c>CDPUserSvc_5f2a1</c>). El catálogo apunta a la PLANTILLA (<c>CDPUserSvc</c>) y el motor escribe
/// sobre <c>HKLM\SYSTEM\CurrentControlSet\Services\CDPUserSvc\Start</c>; aquí se agrupan las instancias
/// bajo su plantilla para que el diagnóstico muestre el estado real sin confundirlo con un servicio más.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsServiceInventorySource(IRegistry registry) : IServiceInventorySource
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";

    public Task<(IReadOnlyList<ServiceEntry> Services, IReadOnlyList<UserServiceTemplate> Templates, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var warnings = new List<string>();
        var entries = new List<ServiceEntry>();
        var raw = new List<(string Name, string DisplayName, string StartMode, string State, string? Path, string? Account, string[] DependsOn)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery("SELECT Name,DisplayName,StartMode,State,PathName,StartName FROM Win32_Service"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(30) });
            using var results = searcher.Get();
            foreach (var item in results.Cast<ManagementObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var service = item;
                raw.Add((
                    service["Name"] as string ?? "",
                    service["DisplayName"] as string ?? "",
                    service["StartMode"] as string ?? "Unknown",
                    service["State"] as string ?? "Unknown",
                    service["PathName"] as string,
                    service["StartName"] as string,
                    service.GetPropertyValue("DependsOnService") as string[] ?? []));
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            warnings.Add("Win32_Service: " + ex.Message);
        }

        var dependentsByService = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in raw)
            foreach (var dependency in service.DependsOn)
            {
                if (!dependentsByService.TryGetValue(dependency, out var list)) dependentsByService[dependency] = list = [];
                list.Add(service.Name);
            }

        foreach (var service in raw)
        {
            var delayed = await registry.ReadValueAsync(ServicesKey + "\\" + service.Name, "DelayedAutostart", cancellationToken);
            var dependents = dependentsByService.TryGetValue(service.Name, out var list) ? list : [];
            entries.Add(new ServiceEntry
            {
                Name = service.Name,
                DisplayName = service.DisplayName,
                StartMode = service.StartMode,
                State = service.State,
                DelayedAutoStart = delayed.ValueExists ? Convert.ToInt32(delayed.Data) != 0 : null,
                Account = service.Account,
                ExecutablePath = service.Path,
                DependsOn = service.DependsOn,
                Dependents = dependents,
                AlreadyOptimized = false,
                DependencyEvidence = dependents.Select(d => new ServiceDependency(d, true, "Win32_Service.DependsOnService")).ToArray()
            });
        }

        var templates = new List<UserServiceTemplate>();
        foreach (var group in entries.Where(e => UserServiceSuffix().IsMatch(e.Name)).GroupBy(e => UserServiceSuffix().Replace(e.Name, ""), StringComparer.OrdinalIgnoreCase))
        {
            var startKey = await registry.ReadValueAsync(ServicesKey + "\\" + group.Key, "Start", cancellationToken);
            templates.Add(new UserServiceTemplate
            {
                Template = group.Key,
                StartMode = startKey.ValueExists ? DescribeStart(Convert.ToInt32(startKey.Data)) : null,
                Instances = group.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                RegistryKey = ServicesKey + "\\" + group.Key
            });
        }

        return ((IReadOnlyList<ServiceEntry>)entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(), (IReadOnlyList<UserServiceTemplate>)templates, (IReadOnlyList<string>)warnings);
    }, cancellationToken);

    /// <summary>Traduce el valor <c>Start</c> del registro al vocabulario de <c>ServiceCapture.StartMode</c>.</summary>
    public static string DescribeStart(int start) => start switch
    {
        0 => "Boot",
        1 => "System",
        2 => "Auto",
        3 => "Manual",
        4 => "Disabled",
        _ => $"Unknown({start})"
    };

    [GeneratedRegex("_[0-9a-fA-F]{4,}$")]
    private static partial Regex UserServiceSuffix();
}
