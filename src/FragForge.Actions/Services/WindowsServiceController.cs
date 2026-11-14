using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController(IRegistry registry) : IServiceController
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery($"SELECT Name,StartMode,State FROM Win32_Service WHERE Name='{Escape(name)}'"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var results = searcher.Get();
        using var service = results.Cast<ManagementObject>().FirstOrDefault();
        if (service is null) return new ServiceCapture { Name = name, Exists = false };
        var delayed = await registry.ReadValueAsync(ServicesKey + "\\" + name, "DelayedAutostart", cancellationToken);
        return new ServiceCapture { Name = name, Exists = true, StartMode = service["StartMode"] as string, State = service["State"] as string, DelayedAutoStart = delayed.ValueExists ? Convert.ToInt32(delayed.Data) != 0 : false, DelayedAutoStartExisted = delayed.ValueExists };
    }, cancellationToken);
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = Find(name);
        using var parameters = service.GetMethodParameters("ChangeStartMode");
        parameters["StartMode"] = startMode switch { "Auto" => "Automatic", "Manual" => "Manual", "Disabled" => "Disabled", _ => throw new ArgumentException("Invalid start mode.") };
        using var result = service.InvokeMethod("ChangeStartMode", parameters, null);
        var code = Convert.ToUInt32(result["ReturnValue"]);
        // 5 = acceso denegado, 1 = no soportado, 10 = servicio deshabilitado, 21 = parámetro inválido.
        if (code != 0) throw new ServiceChangeRejectedException(name, "ChangeStartMode", code);
        if (delayedAutoStart is not null)
        {
            var key = ServicesKey + "\\" + name;
            if (restoreAbsentDelayed) await registry.DeleteValueAsync(key, "DelayedAutostart", cancellationToken);
            else await registry.WriteValueAsync(key, "DelayedAutostart", new RegistryWriteValue("DWord", delayedAutoStart.Value ? 1 : 0), cancellationToken);
        }
    }, cancellationToken);
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = Find(name);
        var current = service["State"] as string;
        if (string.Equals(current, state, StringComparison.OrdinalIgnoreCase)) return;
        var method = state switch { "Running" => "StartService", "Stopped" => "StopService", _ => throw new ArgumentException("Invalid service state.") };
        using var result = service.InvokeMethod(method, null, null);
        var code = Convert.ToUInt32(result["ReturnValue"]);
        if (code is not (0 or 10)) throw new ServiceChangeRejectedException(name, method, code);
    }, cancellationToken);
    private static ManagementObject Find(string name)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery($"SELECT * FROM Win32_Service WHERE Name='{Escape(name)}'"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().FirstOrDefault() ?? throw new ActionNotApplicableException($"El servicio {name} no existe.");
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
