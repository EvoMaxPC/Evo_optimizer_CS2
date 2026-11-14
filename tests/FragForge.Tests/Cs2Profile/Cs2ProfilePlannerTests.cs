using FragForge.Core.Cs2Profile;
using Xunit;

namespace FragForge.Tests.Cs2Profile;

public sealed class Cs2ProfilePlannerTests
{
    private static readonly Cs2OptionCatalog Catalog = Cs2OptionCatalog.LoadEmbedded();

    private static ProfilePlan Plan(Cs2ProfilePreferences preferences) => Cs2ProfilePlanner.Plan(preferences, Catalog);

    [Fact]
    public void RefreshUnknown_DoesNotInventACap_AndWarns()
    {
        // R-L: sin refresco medido no se escribe fps_max. Ni 60 ni 144: ninguno.
        var plan = Plan(new Cs2ProfilePreferences
        {
            FpsCap = FpsCap.DerivedFromRefresh(1.0),
            MeasuredRefreshHz = null
        });

        Assert.DoesNotContain(plan.CfgOptions, o => o.Id == "fps_max");
        Assert.Contains(plan.Warnings, w => w.Contains("No se pudo medir la tasa de refresco", StringComparison.Ordinal));
    }

    [Fact]
    public void DerivedCap_UsesTheMeasuredRefresh()
    {
        // Cálculo a mano: 144 Hz * 1.0 = 144 -> round(144) = 144.
        var plan = Plan(new Cs2ProfilePreferences { FpsCap = FpsCap.DerivedFromRefresh(1.0), MeasuredRefreshHz = 144 });

        var fpsMax = Assert.Single(plan.CfgOptions.Where(o => o.Id == "fps_max"));
        Assert.Equal("144", fpsMax.Value);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void DerivedCap_WithFactor_RoundsHalfAwayFromZero()
    {
        // Cálculo a mano: 165 Hz * 0.5 = 82.5 -> AwayFromZero -> 83. (Con ToEven daría 82: por eso se fija.)
        var plan = Plan(new Cs2ProfilePreferences { FpsCap = FpsCap.DerivedFromRefresh(0.5), MeasuredRefreshHz = 165 });

        Assert.Equal("83", Assert.Single(plan.CfgOptions.Where(o => o.Id == "fps_max")).Value);

        // Cálculo a mano: 165 Hz * 2.0 = 330 -> 330.
        var doubled = Plan(new Cs2ProfilePreferences { FpsCap = FpsCap.DerivedFromRefresh(2.0), MeasuredRefreshHz = 165 });
        Assert.Equal("330", Assert.Single(doubled.CfgOptions.Where(o => o.Id == "fps_max")).Value);
    }

    [Fact]
    public void FixedCap_NeedsAPositiveValue()
    {
        var zero = Plan(new Cs2ProfilePreferences { FpsCap = FpsCap.Fixed(0) });
        Assert.DoesNotContain(zero.CfgOptions, o => o.Id == "fps_max");
        Assert.Contains(zero.Warnings, w => w.Contains("mayor que 0", StringComparison.Ordinal));

        // Cálculo a mano: no hay factor ni refresco de por medio; se escribe el número tal cual.
        var ok = Plan(new Cs2ProfilePreferences { FpsCap = FpsCap.Fixed(240) });
        Assert.Equal("240", Assert.Single(ok.CfgOptions.Where(o => o.Id == "fps_max")).Value);
    }

    [Fact]
    public void CapOverridesAHandPickedFpsMax_SoThereAreNoDuplicates()
    {
        // El usuario tildó fps_max=100 y además pide tope fijo 240: gana el tope, y solo hay UNA línea.
        var plan = Plan(new Cs2ProfilePreferences
        {
            FpsCap = FpsCap.Fixed(240),
            Options = [new SelectedOption { Id = "fps_max", Value = "100" }]
        });

        Assert.Equal("240", Assert.Single(plan.CfgOptions.Where(o => o.Id == "fps_max")).Value);
        Assert.Single(plan.CfgOptions, o => o.Id == "fps_max");
    }

    [Fact]
    public void VsyncSwitch_MapsTo1Or0_AndOnlyWhenAsked()
    {
        Assert.Equal("1", Assert.Single(Plan(new Cs2ProfilePreferences { VSync = true }).CfgOptions.Where(o => o.Id == "mat_vsync")).Value);
        Assert.Equal("0", Assert.Single(Plan(new Cs2ProfilePreferences { VSync = false }).CfgOptions.Where(o => o.Id == "mat_vsync")).Value);

        // Null = no se toca: ninguna línea de mat_vsync.
        Assert.DoesNotContain(Plan(new Cs2ProfilePreferences { VSync = null }).CfgOptions, o => o.Id == "mat_vsync");
    }

    [Fact]
    public void SleepWhenUnfocused_IsInverted()
    {
        // engine_no_focus_sleep = 0 significa "no dormir" (ver la descripción del catálogo), así que pedir
        // "dormir fuera de foco" => 1, y "no dormir" => 0.
        Assert.Equal("0", Assert.Single(Plan(new Cs2ProfilePreferences { SleepWhenUnfocused = false }).CfgOptions.Where(o => o.Id == "engine_no_focus_sleep")).Value);
        Assert.Equal("1", Assert.Single(Plan(new Cs2ProfilePreferences { SleepWhenUnfocused = true }).CfgOptions.Where(o => o.Id == "engine_no_focus_sleep")).Value);
    }

    /// <summary>
    /// Catálogo de prueba con UNA opción <c>Unverified</c>: el catálogo real no tiene ninguna, así que la
    /// regla de "las Unverified vienen destildadas" se prueba aquí y no contra el recurso embebido.
    /// </summary>
    private const string CatalogWithUnverified = """
    { "schemaVersion": 1, "options": [
      { "id": "dudosa", "kind": "Cfg", "text": "dudosa", "valueType": "Bool",
        "description": { "esMx": "a", "enUs": "b" }, "expectedEffect": { "esMx": "a", "enUs": "b" },
        "evidence": "Unverified", "source": "sin fuente", "conflictsWith": [] }
    ] }
    """;

    [Fact]
    public void Unverified_IsOmittedByDefault_AndSaysSo()
    {
        var catalog = Cs2OptionCatalog.Parse(CatalogWithUnverified);
        var preferences = new Cs2ProfilePreferences { Options = [new SelectedOption { Id = "dudosa", Value = "1" }] };

        // Por defecto (IncludeUnverified = false) NO se emite, y se dice por qué. No se omite en silencio.
        Assert.False(preferences.IncludeUnverified);
        var omitted = Cs2ProfilePlanner.Plan(preferences, catalog);
        Assert.Empty(omitted.CfgOptions);
        Assert.Contains(omitted.Warnings, w => w.Contains("no tiene verificación", StringComparison.Ordinal));
        Assert.Contains(omitted.Warnings, w => w.Contains("include-unverified", StringComparison.Ordinal));

        // Con permiso explícito sí se emite, y el valor se respeta tal cual.
        var included = Cs2ProfilePlanner.Plan(preferences with { IncludeUnverified = true }, catalog);
        Assert.Equal("1", Assert.Single(included.CfgOptions).Value);
        Assert.Empty(included.Warnings);
    }

    [Fact]
    public void UnknownIds_AreReported_NotSilentlyDropped()
    {
        var plan = Plan(new Cs2ProfilePreferences { Options = [new SelectedOption { Id = "no-existe", Value = "1" }] });

        Assert.Empty(plan.CfgOptions);
        Assert.Contains(plan.Warnings, w => w.Contains("no está en el catálogo", StringComparison.Ordinal));
    }

    [Fact]
    public void OutputOrder_FollowsTheCatalog_NotTheInputOrder()
    {
        // Se tildan dos opciones en orden INVERSO al del catálogo: la salida debe salir ordenada por catálogo.
        // En cs2-options.json engine_no_focus_sleep es el índice 2 y mat_vsync el índice 4, así que el orden
        // esperado es engine_no_focus_sleep primero, aunque se hayan pedido al revés.
        var plan = Plan(new Cs2ProfilePreferences
        {
            Options = [new SelectedOption { Id = "mat_vsync", Value = "1" }, new SelectedOption { Id = "engine_no_focus_sleep", Value = "0" }]
        });

        Assert.Equal(new[] { "engine_no_focus_sleep", "mat_vsync" }, plan.CfgOptions.Select(o => o.Id));
    }

    [Fact]
    public void LaunchOptions_GoSeparate_AndCarryNoValue()
    {
        var plan = Plan(new Cs2ProfilePreferences
        {
            Options = [new SelectedOption { Id = "-novid" }, new SelectedOption { Id = "-nojoy" }]
        });

        Assert.Empty(plan.CfgOptions);
        Assert.Equal(new[] { "-novid", "-nojoy" }, plan.LaunchOptions.Select(o => o.Text));
        Assert.All(plan.LaunchOptions, o => Assert.Null(o.Value));
    }

    [Fact]
    public void Objective_DoesNotBendTheNumbers()
    {
        // El objetivo cambia el punto de partida de la UI, no la aritmética: con el mismo tope fijo, los tres
        // objetivos producen el mismo valor. Si algún día se sesga, este test lo delata.
        foreach (var objective in new[] { Cs2ProfileObjective.LatenciaMinima, Cs2ProfileObjective.FrametimeEstable, Cs2ProfileObjective.Equilibrado })
        {
            var plan = Plan(new Cs2ProfilePreferences { Objective = objective, FpsCap = FpsCap.Fixed(120) });
            Assert.Equal("120", Assert.Single(plan.CfgOptions.Where(o => o.Id == "fps_max")).Value);
        }
    }
}
