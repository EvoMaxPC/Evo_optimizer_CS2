using FragForge.Actions.Diagnostics;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Diagnostics;

/// <summary>
/// Las cuatro sondas de energía son SOLO LECTURA y no adivinan: si powercfg falla, la confianza es
/// <c>Unknown</c> y la nota lo dice. La batería distingue ausente de desconocido.
/// </summary>
public sealed class PowerProbeTests
{
    private const string Pn64Output = """
        Configuración de energía específica del esquema de energía (solo lectura)
        GUID del subgrupo: 54533251-82be-4824-96c1-47b60b740d00  (Administración de energía del procesador)
          GUID del ajuste: 36687f9e-e3a5-4dbf-b1dc-15eb381c6863  (Preferencia de rendimiento del procesador)
            Valores posibles: Rango mínimo: 0x00000000 Rango máximo: 0x00000064 Incremento: 0x00000001
            Índice de energía actual: 0x00000005
            Índice de energía actual (CC): 0x00000000
          GUID del ajuste: 0cc5b647-c1df-4637-891a-dec35c318584  (Aparcamiento mínimo de núcleos 1)
            Valores posibles: Rango mínimo: 0x00000000 Rango máximo: 0x00000064 Incremento: 0x00000001
            Índice de energía actual: 0x00000000
            Índice de energía actual (CC): 0x00000000
          GUID del ajuste: be337238-0d82-4146-a960-4f3749d470c7  (Modo de impulso del procesador)
            Valores posibles: Rango mínimo: 0x00000000 Rango máximo: 0x00000007 Incremento: 0x00000001
            Índice de energía actual: 0x00000002
            Índice de energía actual (CC): 0x00000000
        """;

    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    /// <summary>
    /// Obtiene la fábrica de una sonda por su clave. El elemento de <c>Registrations</c> es una tupla con
    /// nombre, así que el creador se accede por nombre o por <c>Item2</c>: <c>.Create(...)</c> sobre el
    /// elemento de la lista no existe (CS1061).
    /// </summary>
    private static IDiagnosticProbe Probe(string key, IProcessRunner runner) =>
        PowerProbeFactory.Registrations.Single(r => r.Key == key).Create(runner);

    private static HardwareProfile Profile(bool? portable, bool battery) => new()
    {
        FormFactor = new FormFactorInfo { IsPortable = portable, HasBattery = battery }
    };

    [Fact]
    public async Task ProcessorSettings_ReportsTheEnumeratedValuesAndSaysWhatIsMissing()
    {
        var probe = Probe("power.processor-settings", new FakeRunner(new ProcessResult(0, Pn64Output, "", false)));

        var observation = await probe.ReadAsync(Profile(false, false));

        Assert.Equal("power.processor-settings", observation.Key);
        Assert.Equal("High", observation.Confidence);
        // El AC real de PERFEPP en la PN64 es 5; debe salir de la salida, no de una suposición.
        Assert.Contains("AC=5", observation.Value);
        // Las variantes …64/…65 no están enumeradas: se dice, no se inventan.
        Assert.Contains("No enumeradas", observation.Note);
    }

    [Fact]
    public async Task BoostMode_FlagsZeroFiveAndSix_AndIsReadOnly()
    {
        var probe = Probe("power.boost-mode", new FakeRunner(new ProcessResult(0, Pn64Output, "", false)));

        var observation = await probe.ReadAsync(Profile(false, false));

        Assert.Equal("AC=2", observation.Value);
        Assert.Contains("Solo lectura", observation.Note);
        Assert.Contains("2", observation.Note);
    }

    [Fact]
    public async Task BoostMode_FlagsAggressiveValues()
    {
        const string aggressive = """
            GUID del subgrupo: 54533251-82be-4824-96c1-47b60b740d00
              GUID del ajuste: be337238-0d82-4146-a960-4f3749d470c7
                Valores posibles: Rango mínimo: 0x00000000 Rango máximo: 0x00000007 Incremento: 0x00000001
                Índice de energía actual: 0x00000006
                Índice de energía actual (CC): 0x00000000
            """;
        var probe = Probe("power.boost-mode", new FakeRunner(new ProcessResult(0, aggressive, "", false)));

        var observation = await probe.ReadAsync(Profile(false, false));

        Assert.Equal("AC=6", observation.Value);
        Assert.Contains("térmico", observation.Note);
    }

    [Fact]
    public async Task WhenPowerCfgFails_ConfidenceIsUnknown_AndNoValueIsGuessed()
    {
        var probe = Probe("power.processor-settings", new FakeRunner(new ProcessResult(1, "", "acceso denegado", false)));

        var observation = await probe.ReadAsync(Profile(false, false));

        Assert.Equal("Unknown", observation.Confidence);
        Assert.Equal("Unavailable", observation.Value);
        Assert.Contains("código 1", observation.Note);
    }

    [Fact]
    public async Task ActiveScheme_ReadsTheGuid_NotTheLocalizedName()
    {
        const string spanish = "Esquema de energía activo: Alto rendimiento\r\nGUID del esquema de energía: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Alto rendimiento)\r\n";
        var probe = Probe("power.active-scheme", new FakeRunner(new ProcessResult(0, spanish, "", false)));

        var observation = await probe.ReadAsync(Profile(false, false));

        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", observation.Value);
        Assert.Equal("High", observation.Confidence);
    }

    [Fact]
    public async Task ActiveScheme_ReportsDiscrepancyWithTheProfile_InsteadOfChoosingOne()
    {
        var profile = new HardwareProfile
        {
            Power = new PowerInfo { ActiveSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e" }
        };
        var probe = Probe("power.active-scheme", new FakeRunner(new ProcessResult(0, "GUID del esquema de energía: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "", false)));

        var observation = await probe.ReadAsync(profile);

        Assert.Contains("Discrepancia", observation.Note);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", observation.Value);
    }

    [Fact]
    public async Task Battery_DistinguishesAbsentFromUnknown()
    {
        var battery = new BatteryProbe();

        var absent = await battery.ReadAsync(Profile(false, false));
        var unknown = await battery.ReadAsync(Profile(null, false));

        Assert.Equal("Absent", absent.Value);
        Assert.Equal("Unknown", unknown.Value);
        // Desconocido no puede presentarse como ausente: son estados distintos.
        Assert.NotEqual(absent.Value, unknown.Value);
    }

    [Fact]
    public void TheFourProbesAreRegistered_AndNoneIsACatalogEntry()
    {
        var keys = PowerProbeFactory.Registrations.Select(r => r.Key).ToArray();

        Assert.Equal(["power.active-scheme", "power.processor-settings", "power.boost-mode", "power.battery"], keys);
        // No son entradas de catálogo: sus claves no siguen el esquema de ids de tweaks.
        Assert.All(keys, k => Assert.StartsWith("power.", k, StringComparison.Ordinal));
        Assert.All(keys, k => Assert.DoesNotContain("power.fragforge", k, StringComparison.Ordinal));
    }
}
