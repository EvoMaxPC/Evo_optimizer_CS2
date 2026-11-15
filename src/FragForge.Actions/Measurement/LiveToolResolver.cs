namespace FragForge.Actions.Measurement;

/// <summary>Resultado de resolver la herramienta: ruta aprobada o motivo de rechazo.</summary>
/// <param name="ApprovedPath">Ruta del ejecutable aprobado, o <c>null</c> si fue rechazado.</param>
/// <param name="RejectionReason">Motivo del rechazo, o <c>null</c> si fue aprobado.</param>
public sealed record LiveToolResolution(string? ApprovedPath, string? RejectionReason);

/// <summary>Resuelve la ruta aprobada de PresentMon o el motivo de rechazo. No lanza nada.</summary>
public delegate LiveToolResolution LiveToolResolver();

/// <summary>Constructores de resolvedores reales de herramienta.</summary>
public static class LiveToolResolvers
{
    /// <summary>
    /// Resolvedor real vía <see cref="ToolVerifier"/> con el MISMO criterio de aprobación que
    /// <c>PresentMonFrameCapture.CaptureAsync</c>: solo <see cref="ToolStatus.Approved"/> habilita.
    /// (La rama de "instalación de Intel" de <c>CheckAvailabilityAsync</c> solo da un mensaje accionable:
    /// nunca habilita la captura por sí sola.)
    /// </summary>
    public static LiveToolResolver FromVerifier(ToolVerifier verifier) => () =>
    {
        var verification = verifier.Verify(ToolVerifier.PresentMonKey);
        return verification.Status == ToolStatus.Approved && verification.Path is not null
            ? new LiveToolResolution(verification.Path, null)
            : new LiveToolResolution(null, verification.Message);
    };
}
