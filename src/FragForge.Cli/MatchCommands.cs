using System.Runtime.Versioning;
using FragForge.Actions.Files;
using FragForge.Actions.Measurement;
using FragForge.Actions.Platform;
using FragForge.Actions.Registry;
using FragForge.Core.Analysis.Live;
using FragForge.Core.Persistence;

namespace FragForge.Cli;

public static class MatchCommands
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var options = MatchOptions.Parse(args);
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("match solo funciona en Windows");
            return ExitCodes.Failure;
        }
        return await RunWindowsAsync(options).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsAsync(MatchOptions options)
    {
        using var finish = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; finish.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var verifier = new ToolVerifier(new FragForgePaths().Tools, ToolVerifier.LoadEmbedded());
            var resolver = LiveToolResolvers.FromVerifier(verifier);
            var launcher = new LivePresentMonLauncher();
            var registry = new WindowsRegistry();
            var inspector = new Cs2Inspector(new SteamCs2Resolver(registry), registry);
            var window = new Cs2WindowProbe(new NativeWindowApi());
            var display = new DisplayModeProbe();
            var runner = new MatchRunner(new ConsolePrompt(), new ConsoleKeyReader(), new GlobalHotkeyListener(),
                sessionOptions => new LivePresentMonSession(launcher, LiveElevation.IsElevated, resolver, sessionOptions),
                window.Observe, () => inspector.InspectAsync(),
                () =>
                {
                    var resolution = resolver();
                    if (resolution.ApprovedPath is null)
                        return Task.FromResult(new OrphanPresentMonSearch([], ["No se buscaron procesos anteriores: " + resolution.RejectionReason]));
                    return new OrphanPresentMonDetector(new WmiQuery(), resolution.ApprovedPath).Find();
                },
                () => display.ReadActiveModes().Select(mode => mode.Key).ToArray(), LiveElevation.IsElevated,
                options.SaveLog ? new MatchLogWriter(MatchLogLocation.DefaultDirectory(), () => DateTimeOffset.Now) : null,
                () => DateTimeOffset.Now, (duration, token) => Task.Delay(duration, token));
            return await runner.RunAsync(options, finish.Token).ConfigureAwait(false);
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
