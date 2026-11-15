using FragForge.Actions.Measurement;
using Xunit;

namespace FragForge.Tests.Measurement.Live;

public sealed class LogmanEtwSessionControlTests
{
    private const string Name = "FragForgeLive-035877d0";

    [Fact]
    public async Task ValidName_StopThenConfirmAbsent()
    {
        var calls = new List<string[]>();
        var control = new LogmanEtwSessionControl((args, timeout, _) =>
        {
            calls.Add(args.ToArray());
            Assert.Equal(TimeSpan.FromSeconds(10), timeout);
            return Task.FromResult(new EtwProcessResult(0, "otra sesión", false));
        });
        var result = await control.CloseAsync(Name, CancellationToken.None);
        // Un stop exitoso y una consulta sin el nombre: 2 invocaciones, cierre confirmado.
        Assert.True(result.Closed);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "stop", "FragForgeLive-035877d0", "-ets" }, calls[0]);
        Assert.Equal(new[] { "query", "-ets" }, calls[1]);
    }

    [Theory]
    [InlineData("FragForgeLive-XYZ")]
    [InlineData("PresentMon")]
    [InlineData("FragForgeLive-035877d0 && x")]
    [InlineData("FRAGFORGELIVE-035877d0")]
    [InlineData("FragForgeLive-035877d0\n")]
    [InlineData("")]
    [InlineData(null)]
    public async Task InvalidName_NeverRuns(string? name)
    {
        var calls = 0;
        var control = new LogmanEtwSessionControl((_, _, _) => { calls++; return Task.FromResult(new EtwProcessResult(0, "", false)); });
        var result = await control.CloseAsync(name!, CancellationToken.None);
        // Ninguno de los 7 nombres satisface el patrón exacto: 0 invocaciones.
        Assert.False(result.Closed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NonzeroExit_FailsAndProvidesManualCommand()
    {
        var calls = 0;
        var control = new LogmanEtwSessionControl((_, _, _) => { calls++; return Task.FromResult(new EtwProcessResult(1, "", false)); });
        var result = await control.CloseAsync(Name, CancellationToken.None);
        // Exit 1 != 0: no se consulta y se indica el comando manual.
        Assert.False(result.Closed);
        Assert.Equal(1, calls);
        Assert.Contains("logman stop FragForgeLive-035877d0 -ets", result.Message);
    }

    [Fact]
    public async Task StillListed_Fails()
    {
        var calls = 0;
        var control = new LogmanEtwSessionControl((_, _, _) => { calls++; return Task.FromResult(new EtwProcessResult(0, "FragForgeLive-035877d0 Running", false)); });
        var result = await control.CloseAsync(Name, CancellationToken.None);
        // Stop 0 y consulta aún contiene el nombre: no se confirma el cierre.
        Assert.False(result.Closed);
        Assert.Equal(2, calls);
        Assert.Contains("logman stop FragForgeLive-035877d0 -ets", result.Message);
    }

    [Fact]
    public async Task Timeout_FailsWithManualCommand()
    {
        var control = new LogmanEtwSessionControl((_, _, _) => Task.FromResult(new EtwProcessResult(-1, "", true)));
        var result = await control.CloseAsync(Name, CancellationToken.None);
        // Tiempo agotado antes de confirmar: Closed=false.
        Assert.False(result.Closed);
        Assert.Contains("logman stop FragForgeLive-035877d0 -ets", result.Message);
        Assert.Contains("estado de la sesión ETW es desconocido", result.Message);
    }

    [Fact]
    public async Task Listing_OnlyFirstTokenOwnNames_IndependentOfLanguage()
    {
        var control = new LogmanEtwSessionControl((_, _, _) => Task.FromResult(new EtwProcessResult(0,
            "Nombre Estado\nFragForgeLive-035877d0 Activo\nFragForgeLive-1234abcd Running\nFragForgeLive-035877d0 Running\nPresentMon Activo\nbasura FragForgeLive-ffffffff\nFRAGFORGELIVE-00000000 Active\n", false)));
        var names = await control.ListOwnSessionsAsync(CancellationToken.None);
        // Dos nombres válidos distintos; el duplicado y los tokens posteriores se descartan.
        Assert.Equal(new[] { "FragForgeLive-035877d0", "FragForgeLive-1234abcd" }, names);
    }

    [Fact]
    public async Task TruncatedListing_DoesNotClaimClosure()
    {
        var control = new LogmanEtwSessionControl((args, _, _) => Task.FromResult(args[0] == "stop"
            ? new EtwProcessResult(0, "", false) : new EtwProcessResult(0, "", false, true)));
        var result = await control.CloseAsync(Name, CancellationToken.None);
        // Una consulta truncada no permite concluir que el nombre esté ausente.
        Assert.False(result.Closed);
        Assert.Contains("logman stop FragForgeLive-035877d0 -ets", result.Message);
    }
    [Fact]
    public async Task TimeoutAndFailedKill_ReportUnknownAndManualCommand()
    {
        var control = new LogmanEtwSessionControl((_, _, _) => Task.FromResult(new EtwProcessResult(-1, "", true, KillFailed: true)));
        var result = await control.CloseAsync(Name, CancellationToken.None);
        // Timeout y fallo de la terminación: cierre no confirmado, 0 = false.
        Assert.False(result.Closed);
        Assert.Contains("estado de la sesión ETW es desconocido", result.Message);
        Assert.Contains("No se pudo terminar logman", result.Message);
        Assert.Contains("logman stop FragForgeLive-035877d0 -ets", result.Message);
    }

    [Fact]
    public async Task Cancellation_ReturnsUnknownAndManualCommand()
    {
        using var cts = new CancellationTokenSource();
        var control = new LogmanEtwSessionControl((_, _, token) =>
        {
            cts.Cancel();
            return Task.FromResult(new EtwProcessResult(-1, "", false, Canceled: token.IsCancellationRequested, KillFailed: true));
        });
        var result = await control.CloseAsync(Name, cts.Token);
        // Cancelación con fallo del kill simulado: cierre no confirmado y aviso manual.
        Assert.False(result.Closed);
        Assert.Contains("estado de la sesión ETW es desconocido", result.Message);
        Assert.Contains("No se pudo terminar logman", result.Message);
        Assert.Contains("logman stop FragForgeLive-035877d0 -ets", result.Message);
    }

    [Fact]
    public async Task QueryCancellation_PreservesFailedKillInCloseMessage()
    {
        using var cts = new CancellationTokenSource();
        var control = new LogmanEtwSessionControl((args, _, _) =>
        {
            if (args[0] == "stop") return Task.FromResult(new EtwProcessResult(0, "", false));
            cts.Cancel();
            return Task.FromResult(new EtwProcessResult(-1, "", false, Canceled: true, KillFailed: true));
        });
        var result = await control.CloseAsync(Name, cts.Token);
        // Stop 0 pero consulta cancelada: no se conoce el estado y se informa el fallo del kill.
        Assert.False(result.Closed);
        Assert.Contains("estado de la sesión ETW es desconocido", result.Message);
        Assert.Contains("No se pudo terminar logman", result.Message);
        Assert.Contains("logman stop FragForgeLive-035877d0 -ets", result.Message);
    }

}
