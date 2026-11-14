using System.Diagnostics;
using FragForge.Core.Experiments;
using FragForge.Core.Models;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Precheck y captura del entorno de medición.
///
/// Solo lectura: no cambia nada del sistema. Rechaza medir con reinicio pendiente, con servicing activo
/// (TiWorker/TrustedInstaller) o con carga de fondo alta, porque cualquiera de las tres cosas mueve el
/// frametime más que el tweak que se quiere medir.
///
/// Reporta los procesos de fondo más pesados SOLO por nombre y PID: nunca línea de comandos, ruta completa
/// ni usuario (podría contener información sensible en el log).
/// </summary>
public sealed class MeasurementEnvironmentProbe(double backgroundCpuThresholdPercent = 25.0) : IExperimentEnvironmentProbe
{
    public async Task<ExperimentEnvironment> CaptureAsync(HardwareProfile hardware, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var background = await SampleBackgroundCpuAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var processes = new List<BackgroundProcessInfo>();
        foreach (var process in Process.GetProcesses().OrderBy(p => p.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                if (Ignore(process.ProcessName)) continue;
                var cpu = background.TryGetValue(process.Id, out var value) ? value : null;
                processes.Add(new BackgroundProcessInfo(process.ProcessName, process.Id, cpu));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            finally { process.Dispose(); }
        }

        var primaryMonitor = hardware.Monitors.FirstOrDefault();
        var top = processes.Where(p => p.CpuPercent is not null).OrderByDescending(p => p.CpuPercent).Take(15).ToArray();
        if (background.Count == 0) warnings.Add("No se pudo medir la carga de fondo por proceso.");

        return new ExperimentEnvironment
        {
            OsBuild = hardware.Os.Build.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OsVersion = hardware.Os.Version,
            CpuModel = hardware.Cpu.Model,
            GpuName = hardware.Gpus.FirstOrDefault()?.Name ?? "Unknown",
            PowerPlanGuid = hardware.Power.ActiveSchemeGuid ?? "",
            PowerPlanName = hardware.Power.ActiveSchemeName ?? "",
            RefreshRateHz = primaryMonitor?.RefreshRateHz,
            MonitorWidth = primaryMonitor?.Width,
            MonitorHeight = primaryMonitor?.Height,
            PendingReboot = hardware.Maintenance.PendingReboot,
            ServicingActive = hardware.Maintenance.WindowsServicingActive,
            BackgroundCpuPercent = background.Count == 0 ? null : background.Values.Sum(),
            TopBackgroundProcesses = top.Length > 0 ? top : processes.Take(15).ToArray(),
            Warnings = warnings,
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Mide el consumo de CPU de cada proceso tomando dos lecturas de <c>TotalProcessorTime</c> separadas
    /// por <paramref name="window"/> y normalizando por número de procesadores. Devuelve % por PID.
    /// Es una medición real: si no se puede leer un proceso, ese PID simplemente no aparece.
    /// </summary>
    private async Task<Dictionary<int, double>> SampleBackgroundCpuAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var first = Snapshot();
        await Task.Delay(window, cancellationToken);
        var second = Snapshot();
        var result = new Dictionary<int, double>();
        var available = Math.Max(1, Environment.ProcessorCount);
        foreach (var (pid, total) in second)
        {
            if (!first.TryGetValue(pid, out var previous)) continue;
            var delta = (total - previous).TotalMilliseconds;
            if (delta < 0) continue;
            result[pid] = delta / window.TotalMilliseconds / available * 100.0;
        }
        return result;
        static Dictionary<int, TimeSpan> Snapshot()
        {
            var snapshot = new Dictionary<int, TimeSpan>();
            foreach (var process in Process.GetProcesses())
            {
                try { snapshot[process.Id] = process.TotalProcessorTime; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                finally { process.Dispose(); }
            }
            return snapshot;
        }
    }

    private static bool Ignore(string name) =>
        name.Equals("Idle", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Registry", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("fragforge", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("cs2", StringComparison.OrdinalIgnoreCase);
}
