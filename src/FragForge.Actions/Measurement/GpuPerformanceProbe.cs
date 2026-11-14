using System.Globalization;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Lectura de contadores de la iGPU por WMI (R-F, Capa 6.2).
///
/// Usa las clases <c>Win32_PerfFormattedData_*</c>, que es lo que exige R-F. Son CONTADORES ya formateados
/// por WMI: no se calculan tasas a mano (eso obligaría a muestrear dos veces y a inventar el intervalo).
///
/// Clases que se intentan:
/// <list type="bullet">
/// <item><c>GPUPerformanceCounters_GPUEngine</c>: uso por motor. Se filtra por <c>EngineType</c> igual a
///       <c>3D</c> o que termine en <c>engtype_3D</c>, que es como Windows nombra el motor 3D.</item>
/// <item><c>GPUPerformanceCounters_GPUAdapterMemory</c>: memoria dedicada/compartida del adaptador.</item>
/// </list>
///
/// <para>
/// Si una clase no existe en el equipo, el resultado es <see cref="FeatureState.Unknown"/> y se emite un
/// aviso que NOMBRA la clase. Nunca se convierte "no se pudo leer" en cero (R-L): un cero de uso y un
/// contador ausente no significan lo mismo.
/// </para>
///
/// <para>
/// Solo métricas. No hay ningún texto de "cuello de botella": decidir si la iGPU limita un juego requiere
/// medir el juego, no leer un porcentaje aislado.
/// </para>
/// </summary>
public sealed class GpuPerformanceProbe(IWmiQuery wmi)
{
    /// <summary>Nombre de la clase de motores. Se expone para el aviso y para los tests.</summary>
    public const string EngineClass = "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine";

    /// <summary>Nombre de la clase de memoria de adaptador.</summary>
    public const string MemoryClass = "Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory";

    /// <summary>Filtro del motor 3D. Se compara en minúsculas contra los dos formatos conocidos.</summary>
    public const string ThreeDEngineToken = "3d";

    private static readonly string[] EngineProperties = ["Name", "EngineType", "UtilizationPercentage"];
    private static readonly string[] MemoryProperties = ["Name", "DedicatedUsage", "SharedUsage", "TotalCommitted"];

    /// <summary>
    /// Solo la memoria del adaptador. Existe porque el perfil de hardware (Capa 6.1) necesita el presupuesto
    /// de memoria compartida y NO necesita el uso por motor: consultar el motor ahí seria una consulta de mas
    /// y ademas añadiria avisos sobre una clase que el perfil no usa.
    /// </summary>
    public async Task<GpuCountersResult> ReadAdapterMemoryAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var memory = await TryQueryAsync(MemoryClass, MemoryProperties, warnings, cancellationToken);
        return new GpuCountersResult
        {
            EngineState = FeatureState.Unknown,
            MemoryState = memory.Count > 0 ? FeatureState.Enabled : FeatureState.Unknown,
            Adapters = MapAdapters(memory),
            Warnings = warnings
        };
    }

    public async Task<GpuCountersResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        var engines = await TryQueryAsync(EngineClass, EngineProperties, warnings, cancellationToken);
        var memory = await TryQueryAsync(MemoryClass, MemoryProperties, warnings, cancellationToken);

        var threeD = engines
            .Where(row => IsThreeDEngine(String(row, "EngineType")))
            .Select(row => new GpuEngineUsage(
                String(row, "Name") ?? "(sin nombre)",
                ParsePercent(String(row, "UtilizationPercentage"))))
            .Where(u => u.UtilizationPercent is not null)
            .OrderByDescending(u => u.UtilizationPercent!.Value)
            .ToArray();

        var adapterMemory = MapAdapters(memory);

        // Estado por clase: sin filas NO se sabe, aunque la consulta no haya lanzado (puede devolver una
        // lista vacia en un equipo sin GPU discreta o sin contadores habilitados). Solo "hay datos" es leer.
        var engineState = engines.Count > 0 ? FeatureState.Enabled : FeatureState.Unknown;
        var memoryState = memory.Count > 0 ? FeatureState.Enabled : FeatureState.Unknown;

        return new GpuCountersResult
        {
            EngineState = engineState,
            MemoryState = memoryState,
            ThreeDEngines = threeD,
            Adapters = adapterMemory,
            Warnings = warnings
        };
    }

    /// <summary>
    /// ¿Es el motor 3D? Windows lo reporta como <c>engtype_3D</c> dentro de <c>EngineType</c>, o como
    /// <c>3D</c> en algunos equipos. Se acepta cualquiera que contenga el token, sin distinguir mayúsculas.
    /// </summary>
    public static bool IsThreeDEngine(string? engineType)
    {
        if (string.IsNullOrWhiteSpace(engineType)) return false;
        var normalized = engineType.Trim();
        if (normalized.Equals(ThreeDEngineToken, StringComparison.OrdinalIgnoreCase)) return true;
        // "engtype_3D" → el sufijo despues de '_' es lo que importa.
        var underscore = normalized.LastIndexOf('_');
        var suffix = underscore >= 0 ? normalized[(underscore + 1)..] : normalized;
        return suffix.Equals(ThreeDEngineToken, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<GpuMemoryUsage> MapAdapters(IReadOnlyList<IReadOnlyDictionary<string, object?>> memory) =>
        memory.Select(row => new GpuMemoryUsage(
                String(row, "Name") ?? "(sin nombre)",
                ToUInt64(row, "DedicatedUsage"),
                ToUInt64(row, "SharedUsage"),
                ToUInt64(row, "TotalCommitted")))
            .ToArray();

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> TryQueryAsync(
        string className, IReadOnlyList<string> properties, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            return await wmi.QueryAsync("root\\cimv2", className, properties, where: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La clase puede no existir en este equipo (p. ej. sin contadores de GPU habilitados).
            // Se NOMBRA la clase: es lo que pide el informe, no un "no se pudo leer" genérico.
            warnings.Add($"No se pudo consultar {className}: {ex.Message}. La métrica queda Unknown, no en cero.");
            return [];
        }
    }

    private static string? String(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>R-E: el porcentaje se parsea con cultura invariante, nunca con la de la UI.</summary>
    public static double? ParsePercent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        if (value < 0 || value > 100) return null;
        return value;
    }

    private static ulong? ToUInt64(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            ulong u => u,
            long l when l >= 0 => (ulong)l,
            int i when i >= 0 => (ulong)i,
            string s when ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}

/// <summary>Uso de un motor de la GPU. <c>UtilizationPercent</c> es <c>null</c> si el contador no vino.</summary>
public sealed record GpuEngineUsage(string Name, double? UtilizationPercent);

/// <summary>Memoria de un adaptador, en bytes. Cada campo puede faltar por separado.</summary>
public sealed record GpuMemoryUsage(string Name, ulong? DedicatedBytes, ulong? SharedBytes, ulong? CommittedBytes);

/// <summary>
/// Resultado de la lectura. <see cref="FeatureState.Unknown"/> significa "no se pudo leer", nunca cero.
/// </summary>
public sealed record GpuCountersResult
{
    public FeatureState EngineState { get; init; } = FeatureState.Unknown;
    public FeatureState MemoryState { get; init; } = FeatureState.Unknown;
    public IReadOnlyList<GpuEngineUsage> ThreeDEngines { get; init; } = [];
    public IReadOnlyList<GpuMemoryUsage> Adapters { get; init; } = [];
    /// <summary>Avisos que nombran la clase que falló. Vacío = todo se leyó.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>El motor 3D con más uso, o <c>null</c> si no hay ninguno legible.</summary>
    public GpuEngineUsage? BusiestThreeDEngine => ThreeDEngines.Count > 0 ? ThreeDEngines[0] : null;
}
