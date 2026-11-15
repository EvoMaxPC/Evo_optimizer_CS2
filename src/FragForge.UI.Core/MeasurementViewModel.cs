using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FragForge.UI.Core;

public enum MeasurementState { Inactiva, Midiendo, Terminando }

/// <summary>Control de una corrida; su implementación concreta queda fuera de UI.Core.</summary>
public interface IMeasurementSession
{
    Task RunAsync(int? durationSeconds, bool saveLog, Action<string, bool> output, CancellationToken finishRequested);
    void ShowNow();
    void ToggleSegment();
}

public sealed class MeasurementViewModel : ObservableObject
{
    public const string SaveLogLabel = "Guardar log al terminar";
    public const string AdminNotice = "La app debe estar abierta como administrador; las teclas F9, F10 y F11 no llegan al juego mientras dura la medición.";
    private readonly Func<IMeasurementSession> _sessionFactory;
    private readonly SynchronizationContext? _context;
    private CancellationTokenSource? _finish;
    private IMeasurementSession? _session;
    private Task? _runTask;
    private string _duration = "";
    private string _output = "";
    private bool _saveLog;
    private MeasurementState _state;

    public MeasurementViewModel(Func<IMeasurementSession> sessionFactory)
    {
        _sessionFactory = sessionFactory;
        _context = SynchronizationContext.Current;
        StartCommand = new AsyncRelayCommand(StartAsync, () => State == MeasurementState.Inactiva);
        ShowNowCommand = new RelayCommand(ShowNow, () => State == MeasurementState.Midiendo);
        ToggleSegmentCommand = new RelayCommand(ToggleSegment, () => State == MeasurementState.Midiendo);
        FinishCommand = new AsyncRelayCommand(FinishAsync, () => State == MeasurementState.Midiendo);
    }

    public string Duration { get => _duration; set => SetProperty(ref _duration, value); }
    public bool SaveLog { get => _saveLog; set => SetProperty(ref _saveLog, value); }
    public string Output { get => _output; private set => SetProperty(ref _output, value); }
    public MeasurementState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            StartCommand.NotifyCanExecuteChanged(); ShowNowCommand.NotifyCanExecuteChanged();
            ToggleSegmentCommand.NotifyCanExecuteChanged(); FinishCommand.NotifyCanExecuteChanged();
        }
    }
    public string StateText => State.ToString();
    public string SaveLogText => SaveLogLabel;
    public string NoticeText => AdminNotice;
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand ShowNowCommand { get; }
    public IRelayCommand ToggleSegmentCommand { get; }
    public IAsyncRelayCommand FinishCommand { get; }

    public Task StartAsync()
    {
        if (State != MeasurementState.Inactiva) return Task.CompletedTask;
        int? seconds = null;
        if (Duration.Length > 0)
        {
            if (!int.TryParse(Duration, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 86400)
            {
                Output = "La duración debe ser un número entero entre 1 y 86400 segundos, o estar vacía.";
                return Task.CompletedTask;
            }
            seconds = parsed;
        }
        Output = "";
        _finish = new CancellationTokenSource();
        try { _session = _sessionFactory(); }
        catch (Exception ex)
        {
            Output = "No se pudo iniciar la medición: " + ex.Message;
            _finish.Dispose(); _finish = null;
            return Task.CompletedTask;
        }
        State = MeasurementState.Midiendo;
        _runTask = RunCoreAsync(_session, seconds, SaveLog, _finish.Token);
        return _runTask;
    }

    private async Task RunCoreAsync(IMeasurementSession session, int? seconds, bool saveLog, CancellationToken token)
    {
        try { await session.RunAsync(seconds, saveLog, AppendOutput, token); }
        catch (Exception ex) { AppendOutput("No se pudo completar la medición: " + ex.Message, true); }
        finally
        {
            _session = null;
            _finish?.Dispose(); _finish = null;
            State = MeasurementState.Inactiva;
        }
    }

    private void AppendOutput(string text, bool warning)
    {
        if (_context is not null && SynchronizationContext.Current != _context)
        {
            _context.Send(_ => AppendOnContext(text, warning), null);
            return;
        }
        AppendOnContext(text, warning);
    }

    private void AppendOnContext(string text, bool warning) =>
        Output += (Output.Length == 0 ? "" : "\n") + (warning ? "Aviso: " : "") + text;

    public void ShowNow() { if (State == MeasurementState.Midiendo) _session?.ShowNow(); }
    public void ToggleSegment() { if (State == MeasurementState.Midiendo) _session?.ToggleSegment(); }

    public async Task FinishAsync()
    {
        if (State == MeasurementState.Midiendo)
        {
            State = MeasurementState.Terminando;
            _finish?.Cancel();
        }
        var run = _runTask;
        if (run is not null && State == MeasurementState.Terminando) await run;
    }
}
