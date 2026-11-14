using FragForge.Actions.Security;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Preflight;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Security;

/// <summary>
/// Capa 10.1. La postura es de SOLO LECTURA y lo que no se puede leer es Unknown, nunca Disabled (R-L).
/// Todos estos tests usan dobles: no tocan WMI, ni el registro, ni manage-bde.
/// </summary>
public sealed class SecurityPostureTests
{
    private static WindowsSecurityPostureService Create(
        FakeRegistry? registry = null,
        ScriptedWmi? wmi = null,
        ScriptedRunner? runner = null)
    {
        registry ??= new FakeRegistry();
        var processRunner = runner ?? new ScriptedRunner();
        return new WindowsSecurityPostureService(
            wmi ?? new ScriptedWmi(),
            registry,
            processRunner,
            new NetworkChangePreflight(processRunner));
    }

    [Fact]
    public async Task Vbs_IsUnknown_WhenTheQueryFails()
    {
        var posture = await Create(wmi: new ScriptedWmi { Throw = true }).ReadAsync();

        Assert.Equal(FeatureState.Unknown, posture.Vbs);
        Assert.Equal(FeatureState.Unknown, posture.Hvci);
    }

    /// <summary>Parsea por el VALOR NUMÉRICO del enum, no por texto (R-C).</summary>
    [Theory]
    [InlineData(0, FeatureState.Disabled)]
    [InlineData(2, FeatureState.Enabled)]
    [InlineData(1, FeatureState.Unknown)]
    [InlineData(9, FeatureState.Unknown)]
    public async Task Vbs_IsParsedByNumericValue(int raw, FeatureState expected)
    {
        var wmi = new ScriptedWmi
        {
            Row = new Dictionary<string, object?> { ["VirtualizationBasedSecurityStatus"] = raw, ["SecurityServicesRunning"] = Array.Empty<object>(), ["SecurityServicesConfigured"] = Array.Empty<object>() }
        };

        var posture = await Create(wmi: wmi).ReadAsync();

        Assert.Equal(expected, posture.Vbs);
    }

    /// <summary>HVCI está EN EJECUCIÓN solo si <c>SecurityServicesRunning</c> contiene el valor 2.</summary>
    [Fact]
    public async Task Hvci_IsEnabled_WhenServiceValueTwoIsRunning()
    {
        var wmi = new ScriptedWmi
        {
            Row = new Dictionary<string, object?> { ["VirtualizationBasedSecurityStatus"] = 2, ["SecurityServicesRunning"] = new object[] { 1, 2 }, ["SecurityServicesConfigured"] = new object[] { 2 } }
        };

        var posture = await Create(wmi: wmi).ReadAsync();

        Assert.Equal(FeatureState.Enabled, posture.Hvci);
        Assert.Contains("SecurityServicesRunning", posture.HvciSource, StringComparison.Ordinal);
    }

    /// <summary>Configurado pero no en ejecución NO es Enabled ni Disabled: es Unknown.</summary>
    [Fact]
    public async Task Hvci_ConfiguredButNotRunning_IsUnknown()
    {
        var wmi = new ScriptedWmi
        {
            Row = new Dictionary<string, object?> { ["VirtualizationBasedSecurityStatus"] = 1, ["SecurityServicesRunning"] = new object[] { 1 }, ["SecurityServicesConfigured"] = new object[] { 1, 2 } }
        };

        var posture = await Create(wmi: wmi).ReadAsync();

        Assert.Equal(FeatureState.Unknown, posture.Hvci);
        Assert.Contains("configurado", posture.HvciSource, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Secure Boot en BIOS heredado: el cmdlet falla → Unknown, no Disabled.</summary>
    [Fact]
    public async Task SecureBoot_OnLegacyBios_IsUnknownNotDisabled()
    {
        var runner = new ScriptedRunner { StandardOutput = "unknown" };

        var posture = await Create(runner: runner).ReadAsync();

        Assert.Equal(FeatureState.Unknown, posture.SecureBoot);
        Assert.Contains(posture.Observations, o => o.Key == "secureBoot" && o.Confidence == "Unknown");
    }

    [Fact]
    public async Task SecureBoot_ReadsTheLiteralBooleanTheCmdletEmits()
    {
        var posture = await Create(runner: new ScriptedRunner { StandardOutput = "true\r\n" }).ReadAsync();

        Assert.Equal(FeatureState.Enabled, posture.SecureBoot);
    }

    /// <summary>El valor de registro, si existe, gana: no hace falta lanzar PowerShell.</summary>
    [Fact]
    public async Task SecureBoot_PrefersTheRegistryValue()
    {
        var registry = new FakeRegistry();
        registry.Keys[@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["UEFISecureBootEnabled"] = ("DWord", 1)
        };
        var runner = new ScriptedRunner { StandardOutput = "false" };

        var posture = await Create(registry: registry, runner: runner).ReadAsync();

        Assert.Equal(FeatureState.Enabled, posture.SecureBoot);
        // El registro gana: no se lanzó PowerShell. (manage-bde sí se ejecuta: es el otro consumidor del runner.)
        Assert.Equal(0, runner.CallsTo("powershell.exe"));
    }

    [Theory]
    [InlineData(1, FeatureState.Enabled)]
    [InlineData(0, FeatureState.Disabled)]
    public async Task VulnerableDriverBlocklist_IsParsedByDwordValue(int raw, FeatureState expected)
    {
        var registry = new FakeRegistry();
        registry.Keys[@"HKLM\SYSTEM\CurrentControlSet\Control\CI\Config"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["VulnerableDriverBlocklistEnable"] = ("DWord", raw)
        };

        var posture = await Create(registry: registry).ReadAsync();

        Assert.Equal(expected, posture.VulnerableDriverBlocklist);
    }

    /// <summary>Valor ausente = Unknown. No se asume el valor por defecto de Windows.</summary>
    [Fact]
    public async Task VulnerableDriverBlocklist_MissingValue_IsUnknown()
    {
        var posture = await Create().ReadAsync();

        Assert.Equal(FeatureState.Unknown, posture.VulnerableDriverBlocklist);
        Assert.Contains(posture.Observations, o => o.Key == "vulnerableDriverBlocklist");
    }

    [Fact]
    public async Task ThirdPartyDriver_IsDetectedByServiceNameAndReportedAsData()
    {
        var registry = new FakeRegistry();
        registry.Keys[@"HKLM\SYSTEM\CurrentControlSet\Services\PawnIO"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ImagePath"] = ("ExpandString", @"\??\C:\Windows\System32\drivers\PawnIO.sys")
        };

        var posture = await Create(registry: registry).ReadAsync();

        Assert.True(posture.ThirdPartyKernelDriver);
        Assert.Equal("PawnIO", posture.ThirdPartyKernelDriverName);
        // El contrato NO afirma compatibilidad con ningún anticheat.
        Assert.Contains("revisa los requisitos del proveedor", posture.AnticheatContract, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seguro con", posture.AnticheatContract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThirdPartyDriver_NotFound_IsFalseNotUnknown()
    {
        var posture = await Create().ReadAsync();

        Assert.False(posture.ThirdPartyKernelDriver);
    }

    /// <summary>Si no se pudo comprobar, es null (Unknown), que no es lo mismo que "no está".</summary>
    [Fact]
    public async Task ThirdPartyDriver_WhenTheReadFails_IsNullNotFalse()
    {
        var posture = await Create(registry: new FakeRegistry { ThrowOnRead = true }).ReadAsync();

        Assert.Null(posture.ThirdPartyKernelDriver);
    }

    /// <summary>El servicio NO escribe: la postura se lee sin tocar ni un valor.</summary>
    [Fact]
    public async Task ReadingThePosture_NeverWrites()
    {
        var registry = new FakeRegistry();

        await Create(registry: registry).ReadAsync();

        Assert.Empty(registry.Writes);
        Assert.Empty(registry.Deletes);
    }
}

/// <summary>WMI con una fila fija, o que falla si se le pide.</summary>
public sealed class ScriptedWmi : IWmiQuery
{
    public bool Throw { get; init; }
    public IReadOnlyDictionary<string, object?>? Row { get; init; }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default)
    {
        if (Throw) throw new InvalidOperationException("WMI no disponible (fake).");
        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Row is null ? [] : [Row]);
    }
}

/// <summary>Runner que devuelve una salida fija y cuenta llamadas.</summary>
public sealed class ScriptedRunner : IProcessRunner
{
    public string StandardOutput { get; init; } = "";
    public List<string> Executables { get; } = [];
    public int CallsTo(string fileName) => Executables.Count(e => e.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Executables.Add(request.FileName);
        return Task.FromResult(new ProcessResult(0, StandardOutput, "", false));
    }
}
