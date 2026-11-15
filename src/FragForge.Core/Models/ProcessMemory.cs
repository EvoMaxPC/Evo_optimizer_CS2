namespace FragForge.Core.Models;

public static class ProtectedProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "steam", "steamwebhelper", "steamservice", "gameoverlayui", "vgc", "vgk", "vgtray",
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "services", "lsass", "svchost", "winlogon", "dwm", "explorer", "fontdrvhost", "audiodg", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "MsMpEng", "SecurityHealthService"
    };

    public static string Normalize(string? name)
    {
        var result = name?.Trim() ?? "";
        return result.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }

    public static (string Classification, string Reason) Classify(string? name, long? pid)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0) return ("protegido", "nombre desconocido");
        if (pid is null) return ("protegido", "PID desconocido");
        if (pid <= 4) return ("protegido", "PID de sistema");
        if (Names.Contains(normalized) || normalized.StartsWith("lghub", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("FragForge", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase))
            return ("protegido", "nombre protegido");
        return ("no protegido", "no coincide con la lista de protección");
    }
}

public sealed record ProcessMemoryRow(string? Name, long? ProcessId, double? WorkingSetMb, double? PrivateMb, string Classification, string Reason);
public sealed record ProcessMemoryGroup(string Name, int Instances, double? WorkingSetMb, double? PrivateMb, string Classification, string Reason);
public sealed record SystemMemorySnapshot(double? AvailableMb, double? CommittedMb, double? PercentCommittedBytesInUse);
public sealed record MemorySnapshot(SystemMemorySnapshot? System, IReadOnlyList<ProcessMemoryGroup> Processes);

public static class ProcessMemoryGrouping
{
    public static IReadOnlyList<ProcessMemoryGroup> Group(IEnumerable<ProcessMemoryRow> rows) => rows
        .GroupBy(row => ProtectedProcesses.Normalize(row.Name), StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var list = group.ToArray();
            var protectedRow = list.FirstOrDefault(row => row.Classification == "protegido");
            return new ProcessMemoryGroup(group.Key.Length == 0 ? "(sin nombre)" : group.Key, list.Length,
                Sum(list.Select(row => row.WorkingSetMb)), Sum(list.Select(row => row.PrivateMb)),
                protectedRow?.Classification ?? "no protegido", protectedRow?.Reason ?? "no coincide con la lista de protección");
        })
        .OrderByDescending(row => row.WorkingSetMb.HasValue).ThenByDescending(row => row.WorkingSetMb)
        .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private static double? Sum(IEnumerable<double?> values)
    {
        double total = 0;
        foreach (var value in values)
        {
            if (value is null) return null;
            total += value.Value;
        }
        return total;
    }
}
