using FragForge.Actions.Platform;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Hotfix Capa 0/1 ronda 3 — propiedades reales de <c>MSFT_NetAdapter</c>.
///
/// Síntoma en la PC objetivo: el aviso "MSFT_NetAdapter: Consulta no válida" salía 3 veces y el
/// panel se quedaba sin adaptadores. Causa MEDIDA (11 consultas individuales): el código pedía
/// <c>DriverVersion</c>, <c>MediaType</c> y <c>LinkSpeed</c>, que NO existen en la clase. Cada una
/// tumbaba la consulta entera y producía su propio aviso: de ahí el 3.
///
/// La lista de abajo es la verdad MEDIDA en la clase real (root\StandardCimv2:MSFT_NetAdapter, 2
/// instancias: I225-V y AX210). No es un fixture inventado.
/// </summary>
public sealed class NetAdapterPropertiesTests
{
    /// <summary>
    /// Lista COMPLETA de propiedades reales de <c>root\StandardCimv2:MSFT_NetAdapter</c>, tal como se
    /// midió en la PC objetivo. No se debe editar para que un test pase: si una propiedad no está
    /// aquí, pedirla rompe la consulta en ese equipo.
    /// </summary>
    public static readonly string[] RealMsftNetAdapterProperties =
    [
        "ActiveMaximumTransmissionUnit", "AdditionalAvailability", "AdminLocked", "AutoSense", "Availability",
        "AvailableRequestedStates", "Caption", "CommunicationStatus", "ComponentID", "ConnectorPresent",
        "CreationClassName", "Description", "DetailedStatus", "DeviceID", "DeviceName", "DeviceWakeUpEnable",
        "DriverDate", "DriverDateData", "DriverDescription", "DriverMajorNdisVersion", "DriverMinorNdisVersion",
        "DriverName", "DriverProvider", "DriverVersionString", "ElementName", "EnabledDefault", "EnabledState",
        "EndPointInterface", "ErrorCleared", "ErrorDescription", "FullDuplex", "HardwareInterface", "HealthState",
        "Hidden", "HigherLayerInterfaceIndices", "IdentifyingDescriptions", "IMFilter", "InstallDate",
        "InstanceID", "InterfaceAdminStatus", "InterfaceDescription", "InterfaceGuid", "InterfaceIndex",
        "InterfaceName", "InterfaceOperationalStatus", "InterfaceType", "iSCSIInterface", "LastErrorCode",
        "LinkTechnology", "LowerLayerInterfaceIndices", "MajorDriverVersion", "MaxQuiesceTime", "MaxSpeed",
        "MediaConnectState", "MediaDuplexState", "MinorDriverVersion", "MtuSize", "Name", "NdisMedium",
        "NdisPhysicalMedium", "NetLuid", "NetLuidIndex", "NetworkAddresses", "NotUserRemovable",
        "OperatingStatus", "OperationalStatus", "OperationalStatusDownDefaultPortNotAuthenticated",
        "OperationalStatusDownInterfacePaused", "OperationalStatusDownLowPowerState",
        "OperationalStatusDownMediaDisconnected", "OtherEnabledState", "OtherIdentifyingInfo",
        "OtherLinkTechnology", "OtherNetworkPortType", "OtherPortType", "PermanentAddress", "PnPDeviceID",
        "PortNumber", "PortType", "PowerManagementCapabilities", "PowerManagementSupported", "PowerOnHours",
        "PrimaryStatus", "PromiscuousMode", "ReceiveLinkSpeed", "RequestedSpeed", "RequestedState", "Speed",
        "State", "Status", "StatusDescriptions", "StatusInfo", "SupportedMaximumTransmissionUnit",
        "SystemCreationClassName", "SystemName", "TimeOfLastStateChange", "TotalPowerOnHours",
        "TransitioningToState", "TransmitLinkSpeed", "UsageRestriction", "Virtual", "VlanID", "WdmInterface"
    ];

    [Fact]
    public void EveryPropertyTheCodeRequests_ExistsInTheRealClass()
    {
        // RED-FIRST: con DriverVersion / MediaType / LinkSpeed esto falla y nombra las tres.
        var real = new HashSet<string>(RealMsftNetAdapterProperties, StringComparer.OrdinalIgnoreCase);
        var requested = WindowsHardwareProfiler.NetAdapterProperties;

        Assert.True(requested.Length > 0, "la lista de propiedades solicitadas está vacía");

        var inexistentes = requested.Where(p => !real.Contains(p)).ToArray();
        Assert.True(
            inexistentes.Length == 0,
            $"se piden propiedades que NO existen en MSFT_NetAdapter: [{string.Join(", ", inexistentes)}]. " +
            $"Pedidas: [{string.Join(", ", requested)}]");
    }

    [Theory]
    [InlineData("DriverVersion")]
    [InlineData("MediaType")]
    [InlineData("LinkSpeed")]
    public void TheRetiredProperties_DoNotExistInTheRealClass_SoTheyMustNotBeRequested(string invalid)
    {
        // Las tres que causaban "Consulta no válida". Se fija el hecho medido y que el código ya no
        // las usa: si alguien las vuelve a meter, el test de arriba también falla.
        Assert.DoesNotContain(invalid, RealMsftNetAdapterProperties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(invalid, WindowsHardwareProfiler.NetAdapterProperties, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScopeAndClass_AreTheMeasuredOnes()
    {
        Assert.Equal(@"root\StandardCimv2", WindowsHardwareProfiler.NetAdapterScope, ignoreCase: true);
        Assert.Equal("MSFT_NetAdapter", WindowsHardwareProfiler.NetAdapterClass, ignoreCase: true);
    }

    // --- Mapeo de tipo de medio: solo valores documentados, el resto Unknown (R-L) ---------------

    [Theory]
    [InlineData(14, NetworkKind.Ethernet)] // NdisPhysicalMedium802_3
    [InlineData(9, NetworkKind.WiFi)]      // NdisPhysicalMediumNative802_11
    public void MapPhysicalMedium_MapsOnlyTheDocumentedValues(int value, NetworkKind expected)
    {
        Assert.Equal(expected, WindowsHardwareProfiler.MapPhysicalMedium(value));
    }

    [Theory]
    [InlineData(0)]   // Unspecified
    [InlineData(1)]   // WirelessLan (802.11 legacy)
    [InlineData(2)]   // Ethernet (802.3 legacy) — el mapeo VIEJO trataba 1=Ethernet y 2=WiFi
    [InlineData(15)]  // NdisPhysicalMediumOther
    [InlineData(99)]
    [InlineData(null)]
    public void MapPhysicalMedium_ReturnsUnknownForEverythingElse(int? value)
    {
        // El mapeo anterior era `1 => Ethernet, 2 => WiFi`, que no corresponde a NdisPhysicalMedium.
        // Y su rama por defecto adivinaba Ethernet: convertir "no sé" en "Ethernet" es lo que R-L
        // prohíbe.
        var kind = WindowsHardwareProfiler.MapPhysicalMedium(value);

        Assert.True(
            kind == NetworkKind.Unknown,
            $"NdisPhysicalMedium={value?.ToString() ?? "null"} dio {kind}; con un valor no documentado debe ser Unknown");
    }

    // --- (d) el aviso nombra la consulta, no solo "Consulta no válida" ---------------------------

    [Fact]
    public void QueryFailureWarning_NamesScopeClassAndProperties()
    {
        var text = WindowsHardwareProfiler.DescribeQueryFailure(
            WindowsHardwareProfiler.NetAdapterScope,
            WindowsHardwareProfiler.NetAdapterClass,
            ["Name", "DriverVersion"],
            new InvalidOperationException("Consulta no válida"));

        Assert.Contains("root\\StandardCimv2", text, StringComparison.Ordinal);
        Assert.Contains("MSFT_NetAdapter", text, StringComparison.Ordinal);
        Assert.Contains("DriverVersion", text, StringComparison.Ordinal);
        Assert.Contains("Consulta no válida", text, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryFailureWarning_FallsBackToStar_WhenNoPropertiesWereAsked()
    {
        var text = WindowsHardwareProfiler.DescribeQueryFailure("root\\cimv2", "Win32_Tpm", [], new InvalidOperationException("boom"));

        Assert.Contains("(*)", text, StringComparison.Ordinal);
        Assert.Contains("boom", text, StringComparison.Ordinal);
    }
}
