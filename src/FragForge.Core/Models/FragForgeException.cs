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
