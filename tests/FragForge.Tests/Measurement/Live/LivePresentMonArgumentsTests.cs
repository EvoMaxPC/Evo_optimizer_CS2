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
        var args = LivePresentMonArguments.Build(name, LivePresentMonArguments.DefaultSessionName);
        // 7 entradas: tres flags con valor y dos flags sin valor.
        Assert.Equal(new[] { "--output_stdout", "--process_name", "cs2.exe", "--session_name", "FragForgeLive", "--stop_existing_session", "--no_console_stats" }, args);
        foreach (var forbidden in new[] { "--output_file", "--no_csv", "--v1_metrics", "--v2_metrics", "--timed", "--terminate_after_timed", "--hotkey" })
            Assert.DoesNotContain(forbidden, args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RejectsBlankName(string name) =>
        Assert.Throws<ArgumentException>(() => LivePresentMonArguments.Build(name, "FragForgeLive"));
}
