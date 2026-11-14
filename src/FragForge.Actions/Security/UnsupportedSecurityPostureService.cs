using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Security;

/// <summary>
/// Postura de seguridad en plataformas que no son Windows. Devuelve todo <see cref="FeatureState.Unknown"/>:
/// no se inventa un estado que no se ha medido (R-L).
/// </summary>
public sealed class UnsupportedSecurityPostureService : ISecurityPostureService
{
    public Task<SecurityPosture> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SecurityPosture
        {
            Observations = [new DiagnosticObservation("plataforma", "Unknown", "UnsupportedSecurityPostureService", "Unknown",
                "La postura de seguridad solo se lee en Windows.")]
        });
}
