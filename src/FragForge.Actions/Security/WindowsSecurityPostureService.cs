using System.Globalization;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Preflight;
using FragForge.Actions.Platform;

namespace FragForge.Actions.Security;

/// <summary>
/// Postura de seguridad del equipo (Capa 10.1). **SOLO LECTURA**: no hay ninguna escritura en esta clase.
///
/// <para>
/// Todo lo que no se pueda leer queda en <see cref="FeatureState.Unknown"/>. En particular:
/// Secure Boot en BIOS heredado no expone la consulta, así que el error de PowerShell es <c>Unknown</c>, no
/// <c>Disabled</c>; y un valor de registro ausente es <c>Unknown</c>, no "desactivado".
/// </para>
///
/// <para>
/// Se parsea por <b>valor numérico y por enum</b>, nunca por texto localizado (R-C):
/// <c>VirtualizationBasedSecurityStatus</c> es 0/1/2, <c>SecurityServicesRunning</c> es un array de enteros
/// donde 2 = HVCI, y <c>VulnerableDriverBlocklistEnable</c> es un DWORD 0/1.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecurityPostureService(
    IWmiQuery wmi,
    IRegistry registry,
    IProcessRunner runner,
    NetworkChangePreflight bitLocker) : ISecurityPostureService
{
    /// <summary>Namespace real de <c>Win32_DeviceGuard</c>. No está en root\CIMV2.</summary>
    public const string DeviceGuardScope = @"root\Microsoft\Windows\DeviceGuard";
    public const string DeviceGuardClass = "Win32_DeviceGuard";

    /// <summary>Ruta del escenario de HVCI en el registro.</summary>
    public const string HvciScenarioKey = @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    /// <summary>Lista de bloqueo de drivers vulnerables (WDAC). DWORD: 1 = habilitada.</summary>
    public const string CiConfigKey = @"HKLM\SYSTEM\CurrentControlSet\Control\CI\Config";

    /// <summary>
    /// Valor de <c>SecurityServicesRunning</c> que corresponde a HVCI. Es un enum documentado
    /// (2 = HypervisorEnforcedCodeIntegrity), no una etiqueta.
    /// </summary>
    public const int HvciServiceValue = 2;

    public async Task<SecurityPosture> ReadAsync(CancellationToken cancellationToken = default)
    {
        var observations = new List<DiagnosticObservation>();

        // --- VBS / HVCI -------------------------------------------------------
        IReadOnlyDictionary<string, object?>? guard = null;
        try
        {
            var rows = await wmi.QueryAsync(DeviceGuardScope, DeviceGuardClass,
                ["VirtualizationBasedSecurityStatus", "SecurityServicesRunning", "SecurityServicesConfigured"],
                null, cancellationToken);
            guard = rows.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            observations.Add(new DiagnosticObservation("vbs", "Unknown", "WMI DeviceGuard", "Unknown", "La consulta falló: " + ex.Message));
        }

        var vbs = guard is null
            ? FeatureState.Unknown
            : WmiValues.Int32(guard, "VirtualizationBasedSecurityStatus") switch
            {
                0 => FeatureState.Disabled,
                2 => FeatureState.Enabled,
                _ => FeatureState.Unknown
            };

        var running = guard is null ? [] : WmiValues.Array(guard, "SecurityServicesRunning").Select(x => ToInt(x)).Where(x => x is not null).Select(x => x!.Value).ToArray();
        var configured = guard is null ? [] : WmiValues.Array(guard, "SecurityServicesConfigured").Select(x => ToInt(x)).Where(x => x is not null).Select(x => x!.Value).ToArray();

        // Dos fuentes: WMI dice si el servicio está EN EJECUCIÓN; el escenario de registro dice si está
        // CONFIGURADO. Se prefiere WMI (es el estado real) y se cae al registro si WMI no dijo nada.
        var hvci = FeatureState.Unknown;
        var hvciSource = "Unknown";
        if (guard is not null)
        {
            if (running.Contains(HvciServiceValue)) { hvci = FeatureState.Enabled; hvciSource = "WMI DeviceGuard (SecurityServicesRunning)"; }
            else if (configured.Contains(HvciServiceValue)) { hvci = FeatureState.Unknown; hvciSource = "WMI DeviceGuard (configurado, no en ejecución)"; }
            else if (running.Length > 0) { hvci = FeatureState.Disabled; hvciSource = "WMI DeviceGuard (SecurityServicesRunning)"; }
        }

        if (hvci == FeatureState.Unknown)
        {
            var scenario = await registry.ReadValueAsync(HvciScenarioKey, "Enabled", cancellationToken);
            if (scenario.ValueExists && ToInt(scenario.Data) is { } enabled)
            {
                // El escenario solo dice "configurado". Si está a 0 no se afirma Disabled si WMI ya dijo
                // Unknown: puede hacer falta reiniciar. Se queda Unknown con la explicación.
                hvciSource = "registro (Scenarios\\HypervisorEnforcedCodeIntegrity)";
                observations.Add(new DiagnosticObservation("hvci.scenario", enabled.ToString(CultureInfo.InvariantCulture), hvciSource, "Medium",
                    enabled == 1 ? "Configurado; el estado en ejecución lo da WMI." : "Configurado a 0; puede requerir reinicio para reflejarse."));
            }
        }

        // --- Secure Boot ------------------------------------------------------
        var secureBoot = await ReadSecureBootAsync(cancellationToken);
        if (secureBoot == FeatureState.Unknown)
            observations.Add(new DiagnosticObservation("secureBoot", "Unknown", "Confirm-SecureBootUEFI", "Unknown",
                "No se pudo confirmar. Un equipo con BIOS heredado no soporta esta consulta: no es lo mismo que desactivado."));

        // --- TPM --------------------------------------------------------------
        var (tpm, _) = await ReadTpmAsync(cancellationToken);

        // --- BitLocker (solo estado, reutiliza el preflight de Capa 5) --------
        var bitLockerState = FeatureState.Unknown;
        try
        {
            var status = await bitLocker.ReadAsync(cancellationToken);
            // Protección ACTIVA = cifrado y con protectores. Solo eso es Enabled. Todo lo demás que no
            // sea "sin cifrar" es Unknown, porque un volumen cifrado y suspendido no está protegido pero
            // tampoco está en claro.
            bitLockerState = status.State switch
            {
                BitLockerState.EncryptedProtected => FeatureState.Enabled,
                BitLockerState.NotEncrypted => FeatureState.Disabled,
                _ => FeatureState.Unknown
            };
            observations.Add(new DiagnosticObservation("bitLocker", bitLockerState.ToString(), "manage-bde -status", "Medium", status.Note));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            observations.Add(new DiagnosticObservation("bitLocker", "Unknown", "manage-bde -status", "Unknown", ex.Message));
        }

        // --- Lista de bloqueo de drivers vulnerables ---------------------------
        var blocklistValue = await registry.ReadValueAsync(CiConfigKey, "VulnerableDriverBlocklistEnable", cancellationToken);
        // DWORD 1 = habilitada; 0 = deshabilitada explícitamente. Ausente = Unknown (el valor por defecto
        // depende de la versión de Windows y no se asume).
        var blocklist = !blocklistValue.ValueExists ? FeatureState.Unknown : ToInt(blocklistValue.Data) switch
        {
            1 => FeatureState.Enabled,
            0 => FeatureState.Disabled,
            _ => FeatureState.Unknown
        };
        if (!blocklistValue.ValueExists)
            observations.Add(new DiagnosticObservation("vulnerableDriverBlocklist", "Unknown", "registro CI\\Config", "Unknown",
                "El valor no existe. El valor por defecto depende de la versión de Windows: no se asume."));

        // --- Driver de terceros (PawnIO) --------------------------------------
        var (driverPresent, driverName) = await DetectThirdPartyDriverAsync(cancellationToken);
        if (driverPresent == true)
            observations.Add(new DiagnosticObservation("thirdPartyKernelDriver", driverName ?? "presente", "servicio del sistema", "Medium",
                "Se reporta como DATO. FragForge no afirma que ningún anticheat lo bloquee ni lo permita: no hay fuente. Revisa los requisitos de tu proveedor."));

        return new SecurityPosture
        {
            Vbs = vbs,
            Hvci = hvci,
            HvciSource = hvciSource,
            SecureBoot = secureBoot,
            Tpm = tpm,
            BitLocker = bitLockerState,
            VulnerableDriverBlocklist = blocklist,
            ThirdPartyKernelDriver = driverPresent,
            ThirdPartyKernelDriverName = driverName,
            Observations = observations
        };
    }

    /// <summary>
    /// Secure Boot por <c>Confirm-SecureBootUEFI</c>. La salida se compara con la cadena literal que emite el
    /// propio cmdlet cuando se le pide <c>'True'/'False'</c>: no es texto traducido de la interfaz.
    /// Un error (BIOS heredado, sin UEFI) es <c>Unknown</c>.
    /// </summary>
    private async Task<FeatureState> ReadSecureBootAsync(CancellationToken cancellationToken)
    {
        // El registro es más barato y no depende de PowerShell. Si dice algo, manda.
        var state = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", cancellationToken);
        if (state.ValueExists && ToInt(state.Data) is { } value)
            return value == 1 ? FeatureState.Enabled : FeatureState.Disabled;

        try
        {
            var result = await runner.RunAsync(new ProcessRequest
            {
                FileName = FragForge.Actions.Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
                // Se pide un booleano explícito; "Unknown" en el catch es el camino de BIOS heredado.
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", "try { if (Confirm-SecureBootUEFI) { 'true' } else { 'false' } } catch { 'unknown' }"],
                Timeout = TimeSpan.FromSeconds(20)
            }, cancellationToken);
            return result.StandardOutput.Trim().ToLowerInvariant() switch
            {
                "true" => FeatureState.Enabled,
                "false" => FeatureState.Disabled,
                _ => FeatureState.Unknown
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FeatureState.Unknown;
        }
    }

    private async Task<(FeatureState State, string? Version)> ReadTpmAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await wmi.QueryAsync(WindowsHardwareProfiler.TpmScope, WindowsHardwareProfiler.TpmClass,
                ["IsEnabled_InitialValue", "IsActivated_InitialValue", "SpecVersion"], null, cancellationToken);
            var row = rows.FirstOrDefault();
            if (row is null) return (FeatureState.Unknown, null);
            var enabled = WmiValues.Bool(row, "IsEnabled_InitialValue");
            return enabled switch
            {
                true => (FeatureState.Enabled, WmiValues.String(row, "SpecVersion")),
                false => (FeatureState.Disabled, WmiValues.String(row, "SpecVersion")),
                null => (FeatureState.Unknown, WmiValues.String(row, "SpecVersion"))
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (FeatureState.Unknown, null);
        }
    }

    /// <summary>
    /// Detecta un driver de kernel de terceros instalado (PawnIO / InpOut).
    ///
    /// <para>
    /// Se lee la clave del servicio del sistema, que es donde quedan los drivers de kernel. Es
    /// <b>null</b> si no se pudo comprobar (no es lo mismo que "no está"), <c>true</c> si aparece y
    /// <c>false</c> si la enumeración funcionó y no apareció.
    /// </para>
    /// </summary>
    private async Task<(bool? Present, string? Name)> DetectThirdPartyDriverAsync(CancellationToken cancellationToken)
    {
        // Nombres de servicio que corresponden a este tipo de driver. Se comparan por NOMBRE DE SERVICIO
        // (no por ruta ni por texto visible) y solo para informar.
        string[] known = ["PawnIO", "PawnIOLib", "InpOut32", "InpOutx64"];
        try
        {
            foreach (var name in known)
            {
                var key = await registry.ReadValueAsync($@"HKLM\SYSTEM\CurrentControlSet\Services\{name}", "ImagePath", cancellationToken);
                if (key.ValueExists) return (true, name);
            }
            return (false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, null);
        }
    }

    private static int? ToInt(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }
}
