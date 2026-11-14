namespace FragForge.Core.Models;

public class FragForgeException : Exception
{
    public string Code { get; }
    public FragForgeException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}
public sealed class CatalogValidationException(string message, IReadOnlyList<string> errors) : FragForgeException("catalog_invalid", message)
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
public sealed class ProtectedTargetException(string target, string reason) : FragForgeException("protected_target", $"El destino '{target}' está protegido: {reason}");
public sealed class ActionNotApplicableException(string message) : FragForgeException("action_not_applicable", message);
public sealed class VerificationFailedException(string message) : FragForgeException("verification_failed", message);
public sealed class PreflightFailedException(string message, PreflightReport report) : FragForgeException("preflight_failed", message)
{
    public PreflightReport Report { get; } = report;
}
public sealed class RollbackException(string message, Exception? inner = null) : FragForgeException("rollback_failed", message, inner);
public sealed class ProcessFailedException(string executable, int exitCode, string error) : FragForgeException("process_failed", $"'{executable}' terminó con código {exitCode}: {error}");

/// <summary>
/// Se lanza cuando una medición no puede ejecutarse (herramienta sin verificar, sin frame data, entorno inválido).
/// El CLI la traduce al código de salida 6.
/// </summary>
public sealed class MeasureNotAvailableException : FragForgeException
{
    public MeasureNotAvailableException(string message) : base("measure_not_available", message) { }
}

/// <summary>
/// Un servicio rechazó un cambio de modo de inicio o de estado. Es un error OPERATIVO con explicación
/// legible, no una traza cruda de WMI: el motor lo reporta y sigue con el resto del lote.
/// </summary>
public sealed class ServiceChangeRejectedException(string serviceName, string operation, uint returnCode)
    : FragForgeException("service_change_rejected", Describe(serviceName, operation, returnCode))
{
    public string ServiceName { get; } = serviceName;
    public string Operation { get; } = operation;
    public uint ReturnCode { get; } = returnCode;

    /// <summary>Códigos de Win32_Service devueltos por ChangeStartMode/StartService/StopService.</summary>
    private static string Describe(string serviceName, string operation, uint code) => code switch
    {
        1 => $"{operation}('{serviceName}') rechazado: la operación no está soportada por este servicio.",
        5 => $"{operation}('{serviceName}') rechazado: acceso denegado. Ejecuta FragForge como administrador.",
        10 => $"{operation}('{serviceName}') rechazado: el servicio está deshabilitado y no se puede iniciar.",
        21 => $"{operation}('{serviceName}') rechazado: parámetro inválido para este servicio.",
        _ => $"{operation}('{serviceName}') rechazado con el código WMI {code}."
    };
}
