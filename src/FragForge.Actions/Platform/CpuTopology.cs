using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FragForge.Actions.Platform;

public sealed record CpuTopologyResult(int? PCores, int? ECores, int LogicalProcessors, bool? IsHybrid, string Source);
public interface ICpuTopologyProbe
{
    Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default);
}
public static class CpuTopologyInterpreter
{
    public static CpuTopologyResult FromEfficiencyClasses(IReadOnlyList<(byte EfficiencyClass, int LogicalProcessors)> cores)
    {
        if (cores.Count == 0 || cores.Any(c => c.LogicalProcessors <= 0)) return new(null, null, 0, null, "Unavailable");
        var classes = cores.Select(c => c.EfficiencyClass).Distinct().OrderBy(x => x).ToArray();
        var logical = cores.Sum(c => c.LogicalProcessors);
        if (classes.Length == 1) return new(null, null, logical, false, "GetLogicalProcessorInformationEx: single EfficiencyClass");
        var max = classes[^1];
        var p = cores.Count(c => c.EfficiencyClass == max);
        var e = cores.Count - p;
        return new(p, e, logical, true, "GetLogicalProcessorInformationEx: distinct EfficiencyClass values");
    }
}
[SupportedOSPlatform("windows")]
public sealed class WindowsCpuTopologyProbe : ICpuTopologyProbe
{
    public Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint size = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        if (size == 0 || size > 1024 * 1024) return Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, "GetLogicalProcessorInformationEx failed"));
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size)) return Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, $"GetLogicalProcessorInformationEx error {Marshal.GetLastWin32Error()}"));
            var cores = new List<(byte, int)>();
            var offset = 0;
            while (offset + 8 <= size)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var recordSize = Marshal.ReadInt32(buffer, offset + 4);
                if (recordSize < 8 || offset + recordSize > size) throw new InvalidDataException("Invalid processor information record.");
                if (relationship == 0)
                {
                    var efficiency = Marshal.ReadByte(buffer, offset + 9);
                    var groupCount = Marshal.ReadInt16(buffer, offset + 30);
                    var threads = 0;
                    for (var g = 0; g < groupCount; g++)
                    {
                        var mask = Marshal.ReadIntPtr(buffer, offset + 32 + g * (IntPtr.Size == 8 ? 24 : 16));
                        threads += BitOperations.PopCount(unchecked((ulong)mask.ToInt64()));
                    }
                    cores.Add((efficiency, threads));
                }
                offset += recordSize;
            }
            return Task.FromResult(CpuTopologyInterpreter.FromEfficiencyClasses(cores));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
}
