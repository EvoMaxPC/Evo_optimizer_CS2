using FragForge.Cli;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class MatchOptionsTests
{
    [Fact]
    public void Default_IsFullMatchWithoutLog()
    {
        var options = MatchOptions.Parse([]);
        // Sin opciones: duración desconocida, no se solicita guardar.
        Assert.Null(options.DurationSeconds);
        Assert.False(options.SaveLog);
    }

    [Fact]
    public void DurationAndSave_AreParsed()
    {
        var options = MatchOptions.Parse(["--duration", "30", "--guardar-log"]);
        // 30 segundos está entre 0 y 86400; el flag activa el guardado.
        Assert.Equal(30.0, options.DurationSeconds);
        Assert.True(options.SaveLog);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("86401")]
    public void InvalidDuration_IsRejected(string value)
    {
        // Debe ser numérico y cumplir 0 < duración <= 86400.
        Assert.Throws<ArgumentException>(() => MatchOptions.Parse(["--duration", value]));
    }

    [Fact]
    public void UnknownAndDuplicateOptions_AreRejected()
    {
        // Una opción desconocida y dos apariciones de duration o del flag son inválidas.
        Assert.Throws<ArgumentException>(() => MatchOptions.Parse(["--otro"]));
        Assert.Throws<ArgumentException>(() => MatchOptions.Parse(["--duration", "30", "--duration", "40"]));
        Assert.Throws<ArgumentException>(() => MatchOptions.Parse(["--guardar-log", "--guardar-log"]));
    }
}
