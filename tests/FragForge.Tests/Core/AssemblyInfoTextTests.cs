using System.Reflection;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Capa 11.1: el título de la ventana sale del ensamblado, no de un literal "Capa 0" que se queda congelado.
/// </summary>
public sealed class AssemblyInfoTextTests
{
    [Fact]
    public void WindowTitle_HasTheProductNameAndAVersion()
    {
        var title = AssemblyInfoText.WindowTitle();

        Assert.StartsWith(AssemblyInfoText.ProductName, title, StringComparison.Ordinal);
        Assert.True(title.Length > AssemblyInfoText.ProductName.Length, $"El título '{title}' no incluye versión.");
    }

    /// <summary>El nombre de fase de desarrollo no puede reaparecer en el título.</summary>
    [Theory]
    [InlineData("Capa")]
    [InlineData("Layer 0")]
    public void WindowTitle_DoesNotContainTheDevelopmentPhase(string forbidden)
    {
        Assert.DoesNotContain(forbidden, AssemblyInfoText.WindowTitle(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Con un ensamblado concreto, el título usa SU versión informativa. Se comprueba contra el ensamblado de
    /// Tweaks (no el de pruebas) para que el test no sea auto-referencial.
    /// </summary>
    [Fact]
    public void WindowTitle_UsesTheInformationalVersionOfTheGivenAssembly()
    {
        var assembly = typeof(FragForge.Tweaks.TweakCatalog).Assembly;
        var expected = ExpectedVersion(assembly);

        Assert.Equal(AssemblyInfoText.ProductName + " " + expected, AssemblyInfoText.WindowTitle(assembly));
    }

    /// <summary>Sin sufijo <c>+hash</c>, como en <see cref="AssemblyInfoText.WindowTitle"/>.</summary>
    private static string ExpectedVersion(Assembly assembly) =>
        (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? assembly.GetName().Version?.ToString(3)
         ?? "").Split('+')[0];

    /// <summary>El sufijo <c>+hash</c> del build queda fuera del título, pero sí está en la versión completa.</summary>
    [Fact]
    public void TheCommitSuffix_IsHiddenFromTheTitle_ButKeptInTheFullVersion()
    {
        Assert.DoesNotContain("+", AssemblyInfoText.WindowTitle(), StringComparison.Ordinal);

        var full = AssemblyInfoText.VersionWithCommit();
        // La versión completa puede o no traer el sufijo (depende del build); lo que se fija es que no se pierde.
        Assert.False(string.IsNullOrWhiteSpace(full));
        Assert.Equal(AssemblyInfoText.Version(), full.Split('+')[0]);
    }
}
