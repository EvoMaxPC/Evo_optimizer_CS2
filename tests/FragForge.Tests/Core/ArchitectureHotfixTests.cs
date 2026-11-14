using FragForge.Actions.Platform;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Hotfix Capa 0 — arquitectura.
///
/// Síntoma real en la PC objetivo (es-MX, x64): la pestaña mostraba los 3 tweaks Safe como
/// "No aplicable" con el motivo "Arquitectura 64 bits; se requiere X64.".
/// Causa: la arquitectura se guardaba como texto localizado de WMI ("64 bits") y se comparaba
/// contra el literal "X64" del catálogo.
///
/// Estos tests fijan las dos fuentes válidas (enum de RuntimeInformation y número de
/// Win32_Processor.Architecture) y que lo desconocido NO se convierte en x64.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ArchitectureHotfixTests
{
    private static HardwareProfile ProfileWith(string architecture) =>
        TestProfiles.Pn64() with { Os = TestProfiles.Pn64().Os with { Architecture = architecture } };

    private static ConditionResult EvaluateWindows(HardwareProfile profile, string required = "X64") =>
        new FragForge.Core.Conditions.ConditionFactory()
            .Create(Spec.Condition("windows", new { minBuild = 26100, maxBuild = 26299, architecture = required }))
            .Evaluate(profile);

    // --- El bug, tal cual se vio en la PC ---------------------------------------------------------

    [Fact]
    public void LocalizedText_64bits_IsTreatedAsX64_NotAsAMismatch()
    {
        // RED-FIRST: con el código anterior esto devolvía Unmet("Arquitectura 64 bits; se requiere X64").
        var result = EvaluateWindows(ProfileWith("64 bits"));

        Assert.True(result.IsMet, $"motivo devuelto: {result.Reason}");
        Assert.True(result.IsKnown);
    }

    [Fact]
    public void NumericSource_9_IsTreatedAsX64()
    {
        // Camino de la fuente nueva: Win32_Processor.Architecture = 9.
        var kind = ArchitectureInterpreter.FromProcessorArchitectureNumber(9);

        Assert.Equal(OsArchitectureKind.X64, kind);
        Assert.True(EvaluateWindows(ProfileWith(ArchitectureInterpreter.ToWireValue(kind))).IsMet);
    }

    [Fact]
    public void RuntimeSource_X64_IsTreatedAsX64()
    {
        var kind = ArchitectureInterpreter.FromRuntime(System.Runtime.InteropServices.Architecture.X64);

        Assert.Equal(OsArchitectureKind.X64, kind);
        Assert.True(EvaluateWindows(ProfileWith(ArchitectureInterpreter.ToWireValue(kind))).IsMet);
    }

    // --- Lo que NO debe pasar: adivinar -----------------------------------------------------------

    [Fact]
    public void Arm64_IsNotApplicableForX64Requirement_AndNamesTheEnumValue()
    {
        var kind = ArchitectureInterpreter.FromProcessorArchitectureNumber(12);

        Assert.Equal(OsArchitectureKind.Arm64, kind);
        var result = EvaluateWindows(ProfileWith(ArchitectureInterpreter.ToWireValue(kind)));

        Assert.False(result.IsMet);
        Assert.True(result.IsKnown); // es un "no" seguro, no un "no sé"
        Assert.Contains("Arm64", result.Reason, StringComparison.Ordinal);
        // El motivo nombra el valor de enum, nunca un texto localizado.
        Assert.DoesNotContain("bits", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("procesador raro")]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("   ")]
    [InlineData("Microsoft Windows 11 Pro")]
    public void UnknownArchitecture_IsUnknown_NotNotApplicable_AndSaysDesconocida(string value)
    {
        var result = EvaluateWindows(ProfileWith(value));

        Assert.False(result.IsMet);
        Assert.False(result.IsKnown); // R-L: desconocido no es "no aplicable"
        Assert.Contains("arquitectura desconocida", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]  // MIPS
    [InlineData(2)]  // Alpha
    [InlineData(3)]  // PowerPC
    [InlineData(4)]  // IA64
    [InlineData(8)]  // MIPS64
    [InlineData(99)]
    [InlineData(null)]
    public void UnmappedProcessorNumbers_AreUnknown_NeverX64(int? value)
    {
        Assert.Equal(OsArchitectureKind.Unknown, ArchitectureInterpreter.FromProcessorArchitectureNumber(value));
    }

    // --- ResolveOsArchitecture: el numerico manda, y si falta se usa el proceso -------------------

    [Fact]
    public void ResolveOsArchitecture_PrefersTheNumericWmiValue()
    {
        var warnings = new List<string>();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Architecture"] = 9 };

        var kind = WindowsHardwareProfiler.ResolveOsArchitecture(row, warnings);

        Assert.Equal(OsArchitectureKind.X64, kind);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ResolveOsArchitecture_FallsBackToProcessRuntime_WhenWmiIsMissing_AndWarns()
    {
        var warnings = new List<string>();

        var kind = WindowsHardwareProfiler.ResolveOsArchitecture(null, warnings);

        // El proceso corre en Linux x64 o Windows x64; lo relevante es que no inventa y que avisa.
        Assert.Equal(ArchitectureInterpreter.FromCurrentRuntime(), kind);
        Assert.Single(warnings);
        Assert.Contains("Architecture", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOsArchitecture_WarnsWhenTheNumericValueHasNoEquivalent()
    {
        var warnings = new List<string>();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Architecture"] = 7 };

        var kind = WindowsHardwareProfiler.ResolveOsArchitecture(row, warnings);

        Assert.Equal(ArchitectureInterpreter.FromCurrentRuntime(), kind);
        Assert.Single(warnings);
        Assert.Contains("7", warnings[0], StringComparison.Ordinal);
    }

    // --- El valor guardado siempre es un enum, nunca texto del sistema ---------------------------

    [Fact]
    public void ToWireValue_IsAlwaysAnEnumName()
    {
        foreach (var kind in Enum.GetValues<OsArchitectureKind>())
            Assert.Equal(kind.ToString(), ArchitectureInterpreter.ToWireValue(kind));
    }

    [Fact]
    public void Parse_RoundTripsEveryEnumValue()
    {
        foreach (var kind in Enum.GetValues<OsArchitectureKind>())
            Assert.Equal(kind, ArchitectureInterpreter.Parse(ArchitectureInterpreter.ToWireValue(kind)));
    }
}
