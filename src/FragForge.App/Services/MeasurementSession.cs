using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using FragForge.Actions.Measurement;
using FragForge.Cli;
using FragForge.UI.Core;

namespace FragForge.App.Services;

/// <summary>Adapta la interfaz de la aplicación al mismo runner usado por el CLI.</summary>
[SupportedOSPlatform("windows")]
public sealed class MeasurementSession(Dispatcher dispatcher) : IMeasurementSession
{
    private readonly ButtonHotkeys _hotkeys = new();

    public Task RunAsync(int? durationSeconds, bool saveLog, Action<string, bool> output, CancellationToken finishRequested) => Task.Run(async () =>
    {
        var options = new MatchOptions(durationSeconds, saveLog);
        try
        {
            var runner = MatchRunnerFactory.Create(options, new Prompt(dispatcher, output), new NoConsoleKeys(), _hotkeys);
            await runner.RunAsync(options, finishRequested).ConfigureAwait(false);
        }
        finally { _hotkeys.Dispose(); }
    });

    public void ShowNow() => _hotkeys.Send(HotkeyAction.ShowNow);
    public void ToggleSegment() => _hotkeys.Send(HotkeyAction.ToggleSegment);

    private sealed class NoConsoleKeys : IConsoleKeyReader
    {
        public bool IsAvailable => false;
        public ConsoleKey? TryReadKey() => null;
    }

    private sealed class Prompt(Dispatcher dispatcher, Action<string, bool> output) : IConsolePrompt
    {
        public void WriteLine(string text) => output(text, false);
        public void WriteWarning(string text) => output(text, true);
        public string? Ask(string question) => null;
        public bool? Confirm(string question) => dispatcher.Invoke(() =>
            MessageBox.Show(Application.Current.MainWindow, question, "FragForge", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
    }

    private sealed class ButtonHotkeys : IHotkeySource
    {
        private readonly GlobalHotkeyListener _listener = new();
        private readonly object _gate = new();
        private readonly Queue<HotkeyAction> _pending = new();
        private Action<HotkeyAction>? _pressed;
        public event Action<HotkeyAction>? Pressed
        {
            add { lock (_gate) _pressed += value; }
            remove { lock (_gate) _pressed -= value; }
        }
        public IReadOnlyList<HotkeyRegistration> Registrations => _listener.Registrations;
        public bool TryStart(out string? error)
        {
            _listener.Pressed += Send;
            var result = _listener.TryStart(out error);
            lock (_gate)
                while (_pending.Count > 0) { var action = _pending.Dequeue(); _pressed?.Invoke(action); }
            return result;
        }
        public void Send(HotkeyAction action)
        {
            lock (_gate)
            {
                if (_pressed is null) _pending.Enqueue(action);
                else _pressed(action);
            }
        }
        public void Dispose() { _listener.Pressed -= Send; _listener.Dispose(); }
    }
}
