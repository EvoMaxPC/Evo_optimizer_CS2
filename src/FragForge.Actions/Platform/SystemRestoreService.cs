using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Actions.Registry;

namespace FragForge.Actions.Platform;

[SupportedOSPlatform("windows")]
public sealed class SystemRestoreService(IWmiQuery wmi, IRegistry registry, IProcessRunner runner) : ISystemRestoreService
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string ValueName = "SystemRestorePointCreationFrequency";
    public async Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var systemDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
            var volumes = await wmi.QueryAsync(@"root\cimv2", "Win32_Volume", ["DeviceID", "DriveLetter"], $"DriveLetter='{systemDrive}'", cancellationToken);
            var device = volumes.FirstOrDefault()?["DeviceID"] as string;
            if (device is null) return FeatureState.Unknown;
            var storage = await wmi.QueryAsync(@"root\cimv2", "Win32_ShadowStorage", ["Volume", "DiffVolume", "AllocatedSpace", "MaximumSpace"], null, cancellationToken);
            return storage.Any(s => (s["Volume"] as string ?? s["DiffVolume"] as string ?? "").Contains(device, StringComparison.OrdinalIgnoreCase)) ? FeatureState.Enabled : FeatureState.Disabled;
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException or IOException) { return FeatureState.Unknown; }
    }
    public async Task EnableProtectionAsync(CancellationToken cancellationToken = default)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var script = $"$ErrorActionPreference='Stop'; Enable-ComputerRestore -Drive '{drive.Replace("'", "''")}' -Confirm:$false";
        var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], Timeout = TimeSpan.FromMinutes(2) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("Enable-ComputerRestore", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
    }
    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        if (description.Length > 256) throw new ArgumentException("Restore point description is limited to 256 characters.", nameof(description));
        var original = await registry.ReadValueAsync(Key, ValueName, cancellationToken);
        var missing = new List<string>();
        foreach (var ancestor in RegistryPath.AncestorsFromRoot(Key)) if (!await registry.KeyExistsAsync(ancestor, cancellationToken)) missing.Add(ancestor);
        var capture = RegistryValueConverter.Capture(Key, ValueName, original, missing);
        await registry.WriteValueAsync(Key, ValueName, new RegistryWriteValue("DWord", 0), cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() =>
            {
                using var cls = new ManagementClass(@"root\default:SystemRestore");
                using var input = cls.GetMethodParameters("CreateRestorePoint");
                input["Description"] = description;
                input["RestorePointType"] = 12u;
                input["EventType"] = 100u;
                using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
                return Convert.ToUInt32(output["ReturnValue"]);
            }, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != 0) throw new FragForgeException("restore_point_failed", $"SystemRestore.CreateRestorePoint devolvió {result}. No se aplicarán cambios.");
            long? sequence = null;
            try
            {
                var points = await wmi.QueryAsync(@"root\default", "SystemRestore", ["Description", "SequenceNumber"], null, CancellationToken.None);
                var point = points.Where(p => (p["Description"] as string) == description).OrderByDescending(p => Convert.ToInt64(p["SequenceNumber"] ?? 0L)).FirstOrDefault();
                if (point is not null) sequence = Convert.ToInt64(point["SequenceNumber"]);
            }
            catch (ManagementException) { }
            return new RestorePointResult(sequence, sequence is null ? "Punto creado; WMI no expuso su número de secuencia." : $"Punto de restauración {sequence} creado.");
        }
        finally
        {
            try
            {
                if (capture.ValueExisted) await registry.WriteValueAsync(Key, ValueName, RegistryValueConverter.ToWriteValue(capture), CancellationToken.None);
                else await registry.DeleteValueAsync(Key, ValueName, CancellationToken.None);
                foreach (var ancestor in capture.MissingAncestors)
                {
                    var parent = ancestor[..ancestor.LastIndexOf('\\')];
                    var child = ancestor[(ancestor.LastIndexOf('\\') + 1)..];
                    await registry.DeleteEmptySubKeyAsync(parent, child, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new RollbackException("No se pudo restaurar SystemRestorePointCreationFrequency tras crear el punto.", ex); }
        }
    }
}
