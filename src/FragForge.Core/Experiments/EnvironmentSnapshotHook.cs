using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Core.Experiments;

/// <summary>
/// <see cref="IExperimentHook"/> legítimo: escribe <c>environment.json</c> como <see cref="ExperimentArtifact"/>
/// en cada fase del ciclo de vida de un tweak.
///
/// NO modifica Windows. NO retiene el gate. Solo lee el perfil de hardware que ya viene en el contexto
/// y escribe un archivo dentro del directorio de artefactos del snapshot.
/// </summary>
public sealed class EnvironmentSnapshotHook : IExperimentHook
{
    public int Order => 10;

    public async Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default)
    {
        var directory = context.ArtifactDirectory ?? throw new InvalidOperationException("EnvironmentSnapshotHook requiere ArtifactDirectory.");
        Directory.CreateDirectory(directory);
        var name = $"environment-{context.Phase.ToString().ToLowerInvariant()}.json";
        var path = Path.Combine(directory, name);
        var hardware = context.Hardware;
        var payload = new
        {
            schemaVersion = 1,
            operationId = context.OperationId,
            snapshotId = context.SnapshotId,
            phase = context.Phase.ToString(),
            tweakId = context.Tweak.Id,
            timestamp = context.Timestamp,
            hardware = new
            {
                machine = hardware.MachineName,
                osBuild = hardware.Os.Build,
                osVersion = hardware.Os.Version,
                cpu = hardware.Cpu.Model,
                hybrid = hardware.Cpu.IsHybrid,
                gpu = hardware.Gpus.Select(g => g.Name).ToArray(),
                refresh = hardware.Monitors.Select(m => new { m.Width, m.Height, m.RefreshRateHz }).ToArray(),
                powerPlan = hardware.Power.ActiveSchemeGuid,
                memoryChannels = hardware.Memory.Channels.ToString(),
                pendingReboot = hardware.Maintenance.PendingReboot,
                servicing = hardware.Maintenance.WindowsServicingActive
            }
        };
        await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonDefaults.Options), cancellationToken);
        return [new ExperimentArtifact(name, path, "application/json")];
    }
}
