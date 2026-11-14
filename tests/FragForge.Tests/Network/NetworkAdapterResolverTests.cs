using System.Runtime.Versioning;
using FragForge.Actions.Network;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Network;

/// <summary>
/// Capa 5.1 — resolución por identidad de hardware (R-I).
///
/// Lo que estas pruebas impiden es una regresión concreta: que alguien vuelva a identificar un adaptador por su
/// nombre visible. Si el resolutor empieza a mirar <c>Name</c> o <c>Description</c>, aquí se ve.
///
/// Los GUID son los medidos en la PC objetivo: I225-V = {D72A2249-535C-4478-9FDD-9B3A439F6395} y
/// AX210 = {0F4DC9CC-3733-4C8F-96FD-92AAB123BD6F}.
///
/// El resolutor está marcado <c>[SupportedOSPlatform("windows")]</c>, así que el test hereda la marca (R-N).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkAdapterResolverTests
{
    private const string I225Guid = "{D72A2249-535C-4478-9FDD-9B3A439F6395}";
    private const string Ax210Guid = "{0F4DC9CC-3733-4C8F-96FD-92AAB123BD6F}";
    private const string I225HardwareId = @"PCI\VEN_8086&DEV_15F3&SUBSYS_87D21043&REV_01";
    private const string Ax210HardwareId = @"PCI\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A";

    [Fact]
    public async Task List_ReturnsBothAdaptersWithTheirInterfaceGuid()
    {
        var resolver = Create(out _);

        var adapters = await resolver.ListAsync();

        Assert.Equal(2, adapters.Count);
        var eth = Assert.Single(adapters, a => a.HardwareId!.Contains("DEV_15F3", StringComparison.Ordinal));
        Assert.Equal(I225Guid.ToLowerInvariant(), eth.InterfaceGuid);
        Assert.Equal(NetworkKind.Ethernet, eth.Kind);
        Assert.Equal(14, eth.NdisPhysicalMedium);
        Assert.True(eth.IsPhysical);
    }

    [Fact]
    public async Task Resolve_ByInterfaceGuid_FindsExactlyOne()
    {
        var resolver = Create(out _);

        var adapter = await resolver.ResolveAsync(new NetworkAdapterSelector { InterfaceGuid = I225Guid });

        Assert.NotNull(adapter);
        Assert.Equal(I225Guid.ToLowerInvariant(), adapter!.InterfaceGuid);
        Assert.Contains("I225-V", adapter.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ByHardwareId_IgnoresNameAndDescription()
    {
        var resolver = Create(out _);

        // "DEV_15F3" es el I225-V. Ni el nombre ni la descripción intervienen.
        var adapter = await resolver.ResolveAsync(new NetworkAdapterSelector { HardwareIdContains = "DEV_15F3" });

        Assert.NotNull(adapter);
        Assert.Equal(I225Guid.ToLowerInvariant(), adapter!.InterfaceGuid);
    }

    /// <summary>
    /// El caso que importa: si se renombra la conexión y el driver cambia el texto, la resolución por hardware
    /// sigue funcionando. Con la ruta vieja (Description.Contains) esto habría fallado.
    /// </summary>
    [Fact]
    public async Task Resolve_StillWorksWhenNameAndDescriptionAreRenamed()
    {
        var resolver = Create(out _, renameEverything: true);

        var adapter = await resolver.ResolveAsync(new NetworkAdapterSelector { InterfaceGuid = I225Guid });

        Assert.NotNull(adapter);
        Assert.Equal("Conexión local 7", adapter!.Name);
        Assert.Equal("Controlador de red genérico", adapter.Description);
    }

    /// <summary>Un selector sin identidad de hardware NO resuelve: es la garantía de R-I.</summary>
    [Fact]
    public async Task Resolve_WithoutHardwareIdentity_ReturnsNull()
    {
        var resolver = Create(out _);

        var adapter = await resolver.ResolveAsync(new NetworkAdapterSelector());

        Assert.Null(adapter);
    }

    [Fact]
    public void Selector_KnowsWhetherItHasHardwareIdentity()
    {
        Assert.False(new NetworkAdapterSelector().HasHardwareIdentity);
        Assert.False(new NetworkAdapterSelector { InterfaceGuid = "   " }.HasHardwareIdentity);
        Assert.True(new NetworkAdapterSelector { InterfaceGuid = I225Guid }.HasHardwareIdentity);
        Assert.True(new NetworkAdapterSelector { HardwareIdContains = "DEV_15F3" }.HasHardwareIdentity);
    }

    /// <summary>Si el hardware ID no coincide con nada, no se devuelve "el primero": null.</summary>
    [Fact]
    public async Task Resolve_WithUnknownHardwareId_ReturnsNull()
    {
        var resolver = Create(out _);

        Assert.Null(await resolver.ResolveAsync(new NetworkAdapterSelector { HardwareIdContains = "DEV_FFFF" }));
    }

    /// <summary>Un adaptador sin InterfaceGuid no entra: sin identidad no hay ruta de apply (R-I).</summary>
    [Fact]
    public async Task List_SkipsAdaptersWithoutInterfaceGuid()
    {
        var wmi = new RowWmi();
        // Una fila sin InterfaceGuid: no debe aparecer.
        wmi.Rows.Add(Row((("InterfaceGuid", ""), ("Name", "Sin GUID"), ("InterfaceDescription", "x"))));
        var resolver = new WindowsNetworkAdapterResolver(wmi, new FakeRegistry());

        Assert.Empty(await resolver.ListAsync());
    }

    [Fact]
    public async Task List_WhenWmiFails_ReturnsEmptyInsteadOfInventingAdapters()
    {
        var wmi = new RowWmi { Throw = true };
        var resolver = new WindowsNetworkAdapterResolver(wmi, new FakeRegistry());

        Assert.Empty(await resolver.ListAsync());
    }

    [Theory]
    [InlineData("{D72A2249-535C-4478-9FDD-9B3A439F6395}", "{d72a2249-535c-4478-9fdd-9b3a439f6395}")]
    [InlineData("d72a2249-535c-4478-9fdd-9b3a439f6395", "{d72a2249-535c-4478-9fdd-9b3a439f6395}")]
    [InlineData("  {D72A2249-535C-4478-9FDD-9B3A439F6395}  ", "{d72a2249-535c-4478-9fdd-9b3a439f6395}")]
    public void NormalizeGuid_AcceptsAllCommonForms(string input, string expected)
    {
        Assert.Equal(expected, WindowsNetworkAdapterResolver.NormalizeGuid(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-es-un-guid")]
    [InlineData("Ethernet")]
    public void NormalizeGuid_RejectsAnythingThatIsNotAGuid(string input)
    {
        // Un nombre visible NO es un GUID: si se colara aquí, R-I estaría roto.
        Assert.Null(WindowsNetworkAdapterResolver.NormalizeGuid(input));
    }

    /// <summary>
    /// El NetCfgInstanceId sale del registro de la clase de red y coincide con el InterfaceGuid. Es la misma
    /// identidad por dos vías.
    /// </summary>
    [Fact]
    public async Task List_ReadsNetCfgInstanceIdFromTheNetworkClassRegistry()
    {
        var registry = new FakeRegistry();
        await registry.WriteValueAsync(WindowsNetworkAdapterResolver.NetworkClassKey + @"\0001", "NetCfgInstanceId", new RegistryWriteValue("String", I225Guid));
        var wmi = new RowWmi { Rows = { I225Row(), Ax210Row() } };

        var adapters = await new WindowsNetworkAdapterResolver(wmi, registry).ListAsync();

        var eth = Assert.Single(adapters, a => a.HardwareId!.Contains("DEV_15F3", StringComparison.Ordinal));
        Assert.Equal(I225Guid, eth.NetCfgInstanceId, ignoreCase: true);
    }

    private static WindowsNetworkAdapterResolver Create(out FakeRegistry registry, bool renameEverything = false)
    {
        registry = new FakeRegistry();
        var wmi = new RowWmi();
        wmi.Rows.Add(renameEverything
            ? Row(("InterfaceGuid", I225Guid), ("Name", "Conexión local 7"), ("InterfaceDescription", "Controlador de red genérico"),
                  ("PnPDeviceID", I225HardwareId), ("NdisPhysicalMedium", 14), ("HardwareInterface", true), ("Virtual", false),
                  ("Speed", 1_000_000_000UL), ("State", "2"))
            : I225Row());
        wmi.Rows.Add(Ax210Row());
        return new WindowsNetworkAdapterResolver(wmi, registry);
    }

    private static IReadOnlyDictionary<string, object?> I225Row() =>
        Row(("InterfaceGuid", I225Guid), ("Name", "Ethernet"), ("InterfaceDescription", "Intel(R) Ethernet Controller I225-V"),
            ("PnPDeviceID", I225HardwareId), ("NdisPhysicalMedium", 14), ("HardwareInterface", true), ("Virtual", false),
            ("Speed", 2_500_000_000UL), ("State", "2"));

    private static IReadOnlyDictionary<string, object?> Ax210Row() =>
        Row(("InterfaceGuid", Ax210Guid), ("Name", "Wi-Fi"), ("InterfaceDescription", "Intel(R) Wi-Fi 6E AX210 160MHz"),
            ("PnPDeviceID", Ax210HardwareId), ("NdisPhysicalMedium", 9), ("HardwareInterface", true), ("Virtual", false),
            ("Speed", 2_400_000_000UL), ("State", "2"));

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Doble de WMI con filas: <c>FakeWmiQuery</c> siempre devuelve lista vacía.</summary>
    private sealed class RowWmi : IWmiQuery
    {
        public List<IReadOnlyDictionary<string, object?>> Rows { get; } = [];
        public bool Throw { get; init; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
            string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default)
        {
            if (Throw) throw new InvalidOperationException("Invalid namespace");
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Rows);
        }
    }
}
