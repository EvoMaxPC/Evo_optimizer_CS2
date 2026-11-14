using FragForge.Actions.Measurement;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Gpu;

/// <summary>
/// Capa 6.2 — contadores de la iGPU por WMI (R-F).
///
/// El doble de <see cref="IWmiQuery"/> es propio porque <c>FakeWmiQuery</c> siempre devuelve una lista
/// vacía y aquí hace falta poder devolver filas o lanzar, que es justo lo que hay que probar: que una
/// clase ausente quede en Unknown y NO en cero.
///
/// Ninguna prueba toca WMI real: no hay Windows en el entorno y, aunque lo hubiera, el valor de los
/// contadores depende de lo que esté dibujando la pantalla en ese instante.
/// </summary>
public sealed class GpuPerformanceProbeTests
{
    [Fact]
    public async Task WithBothClassesPresent_ReadsThreeDEngineAndMemory()
    {
        var wmi = new ScriptedWmi()
            .With(GpuPerformanceProbe.EngineClass,
            [
                Row(("Name", "engtype_3D"), ("EngineType", "engtype_3D"), ("UtilizationPercentage", "42")),
                Row(("Name", "engtype_Copy"), ("EngineType", "engtype_Copy"), ("UtilizationPercentage", "7"))
            ])
            .With(GpuPerformanceProbe.MemoryClass,
            [
                Row(("Name", "Intel(R) UHD Graphics"), ("DedicatedUsage", 0UL), ("SharedUsage", 1073741824UL), ("TotalCommitted", 2684354560UL))
            ]);

        var result = await new GpuPerformanceProbe(wmi).ReadAsync();

        Assert.Equal(FeatureState.Enabled, result.EngineState);
        Assert.Equal(FeatureState.Enabled, result.MemoryState);
        // Solo el motor 3D: el de copia no entra.
        var engine = Assert.Single(result.ThreeDEngines);
        Assert.Equal(42d, engine.UtilizationPercent);
        Assert.Equal(42d, result.BusiestThreeDEngine!.UtilizationPercent);

        var adapter = Assert.Single(result.Adapters);
        Assert.Equal(2684354560UL, adapter.CommittedBytes);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// El caso que importa (R-L): si la clase no existe, el estado es Unknown y el aviso NOMBRA la clase.
    /// Un cero sería una mentira: "no hay contadores" no es "la GPU está al 0%".
    /// </summary>
    [Fact]
    public async Task WhenTheEngineClassIsMissing_StateIsUnknown_AndTheWarningNamesTheClass()
    {
        var wmi = new ScriptedWmi().Throwing(GpuPerformanceProbe.EngineClass, "Invalid class");

        var result = await new GpuPerformanceProbe(wmi).ReadAsync();

        Assert.Equal(FeatureState.Unknown, result.EngineState);
        Assert.Empty(result.ThreeDEngines);
        Assert.Null(result.BusiestThreeDEngine);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(GpuPerformanceProbe.EngineClass, warning, StringComparison.Ordinal);
        Assert.Contains("Unknown", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Una consulta que NO lanza pero devuelve cero filas tampoco es "leído": sin filas no se sabe.
    /// </summary>
    [Fact]
    public async Task WithNoRows_StateIsUnknown_NotZero()
    {
        var result = await new GpuPerformanceProbe(new ScriptedWmi()).ReadAsync();

        Assert.Equal(FeatureState.Unknown, result.EngineState);
        Assert.Equal(FeatureState.Unknown, result.MemoryState);
        Assert.Null(result.BusiestThreeDEngine);
    }

    /// <summary>
    /// El perfil de hardware solo necesita la memoria: no debe consultar la clase de motores ni arrastrar
    /// sus avisos. Si consultara ambas, el perfil avisaría de una clase que no usa.
    /// </summary>
    [Fact]
    public async Task ReadAdapterMemory_OnlyQueriesTheMemoryClass()
    {
        var wmi = new ScriptedWmi()
            .Throwing(GpuPerformanceProbe.EngineClass, "no deberia consultarse")
            .With(GpuPerformanceProbe.MemoryClass,
            [
                Row(("Name", "Intel(R) UHD Graphics"), ("DedicatedUsage", 0UL), ("SharedUsage", 2147483648UL), ("TotalCommitted", 4294967296UL))
            ]);

        var result = await new GpuPerformanceProbe(wmi).ReadAdapterMemoryAsync();

        Assert.Empty(result.Warnings);
        Assert.Equal(FeatureState.Unknown, result.EngineState);
        Assert.Equal(FeatureState.Enabled, result.MemoryState);
        Assert.Equal(4294967296UL, Assert.Single(result.Adapters).CommittedBytes);
    }

    /// <summary>Sin la clase de memoria, el presupuesto es Unknown: el perfil escribirá null, no cero.</summary>
    [Fact]
    public async Task ReadAdapterMemory_WhenClassMissing_IsUnknown()
    {
        var result = await new GpuPerformanceProbe(new ScriptedWmi().Throwing(GpuPerformanceProbe.MemoryClass, "Invalid class"))
            .ReadAdapterMemoryAsync();

        Assert.Equal(FeatureState.Unknown, result.MemoryState);
        Assert.Empty(result.Adapters);
        Assert.Contains(GpuPerformanceProbe.MemoryClass, Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("engtype_3D", true)]
    [InlineData("3D", true)]
    [InlineData("3d", true)]
    [InlineData("engtype_Copy", false)]
    [InlineData("engtype_VideoDecode", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsThreeDEngine_MatchesBothKnownForms(string? engineType, bool expected)
    {
        Assert.Equal(expected, GpuPerformanceProbe.IsThreeDEngine(engineType));
    }

    /// <summary>R-E: el porcentaje se parsea con cultura invariante.</summary>
    [Theory]
    [InlineData("42.5", 42.5)]
    [InlineData("0", 0.0)]
    [InlineData("100", 100.0)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData("101", null)]
    [InlineData("-1", null)]
    public void ParsePercent_IsInvariantAndBounded(string raw, double? expected)
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(expected, GpuPerformanceProbe.ParsePercent(raw));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void ClassNamesAreTheOnesR_FRequires()
    {
        // R-F: contadores de rendimiento por Win32_PerfFormattedData_*. Se fija el nombre para que un
        // renombre accidental lo rompa aquí y no en la PC del usuario.
        Assert.Equal("Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine", GpuPerformanceProbe.EngineClass);
        Assert.Equal("Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory", GpuPerformanceProbe.MemoryClass);
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Doble de WMI que devuelve lo que se le dice, o lanza por clase.</summary>
    private sealed class ScriptedWmi : IWmiQuery
    {
        private readonly Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> _rows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _throw = new(StringComparer.OrdinalIgnoreCase);

        public ScriptedWmi With(string className, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        {
            _rows[className] = rows;
            return this;
        }

        public ScriptedWmi Throwing(string className, string message)
        {
            _throw[className] = message;
            return this;
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
            string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default)
        {
            if (_throw.TryGetValue(className, out var message)) throw new InvalidOperationException(message);
            return Task.FromResult(_rows.TryGetValue(className, out var rows) ? rows : []);
        }
    }
}
