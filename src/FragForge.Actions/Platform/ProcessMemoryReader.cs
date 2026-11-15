using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Platform;

/// <summary>Consulta WMI de solo lectura; no abre ni modifica procesos.</summary>
public sealed class ProcessMemoryReader(IWmiQuery wmi)
{
    public async Task<MemorySnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var processes = await wmi.QueryAsync(@"root\cimv2", "Win32_Process",
            ["Name", "ProcessId", "WorkingSetSize", "PrivatePageCount", "ExecutablePath"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var memory = await wmi.QueryAsync(@"root\cimv2", "Win32_PerfFormattedData_PerfOS_Memory",
            ["AvailableMBytes", "CommittedBytes", "PercentCommittedBytesInUse"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var rows = processes.Select(row =>
        {
            var name = WmiValues.String(row, "Name");
            var rawPid = WmiValues.UInt64(row, "ProcessId");
            long? pid = rawPid is <= long.MaxValue ? (long)rawPid.Value : null;
            var (classification, reason) = ProtectedProcesses.Classify(name, pid);
            return new ProcessMemoryRow(name, pid, ToMb(WmiValues.UInt64(row, "WorkingSetSize")),
                ToMb(WmiValues.UInt64(row, "PrivatePageCount")), classification, reason);
        }).ToArray();
        var system = memory.FirstOrDefault();
        return new MemorySnapshot(system is null ? null : new SystemMemorySnapshot(
            WmiValues.UInt64(system, "AvailableMBytes"), ToMb(WmiValues.UInt64(system, "CommittedBytes")),
            WmiValues.UInt64(system, "PercentCommittedBytesInUse")), ProcessMemoryGrouping.Group(rows)) { ProcessRows = rows };
    }

    private static double? ToMb(ulong? bytes) => bytes is null ? null : bytes.Value / 1048576.0;
}
