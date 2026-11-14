using FragForge.Core.Cs2Profile;
using Xunit;

namespace FragForge.Tests.Cs2Profile;

public sealed class Cs2ProfileComparerTests
{
    private static readonly Cs2OptionCatalog Catalog = Cs2OptionCatalog.LoadEmbedded();

    private static Cs2ProfilePreferences Profile() => new()
    {
        FpsCap = FpsCap.Fixed(240),
        VSync = false,
        Options = [new SelectedOption { Id = "-novid" }]
    };

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SameDifferentAndMissing_AreDistinguished()
    {
        var comparison = Cs2ProfileComparer.Compare(
            Profile(), Catalog,
            autoexecValues: Values(("fps_max", "240"), ("mat_vsync", "1")),
            storedConvars: Values(),
            currentLaunchOptions: "-novid -console");

        // fps_max 240 == 240 -> Same. mat_vsync 1 != 0 -> Different. -novid ya está -> Same.
        Assert.Equal(ValueRelation.Same, comparison.Options.Single(o => o.Id == "fps_max").Relation);
        Assert.Equal(ValueRelation.Different, comparison.Options.Single(o => o.Id == "mat_vsync").Relation);
        Assert.Equal(ValueRelation.Same, comparison.Options.Single(o => o.Id == "-novid").Relation);
        Assert.Equal(2, comparison.SameCount);
        Assert.Equal(1, comparison.DifferentCount);
    }

    [Fact]
    public void MissingLaunchOption_IsReportedSeparately()
    {
        var comparison = Cs2ProfileComparer.Compare(Profile(), Catalog, null, null, "-console");

        var novid = comparison.Options.Single(o => o.Id == "-novid");
        Assert.Equal(ValueRelation.MissingLaunchOption, novid.Relation);
        Assert.Null(novid.CurrentValue);
    }

    [Fact]
    public void NotPresent_WhenNothingWasRead()
    {
        var comparison = Cs2ProfileComparer.Compare(Profile(), Catalog, null, null, null);

        var fpsMax = comparison.Options.Single(o => o.Id == "fps_max");
        Assert.Equal(ValueRelation.NotPresent, fpsMax.Relation);
        Assert.Null(fpsMax.CurrentSource);
    }

    [Fact]
    public void StoredConvars_AreUsed_WhenAutoexecHasNothing()
    {
        var comparison = Cs2ProfileComparer.Compare(
            Profile(), Catalog,
            autoexecValues: Values(),
            storedConvars: Values(("fps_max", "120")),
            currentLaunchOptions: "");

        var fpsMax = comparison.Options.Single(o => o.Id == "fps_max");
        Assert.Equal(ValueRelation.Different, fpsMax.Relation);
        Assert.Equal("convars guardadas", fpsMax.CurrentSource);
        Assert.Equal("120", fpsMax.CurrentValue);
    }

    [Fact]
    public void PrecedenceConflict_IsDeclaredNotVerified_NeverDecided()
    {
        // La misma convar con distinto valor en autoexec y en las guardadas: NO se elige un ganador.
        var comparison = Cs2ProfileComparer.Compare(
            Profile(), Catalog,
            autoexecValues: Values(("fps_max", "240")),
            storedConvars: Values(("fps_max", "120")),
            currentLaunchOptions: "");

        Assert.Contains(comparison.Warnings, w => w.Contains("no está verificada", StringComparison.Ordinal));
        Assert.Contains(comparison.Warnings, w => w.Contains("'240'", StringComparison.Ordinal) && w.Contains("'120'", StringComparison.Ordinal));
        // El valor mostrado como actual es el de autoexec (el que el usuario escribió), y así se etiqueta.
        var fpsMax = comparison.Options.Single(o => o.Id == "fps_max");
        Assert.Equal("autoexec", fpsMax.CurrentSource);
        Assert.Equal("240", fpsMax.CurrentValue);
    }

    [Fact]
    public void ValuesAreComparedAsText_NoSilentNormalization()
    {
        // "240" y "240.0" NO se consideran iguales: reinterpretar valores ajenos es cómo se dice algo falso.
        var comparison = Cs2ProfileComparer.Compare(
            Profile(), Catalog,
            autoexecValues: Values(("fps_max", "240.0")),
            storedConvars: Values(),
            currentLaunchOptions: "");

        Assert.Equal(ValueRelation.Different, comparison.Options.Single(o => o.Id == "fps_max").Relation);
    }

    [Fact]
    public void UnreadableLaunchOptions_IsAWarning_NotAFalseDifference()
    {
        var comparison = Cs2ProfileComparer.Compare(Profile(), Catalog, null, null, null);

        Assert.Contains(comparison.Warnings, w => w.Contains("No se pudieron leer las opciones de lanzamiento", StringComparison.Ordinal));
    }

    [Fact]
    public void LaunchOptionTokenMatch_DoesNotMatchSubstrings()
    {
        // "-novid" no debe darse por presente porque exista "-novidX" en las opciones.
        var comparison = Cs2ProfileComparer.Compare(Profile(), Catalog, null, null, "-novidX -console");

        Assert.Equal(ValueRelation.MissingLaunchOption, comparison.Options.Single(o => o.Id == "-novid").Relation);
    }

    [Fact]
    public void ComparisonNeverWrites_ItIsAPureFunctionOfItsInputs()
    {
        var autoexec = Values(("fps_max", "240"));
        var stored = Values(("mat_vsync", "1"));

        _ = Cs2ProfileComparer.Compare(Profile(), Catalog, autoexec, stored, "-novid");

        // Los diccionarios de entrada quedan intactos: no se normalizan ni se añaden claves.
        Assert.Single(autoexec);
        Assert.Single(stored);
        Assert.Equal("240", autoexec["fps_max"]);
    }
}
