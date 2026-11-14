using FragForge.Actions.Measurement;
using FragForge.Core.Experiments;
using Xunit;

namespace FragForge.Tests.Measurement;

/// <summary>
/// Contrato de <see cref="MeasurementEnvironmentProbe.Validate"/>: devuelve la lista de problemas que
/// invalidan una comparación A/B, y una lista VACÍA significa "el entorno sirve para medir".
///
/// La sonda es de solo lectura y estas pruebas no tocan el sistema: solo se le pasa un
/// <see cref="ExperimentEnvironment"/> ya construido.
/// </summary>
public sealed class EnvironmentValidateTests
{
    /// <summary>Entorno limpio: sin reinicio pendiente, sin servicing y con carga de fondo baja.</summary>
    private static ExperimentEnvironment Clean() => new()
    {
        OsBuild = "26200",
        OsVersion = "25H2",
        CpuModel = "12th Gen Intel Core i3-1220P",
        GpuName = "Intel UHD Graphics",
        PowerPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        RefreshRateHz = 200,
        MonitorWidth = 1920,
        MonitorHeight = 1080,
        PendingReboot = false,
        ServicingActive = false,
        BackgroundCpuPercent = 3.5
    };

    [Fact]
    public void CleanEnvironment_IsValid()
    {
        var problems = new MeasurementEnvironmentProbe().Validate(Clean());

        Assert.Empty(problems);
    }

    [Fact]
    public void PendingReboot_InvalidatesTheComparison()
    {
        var environment = Clean() with { PendingReboot = true };

        var problems = new MeasurementEnvironmentProbe().Validate(environment);

        Assert.Single(problems);
        Assert.Contains("reinicio pendiente", problems[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveServicing_InvalidatesTheComparison()
    {
        var environment = Clean() with { ServicingActive = true };

        var problems = new MeasurementEnvironmentProbe().Validate(environment);

        Assert.Single(problems);
        Assert.Contains("Servicing", problems[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackgroundCpuAboveThreshold_InvalidatesTheComparison()
    {
        // El umbral por defecto es 25 %: 40 % lo supera.
        var environment = Clean() with { BackgroundCpuPercent = 40.0 };

        var problems = new MeasurementEnvironmentProbe().Validate(environment);

        Assert.Single(problems);
        Assert.Contains("carga de fondo", problems[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackgroundCpuAtTheThreshold_IsStillValid()
    {
        // El límite se compara estrictamente: justo en el umbral no es un problema.
        var environment = Clean() with { BackgroundCpuPercent = 25.0 };

        var problems = new MeasurementEnvironmentProbe().Validate(environment);

        Assert.Empty(problems);
    }

    [Fact]
    public void UnknownBackgroundCpu_IsNotTreatedAsHighLoad()
    {
        // Dato desconocido != carga alta. Si el sensor falló, no se inventa un veredicto: bloquear
        // aquí convertiría un fallo de medición en un fallo del entorno.
        var environment = Clean() with { BackgroundCpuPercent = null };

        var problems = new MeasurementEnvironmentProbe().Validate(environment);

        Assert.Empty(problems);
    }

    [Fact]
    public void TheThresholdIsConfigurable()
    {
        var environment = Clean() with { BackgroundCpuPercent = 10.0 };

        Assert.Empty(new MeasurementEnvironmentProbe(25.0).Validate(environment));
        Assert.Single(new MeasurementEnvironmentProbe(5.0).Validate(environment));
    }

    [Fact]
    public void SeveralProblems_AreAllReported_NotJustTheFirst()
    {
        var environment = Clean() with { PendingReboot = true, ServicingActive = true, BackgroundCpuPercent = 90.0 };

        var problems = new MeasurementEnvironmentProbe().Validate(environment);

        // Cada motivo por separado: el mensaje del runner los une todos.
        Assert.Equal(3, problems.Count);
        Assert.All(problems, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public void NullEnvironment_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MeasurementEnvironmentProbe().Validate(null!));
    }
}
