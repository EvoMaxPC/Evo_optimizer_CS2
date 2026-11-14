using FragForge.Actions.Power;
using Xunit;

namespace FragForge.Tests.Actions;

/// <summary>
/// Parser de <c>powercfg /qh</c> bajo R-C: ni una sola etiqueta localizada. Se prueba con salida
/// REAL en es-MX de los dos tipos de bloque (rango y enumerado), con nombres mezclados español/inglés
/// y con líneas cortadas a mitad de descripción, que es como sale en un equipo real.
/// </summary>
public sealed class PowerCfgQueryParserTests
{
    private const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string PcieSubgroup = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string PerfEppSetting = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string AspmSetting = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    /// <summary>Salida real es-MX: bloque de rango (PERFEPP) y bloque enumerado (ASPM).</summary>
    private const string RealEsMxOutput = """
        Configuración de energía actual

        GUID de configuración de energía: 54533251-82be-4824-96c1-47b60b740d00  (Procesador)
          GUID de alias de configuración de energía: 54533251-82be-4824-96c1-47b60b740d00  (Procesador)
            GUID de configuración de energía: 36687f9e-e3a5-4dbf-b1dc-15eb381c6863  (rendimiento mínimo del procesador)
              Límites de configuración de energía mínimos posibles: 0x00000000
              Límites de configuración de energía máximos posibles: 0x00000064
              Incremento de configuración de energía posible: 0x00000001
              Índice de configuración de energía actual de CA: 0x00000005
              Índice de configuración de energía actual de CC: 0x00000000

        GUID de configuración de energía: 501a4d13-42af-4429-9fd1-a8218c268e20  (PCI Express)
          GUID de alias de configuración de energía: 501a4d13-42af-4429-9fd1-a8218c268e20  (PCI Express)
            GUID de configuración de energía: ee12f906-d277-404b-b6da-e5fa1a576df5  (vínculo de estado de energía de
              PCI Express). Este ajuste controla el estado de energía del enlace
              Valores de configuración de energía posibles:
                000
                001
                002
              Índice de configuración de energía actual de CA: 0x00000000
              Índice de configuración de energía actual de CC: 0x00000000
        """;

    [Fact]
    public void RangeBlock_ParsesMinimumMaximumIncrementAndBothIndices()
    {
        var setting = PowerCfgQueryParser.Find(RealEsMxOutput, ProcessorSubgroup, PerfEppSetting);
        Assert.NotNull(setting);
        Assert.Equal(5, setting!.AcIndex);
        Assert.Equal(0, setting.DcIndex);
        Assert.Equal(0, setting.Minimum);
        Assert.Equal(100, setting.Maximum);
        Assert.Equal(1, setting.Increment);
        Assert.True(setting.IsRange);
        Assert.False(setting.IsEnumerated);
    }

    [Fact]
    public void RangeBlock_AllowsOnlyValuesInsideTheEnumeratedRange()
    {
        var setting = PowerCfgQueryParser.Find(RealEsMxOutput, ProcessorSubgroup, PerfEppSetting)!;
        Assert.True(setting.Allows(0));
        Assert.True(setting.Allows(5));
        Assert.True(setting.Allows(100));
        Assert.False(setting.Allows(101));   // fuera de máximo
        Assert.False(setting.Allows(-1));    // negativo
    }

    [Fact]
    public void EnumeratedBlock_ParsesPossibleIndices_AndRejectsAnythingOutsideTheList()
    {
        var setting = PowerCfgQueryParser.Find(RealEsMxOutput, PcieSubgroup, AspmSetting);
        Assert.NotNull(setting);
        Assert.Equal(0, setting!.AcIndex);
        Assert.Equal(0, setting.DcIndex);
        Assert.False(setting.IsRange);
        Assert.True(setting.IsEnumerated);
        Assert.Equal(new[] { 0, 1, 2 }, setting.PossibleIndices);

        Assert.True(setting.Allows(0));
        Assert.True(setting.Allows(1));
        Assert.True(setting.Allows(2));
        Assert.False(setting.Allows(3));
        Assert.False(setting.Allows(100));
    }

    [Fact]
    public void Parser_DoesNotDependOnLocalizedLabels()
    {
        // La MISMA estructura con etiquetas en inglés debe dar exactamente el mismo resultado.
        const string English = """
            Current power scheme

            Power Scheme GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor)
              Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor)
                Power Setting GUID: 36687f9e-e3a5-4dbf-b1dc-15eb381c6863  (Processor performance boost mode)
                  Minimum Possible Setting: 0x00000000
                  Maximum Possible Setting: 0x00000064
                  Possible Setting Increment: 0x00000001
                  Current AC Power Setting Index: 0x00000005
                  Current DC Power Setting Index: 0x00000000
            """;
        var spanish = PowerCfgQueryParser.Find(RealEsMxOutput, ProcessorSubgroup, PerfEppSetting)!;
        var english = PowerCfgQueryParser.Find(English, ProcessorSubgroup, PerfEppSetting)!;

        Assert.Equal(spanish.AcIndex, english.AcIndex);
        Assert.Equal(spanish.DcIndex, english.DcIndex);
        Assert.Equal(spanish.Minimum, english.Minimum);
        Assert.Equal(spanish.Maximum, english.Maximum);
        Assert.Equal(spanish.Increment, english.Increment);
    }

    // ---------- Fixtures reales de la PN64 (salida de /qh, es-MX) ----------

    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string CoreParking1 = "0cc5b647-c1df-4637-891a-dec35c318584";

    /// <summary>
    /// La PN64: USB (enumerado, SIN línea de alias) y CPMINCORES1 (rango), en ese orden. El bloque
    /// USB no tiene alias, lo que prueba que el bloque se identifica por GUID y no por etiqueta.
    /// </summary>
    private const string Pn64Output = """
        Configuración de energía actual

        GUID de configuración de energía: 2a737441-1930-4402-8d77-b2bebba308a3  (Configuración USB)
            GUID de configuración de energía: 48e6b7a6-50f5-4782-a5d4-53bb8f07e226  (suspensión selectiva USB)
              Valores de configuración de energía posibles:
                000
                001
              Índice de configuración de energía actual de CA: 0x00000001
              Índice de configuración de energía actual de CC: 0x00000001

        GUID de configuración de energía: 54533251-82be-4824-96c1-47b60b740d00  (Procesador)
            GUID de configuración de energía: 0cc5b647-c1df-4637-891a-dec35c318584  (aparcamiento de núcleos del procesador, mínima clase 1)
              Límites de configuración de energía mínimos posibles: 0x00000000
              Límites de configuración de energía máximos posibles: 0x00000064
              Incremento de configuración de energía posible: 0x00000001
              Índice de configuración de energía actual de CA: 0x00000000
              Índice de configuración de energía actual de CC: 0x00000000
        """;

    [Fact]
    public void UsbSetting_ParsesWithoutAnAliasLine()
    {
        // El bloque USB no trae "Alias de GUID": se identifica por su GUID y su subgrupo.
        var usb = PowerCfgQueryParser.Find(Pn64Output, UsbSubgroup, UsbSetting);
        Assert.NotNull(usb);
        Assert.Equal(1, usb!.AcIndex);
        Assert.Equal(1, usb.DcIndex);
        Assert.Equal(new[] { 0, 1 }, usb.PossibleIndices);
        Assert.False(usb.IsRange);
        Assert.True(usb.IsEnumerated);
        Assert.True(usb.Allows(0));
        Assert.False(usb.Allows(2)); // el 2 no está enumerado
    }

    [Fact]
    public void CoreParkingVariant1_ParsesAsARange()
    {
        var parking = PowerCfgQueryParser.Find(Pn64Output, ProcessorSubgroup, CoreParking1);
        Assert.NotNull(parking);
        Assert.Equal(0, parking!.AcIndex);
        Assert.Equal(0, parking.DcIndex);
        Assert.Equal(0, parking.Minimum);
        Assert.Equal(100, parking.Maximum);
        Assert.Equal(1, parking.Increment);
        Assert.True(parking.Allows(100));
    }

    [Fact]
    public void TwoBlocksOfTheSameSubgroup_AreDistinguishedByTheirSettingGuid()
    {
        // El subgrupo se repite; lo que distingue es el GUID del setting. Un parser que se guiara por
        // "la primera aparición del subgrupo" devolvería el bloque equivocado.
        var all = PowerCfgQueryParser.Parse(Pn64Output);
        Assert.Equal(2, all.Count);
        Assert.Equal(UsbSetting, all[0].SettingGuid);
        Assert.Equal(CoreParking1, all[1].SettingGuid);
        Assert.Equal(UsbSubgroup, all[0].SubgroupGuid);
        Assert.Equal(ProcessorSubgroup, all[1].SubgroupGuid);
    }

    [Fact]
    public void MissingSetting_ReturnsNull_SoTheCallerCanReportNotApplicable()
    {
        // R-L: si el sistema no enumera el setting, no se asume nada: el llamador devuelve NotApplicable.
        Assert.Null(PowerCfgQueryParser.Find(RealEsMxOutput, ProcessorSubgroup, "be337238-0d82-4146-a960-4f3749d470c7"));
        Assert.Null(PowerCfgQueryParser.Find("", ProcessorSubgroup, PerfEppSetting));
    }

    [Fact]
    public void BlockWithoutTwoIndices_IsIgnored_InsteadOfGuessing()
    {
        const string WithoutIndices = """
            GUID de configuración de energía: 54533251-82be-4824-96c1-47b60b740d00  (Procesador)
              GUID de configuración de energía: 36687f9e-e3a5-4dbf-b1dc-15eb381c6863  (sin índices)
                Este equipo no expone índices para este ajuste.
            """;
        Assert.Empty(PowerCfgQueryParser.Parse(WithoutIndices));
    }

    [Fact]
    public void CutLinesAtTheEndOfADescription_DoNotLeakIntoTheNextSetting()
    {
        // El texto cortado del bloque ASPM no debe contaminar el bloque siguiente ni el anterior.
        const string Two = RealEsMxOutput + """

        GUID de configuración de energía: 2a737441-1930-4402-8d77-b2bebba308a3  (Configuración USB)
          GUID de configuración de energía: 48e6b7a6-50f5-4782-a5d4-53bb8f07e226  (suspensión selectiva USB)
            Valores de configuración de energía posibles:
              000
              001
            Índice de configuración de energía actual de CA: 0x00000001
            Índice de configuración de energía actual de CC: 0x00000001
        """;
        var all = PowerCfgQueryParser.Parse(Two);
        Assert.Equal(3, all.Count);

        var usb = PowerCfgQueryParser.Find(Two, "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226")!;
        Assert.Equal(1, usb.AcIndex);
        Assert.Equal(1, usb.DcIndex);
        Assert.Equal(new[] { 0, 1 }, usb.PossibleIndices);
    }
}
