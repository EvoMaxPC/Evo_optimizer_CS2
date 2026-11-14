using FragForge.Actions.Power;
using Xunit;

namespace FragForge.Tests.PowerCfgRealShape;

/// <summary>
/// Salida REAL de powercfg /qh en la PN64 (es-MX, Windows 11 build 26200). Los textos con tilde van con
/// escapes \u para que el archivo sea ASCII. Fija la jerarquia real: plan (sangria 0), subgrupo (2), ajuste (4).
/// </summary>
public sealed class PowerCfgQueryParserRealShapeTests
{
    private const string Plan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Processor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string PciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string CoreParkingMin1 = "0cc5b647-c1df-4637-891a-dec35c318584";
    private const string Aspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    private static readonly string RealQh = string.Join("\n",
        "GUID de plan de energ\u00eda: " + Plan + "  (Alto rendimiento)",
        "  Alias de GUID: SCHEME_MIN",
        "  GUID de subgrupo: " + Processor + "  (Administraci\u00f3n de energ\u00eda del procesador)",
        "    Alias de GUID: SUB_PROCESSOR",
        "    GUID de configuraci\u00f3n de energ\u00eda: " + CoreParkingMin1 + "  (N\u00facleos m\u00ednimos de detenci\u00f3n del n\u00facleo, clase 1)",
        "      Alias de GUID: CPMINCORES1",
        "      M\u00ednima configuraci\u00f3n posible: 0x00000000",
        "      M\u00e1xima configuraci\u00f3n posible: 0x00000064",
        "      Incremento de configuraci\u00f3n posible: 0x00000001",
        "      Unidades de configuraci\u00f3n posibles: %",
        "    \u00cdndice de configuraci\u00f3n de corriente alterna actual: 0x00000000",
        "    \u00cdndice de configuraci\u00f3n de corriente continua actual: 0x00000000",
        "",
        "  GUID de subgrupo: " + PciExpress + "  (PCI Express)",
        "    Alias de GUID: SUB_PCIEXPRESS",
        "    GUID de configuraci\u00f3n de energ\u00eda: " + Aspm + "  (Administraci\u00f3n de energ\u00eda del estado de v\u00ednculos)",
        "      Alias de GUID: ASPM",
        "      \u00cdndice de configuraci\u00f3n posible: 000",
        "      Nombre descriptivo de configuraci\u00f3n posible: Desactivar",
        "      \u00cdndice de configuraci\u00f3n posible: 001",
        "      Nombre descriptivo de configuraci\u00f3n posible: Ahorro moderado de energ\u00eda",
        "      \u00cdndice de configuraci\u00f3n posible: 002",
        "      Nombre descriptivo de configuraci\u00f3n posible: M\u00e1ximo ahorro de energ\u00eda",
        "    \u00cdndice de configuraci\u00f3n de corriente alterna actual: 0x00000000",
        "    \u00cdndice de configuraci\u00f3n de corriente continua actual: 0x00000000",
        "");

    [Fact]
    public void Qh_ParsesTheRangeSetting_WithItsRealSubgroup()
    {
        var q = PowerCfgQueryParser.Find(RealQh, Processor, CoreParkingMin1);
        Assert.NotNull(q);
        Assert.Equal(0, q!.AcIndex);
        Assert.Equal(0, q.DcIndex);
        Assert.Equal(0, q.Minimum);
        Assert.Equal(100, q.Maximum);
        Assert.Equal(1, q.Increment);
        Assert.Empty(q.PossibleIndices);
    }

    [Fact]
    public void Qh_ParsesTheEnumeratedSetting_AndAttributesItToThePciSubgroup()
    {
        var q = PowerCfgQueryParser.Find(RealQh, PciExpress, Aspm);
        Assert.NotNull(q);
        Assert.Equal(new[] { 0, 1, 2 }, q!.PossibleIndices);
        Assert.Equal(0, q.AcIndex);
        Assert.Equal(0, q.DcIndex);
        Assert.Null(q.Minimum);
    }

    [Fact]
    public void Qh_ReturnsExactlyTheTwoSettings_NeverPlanOrSubgroupBlocks()
    {
        var all = PowerCfgQueryParser.Parse(RealQh);
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, s => s.SettingGuid == Processor || s.SettingGuid == PciExpress || s.SettingGuid == Plan);
    }

    [Fact]
    public void Q_WithHiddenSettings_ReturnsOnlyThePlanHeader_AndParsesToNothing()
    {
        // En la PN64, /q de PERFEPP y PERFBOOSTMODE devuelve SOLO la cabecera del plan; por eso se lee con /qh.
        var onlyHeader = string.Join("\n",
            "GUID de plan de energ\u00eda: " + Plan + "  (Alto rendimiento)",
            "  Alias de GUID: SCHEME_MIN",
            "");
        Assert.Empty(PowerCfgQueryParser.Parse(onlyHeader));
    }
}