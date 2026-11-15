using FragForge.Actions.Measurement;
using Xunit;

namespace FragForge.Tests.Measurement.Live;

public sealed class HotkeyMappingTests
{
    [Fact]
    public void Defaults_MapActionsToF9F10F11()
    {
        // F9 = 0x78, F10 = 0x79, F11 = 0x7A; tres registros en ese orden.
        Assert.Equal(new[] { (1, 0x78u, HotkeyAction.ToggleSegment, "F9"),
            (2, 0x79u, HotkeyAction.ShowNow, "F10"), (3, 0x7Au, HotkeyAction.Finish, "F11") },
            HotkeyMapping.Defaults.Select(x => (x.Id, x.Key, x.Registration.Action, x.Registration.Description)).ToArray());
    }

    [Fact]
    public void IdMapping_RecognizesOnlyThreeIds()
    {
        // IDs 1, 2 y 3 representan respectivamente segmento, mostrar y terminar.
        Assert.Equal(HotkeyAction.ToggleSegment, HotkeyMapping.MapId(1));
        Assert.Equal(HotkeyAction.ShowNow, HotkeyMapping.MapId(2));
        Assert.Equal(HotkeyAction.Finish, HotkeyMapping.MapId(3));
        Assert.Null(HotkeyMapping.MapId(4));
    }

    [Fact]
    public void PartialError_NamesFailedAndRegisteredKeys()
    {
        var message = HotkeyMapping.PartialError(["F10"], [new(HotkeyAction.ToggleSegment, "F9"), new(HotkeyAction.Finish, "F11")]);
        // F10 falló; F9 y F11 quedaron registradas.
        Assert.Equal("no se pudo registrar F10 (otra aplicación la usa); teclas registradas: F9, F11", message);
    }
}
