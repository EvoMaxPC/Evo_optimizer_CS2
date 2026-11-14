using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Core.Preflight;

public sealed class PreflightService(ISystemRestoreService restore, ITweakCatalog catalog, NetworkChangePreflight? networkChanges = null) : IPreflightService
{
    public const ulong MinimumFreeBytes = 512UL * 1024 * 1024;

    /// <summary>
    /// ¿La operación puede reiniciar el enlace? Se decide por el CATÁLOGO, no por el perfil elegido: los tweaks
    /// de red son opt-in y se aplican por id, así que un perfil "Safe" puede incluir uno.
    ///
    /// Si el catálogo tiene algún tweak que reinicia el enlace, se consulta BitLocker. Es deliberadamente
    /// conservador: una consulta de solo lectura de más es mejor que un aviso de menos.
    /// </summary>
    private bool MayRestartNetworkLink() =>
        catalog.Definitions.Any(d => d.RestartsNetworkLink && !d.ReadOnly);
    public async Task<PreflightReport> CheckAsync(ApplyOptions options, HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        var checks = new List<PreflightCheck>();
        if (options.DryRun)
        {
            checks.Add(new("dry-run", PreflightSeverity.Info, "Simulación: no se modificará el sistema ni se creará un punto de restauración."));
            return PreflightReport.Passed(checks);
        }
        checks.Add(profile.IsUnsupportedPlatform ? new("platform", PreflightSeverity.Blocking, "Las acciones reales requieren Windows 11 x64.", "Ejecuta FragForge en el equipo objetivo.") : new("platform", PreflightSeverity.Info, "Plataforma Windows detectada."));
        checks.Add(profile.Os.Build is >= 26100 and <= 26299 ? new("os", PreflightSeverity.Info, $"Windows 11 build {profile.Os.Build} dentro del rango validado de Capa 0.") : new("os", PreflightSeverity.Blocking, $"Build {profile.Os.Build} fuera del rango 26100–26299.", "Actualiza el catálogo y valida compatibilidad antes de aplicar."));
        checks.Add(profile.Maintenance.IsAdministrator ? new("admin", PreflightSeverity.Info, "Proceso elevado.") : new("admin", PreflightSeverity.Blocking, "Se requieren privilegios de administrador.", "Acepta UAC o inicia la terminal como administrador."));
        if (profile.Maintenance.PendingReboot) checks.Add(new("reboot", PreflightSeverity.Blocking, "Hay un reinicio pendiente: " + string.Join("; ", profile.Maintenance.PendingRebootReasons), "Reinicia Windows y vuelve a escanear. Force no omite esta protección."));
        if (profile.Maintenance.WindowsServicingActive) checks.Add(new("servicing", PreflightSeverity.Blocking, "Windows Update/COMPONENTES está activo: " + string.Join(", ", profile.Maintenance.ServicingProcesses), "Espera a que termine TiWorker/TrustedInstaller; no los detengas."));
        checks.Add(profile.Maintenance.SystemDriveFreeBytes >= MinimumFreeBytes ? new("disk", PreflightSeverity.Info, $"Espacio libre: {profile.Maintenance.SystemDriveFreeBytes / 1024 / 1024} MiB.") : new("disk", PreflightSeverity.Blocking, "Menos de 512 MiB libres en la unidad de sistema.", "Libera espacio antes de crear snapshots o puntos de restauración."));
        checks.Add(catalog.Definitions.Count > 0 ? new("catalog", PreflightSeverity.Info, $"Catálogo válido: {catalog.Definitions.Count} tweaks.") : new("catalog", PreflightSeverity.Blocking, "El catálogo está vacío."));
        if (options.CreateRestorePoint)
        {
            var state = await restore.GetProtectionStateAsync(cancellationToken);
            if (state == FeatureState.Enabled) checks.Add(new("restore", PreflightSeverity.Info, "Protección del sistema habilitada."));
            else if (options.EnableSystemProtection && state != FeatureState.NotPresent) checks.Add(new("restore", PreflightSeverity.Warning, "La protección del sistema se habilitará para la unidad de sistema porque lo solicitaste explícitamente."));
            else checks.Add(new("restore", PreflightSeverity.Blocking, state == FeatureState.NotPresent ? "System Restore no está disponible en esta edición." : "La protección del sistema está desactivada o no pudo confirmarse.", "Habilítala en Protección del sistema o usa --enable-system-protection; --no-restore-point solo omite el punto, no el snapshot/journal."));
        }
        else checks.Add(new("restore", PreflightSeverity.Warning, "Punto de restauración omitido por opción explícita. El snapshot y journal siguen siendo obligatorios."));
        // Capa 5.3 (R-J): BitLocker se consulta SOLO si la operación puede reiniciar el enlace de red. En el
        // resto de operaciones no se ejecuta manage-bde: no se paga el coste ni se pide nada al usuario.
        if (networkChanges is not null && MayRestartNetworkLink())
            checks.AddRange(NetworkChangePreflight.ToChecks(await networkChanges.ReadAsync(cancellationToken)));
        return PreflightReport.Passed(checks);
    }
}
