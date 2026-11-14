using FragForge.Core.Abstractions;
using FragForge.Core.Localization;
using FragForge.Core.Models;

namespace FragForge.Core.Preflight;

public sealed class PreflightService(
    ISystemRestoreService restore,
    ITweakCatalog catalog,
    NetworkChangePreflight? networkChanges = null,
    Localizer? localizer = null) : IPreflightService
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
            // CORRECCION (ronda de fallos 1, C3): antes se retornaba AQUI y el dry-run nunca llegaba a
            // RecoveryCoverage (:51), asi que la vista previa salia limpia para un tweak que despues
            // fallaba al aplicar. El contrato pide lo contrario: el dry-run NO lanza, pero ENSENA el
            // aviso. Se evalua igual y se DEGRADA a Warning: un dry-run no cambia nada, de modo que no
            // hay nada que impedir, pero el usuario ve por que no se podra aplicar.
            checks.AddRange(RecoveryCoverage(options.SelectedIds)
                .Select(c => c with { Severity = PreflightSeverity.Warning }));
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
        checks.AddRange(RecoveryCoverage(options.SelectedIds));
        return PreflightReport.Passed(checks);
    }

    /// <summary>
    /// Idioma del mensaje de R-J.
    /// </summary>
    /// <remarks>
    /// CORRECCION (ronda 9d): en la 9b escribi que "Localizer no esta registrado en CompositionRoot" y que
    /// por eso se caia a es-MX. Era FALSO. CompositionRoot.cs:95 registra el Localizer (cargado en :77 con
    /// options.Culture), y el contenedor lo inyecta en este parametro porque el tipo es Localizer. El
    /// fallback a es-MX solo aplica a quien construya el servicio a mano (tests, o un `new` directo).
    ///
    /// Se mantiene el valor por defecto para no romper esas construcciones; el resto de los mensajes de este
    /// preflight siguen en espanol fijo, y traducirlos todos es otra tarea.
    /// </remarks>
    /// <summary>
    /// Idioma del mensaje de R-J.
    /// </summary>
    /// <remarks>
    /// CORRECCION (ronda 9d): en la 9b escribi que "Localizer no esta registrado en CompositionRoot" y que
    /// por eso se caia a es-MX. Era FALSO. CompositionRoot.cs:95 registra el Localizer (cargado en :77 con
    /// options.Culture), y el contenedor lo inyecta en este parametro porque su tipo es Localizer. El
    /// fallback solo aplica a quien construya el servicio a mano (tests, o un `new` directo).
    ///
    /// El resto de mensajes de este preflight siguen en espanol fijo; traducirlos todos es otra tarea.
    /// </remarks>
    private Localizer Text => localizer ?? LocalizerVacio;

    /// <summary>
    /// Solo para construcciones a mano sin localizador (un `new` en un test). NO lleva textos: si se usa,
    /// el mensaje sale como la CLAVE, que es lo que hace visible que falta inyectar el Localizer.
    /// Los textos viven en FragForge.Tweaks/Localization/{es-MX,en-US}.json, la misma fuente que el resto
    /// de la app, para no tener dos diccionarios que se desincronicen.
    /// </summary>
    private static readonly Localizer LocalizerVacio = new("es-MX", new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// R-J: nada que necesite recuperacion FUERA DE BANDA puede aplicarse mientras el motor no escriba ese
    /// script. <see cref="TweakEngine"/> no invoca <c>RecoveryWriter</c> (no aparece en el fichero), asi que
    /// si un cambio deja el equipo sin red o sin arranque, la unica vuelta atras seria a mano.
    ///
    /// <para>
    /// SE JUZGA SOLO LO SELECCIONADO, no el catalogo entero. Antes se miraba el catalogo y eso bloqueaba
    /// TODO apply mientras existiera un tweak de red en el catalogo: rompia la app. El catalogo dice lo que
    /// PUEDE aplicarse; <see cref="ApplyOptions.SelectedIds"/> dice lo que SE VA a aplicar, y solo eso puede
    /// impedir la operacion.
    /// </para>
    /// <para>
    /// SIN IDS no se bloquea. <c>SelectedIds</c> vacio significa "no se sabe que se aplica" (llamada vieja,
    /// dry-run, o un preflight de solo lectura como el del informe). Bloquear en ese caso seria volver al
    /// problema que esto arregla. El motor SIEMPRE lo rellena antes de aplicar (TweakEngine.cs:139-146), asi
    /// que la ruta de apply nunca cae en la rama permisiva.
    /// </para>
    /// </summary>
    private IEnumerable<PreflightCheck> RecoveryCoverage(IReadOnlySet<string> selectedIds)
    {
        if (selectedIds.Count == 0) yield break;
        var affected = catalog.Definitions
            .Where(d => !d.ReadOnly && selectedIds.Contains(d.Id) && NeedsOutOfBandRecovery(d))
            .Select(d => d.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (affected.Length == 0) yield break;
        yield return new("recovery",
            PreflightSeverity.Blocking,
            Text.Format("preflight.recovery", string.Join(", ", affected)),
            "No hay script de recuperación: se retomará cuando el motor lo escriba (ronda 10). Mientras tanto, aplica esos cambios con la herramienta del fabricante.");
    }

    /// <summary>
    /// ¿Este tweak necesita recuperacion fuera de banda? Los tres casos del encargo: reinicia el enlace de
    /// red, toca propiedades del adaptador de red, o usa bcdedit. Tambien cualquier Kind de driver/dispositivo,
    /// que hoy no existe en el catalogo pero se contempla por nombre para que no entre sin querer.
    /// </summary>
    /// <remarks>
    /// Es <c>internal</c> y no <c>private</c> a proposito: lo usa tambien
    /// <see cref="FragForge.Core.Sessions.GameSessionService"/> para dejar fuera de una sesion de juego
    /// cualquier tweak que necesite recuperacion fuera de banda. Dos copias de esta regla se
    /// desincronizarian y la sesion aceptaria algo que el preflight bloquea. <c>internal</c> porque es
    /// FragForge.Core quien lo consume, sin tocar Abstractions.
    /// </remarks>
    internal static bool NeedsOutOfBandRecovery(TweakDefinition definition)
    {
        if (definition.RestartsNetworkLink) return true;
        foreach (var action in definition.Actions)
        {
            if (action.Kind.Equals("NetAdapterProperty", StringComparison.OrdinalIgnoreCase)) return true;
            if (action.Kind.Equals("Bcdedit", StringComparison.OrdinalIgnoreCase)) return true;
            if (action.Kind.Contains("Driver", StringComparison.OrdinalIgnoreCase)) return true;
            if (action.Kind.Contains("Device", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
