using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Core.Preflight;

/// <summary>
/// Preflight de SOLO LECTURA para cambios en el adaptador de red (Capa 5.3, R-J).
///
/// <para>
/// Un cambio de propiedad avanzada puede hacer que el driver reinicie el dispositivo. En un equipo con
/// BitLocker eso es una operación sensible: si el reinicio cae en un momento malo, Windows pedirá la clave de
/// recuperación al arrancar. Este preflight <b>no enciende ni apaga BitLocker</b>: solo lo consulta y avisa.
/// </para>
///
/// <para>
/// La consulta es <c>manage-bde -status</c> en modo lectura. Se parsea por <b>código de estado numérico</b>
/// (<c>Conversion Status</c> / <c>Protection Status</c>), nunca por la etiqueta traducida (R-C): la salida de
/// <c>manage-bde</c> está localizada, y en es-MX dice "Totalmente descifrado" donde en en-US dice
/// "Fully Decrypted".
/// </para>
///
/// <para>
/// Si no se puede leer, el resultado es <see cref="BitLockerState.Unknown"/> y el aviso lo dice. No se asume
/// "sin cifrado" porque sería justo el caso peligroso.
/// </para>
/// </summary>
public sealed partial class NetworkChangePreflight(IProcessRunner runner)
{
    /// <summary>
    /// Consulta el estado. <c>manage-bde</c> devuelve texto localizado, así que se busca el estado por
    /// palabras clave NO traducibles del formato (los porcentajes y los identificadores numéricos) y, si no
    /// se reconoce, se devuelve Unknown.
    /// </summary>
    public async Task<BitLockerStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(new ProcessRequest
            {
                // Core no puede referenciar FragForge.Actions (dependencia invertida), así que la ruta se
                // compone aquí igual que hace ProcessRunner.SystemExecutable. manage-bde vive en System32.
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "manage-bde.exe"),
                // -status sin -protectors ni -on: SOLO consulta. No hay ninguna ruta de escritura en esta clase.
                Arguments = ["-status"],
                Timeout = TimeSpan.FromSeconds(30)
            }, cancellationToken);
        }
        catch (Exception)
        {
            // manage-bde puede no existir (edición sin BitLocker) o fallar por permisos.
            return BitLockerStatus.Unknown("No se pudo ejecutar manage-bde -status.");
        }

        if (!result.IsSuccess)
        {
            // Un error de manage-bde sobre un volumen sin cifrar es habitual. Sin salida legible: Unknown.
            return BitLockerStatus.Unknown($"manage-bde devolvió código {result.ExitCode}.");
        }
        return Parse(result.StandardOutput);
    }

    /// <summary>
    /// Interpreta la salida de <c>manage-bde -status</c>.
    ///
    /// Se aceptan los dos indicadores que Windows imprime de forma estable entre idiomas:
    /// <list type="bullet">
    /// <item><c>Conversion Status</c> con el porcentaje (<c>100.0%</c> = cifrado completo, <c>0.0%</c> = sin cifrar).</item>
    /// <item><c>Protection Status</c> con <c>On</c>/<c>Off</c> (estos dos SÍ son literales en inglés en todas las
    ///       versiones que se han visto; si no aparecen, Unknown).</item>
    /// </list>
    /// Ante cualquier ambigüedad, Unknown: es el único valor que no lleva a una decisión equivocada.
    /// </summary>
    internal static BitLockerStatus Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return BitLockerStatus.Unknown("manage-bde no devolvió texto.");

        // Porcentaje de conversión: se busca el número junto al signo %, que no se traduce.
        var percents = PercentRegex().Matches(output).Select(m => m.Groups[1].Value).Select(v =>
            double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
            .Where(d => d is not null).Select(d => d!.Value).ToArray();

        // Si no hay NINGÚN porcentaje, la salida no es de manage-bde: no se puede concluir nada. Este es el
        // caso de un error de permisos o de una edición sin BitLocker, y tratarlo como "sin cifrado" sería
        // exactamente el error peligroso (se asumiría que no hay clave de recuperación).
        if (percents.Length == 0) return BitLockerStatus.Unknown("manage-bde no reportó ningún porcentaje de conversión; no se asume que no haya cifrado.");

        var encrypted = percents.Max() >= 100.0;
        var notEncrypted = percents.All(p => p <= 0.0);

        if (!encrypted && !notEncrypted)
            return new BitLockerStatus { State = BitLockerState.Unknown, Note = "Hay una conversión de BitLocker en curso; no se aplican cambios de red mientras tanto." };

        if (notEncrypted) return new BitLockerStatus { State = BitLockerState.NotEncrypted, Note = "Ningún volumen reporta cifrado completo." };

        // Cifrado: hace falta saber si la protección está activa y si hay recuperación disponible.
        var protectionOn = output.Contains("Protection On", StringComparison.OrdinalIgnoreCase) || output.Contains("Protection Status: On", StringComparison.OrdinalIgnoreCase);
        var protectionOff = output.Contains("Protection Off", StringComparison.OrdinalIgnoreCase) || output.Contains("Protection Status: Off", StringComparison.OrdinalIgnoreCase);

        if (protectionOn) return new BitLockerStatus { State = BitLockerState.EncryptedProtected, Note = "BitLocker activo con protección habilitada." };
        if (protectionOff) return new BitLockerStatus { State = BitLockerState.EncryptedSuspended, Note = "BitLocker activo con la protección SUSPENDIDA." };
        return new BitLockerStatus { State = BitLockerState.EncryptedUnknownProtection, Note = "Volumen cifrado pero no se pudo leer el estado de protección." };
    }

    /// <summary>Porcentaje de conversión, p. ej. <c>100.0%</c>. El signo % no se traduce.</summary>
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    /// <summary>
    /// Convierte el estado en comprobaciones de preflight. <b>De las que produce ESTA función ninguna es
    /// Blocking</b>: aquí solo se informa del estado de BitLocker y el usuario decide. Lo que sí se hace es
    /// advertir con claridad cuando el cifrado está activo.
    ///
    /// <para>
    /// Cuidado al leer esto: no significa que un cambio de red pueda aplicarse. La comprobación Blocking
    /// para los tweaks que necesitan recuperación fuera de banda —incluido todo lo que reinicie el enlace—
    /// vive en <c>PreflightService.RecoveryCoverage</c>, no aquí, porque la decide el CATÁLOGO y este tipo
    /// solo mira BitLocker. Mientras el motor no escriba el script de recuperación (R-J), esa comprobación
    /// bloquea el apply.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PreflightCheck> ToChecks(BitLockerStatus status)
    {
        var remediation = "Guardar la clave de recuperación de BitLocker antes de continuar.";
        return status.State switch
        {
            BitLockerState.NotEncrypted => [new("bitlocker", PreflightSeverity.Info, "BitLocker no está cifrando ningún volumen: el reinicio del adaptador no pide clave de recuperación. " + status.Note)],
            BitLockerState.EncryptedProtected => [new("bitlocker", PreflightSeverity.Warning, "BitLocker está activo y protegiendo. Un reinicio del adaptador en mal momento puede pedir la clave de recuperación. " + status.Note, remediation)],
            BitLockerState.EncryptedSuspended => [new("bitlocker", PreflightSeverity.Warning, "BitLocker tiene la protección suspendida: se arranca sin clave hasta el próximo reinicio. " + status.Note, remediation)],
            BitLockerState.EncryptedUnknownProtection => [new("bitlocker", PreflightSeverity.Warning, "Hay un volumen cifrado con BitLocker pero no se pudo leer su estado de protección. " + status.Note, remediation)],
            _ => [new("bitlocker", PreflightSeverity.Info, "Estado de BitLocker desconocido; no se asume que no haya cifrado. " + status.Note, remediation)]
        };
    }
}

public enum BitLockerState
{
    /// <summary>No se pudo leer. NO significa "sin cifrado".</summary>
    Unknown,
    NotEncrypted,
    EncryptedProtected,
    EncryptedSuspended,
    EncryptedUnknownProtection
}

public sealed record BitLockerStatus
{
    public BitLockerState State { get; init; } = BitLockerState.Unknown;
    public string Note { get; init; } = "";

    public static BitLockerStatus Unknown(string note) => new() { State = BitLockerState.Unknown, Note = note };
}
