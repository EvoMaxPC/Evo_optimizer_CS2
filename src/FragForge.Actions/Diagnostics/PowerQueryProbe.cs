using System.Globalization;
using FragForge.Actions.Power;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Diagnostics;

/// <summary>
/// Guids de los ajustes de energía que la Capa 3 consulta. NO son una lista de valores válidos: esos
/// los enumera el sistema. Solo identifican el ajuste, por GUID y nunca por nombre localizado (R-C).
/// </summary>
public static class PowerSettingIds
{
    public const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    public const string PcieSubgroup = "501a4d13-42af-4429-9fd1-a8218c268e20";
    public const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";

    /// <summary>Preferencia de rendimiento del procesador (PERFEPP) y sus variantes.</summary>
    public const string PerfEpp = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    public const string PerfEpp1 = "36687f9e-e3a5-4dbf-b1dc-15eb381c6864";
    public const string PerfEpp2 = "36687f9e-e3a5-4dbf-b1dc-15eb381c6865";

    /// <summary>Aparcamiento de núcleos (CPMINCORES): base y variante 1.</summary>
    public const string CoreParkingMin = "0cc5b647-c1df-4637-891a-dec35c318583";
    public const string CoreParkingMin1 = "0cc5b647-c1df-4637-891a-dec35c318584";

    /// <summary>Modo de impulso del procesador (PERFBOOSTMODE). En la PN64 vale 2.</summary>
    public const string PerfBoostMode = "be337238-0d82-4146-a960-4f3749d470c7";

    /// <summary>Vínculo de estado de energía de PCI Express (ASPM).</summary>
    public const string PcieAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    /// <summary>Suspensión selectiva de USB.</summary>
    public const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
}

/// <summary>
/// Sonda de solo lectura sobre <c>powercfg /qh</c>. Ejecuta el comando, parsea por GUID y tokens hex
/// (nunca por etiqueta) y delega la forma de la observación a la función que se le pasa.
///
/// Si <c>powercfg</c> falla o no devuelve bloques legibles, la confianza es <c>Unknown</c> y la nota lo
/// explica: nunca se presenta un valor inventado como si se hubiera leído.
/// </summary>
public sealed class PowerQueryProbe(string key, IProcessRunner runner, Func<IReadOnlyList<PowerSettingQuery>, (string Value, string Confidence, string Note)> shape) : IDiagnosticProbe
{
    public string Key { get; } = key;

    public async Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(new ProcessRequest
            {
                FileName = Platform.ProcessRunner.SystemExecutable("powercfg.exe"),
                Arguments = ["/qh"],
                Timeout = TimeSpan.FromSeconds(30)
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DiagnosticObservation(Key, "Unavailable", "powercfg /qh", "Unknown", "No se pudo ejecutar powercfg: " + ex.Message);
        }

        if (!result.IsSuccess)
            return new DiagnosticObservation(Key, "Unavailable", "powercfg /qh", "Unknown", $"powercfg /qh terminó con código {result.ExitCode}; no se asume ningún valor.");

        var parsed = PowerCfgQueryParser.Parse(result.StandardOutput);
        if (parsed.Count == 0)
            return new DiagnosticObservation(Key, "Unavailable", "powercfg /qh", "Unknown", "powercfg /qh no devolvió ningún bloque de ajuste legible.");

        var (value, confidence, note) = shape(parsed);
        return new DiagnosticObservation(Key, value, "powercfg /qh", confidence, note);
    }

    /// <summary>Formatea un ajuste con su valor AC y lo que el sistema enumera para él.</summary>
    internal static string Describe(PowerSettingQuery setting) =>
        $"AC={setting.AcIndex.ToString(CultureInfo.InvariantCulture)} ({setting.Describe()})";
}

/// <summary>
/// Las cuatro sondas de energía de la Capa 3. Se registran como <see cref="IDiagnosticProbe"/> y
/// aparecen en <c>fragforge probe</c>/<c>doctor</c>; NO son entradas de catálogo.
/// </summary>
public static class PowerProbeFactory
{
    public static IReadOnlyList<(string Key, Func<IProcessRunner, IDiagnosticProbe> Create)> Registrations { get; } =
    [
        // Plan activo, identificado por GUID. El nombre es localizable: solo se usa como pista.
        ("power.active-scheme", runner => new ActiveSchemeProbe(runner)),

        // Valores AC + rango/lista enumerada de cada ajuste del procesador que el sistema exponga.
        ("power.processor-settings", runner => new PowerQueryProbe("power.processor-settings", runner, parsed =>
        {
            var wanted = new[] { PowerSettingIds.PerfEpp, PowerSettingIds.PerfEpp1, PowerSettingIds.PerfEpp2, PowerSettingIds.CoreParkingMin, PowerSettingIds.CoreParkingMin1 };
            var found = parsed.Where(q => wanted.Contains(q.SettingGuid, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 0) return ("Unavailable", "Unknown", "El sistema no enumera ninguno de los ajustes de procesador de la Capa 3; no se asume nada.");
            var text = string.Join("; ", found.Select(q => $"{q.SettingGuid} {PowerQueryProbe.Describe(q)}"));
            // Si faltan variantes, se dice: en la PN64 no todas las clases están enumeradas.
            var missing = wanted.Except(found.Select(q => q.SettingGuid), StringComparer.OrdinalIgnoreCase).ToArray();
            var note = missing.Length == 0 ? "Todas las variantes están enumeradas." : "No enumeradas en este equipo: " + string.Join(", ", missing);
            return (text, "High", note);
        })),

        // Modo de impulso: finding si el valor es 0, 5 o 6. No lo escribe.
        ("power.boost-mode", runner => new PowerQueryProbe("power.boost-mode", runner, parsed =>
        {
            var setting = parsed.FirstOrDefault(q => q.SettingGuid.Equals(PowerSettingIds.PerfBoostMode, StringComparison.OrdinalIgnoreCase));
            if (setting is null) return ("Unknown", "Unknown", "El sistema no enumera PERFBOOSTMODE; no se asume su valor.");
            var ac = setting.AcIndex;
            var finding = ac switch
            {
                0 => "Deshabilitado (0): no se aplica impulso al cargar.",
                5 => "Agresivo (5): impulso agresivo al cargar; puede subir temperatura.",
                6 => "Agresivo de forma sostenida (6): impulso agresivo sostenido; mayor riesgo térmico.",
                _ => $"{ac}: valor observado fuera de los casos que este finding marca (0, 5, 6)."
            };
            return ($"AC={ac}", "High", finding + " Solo lectura: FragForge no escribe este ajuste.");
        })),

        // Batería: unknown NO es lo mismo que ausente.
        ("power.battery", _ => new BatteryProbe())
    ];
}

/// <summary>Plan activo por GUID. El GUID se lee con regex; el nombre solo se reporta como pista.</summary>
public sealed class ActiveSchemeProbe(IProcessRunner runner) : IDiagnosticProbe
{
    private static readonly System.Text.RegularExpressions.Regex GuidPattern = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public string Key => "power.active-scheme";

    public async Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessRequest
            {
                FileName = Platform.ProcessRunner.SystemExecutable("powercfg.exe"),
                Arguments = ["/getactivescheme"],
                Timeout = TimeSpan.FromSeconds(30)
            }, cancellationToken);

            if (!result.IsSuccess)
                return new DiagnosticObservation(Key, "Unavailable", "powercfg /getactivescheme", "Unknown", $"powercfg terminó con código {result.ExitCode}.");

            var match = GuidPattern.Match(result.StandardOutput);
            if (!match.Success)
                return new DiagnosticObservation(Key, "Unavailable", "powercfg /getactivescheme", "Unknown", "La salida no contiene ningún GUID; no se adivina el plan.");

            // El perfil ya trae el GUID leído por WMI: si discrepan, se dice (no se elige uno).
            var fromProfile = profile.Power.ActiveSchemeGuid;
            var note = fromProfile is not null && !fromProfile.Equals(match.Value, StringComparison.OrdinalIgnoreCase)
                ? $"Discrepancia con el perfil ({fromProfile}); se reporta el de powercfg."
                : null;
            return new DiagnosticObservation(Key, match.Value.ToLowerInvariant(), "powercfg /getactivescheme", "High", note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DiagnosticObservation(Key, "Unavailable", "powercfg /getactivescheme", "Unknown", ex.Message);
        }
    }
}

/// <summary>
/// Batería. Distingue tres estados que NO son equivalentes: ausente, presente y desconocido. El dato
/// sale del perfil (<c>FormFactor.HasBattery</c>), que se llena por WMI <c>Win32_Battery</c>.
/// </summary>
public sealed class BatteryProbe : IDiagnosticProbe
{
    public string Key => "power.battery";

    public Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var form = profile.FormFactor;
        // IsPortable nulo significa que el sondeo no pudo determinarlo: eso es "desconocido", no "ausente".
        if (form.IsPortable is null && !form.HasBattery)
            return Task.FromResult(new DiagnosticObservation(Key, "Unknown", "WMI Win32_Battery / chasis", "Unknown", "El factor de forma no se pudo determinar: desconocido no es lo mismo que ausente."));
        return Task.FromResult(new DiagnosticObservation(
            Key,
            form.HasBattery ? "Present" : "Absent",
            "WMI Win32_Battery",
            "High",
            form.HasBattery ? "Equipo con batería: FragForge no escribe valores de CC." : "Equipo sin batería: se permite escribir AC y DC."));
    }
}
