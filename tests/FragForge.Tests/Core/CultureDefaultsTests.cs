using FragForge.Core.Localization;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Hotfix Capa 0 — combo de idioma sin selección.
///
/// Lo comprobable sin WPF es que el valor inicial SIEMPRE exista en la lista de opciones del combo:
/// si el valor por defecto no está en el ItemsSource, WPF no selecciona nada y el combo queda vacío.
/// </summary>
public sealed class CultureDefaultsTests
{
    [Theory]
    [InlineData("es-MX", "es-MX")]
    [InlineData("es-ES", "es-MX")] // misma lengua, otra región -> cae en la soportada
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("de-DE", "es-MX")] // no soportada -> primera por defecto
    [InlineData("", "es-MX")]
    [InlineData(null, "es-MX")]
    public void PickInitialCulture_AlwaysReturnsAValuePresentInTheList(string? uiCulture, string expected)
    {
        var chosen = CultureDefaults.PickInitialCulture(CultureDefaults.Supported, uiCulture);

        Assert.Equal(expected, chosen);
        Assert.Contains(chosen, CultureDefaults.Supported);
    }

    [Fact]
    public void PickInitialCulture_PrefersEsMx_WhenTheUiLanguageIsUnknown()
    {
        Assert.Equal("es-MX", CultureDefaults.PickInitialCulture(["en-US", "es-MX"], "fr-FR"));
    }

    [Fact]
    public void PickInitialCulture_FallsBackToEnUs_WhenEsMxIsNotAvailable()
    {
        Assert.Equal("en-US", CultureDefaults.PickInitialCulture(["en-US"], "fr-FR"));
    }

    [Fact]
    public void PickInitialCulture_ReturnsNull_WhenTheListIsEmpty()
    {
        // No se inventa una selección que no existe en el ItemsSource.
        Assert.Null(CultureDefaults.PickInitialCulture([], "es-MX"));
    }

    [Fact]
    public void Supported_MatchesTheEmbeddedResources() => Assert.Equal(["es-MX", "en-US"], CultureDefaults.Supported);
}
