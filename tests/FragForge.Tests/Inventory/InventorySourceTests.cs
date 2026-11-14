using System.Text.Json;
using FragForge.Actions.Inventory;
using FragForge.Core.Abstractions;
using FragForge.Core.Atlas;
using FragForge.Core.Inventory;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Inventory;

public sealed class StartupInventorySourceTests
{
    private static FakeRegistry RegistryWith(Action<FakeRegistry> configure)
    {
        var registry = new FakeRegistry();
        configure(registry);
        return registry;
    }

    [Fact]
    public async Task ReadsTheInteractiveUsersRunKey_ViaItsHive_AndCrossesItWithStartupApproved()
    {
        const string Sid = "S-1-5-21-1-2-3-1001";
        var registry = RegistryWith(r =>
        {
            // El hive DEL SID, no HKCU: es la regla R-H (el HKCU del proceso no es el del jugador).
            r.Keys[$@"HKU\{Sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["OneDrive"] = ("String", "\"C:\\Users\\x\\OneDrive.exe\" /background"),
                ["Steam"] = ("String", "C:\\Steam\\steam.exe -silent")
            };
            r.Keys[$@"HKU\{Sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["OneDrive"] = ("Binary", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })
            };
        });

        var (entries, warnings, notes) = await new WindowsStartupInventorySource(registry).ReadAsync(Sid);
        Assert.DoesNotContain(warnings, w => w.Contains("No se pudo", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains(Sid, StringComparison.Ordinal));

        var oneDrive = entries.Single(e => e.Name == "OneDrive");
        Assert.Equal(StartupLocation.RunHkcu, oneDrive.Location);
        Assert.False(oneDrive.ApprovedEnabled);
        Assert.Equal(0x03, oneDrive.ApprovalFirstByte);

        // Steam no tiene valor de aprobación: estado DESCONOCIDO, no "habilitado".
        var steam = entries.Single(e => e.Name == "Steam");
        Assert.Null(steam.ApprovedEnabled);
        Assert.Null(steam.ApprovalFirstByte);

        // Nada se leyó del HKCU del proceso: la clave nunca se tocó.
        Assert.DoesNotContain(registry.ReadCount.Keys, k => k.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownSid_DoesNotReadHkcu_AndSaysSoInNotes_NotInWarnings()
    {
        const string Sid = "S-1-5-21-9-9-9-1001";
        var registry = RegistryWith(r =>
        {
            // Hay datos en el hive del SID y en el HKCU del proceso: NINGUNO debe usarse sin SID.
            r.Keys[$@"HKU\{Sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"] = new(StringComparer.OrdinalIgnoreCase) { ["Oculto"] = ("String", "oculto.exe") };
            r.Keys[@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"] = new(StringComparer.OrdinalIgnoreCase) { ["DelProceso"] = ("String", "proceso.exe") };
        });

        var (entries, warnings, notes) = await new WindowsStartupInventorySource(registry).ReadAsync(null);

        Assert.DoesNotContain(entries, e => e.Location == StartupLocation.RunHkcu);
        Assert.DoesNotContain(entries, e => e.Name is "Oculto" or "DelProceso");
        Assert.Contains(notes, n => n.Contains("SID", StringComparison.OrdinalIgnoreCase) && n.Contains("no se leyó", StringComparison.OrdinalIgnoreCase));
        // Es un ámbito no leído, no un fallo: no va a Warnings.
        Assert.DoesNotContain(warnings, w => w.Contains("SID", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(registry.ReadCount.Keys, k => k.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnInventoryWithOnlyNotes_IsStillComplete()
    {
        var (entries, warnings, notes) = await new WindowsStartupInventorySource(new FakeRegistry()).ReadAsync(null);
        // Sin registro sembrado no hay entradas de Run; las carpetas Startup del equipo de pruebas son
        // irrelevantes para esta aserción, que va sobre el estado del inventario.
        Assert.DoesNotContain(entries, e => e.Location is StartupLocation.RunHkcu or StartupLocation.RunHklm or StartupLocation.RunWow6432);
        Assert.Empty(warnings);
        Assert.NotEmpty(notes);
        var inventory = new SystemInventory { MachineName = "probe", Warnings = warnings, Notes = notes };
        Assert.True(inventory.IsComplete);
        var withFailure = new SystemInventory { MachineName = "probe", Warnings = ["algo falló"], Notes = notes };
        Assert.False(withFailure.IsComplete);
    }

    [Fact]
    public async Task ReadsHklmAndWow6432Locations()
    {
        var registry = RegistryWith(r =>
        {
            r.Keys[@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"] = new(StringComparer.OrdinalIgnoreCase) { ["MsTeams"] = ("String", "teams.exe") };
            r.Keys[@"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run"] = new(StringComparer.OrdinalIgnoreCase) { ["Old32"] = ("String", "old.exe") };
        });

        var entries = (await new WindowsStartupInventorySource(registry).ReadAsync("S-1-5-21-1-2-3-1001")).Entries;
        Assert.Contains(entries, e => e is { Name: "MsTeams", Location: StartupLocation.RunHklm });
        Assert.Contains(entries, e => e is { Name: "Old32", Location: StartupLocation.RunWow6432 });
    }

    [Fact]
    public async Task Wow6432Entries_AreApprovedFromRun32_NotFromRun()
    {
        // Wow6432Node se aprueba en StartupApproved\Run32; cruzarlo contra \Run da estados falsos.
        const string Sid = "S-1-5-21-1-2-3-1001";
        var registry = RegistryWith(r =>
        {
            r.Keys[@"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Old32"] = ("String", "old.exe")
            };
            // En Run32 está DESHABILITADO (0x03) y en Run ENABLADO (0x02): debe mandar Run32.
            r.Keys[$@"HKU\{Sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Old32"] = ("Binary", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })
            };
            r.Keys[$@"HKU\{Sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Old32"] = ("Binary", new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })
            };
        });

        var entries = (await new WindowsStartupInventorySource(registry).ReadAsync(Sid)).Entries;
        var item = Assert.Single(entries);
        Assert.Equal(StartupLocation.RunWow6432, item.Location);
        Assert.False(item.ApprovedEnabled);
        Assert.Equal(0x03, item.ApprovalFirstByte);
        Assert.EndsWith(@"StartupApproved\Run32", item.ApprovalKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(registry.ReadCount.Keys, k => k.EndsWith(@"StartupApproved\Run::Old32", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnrecognizedApprovalByte_StaysUnknown()
    {
        const string Sid = "S-1-5-21-1-2-3-1001";
        var registry = new FakeRegistry();
        registry.Keys[$@"HKU\{Sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Raro"] = ("String", "raro.exe")
        };
        registry.Keys[$@"HKU\{Sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Raro"] = ("Binary", new byte[] { 0x42, 0x01 })
        };
        var entries = (await new WindowsStartupInventorySource(registry).ReadAsync(Sid)).Entries;
        var item = Assert.Single(entries);
        Assert.Null(item.ApprovedEnabled);
        Assert.Equal(0x42, item.ApprovalFirstByte);
    }

    [Fact]
    public async Task UnsupportedSource_DeclaresTheReasonInsteadOfReturningSilence()
    {
        var (entries, warnings, _) = await new UnsupportedStartupInventorySource().ReadAsync(null);
        Assert.Empty(entries);
        Assert.Contains(warnings, w => w.Contains("Windows", StringComparison.Ordinal));
    }
}

public sealed class TaskInventorySourceTests
{
    /// <summary>Política mínima que autoriza exactamente el script fijo y rechaza cualquier otro.</summary>
    private sealed class PolicyStub : IProtectionPolicy
    {
        public bool Allow { get; init; } = true;
        public void EnsurePowerShellAllowed(string script)
        {
            if (!Allow || !script.Contains("Get-ScheduledTask", StringComparison.Ordinal)) throw new ProtectedTargetException("powershell", "script no autorizado");
        }
        public void EnsureRegistryAllowed(string key, string? valueName = null) => throw new NotSupportedException();
        public void EnsureServiceAllowed(string service) => throw new NotSupportedException();
        public void EnsureTaskAllowed(string taskPath) => throw new NotSupportedException();
        public void EnsureBcdAllowed(string element) => throw new NotSupportedException();
        public void EnsureFileAllowed(string path) => throw new NotSupportedException();
        public void EnsureNetworkPropertyAllowed(string adapter, string keyword) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ParsesTheFixedScriptOutput()
    {
        var runner = new FakeProcessRunner
        {
            Handler = _ => new ProcessResult(0, """
                [{"path":"\\Microsoft\\Windows\\Foo\\","name":"Bar","state":"Ready","enabled":false,"author":"Microsoft","lastResult":"0"},
                 {"path":"\\","name":"Solo","state":"Ready","enabled":true,"author":null,"lastResult":null}]
                """, "", false)
        };

        var (tasks, warnings) = await new WindowsTaskInventorySource(runner, new PolicyStub()).ReadAsync();
        Assert.Empty(warnings);
        Assert.Equal(@"\Microsoft\Windows\Foo\Bar", tasks[0].Path);
        Assert.False(tasks[0].Enabled);
        Assert.Equal(@"\Solo", tasks[1].Path);
        Assert.True(tasks[1].Enabled);
    }

    [Fact]
    public async Task HandlesASingleTask_WhichPowerShellSerializesAsAnObject()
    {
        var runner = new FakeProcessRunner
        {
            Handler = _ => new ProcessResult(0, """{"path":"\\","name":"Unica","state":"Ready","enabled":true}""", "", false)
        };
        var tasks = (await new WindowsTaskInventorySource(runner, new PolicyStub()).ReadAsync()).Tasks;
        Assert.Single(tasks);
        Assert.Equal(@"\Unica", tasks[0].Path);
    }

    [Fact]
    public async Task StripsTheUtf8BomThatPowerShellEmits()
    {
        var runner = new FakeProcessRunner
        {
            Handler = _ => new ProcessResult(0, "\uFEFF[{\"path\":\"\\\\\",\"name\":\"ConBom\",\"enabled\":true}]", "", false)
        };
        var tasks = (await new WindowsTaskInventorySource(runner, new PolicyStub()).ReadAsync()).Tasks;
        Assert.Equal(@"\ConBom", Assert.Single(tasks).Path);
    }

    [Fact]
    public async Task NonZeroExit_IsAWarningWithTheStderr_NotAnEmptySuccess()
    {
        var runner = new FakeProcessRunner { Handler = _ => new ProcessResult(1, "", "Access is denied.", false) };
        var (tasks, warnings) = await new WindowsTaskInventorySource(runner, new PolicyStub()).ReadAsync();
        Assert.Empty(tasks);
        Assert.Contains(warnings, w => w.Contains("Access is denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedScript_DoesNotRunAnything()
    {
        var runner = new FakeProcessRunner();
        var (tasks, warnings) = await new WindowsTaskInventorySource(runner, new PolicyStub { Allow = false }).ReadAsync();
        Assert.Empty(tasks);
        Assert.Empty(runner.Requests);
        Assert.Contains(warnings, w => w.Contains("política", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MalformedJson_IsAWarning_NotACrash()
    {
        var runner = new FakeProcessRunner { Handler = _ => new ProcessResult(0, "{ esto no es json", "", false) };
        var (tasks, warnings) = await new WindowsTaskInventorySource(runner, new PolicyStub()).ReadAsync();
        Assert.Empty(tasks);
        Assert.Contains(warnings, w => w.Contains("ilegible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Timeout_IsReported()
    {
        var runner = new FakeProcessRunner { Handler = _ => new ProcessResult(0, "", "", true) };
        var (_, warnings) = await new WindowsTaskInventorySource(runner, new PolicyStub()).ReadAsync();
        Assert.Contains(warnings, w => w.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ServiceInventoryTests
{
    [Theory]
    [InlineData(0, "Boot")]
    [InlineData(1, "System")]
    [InlineData(2, "Auto")]
    [InlineData(3, "Manual")]
    [InlineData(4, "Disabled")]
    [InlineData(7, "Unknown(7)")]
    public void DescribeStart_TranslatesTheRegistryValue(int start, string expected) =>
        Assert.Equal(expected, WindowsServiceInventorySource.DescribeStart(start));

    [Fact]
    public void Compose_KeepsWorkingWhenOneSourceFails()
    {
        // La composición se prueba con fuentes que devuelven warnings: el inventario debe salir
        // INCOMPLETO y con el motivo, no como un sistema limpio.
        var inventory = new SystemInventory { MachineName = "x", Warnings = ["Servicios: acceso denegado"] };
        Assert.False(inventory.IsComplete);
    }

    [Fact]
    public async Task UnsupportedSystemInventory_DeclaresEverySectionUnavailable()
    {
        var inventory = await new UnsupportedSystemInventory(new FakeHardwareProfiler()).BuildAsync();
        Assert.False(inventory.IsComplete);
        Assert.Contains(inventory.Warnings, w => w.Contains("servicios", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Warnings, w => w.Contains("tareas", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Warnings, w => w.Contains("arranque", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Warnings, w => w.Contains("Appx", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(inventory.Services);
    }
}

public sealed class AtlasProfileTests
{
    private static AtlasProfile FromEvidence(params string[] evidence) =>
        FragForge.Core.Atlas.AtlasProfile.FromHardware(TestProfiles.Pn64() with
        {
            Playbooks = [new PlaybookDetection("Atlas Playbook (heurística)", "Low", evidence)]
        });

    [Fact]
    public void StoppedServicesAlone_AreNotAtlas()
    {
        var profile = FromEvidence("Servicios no corriendo (no concluyente): SysMain,WSearch,Spooler,DiagTrack");
        Assert.False(profile.Detected);
        Assert.Equal(FragForge.Core.Atlas.Confidence.None, profile.Confidence);
    }

    [Fact]
    public void OrganizationMarker_DetectsWithMediumAtMost()
    {
        var profile = FromEvidence("RegisteredOrganization=Atlas Optimizer");
        Assert.True(profile.Detected);
        Assert.True(profile.Confidence <= FragForge.Core.Atlas.Confidence.Medium);
    }

    [Fact]
    public void StrongPlusUnknown_CapsAtLow()
    {
        var profile = FromEvidence("RegisteredOrganization=Atlas", "Servicios no corriendo: SysMain");
        Assert.Equal(FragForge.Core.Atlas.Confidence.Low, profile.Confidence);
    }

    [Fact]
    public void NoPlaybooks_MeansNoDetection()
    {
        var profile = FragForge.Core.Atlas.AtlasProfile.FromHardware(TestProfiles.Pn64() with { Playbooks = [] });
        Assert.False(profile.Detected);
        Assert.Contains("no se aplica", profile.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfidenceIsNeverHigh()
    {
        var profile = FromEvidence("RegisteredOrganization=Atlas", "Organization=Atlas", "ProgramDataAtlas folder");
        Assert.NotEqual(FragForge.Core.Atlas.Confidence.High, profile.Confidence);
    }

    [Fact]
    public void AtlasCondition_IsNotMetByStoppedServices()
    {
        var factory = new FakeConditionFactoryProbe();
        var condition = factory.Create(new ConditionSpec { Type = "atlas", Parameters = JsonDocument.Parse("""{"expected":true}""").RootElement.Clone() });
        var result = condition.Evaluate(TestProfiles.Pn64());
        Assert.False(result.IsMet);
    }
}
