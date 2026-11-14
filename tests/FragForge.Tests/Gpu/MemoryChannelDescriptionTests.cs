using FragForge.Actions.Platform;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Gpu;

/// <summary>
/// Capa 6.1 — la frase de canales de RAM.
///
/// La regla de la ronda es explícita: WMI NO confirma dual-channel, así que el texto solo puede decir
/// "probable" y "no confirmable". Estas pruebas fijan que nunca se afirme, y que con un solo módulo o con
/// módulos distintos ni siquiera se sugiera.
///
/// <c>DescribeChannels</c> es puro (recibe la lista de módulos ya leída), así que no hace falta WMI ni
/// Windows para probarlo. Pero VIVE en <c>WindowsHardwareProfiler</c>, que está marcada
/// <c>[SupportedOSPlatform("windows")]</c>: sin la marca en el test, cada acceso daría CA1416 en un
/// proyecto net10.0. Mismo patrón que NetAdapterPropertiesTests.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class MemoryChannelDescriptionTests
{
    [Fact]
    public void WithTwoEqualModules_SaysProbableAndNotConfirmable()
    {
        var text = WindowsHardwareProfiler.DescribeChannels([Module(8UL << 30, 3200), Module(8UL << 30, 3200)]);

        Assert.Contains("2 módulos iguales", text, StringComparison.Ordinal);
        Assert.Contains("dual-channel probable", text, StringComparison.Ordinal);
        Assert.Contains("no confirmable", text, StringComparison.Ordinal);

        // Lo que NUNCA puede aparecer: una afirmación en firme.
        Assert.DoesNotContain("es dual-channel", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirmado", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithOneModule_SaysSingleChannel_NotProbableDual()
    {
        var text = WindowsHardwareProfiler.DescribeChannels([Module(16UL << 30, 3200)]);

        Assert.Contains("1 módulo", text, StringComparison.Ordinal);
        Assert.Contains("single-channel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dual-channel probable", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithDifferentCapacities_DoesNotSuggestDualChannel()
    {
        var text = WindowsHardwareProfiler.DescribeChannels([Module(8UL << 30, 3200), Module(16UL << 30, 3200)]);

        Assert.Contains("no se puede afirmar dual-channel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("probable", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithDifferentSpeeds_DoesNotSuggestDualChannel()
    {
        var text = WindowsHardwareProfiler.DescribeChannels([Module(8UL << 30, 2666), Module(8UL << 30, 3200)]);

        Assert.Contains("no se puede afirmar dual-channel", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoModules_SaysNothingAboutChannels()
    {
        var text = WindowsHardwareProfiler.DescribeChannels([]);

        Assert.Contains("no devolvió módulos", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dual-channel", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>La frase siempre deja constancia de que WMI no ve el entrelazado.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AlwaysExplainsThatWmiDoesNotSeeInterleaving(int moduleCount)
    {
        var modules = Enumerable.Range(0, moduleCount).Select(_ => Module(8UL << 30, 3200)).ToArray();
        var text = WindowsHardwareProfiler.DescribeChannels(modules);

        Assert.Contains("no el entrelazado", text, StringComparison.Ordinal);
    }

    private static MemoryModuleInfo Module(ulong capacityBytes, int speed) =>
        new() { CapacityBytes = capacityBytes, SpeedMtPerSecond = speed, ConfiguredSpeedMtPerSecond = speed };
}
