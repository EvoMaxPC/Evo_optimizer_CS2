using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;

namespace FragForge.Actions.Platform;

[SupportedOSPlatform("windows")]
public sealed class WmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!System.Text.RegularExpressions.Regex.IsMatch(className, "^[A-Za-z_][A-Za-z0-9_]*$") || properties.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p, "^[A-Za-z_][A-Za-z0-9_]*$"))) throw new ArgumentException("Invalid WMI identifier.");
        var query = "SELECT " + (properties.Count == 0 ? "*" : string.Join(",", properties)) + " FROM " + className + (where is null ? "" : " WHERE " + where);
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = false });
        using var collection = searcher.Get();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in collection)
        {
            using var managementObject = (ManagementBaseObject)item;
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.Count == 0 ? managementObject.Properties.Cast<PropertyData>().Select(p => p.Name) : properties)
            {
                try { row[property] = Normalize(managementObject[property]); }
                catch (ManagementException) { row[property] = null; }
            }
            rows.Add(row);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)rows;
    }, cancellationToken);
    private static object? Normalize(object? value) => value switch
    {
        string[] strings => strings.ToArray(),
        ushort[] numbers => numbers.ToArray(),
        uint[] numbers => numbers.ToArray(),
        ulong[] numbers => numbers.ToArray(),
        bool[] values => values.ToArray(),
        ManagementBaseObject embedded => embedded.ToString(),
        _ => value
    };
}
