using FragForge.Actions.Measurement;
using Xunit;

namespace FragForge.Tests.Measurement.Live;

public sealed class LivePresentMonArgumentsTests
{
    [Theory]
    [InlineData("cs2")]
    [InlineData("cs2.exe")]
    public void Build_NormalizesAndUsesOnlyLiveFlags(string name)
    {
        var args = LivePresentMonArguments.Build(name, "sesion-de-prueba");
        // 6 entradas: dos flags con valor y dos flags sin valor.
        Assert.Equal(new[] { "--output_stdout", "--process_name", "cs2.exe", "--session_name", "sesion-de-prueba", "--no_console_stats" }, args);
        Assert.DoesNotContain("--stop_existing_session", args);
        foreach (var forbidden in new[] { "--output_file", "--no_csv", "--v1_metrics", "--v2_metrics", "--timed", "--terminate_after_timed", "--hotkey" })
            Assert.DoesNotContain(forbidden, args);
    }

    [Fact]
    public void NewSessionName_HasPrefixAndEightHexDigits()
    {
        var first = LivePresentMonArguments.NewSessionName();
        var second = LivePresentMonArguments.NewSessionName();
        // Prefijo de 14 caracteres + 8 hexadecimales = 22.
        Assert.StartsWith("FragForgeLive-", first);
        Assert.Equal(22, first.Length);
        Assert.Matches("^FragForgeLive-[0-9a-f]{8}$", first);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RejectsBlankName(string name) =>
        Assert.Throws<ArgumentException>(() => LivePresentMonArguments.Build(name, "FragForgeLive"));
}
