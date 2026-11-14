using FragForge.Actions.Platform;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Hotfix Capa 0 — TPM y adaptadores de red.
///
/// Síntomas reales en la PC objetivo:
/// <list type="bullet">
/// <item>Panel: "TPM=NotPresent" y aviso "Win32_Tpm: Espacio de nombres no válido". La consulta
/// FALLABA y aun así el valor salía "no presente".</item>
/// <item>Avisos: "MSFT_NetAdapter: Consulta no válida".</item>
/// </list>
///
/// Tests herméticos: no se toca la PC, todo son filas y fakes.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TpmAndNetworkHotfixTests
{
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params Dictionary<string, object?>[] rows) => rows;

    // --- TPM: el fallo de consulta NUNCA es "no presente" ---------------------------------------

    [Fact]
    public void Tpm_QueryFailed_IsUnknown_NotNotPresent()
    {
        // RED-FIRST: el código anterior tragaba la excepción, devolvía lista vacía y salía NotPresent.
        var state = WindowsHardwareProfiler.ResolveTpmState(Rows(), queryFailed: true);

        Assert.Equal(FeatureState.Unknown, state);
        Assert.NotEqual(FeatureState.NotPresent, state);
    }

    [Fact]
    public void Tpm_QuerySucceededWithZeroInstances_IsNotPresent()
    {
        var state = WindowsHardwareProfiler.ResolveTpmState(Rows(), queryFailed: false);

        Assert.Equal(FeatureState.NotPresent, state);
    }

    [Fact]
    public void Tpm_QuerySucceededWithOneEnabledInstance_IsEnabled()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["IsEnabled_InitialValue"] = true,
            ["IsActivated_InitialValue"] = true,
            ["SpecVersion"] = "2.0"
        };

        var state = WindowsHardwareProfiler.ResolveTpmState(Rows(row), queryFailed: false);

        Assert.Equal(FeatureState.Enabled, state);
    }

    [Fact]
    public void Tpm_OneInstanceButNotActivated_IsDisabled_NotEmptyPresent()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["IsEnabled_InitialValue"] = true,
            ["IsActivated_InitialValue"] = false
        };

        var state = WindowsHardwareProfiler.ResolveTpmState(Rows(row), queryFailed: false);

        Assert.Equal(FeatureState.Disabled, state);
    }

    [Fact]
    public void Tpm_Namespace_IsTheSecurityOne()
    {
        // El namespace anterior (root\Microsoft\Windows\TPM) no existe: de ahí "Espacio de nombres no válido".
        Assert.Equal(@"root\CIMV2\Security\MicrosoftTpm", WindowsHardwareProfiler.TpmScope, ignoreCase: true);
        Assert.Equal("Win32_Tpm", WindowsHardwareProfiler.TpmClass, ignoreCase: true);
    }

    // --- Red: el namespace ya era correcto; se fija para que no vuelva a "arreglarse" mal ---------

    [Fact]
    public void NetAdapter_Namespace_IsStandardCimv2()
    {
        Assert.Equal(@"root\StandardCimv2", WindowsHardwareProfiler.NetAdapterScope, ignoreCase: true);
        Assert.Equal("MSFT_NetAdapter", WindowsHardwareProfiler.NetAdapterClass, ignoreCase: true);
    }

    [Fact]
    public async Task Profile_WithFailingTpmAndNetworkQueries_DoesNotBreak_AndTpmIsUnknown()
    {
        // Perfil completo con WMI que FALLA para TPM y para red: el perfil debe construirse igual,
        // el TPM queda Unknown (no NotPresent) y ambos fallos quedan como aviso.
        var wmi = new SelectiveFailingWmi(
            failFor: ["Win32_Tpm", "MSFT_NetAdapter"]);

        var profile = await CreateProfiler(wmi).ProfileAsync();

        Assert.Equal(FeatureState.Unknown, profile.Security.Tpm);
        Assert.Empty(profile.NetworkAdapters);
        Assert.Contains(profile.ProbeWarnings, w => w.Contains("Win32_Tpm", StringComparison.Ordinal));
        Assert.Contains(profile.ProbeWarnings, w => w.Contains("MSFT_NetAdapter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Profile_WithSuccessfulTpmQueryAndNoInstances_IsNotPresent_WithoutWarning()
    {
        var wmi = new SelectiveFailingWmi(failFor: []);

        var profile = await CreateProfiler(wmi).ProfileAsync();

        Assert.Equal(FeatureState.NotPresent, profile.Security.Tpm);
    }

    [Fact]
    public async Task Profile_TakesTheArchitectureFromTheNumericProcessorValue_NotFromLocalizedText()
    {
        // Win32_OperatingSystem devuelve "64 bits" (lo que rompía todo) y Win32_Processor.Architecture = 9.
        var wmi = new SelectiveFailingWmi(failFor: [])
        {
            Rows = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Win32_OperatingSystem"] = Rows(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Caption"] = "Microsoft Windows 11 Pro",
                    ["Version"] = "10.0.26200",
                    ["BuildNumber"] = "26200",
                    ["OSArchitecture"] = "64 bits"
                }),
                ["Win32_Processor"] = Rows(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Name"] = "12th Gen Intel(R) Core(TM) i3-1220P",
                    ["Manufacturer"] = "GenuineIntel",
                    ["Architecture"] = 9,
                    ["NumberOfCores"] = "10",
                    ["NumberOfLogicalProcessors"] = "12"
                })
            }
        };

        var profile = await CreateProfiler(wmi).ProfileAsync();

        // "X64", el valor de enum: nunca "64 bits".
        Assert.Equal("X64", profile.Os.Architecture);
        Assert.NotEqual("64 bits", profile.Os.Architecture);

        // Y con ese valor, la condición del catálogo se cumple.
        var condition = new FragForge.Core.Conditions.ConditionFactory()
            .Create(Spec.Condition("windows", new { minBuild = 26100, maxBuild = 26299, architecture = "X64" }))
            .Evaluate(profile);
        Assert.True(condition.IsMet, condition.Reason);
    }

    private static WindowsHardwareProfiler CreateProfiler(IWmiQuery wmi) => new(
        wmi,
        new FakeRegistry(),
        new FakeProcessRunner(),
        new FakeGameLocator(),
        new StubTopology(),
        new FakeSystemRestoreService());

    private sealed class StubTopology : ICpuTopologyProbe
    {
        public Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CpuTopologyResult(2, 8, 12, true, "fake"));
    }

    /// <summary>WMI falso: falla solo para las clases indicadas y devuelve las filas configuradas.</summary>
    private sealed class SelectiveFailingWmi(params string[] failFor) : IWmiQuery
    {
        private readonly HashSet<string> _failFor = new(failFor, StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Rows { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
            string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default)
        {
            if (_failFor.Contains(className))
                throw new InvalidOperationException($"{className}: Espacio de nombres no válido (fake).");
            return Task.FromResult(Rows.TryGetValue(className, out var rows)
                ? rows
                : (IReadOnlyList<IReadOnlyDictionary<string, object?>>)[]);
        }
    }
}
