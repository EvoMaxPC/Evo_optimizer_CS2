using FragForge.Cli;
using Xunit;

namespace FragForge.Tests.Cli;

/// <summary>
/// El mantenimiento tiene una lista CERRADA de verbos y argumentos fijos: el usuario no puede inyectar
/// un comando. Estas pruebas fijan esa propiedad, que es la que hace seguro exponerlo.
/// </summary>
public sealed class MaintenanceCommandTests
{
    [Theory]
    [InlineData("retrim")]
    [InlineData("component-cleanup")]
    public void EveryVerb_BuildsAFixedCommandLine(string verb)
    {
        var (fileName, arguments) = MaintenanceCommands.BuildCommand(verb, @"C:\");

        Assert.False(string.IsNullOrWhiteSpace(fileName));
        Assert.NotEmpty(arguments);
        // Los argumentos son fijos: no llevan rutas del usuario ni interpolación de texto libre.
        Assert.All(arguments, a => Assert.False(string.IsNullOrWhiteSpace(a)));
    }

    [Fact]
    public void Retrim_UsesTheRetrimSwitchOnTheSystemVolume()
    {
        var (fileName, arguments) = MaintenanceCommands.BuildCommand("retrim", @"C:\");

        Assert.EndsWith("defrag.exe", fileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([@"C:\", "/L"], arguments);
    }

    [Fact]
    public void ComponentCleanup_NeverUsesResetBase()
    {
        var (fileName, arguments) = MaintenanceCommands.BuildCommand("component-cleanup", @"C:\");

        Assert.EndsWith("Dism.exe", fileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/StartComponentCleanup", arguments);
        // /ResetBase hace permanentes las actualizaciones instaladas: nunca debe aparecer.
        Assert.DoesNotContain(arguments, a => a.Equals("/ResetBase", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("/ResetBase", string.Join(' ', arguments), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownVerb_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => MaintenanceCommands.BuildCommand("cleanmgr", @"C:\"));
        Assert.DoesNotContain("cleanmgr", MaintenanceCommands.Verbs);
    }

    [Fact]
    public void TheVerbListIsClosedAndDocumented()
    {
        Assert.Equal(["retrim", "component-cleanup"], MaintenanceCommands.Verbs);
    }
}
