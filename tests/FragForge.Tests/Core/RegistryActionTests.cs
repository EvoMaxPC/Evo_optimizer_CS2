using FragForge.Actions.Registry;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Core;

public sealed class RegistryActionTests
{
    private const string HkcuKey = @"HKCU\Software\FragForgeTests";
    private const string Key = @"HKU\S-1-5-21-1111111111-2222222222-3333333333-1001\Software\FragForgeTests";
    private static ActionExecutionContext Context(HardwareProfile? profile = null) => new() { OperationId = "test", Hardware = profile ?? TestProfiles.Pn64(), TargetUserSid = TestProfiles.Pn64().InteractiveUserSid };
    private static RegistryAction Create(FakeRegistry registry, params object[] values) => new(registry, ProtectionPolicy.CreateDefault(), Spec.Action("Registry", Spec.Registry(values)));

    [Fact]
    public async Task CaptureApplyRevert_RestoresExistingValueExactly()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Existing", "DWord", 7);
        var action = Create(registry, Spec.Value(HkcuKey, "Existing", "DWord", 1), Spec.Value(HkcuKey, "Second", "String", "value"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(1, registry.GetInt(Key, "Existing"));
        Assert.Equal("value", registry.GetString(Key, "Second"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.RevertAsync(before, Context());
        Assert.Equal(7, registry.GetInt(Key, "Existing"));
        Assert.Null(registry.GetString(Key, "Second"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyRevertAsync(before, Context())).Outcome);
    }

    [Fact]
    public async Task Revert_DeletesValueThatDidNotExist_AndRemovesEmptyAncestors()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "NewValue", "DWord", 5));
        var before = await action.CaptureAsync(Context());
        Assert.Null(registry.GetInt(Key, "NewValue"));
        await action.ApplyAsync(before, Context());
        Assert.Equal(5, registry.GetInt(Key, "NewValue"));
        await action.RevertAsync(before, Context());
        Assert.Null(registry.GetInt(Key, "NewValue"));
        Assert.False(await registry.KeyExistsAsync(Key));
        Assert.Equal(1, registry.Deletes[$@"{Key}::NewValue"]);
    }

    [Fact]
    public async Task Apply_IsIdempotent_WhenTargetAlreadyMatches()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Value", "DWord", 1);
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var before = await action.CaptureAsync(Context());
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.ApplyAsync(before, Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(1, registry.GetInt(Key, "Value"));
    }

    [Fact]
    public async Task Binary_And_MultiString_AreRoundTripped()
    {
        var registry = new FakeRegistry();
        var payload = new byte[] { 1, 2, 3, 4, 0, 255 };
        registry.Set(Key, "Blob", "Binary", new byte[] { 9, 9 });
        registry.Set(Key, "List", "MultiString", new[] { "old", "values" });
        var action = Create(registry, Spec.Value(HkcuKey, "Blob", "Binary", Convert.ToBase64String(payload)), Spec.Value(HkcuKey, "List", "MultiString", new[] { "new" }));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal(payload, (byte[])registry.Keys[Key]["Blob"].Data);
        await action.RevertAsync(before, Context());
        Assert.Equal(new byte[] { 9, 9 }, (byte[])registry.Keys[Key]["Blob"].Data);
        Assert.Equal(new[] { "old", "values" }, (string[])registry.Keys[Key]["List"].Data);
    }

    [Fact]
    public async Task AddToken_PreservesExistingFlagsAndRevertRestoresExactly()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Layers", "String", "~ RUNASADMIN");
        var action = Create(registry, Spec.Value(HkcuKey, "Layers", "String", "DISABLEDXMAXIMIZEDWINDOWEDMODE", "AddToken"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", registry.GetString(Key, "Layers"));
        await action.ApplyAsync(before, Context());
        Assert.Equal("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", registry.GetString(Key, "Layers"));
        Assert.Equal(VerificationOutcome.Matches, (await action.VerifyAsync(before, Context())).Outcome);
        await action.RevertAsync(before, Context());
        Assert.Equal("~ RUNASADMIN", registry.GetString(Key, "Layers"));
    }

    [Fact]
    public async Task AddToken_RevertDeletesValueThatDidNotExist()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Layers", "String", "TOKEN", "AddToken"));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        Assert.Equal("TOKEN", registry.GetString(Key, "Layers"));
        await action.RevertAsync(before, Context());
        Assert.Null(registry.GetString(Key, "Layers"));
    }

    [Fact]
    public async Task Hkcu_IsResolvedToTheInteractiveUserSid_NotTheProcessHive()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var before = await action.CaptureAsync(Context());
        await action.ApplyAsync(before, Context());
        var resolved = @"HKU\" + TestProfiles.Pn64().InteractiveUserSid + HkcuKey[4..];
        Assert.True(registry.Keys.ContainsKey(resolved));
        Assert.False(registry.Keys.ContainsKey(HkcuKey));
    }

    [Fact]
    public async Task Denylist_BlocksDefenderRegistry_AtConstruction()
    {
        var registry = new FakeRegistry();
        var ex = Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows Defender\Features", "TamperProtection", "DWord", 0)));
        Assert.Equal("protected_target", ex.Code);
    }

    [Fact]
    public async Task Denylist_BlocksServiceKeyUnderServices()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend", "Start", "DWord", 4)));
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SYSTEM\CurrentControlSet\Services\ipfsvc", "Start", "DWord", 4)));
    }

    [Fact]
    public async Task Denylist_BlocksDebuggerValueName()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe", "Debugger", "String", "calc.exe")));
    }

    [Fact]
    public async Task Denylist_BlocksCriticalIfeoImages()
    {
        var registry = new FakeRegistry();
        Assert.Throws<ProtectedTargetException>(() => Create(registry, Spec.Value(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\lsass.exe\PerfOptions", "CpuPriorityClass", "DWord", 1)));
    }

    [Fact]
    public void MissingUserSid_FailsForUserHive()
    {
        var registry = new FakeRegistry();
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        Assert.Throws<InvalidOperationException>(() => RegistryPath.ResolveUserHive(HkcuKey, null));
    }

    [Fact]
    public async Task DryRun_DescribesWithoutWriting()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Value", "DWord", 3);
        var action = Create(registry, Spec.Value(HkcuKey, "Value", "DWord", 1));
        var context = Context();
        var before = await action.CaptureAsync(context);
        var description = await action.DryRunAsync(context);
        Assert.Contains("3", description, StringComparison.Ordinal);
        Assert.Equal(3, registry.GetInt(Key, "Value"));
        Assert.Empty(registry.Writes);
        _ = before;
    }
}
