namespace FragForge.Core.Models;

/// <summary>
/// Postura de seguridad del equipo (Capa 10.1), en SOLO LECTURA.
///
/// <para>
/// Cada campo es un <see cref="FeatureState"/>. <c>Unknown</c> es un valor de primera clase: significa que
/// no se pudo leer, y NUNCA se interpreta como "desactivado" (R-L). Un equipo con Secure Boot en BIOS
/// heredado no expone la consulta; eso es <c>Unknown</c>, no <c>Disabled</c>.
/// </para>
///
/// <para>
/// Este modelo NO describe lo que hacen los anticheat: describe el estado del sistema operativo. Es
/// información para el usuario, no un veredicto de compatibilidad.
/// </para>
/// </summary>
public sealed record SecurityPosture
{
    /// <summary><c>Win32_DeviceGuard.VirtualizationBasedSecurityStatus</c> (0 = off, 1 = habilitado sin ejecutar, 2 = en ejecución).</summary>
    public FeatureState Vbs { get; init; } = FeatureState.Unknown;

    /// <summary>HVCI / Integridad de memoria: <c>SecurityServicesRunning</c> contiene 2, o el escenario de registro.</summary>
    public FeatureState Hvci { get; init; } = FeatureState.Unknown;

    /// <summary>De dónde salió <see cref="Hvci"/>: <c>WMI DeviceGuard</c>, <c>registro</c> o <c>Unknown</c>.</summary>
    public string HvciSource { get; init; } = "Unknown";

    /// <summary><c>Confirm-SecureBootUEFI</c>. En BIOS heredado la consulta falla y esto es <c>Unknown</c>.</summary>
    public FeatureState SecureBoot { get; init; } = FeatureState.Unknown;

    /// <summary><c>Win32_Tpm</c> (<c>Security\MicrosoftTpm</c>).</summary>
    public FeatureState Tpm { get; init; } = FeatureState.Unknown;

    public string? TpmVersion { get; init; }

    /// <summary>Estado de BitLocker del volumen del sistema, vía <c>manage-bde -status</c>. Solo estado.</summary>
    public FeatureState BitLocker { get; init; } = FeatureState.Unknown;

    /// <summary>
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\CI\Config\VulnerableDriverBlocklistEnable</c>.
    /// Es la lista de bloqueo de drivers vulnerables de Microsoft (WDAC). No es la postura de anticheat.
    /// </summary>
    public FeatureState VulnerableDriverBlocklist { get; init; } = FeatureState.Unknown;

    /// <summary>
    /// ¿Hay un driver de terceros de tipo PawnIO/InpOut instalado? Se reporta como DATO.
    ///
    /// <para>
    /// NO se afirma que ningún anticheat lo bloquee ni lo permita: no hay fuente que lo respalde. Tampoco se
    /// afirma que sea seguro . Es un <c>bool?</c> porque "no se pudo determinar" no es lo mismo que "no está".
    /// </para>
    /// </summary>
    public bool? ThirdPartyKernelDriver { get; init; }

    /// <summary>Nombre del driver detectado, si lo hay. Ej. <c>PawnIO</c>.</summary>
    public string? ThirdPartyKernelDriverName { get; init; }

    /// <summary>
    /// Nota fija sobre requisitos de anticheat. NO se afirma compatibilidad con ninguno: se remite al
    /// proveedor. Es texto, no un resultado de sonda.
    /// </summary>
    public string AnticheatContract { get; init; } =
        "FragForge no inyecta, no abre handles a cs2.exe, no modifica archivos del juego y no toca Steam ni " +
        "procesos de anticheat. Los requisitos de cada anticheat (VAC, FACEIT, etc.) los define su proveedor: " +
        "revisa los requisitos del proveedor. Esta app no afirma compatibilidad con ninguno.";

    /// <summary>Observaciones por componente, para poder decir POR QUÉ algo es Unknown.</summary>
    public IReadOnlyList<DiagnosticObservation> Observations { get; init; } = [];
}
