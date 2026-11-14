using FragForge.Actions.Measurement;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Measurement;

/// <summary>
/// Localización del ejecutable de PresentMon. Nunca PATH; orden configuración → instalación de Intel → error.
/// Los tests no dependen de que PresentMon esté instalado en la máquina que corre la suite.
/// </summary>
public sealed class PresentMonLocatorTests
{
    [Fact]
    public void ConfiguredPathWins_WhenItExists()
    {
        var path = Path.Combine(Path.GetTempPath(), "presentmon-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "fake");
        try
        {
            var location = PresentMonLocator.Locate(path);

            Assert.True(location.Found);
            Assert.True(location.IsConfigured);
            Assert.Equal(path, location.Path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MissingConfiguredPath_ReportsIt_AndDoesNotFallBackSilently()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".exe");

        var location = PresentMonLocator.Locate(missing);

        Assert.False(location.Found);
        Assert.Contains("no existe", location.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, location.Detail);
    }

    [Fact]
    public void NonWindows_DoesNotThrow_AndReportsNotFound()
    {
        // En Linux/macOS no hay %ProgramFiles%: debe devolver NotFound sin excepción, nunca buscar en PATH.
        var location = PresentMonLocator.Locate();

        if (OperatingSystem.IsWindows())
        {
            // En Windows puede encontrarlo o no; en ambos casos no debe lanzar y el detalle no puede estar vacío.
            Assert.False(string.IsNullOrWhiteSpace(location.Detail));
        }
        else
        {
            Assert.False(location.Found);
            Assert.False(string.IsNullOrWhiteSpace(location.Detail));
        }
    }

    [Fact]
    public void Locate_NeverSearchesPath()
    {
        // Contrato explícito: sin configuración ni instalación, el resultado es NotFound aunque haya un
        // PresentMon.exe en PATH. Se comprueba que el detalle no mencione una resolución por PATH.
        var location = PresentMonLocator.Locate();

        Assert.DoesNotContain("PATH", location.Detail.Replace("no busca en PATH", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureAvailability_WhenToolMissing_OffersTheInstalledLocationInTheMessage()
    {
        using var tools = new TempDirectory();
        var allowlist = new ToolAllowlist
        {
            Tools = [new AllowedTool { Key = ToolVerifier.PresentMonKey, FileName = "PresentMon.exe", Sha256 = null }]
        };
        var capture = new PresentMonFrameCapture(new NoopLauncher(), new ToolVerifier(tools.Path, allowlist));

        var availability = await capture.CheckAvailabilityAsync();

        Assert.False(availability.IsAvailable);
        Assert.Contains("PresentMon.exe", availability.Reason, StringComparison.Ordinal);
        // El hash sigue siendo obligatorio: el mensaje debe hablar de la lista blanca.
        Assert.True(
            availability.Reason.Contains("lista blanca", StringComparison.OrdinalIgnoreCase) ||
            availability.Reason.Contains("SHA-256", StringComparison.OrdinalIgnoreCase),
            "El mensaje debe indicar que el hash sigue pendiente: " + availability.Reason);
    }

    private sealed class NoopLauncher : IToolProcessLauncher
    {
        public Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolProcessResult(0, false, false, "", ""));
    }
}
