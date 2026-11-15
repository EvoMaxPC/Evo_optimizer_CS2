using System.Runtime.Versioning;
using FragForge.Actions.Measurement;

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
            var runner = MatchRunnerFactory.Create(options, new ConsolePrompt(), new ConsoleKeyReader(), new GlobalHotkeyListener());
            return await runner.RunAsync(options, finish.Token).ConfigureAwait(false);
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
