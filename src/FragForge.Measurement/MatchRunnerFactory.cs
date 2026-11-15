using System.Runtime.Versioning;
using FragForge.Actions.Files;
using FragForge.Actions.Measurement;
using FragForge.Actions.Platform;
using FragForge.Actions.Registry;
using FragForge.Core.Analysis.Live;
using FragForge.Core.Persistence;

namespace FragForge.Cli;

/// <summary>Composición compartida de la medición real para consola y aplicación.</summary>
[SupportedOSPlatform("windows")]
public static class MatchRunnerFactory
{
    public static MatchRunner Create(MatchOptions options, IConsolePrompt prompt, IConsoleKeyReader keys, IHotkeySource hotkeys)
    {
        var verifier = new ToolVerifier(new FragForgePaths().Tools, ToolVerifier.LoadEmbedded());
        var resolver = LiveToolResolvers.FromVerifier(verifier);
        var launcher = new LivePresentMonLauncher();
        var registry = new WindowsRegistry();
        var inspector = new Cs2Inspector(new SteamCs2Resolver(registry), registry);
        var window = new Cs2WindowProbe(new NativeWindowApi());
        var display = new DisplayModeProbe();
        return new MatchRunner(prompt, keys, hotkeys,
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
            () => DateTimeOffset.Now, (duration, token) => Task.Delay(duration, token),
            new LogmanEtwSessionControl(),
            (sessionOptions, wrap) => new LivePresentMonSession(wrap(launcher), LiveElevation.IsElevated, resolver, sessionOptions));
    }
}
