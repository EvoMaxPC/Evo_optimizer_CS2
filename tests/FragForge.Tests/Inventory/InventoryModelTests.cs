using System.Text.Json;
using FragForge.Actions.Inventory;
using FragForge.Core.Inventory;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Tiers;
using Xunit;

namespace FragForge.Tests.Inventory;

public sealed class StartupApprovalTests
{
    [Theory]
    [InlineData(0x02, true)]
    [InlineData(0x06, true)]
    [InlineData(0x0E, true)]
    [InlineData(0x03, false)]
    [InlineData(0x07, false)]
    [InlineData(0x0F, false)]
    [InlineData(0x7F, null)]
    public void FirstByte_DecidesEnabledState(int firstByte, bool? expected)
    {
        var value = new byte[] { (byte)firstByte, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        Assert.Equal(expected, StartupApproval.IsEnabled(value));
    }

    [Fact]
    public void EmptyValue_IsUnknown_NotEnabled()
    {
        Assert.Null(StartupApproval.IsEnabled(ReadOnlySpan<byte>.Empty));
        Assert.Contains("Sin valor", StartupApproval.Explain(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void UnrecognizedByte_IsExplainedAsUnknown_NotAsEnabled()
    {
        var text = StartupApproval.Explain([0x42, 0x00]);
        Assert.Contains("desconocido", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Habilitado (", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Base64Overload_DecodesRealRegistryData()
    {
        Assert.True(StartupApproval.IsEnabled(Convert.ToBase64String([0x02, 0x00, 0x00])));
        Assert.False(StartupApproval.IsEnabled(Convert.ToBase64String([0x03, 0x00, 0x00])));
        Assert.Null(StartupApproval.IsEnabled("no-es-base64!!!"));
        Assert.Null(StartupApproval.IsEnabled((string?)null));
    }

    [Fact]
    public void EnabledAndDisabledSets_DoNotOverlap()
    {
        Assert.Empty(StartupApproval.EnabledBytes.Intersect(StartupApproval.DisabledBytes));
    }
}

public sealed class InventorySerializationTests
{
    [Fact]
    public void SystemInventory_RoundTripsWithCamelCaseAndStrictEnums()
    {
        var inventory = new SystemInventory
        {
            MachineName = "PN64",
            OsBuild = "26200",
            Services = [new ServiceEntry { Name = "DiagTrack", StartMode = "Auto", State = "Running", Dependents = ["X"] }],
            Tasks = [new TaskEntry { Path = "\\Microsoft\\Windows\\Foo", Enabled = false }],
            Startup = [new StartupEntry { Name = "OneDrive", Location = StartupLocation.RunHkcu, ApprovedEnabled = false, ApprovalFirstByte = 0x03 }],
            Appx = [new AppxEntry { Name = "Microsoft.Maps" }],
            Warnings = ["Servicios: acceso denegado"]
        };
        var json = JsonSerializer.Serialize(inventory, JsonDefaults.Options);
        Assert.Contains("\"startupLocation\": \"RunHkcu\"", json);
        var back = JsonSerializer.Deserialize<SystemInventory>(json, JsonDefaults.Options);
        Assert.NotNull(back);
        Assert.Equal("PN64", back!.MachineName);
        Assert.Equal(StartupLocation.RunHkcu, back.Startup[0].Location);
        Assert.Equal(0x03, back.Startup[0].ApprovalFirstByte);
        Assert.False(back.IsComplete);
    }

    [Fact]
    public void Notes_RoundTrip_AndDoNotMakeTheInventoryIncomplete()
    {
        // Notes es un campo ADITIVO: se persiste con el inventario y NO cuenta como fallo de lectura.
        var inventory = new SystemInventory
        {
            MachineName = "PN64",
            Notes = ["SID del usuario interactivo desconocido: no se leyó el arranque HKCU."],
            Warnings = []
        };
        var json = JsonSerializer.Serialize(inventory, JsonDefaults.Options);
        Assert.Contains("\"notes\"", json);
        var back = JsonSerializer.Deserialize<SystemInventory>(json, JsonDefaults.Options);
        Assert.NotNull(back);
        Assert.Single(back!.Notes);
        Assert.True(back.IsComplete);
    }

    [Fact]
    public void UnknownJsonMember_InInventory_IsRejected()
    {
        var json = """{"machineName":"x","campo":"desconocido"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SystemInventory>(json, JsonDefaults.Options));
    }

    [Fact]
    public void AuditReport_WithoutWarnings_CarriesTheNotesAndStaysComplete()
    {
        // La decisión de `audit` es `inventory.IsComplete`: con solo notas debe ser TRUE (exit 0).
        // Las notas del inventario además viajan al informe para que se vean sin abrir el inventario.
        var inventory = new SystemInventory
        {
            MachineName = "PN64",
            Notes = ["SID del usuario interactivo desconocido: no se leyó el arranque HKCU."]
        };
        var report = AuditReport.From(inventory, ServiceTierCatalog.Empty);
        Assert.True(inventory.IsComplete);
        Assert.Contains(report.Notes, n => n.Contains("SID del usuario interactivo", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyInventory_IsComplete_ButHasNoData()
    {
        var inventory = new SystemInventory { MachineName = "x" };
        Assert.True(inventory.IsComplete);
        Assert.Empty(inventory.Services);
    }
}

public sealed class AuditReportTests
{
    [Fact]
    public void AuditReport_SerializesWithOwnSchemaVersion_AndDoesNotTouchReportDocument()
    {
        var report = new AuditReport
        {
            Inventory = new SystemInventory { MachineName = "PN64" },
            Notes = ["Solo lectura"]
        };
        var json = JsonSerializer.Serialize(report, JsonDefaults.Options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("PN64", document.RootElement.GetProperty("inventory").GetProperty("machineName").GetString());
    }
}

public sealed class TierCatalogTests
{
    [Fact]
    public void EmbeddedTiers_LoadAndValidate()
    {
        var catalog = ServiceTierCatalog.LoadEmbedded();
        Assert.NotEmpty(catalog.Entries);
        Assert.True(catalog.IsApplicable("DiagTrack"));
        Assert.True(catalog.IsApplicable("dmwappushservice"));
    }

    [Fact]
    public void CoreServices_AreNeverApplicable_EvenIfAbsentFromJson()
    {
        var catalog = ServiceTierCatalog.LoadEmbedded();
        foreach (var core in ServiceTierCatalog.CoreNeverTouch)
        {
            Assert.Equal(SystemTier.NeverTouch, catalog.TierOf(core));
            Assert.False(catalog.IsApplicable(core));
        }
    }

    [Fact]
    public void T3AndUnknown_AreNotApplicable()
    {
        var catalog = ServiceTierCatalog.LoadEmbedded();
        Assert.Equal(SystemTier.MeasureOnly, catalog.TierOf("PcaSvc"));
        Assert.False(catalog.IsApplicable("PcaSvc"));
        Assert.False(catalog.IsApplicable("ServicioQueNoExiste"));
    }

    [Fact]
    public void EntryWithoutReasonOrReference_FailsToLoad()
    {
        var json = """{"schemaVersion":1,"services":[{"name":"Foo","tier":"T1","reason":"corto","reference":""}]}""";
        var error = Assert.Throws<InvalidDataException>(() => ServiceTierCatalog.Parse(json));
        Assert.Contains("Foo", error.Message);
    }

    [Fact]
    public void DeclaringACoreServiceWithNonT0Tier_FailsToLoad()
    {
        var json = """{"schemaVersion":1,"services":[{"name":"RpcSs","tier":"T1","reason":"Esto sería un desastre absoluto.","reference":"https://example.invalid"}]}""";
        Assert.Throws<InvalidDataException>(() => ServiceTierCatalog.Parse(json));
    }

    [Fact]
    public void DuplicateEntries_AreRejected()
    {
        var json = """{"schemaVersion":1,"services":[
            {"name":"Foo","tier":"T1","reason":"Motivo suficientemente largo.","reference":"https://a"},
            {"name":"foo","tier":"T2","reason":"Motivo suficientemente largo.","reference":"https://b"}]}""";
        Assert.Throws<InvalidDataException>(() => ServiceTierCatalog.Parse(json));
    }

    [Fact]
    public void InvalidJson_ThrowsInvalidData_NotJsonException()
    {
        Assert.Throws<InvalidDataException>(() => ServiceTierCatalog.Parse("{ no json"));
    }
}

public sealed class ImpactSimulatorTests
{
    private static ServiceTierCatalog Tiers()
    {
        var json = """
        {"schemaVersion":1,"services":[
          {"name":"SafeSvc","tier":"T1","reason":"Servicio seguro de prueba.","reference":"https://a"},
          {"name":"OptSvc","tier":"T2","reason":"Servicio opcional de prueba.","reference":"https://b"},
          {"name":"MeasureSvc","tier":"T3","reason":"Solo medir en la prueba.","reference":"https://c"}]}
        """;
        return ServiceTierCatalog.Parse(json);
    }

    private static TweakDefinition Def(string id, string? service) => new()
    {
        Id = id,
        Category = "Debloat",
        Title = new LocalizedText { EsMx = id, EnUs = id },
        Description = new LocalizedText { EsMx = id, EnUs = id },
        Evidence = EvidenceLevel.Low,
        Risk = RiskLevel.Safe,
        Actions = service is null ? [] : [new ActionSpec
        {
            Kind = "Service",
            Parameters = JsonDocument.Parse($$"""{"name":"{{service}}","startMode":"Disabled"}""").RootElement.Clone()
        }],
        ExpectedImpact = new ExpectedImpact(),
        References = ["https://example.invalid"]
    };

    private static SystemInventory Inventory(params (string Name, string[] Dependents)[] services) => new()
    {
        MachineName = "test",
        Services = services.Select(s => new ServiceEntry { Name = s.Name, Dependents = s.Dependents }).ToArray()
    };

    [Fact]
    public void SafeServiceWithoutDependents_IsNotBlocked()
    {
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", "SafeSvc")], Inventory(("SafeSvc", [])));
        Assert.True(report.Safe);
    }

    [Fact]
    public void UnknownService_IsBlocked_BecauseItIsT0ByDefault()
    {
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", "ServicioInventado")], Inventory(("ServicioInventado", [])));
        Assert.False(report.Safe);
        Assert.Contains("T0", report.Summary);
    }

    [Fact]
    public void MeasureOnlyService_IsBlocked()
    {
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", "MeasureSvc")], Inventory(("MeasureSvc", [])));
        Assert.False(report.Safe);
    }

    [Fact]
    public void CoreDependent_BlocksTheWholeTweak()
    {
        // RpcSs es núcleo: si depende de SafeSvc, tocar SafeSvc es inaceptable.
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", "SafeSvc")], Inventory(("SafeSvc", ["RpcSs"])));
        Assert.False(report.Safe);
        Assert.Contains("RpcSs", report.Summary);
    }

    [Fact]
    public void TransitiveCoreDependent_AlsoBlocks()
    {
        // SafeSvc <- Mid <- RpcSs: el núcleo está a dos saltos.
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", "SafeSvc")], Inventory(("SafeSvc", ["Mid"]), ("Mid", ["RpcSs"])));
        Assert.False(report.Safe);
    }

    [Fact]
    public void NonCoreDependents_AreListedAsWarningsNotBlockers()
    {
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", "OptSvc")], Inventory(("OptSvc", ["AppOne"]), ("AppOne", [])));
        Assert.True(report.Safe);
        Assert.Contains("AppOne", report.Assessments[0].AffectedDependents.Single());
    }

    [Fact]
    public void MissingServiceOnThisMachine_IsANoteNotABlock()
    {
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", "SafeSvc")], Inventory());
        Assert.True(report.Safe);
        Assert.Contains(report.Assessments[0].Notes, note => note.Contains("NotApplicable", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownTweakId_IsBlocked()
    {
        var report = new ImpactSimulator(Tiers()).Simulate(["no-existe"], [Def("t1", "SafeSvc")], Inventory(("SafeSvc", [])));
        Assert.False(report.Safe);
    }

    [Fact]
    public void CyclicDependents_DoNotHang()
    {
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", "SafeSvc")], Inventory(("SafeSvc", ["A"]), ("A", ["B"]), ("B", ["A", "SafeSvc"])));
        Assert.NotNull(report);
    }

    [Fact]
    public void TweakWithoutServiceActions_HasNoGraphImpact()
    {
        var report = new ImpactSimulator(Tiers()).Simulate(["t1"], [Def("t1", null)], Inventory());
        Assert.True(report.Safe);
        Assert.Contains(report.Assessments[0].Notes, note => note.Contains("No toca servicios", StringComparison.Ordinal));
    }
}
