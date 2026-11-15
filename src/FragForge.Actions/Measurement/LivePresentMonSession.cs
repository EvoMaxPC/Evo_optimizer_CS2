using System.Diagnostics;
using FragForge.Core.Analysis.Live;

namespace FragForge.Actions.Measurement;

/// <summary>Estado del ciclo de vida de una sesión de medición en vivo.</summary>
public enum LiveSessionState
{
    /// <summary>Aún no se llamó a <c>StartAsync</c>.</summary>
    NotStarted,

    /// <summary>PresentMon lanzado, aún sin frames (esperar a cs2 NO es fallo).</summary>
    WaitingForFrames,

    /// <summary>Llegó el primer frame.</summary>
    Running,

    /// <summary>Se alcanzó la duración fija: el proceso fue matado.</summary>
    Completed,

    /// <summary>La pidió el usuario (<c>StopAsync</c>).</summary>
    Stopped,

    /// <summary>Modo fijo o largo, sin frames nuevos por más de <c>StallGrace</c>.</summary>
    Stalled,

    /// <summary>PresentMon terminó solo, con código 0 y con frames.</summary>
    ProcessEnded,

    /// <summary>No se pudo medir (sin elevación, herramienta rechazada, error de lanzamiento o de salida).</summary>
    Failed,
}

/// <summary>Opciones de una sesión de medición en vivo.</summary>
public sealed record LiveSessionOptions
{
    /// <summary>Nombre del proceso a medir (con o sin ".exe").</summary>
    public string ProcessName { get; init; } = "cs2";

    /// <summary>Duración fija en segundos, o <c>null</c> para medir sin tope.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Nombre de sesión ETW; null genera un nombre nuevo al iniciar.</summary>
    public string? SessionName { get; init; }

    /// <summary>Gracia ante falta de frames; null (por defecto) nunca declara Stalled, para partida completa.</summary>
    public TimeSpan? StallGrace { get; init; }

    /// <summary>Gracia sugerida para quien active detección de stall, no validada.</summary>
    public static readonly TimeSpan DefaultStallGrace = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Sesión de medición en vivo: lanza PresentMon con <c>--output_stdout</c> por pipe anónimo, bombea el
/// stream en trozos arbitrarios hacia <see cref="Ingest"/> y controla el ciclo de vida del proceso.
/// El estado lo escribe el bombeo y lo lee la UI: todo acceso es thread-safe.
/// Nunca toca procesos ajenos: PresentMon es hijo propio y solo se mata ese árbol.
/// </summary>
public sealed class LivePresentMonSession : IAsyncDisposable
{
    private const int ReadBufferSize = 4096;

    /// <summary>Tope de espera del bombeo al detener, no validado.</summary>
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(5);

    private static readonly Stopwatch SharedClock = Stopwatch.StartNew();

    private readonly object _gate = new();
    private readonly ILivePresentMonLauncher _launcher;
    private readonly Func<bool> _isElevated;
    private readonly LiveToolResolver _toolResolver;
    private readonly LiveSessionOptions _options;
    private readonly Func<long> _nowMs;

    private ILivePresentMonProcess? _process;
    private Task? _pumpTask;
    private Task? _stopTask;
    private LiveSessionState _state = LiveSessionState.NotStarted;
    private string? _failureMessage;
    private string? _killError;
    private string? _effectiveSessionName;
    private string _stderrTail = "";
    private int? _exitCode;
    private bool _started;
    private bool _killCalled;
    private int _ingestCompleted;
    private long _lastFramesParsed;
    private long _lastFrameMs;

    public LivePresentMonSession(ILivePresentMonLauncher launcher, Func<bool> isElevated,
        LiveToolResolver toolResolver, LiveSessionOptions options, Func<long>? nowMs = null)
    {
        _launcher = launcher;
        _isElevated = isElevated;
        _toolResolver = toolResolver;
        _options = options;
        _nowMs = nowMs ?? DefaultNowMs;
        Ingest = new LiveFrameIngest(options.DurationSeconds);
    }

    public LiveFrameIngest Ingest { get; }
    public LiveSessionState State { get { lock (_gate) return _state; } }
    public string? FailureMessage { get { lock (_gate) return _failureMessage; } }
    /// <summary>Error al cerrar PresentMon, o null si no se informó ninguno.</summary>
    public string? KillError { get { lock (_gate) return _killError; } }
    /// <summary>Nombre usado al iniciar, o null si aún no se intentó lanzar PresentMon.</summary>
    public string? EffectiveSessionName { get { lock (_gate) return _effectiveSessionName; } }
    public string StderrTail { get { lock (_gate) return _stderrTail; } }
    public int? ExitCode { get { lock (_gate) return _exitCode; } }

    private static bool IsTerminal(LiveSessionState state) => state is LiveSessionState.Completed
        or LiveSessionState.Stopped or LiveSessionState.Stalled or LiveSessionState.ProcessEnded or LiveSessionState.Failed;

    // Único punto de escritura de estados terminales; el primero que llega gana bajo _gate.
    private bool SetTerminalLocked(LiveSessionState state, string? message = null)
    {
        if (IsTerminal(_state)) return false;
        _state = state;
        _failureMessage = message;
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started || IsTerminal(_state)) return Task.CompletedTask;
            _started = true;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_isElevated())
            {
                lock (_gate) SetTerminalLocked(LiveSessionState.Failed,
                    "PresentMon necesita permisos de administrador; abrí FragForge como administrador.");
                return Task.CompletedTask;
            }
            var resolution = _toolResolver();
            if (resolution.ApprovedPath is null)
            {
                lock (_gate) SetTerminalLocked(LiveSessionState.Failed, resolution.RejectionReason ?? "Herramienta no aprobada.");
                return Task.CompletedTask;
            }
            // Serializa el lanzamiento con Stop: no se puede crear un hijo después de detener.
            lock (_gate)
            {
                if (IsTerminal(_state)) return Task.CompletedTask;
                _effectiveSessionName = _options.SessionName ?? LivePresentMonArguments.NewSessionName();
                var arguments = LivePresentMonArguments.Build(_options.ProcessName, _effectiveSessionName);
                _process = _launcher.Start(resolution.ApprovedPath, arguments);
                _state = LiveSessionState.WaitingForFrames;
                // Task.Run evita continuaciones síncronas bajo el lock, incluso con fakes.
                var process = _process;
                var pump = Task.Run(() => PumpAsync(process));
                _pumpTask = pump;
                _ = Task.Run(() => ObserveExitAsync(process, pump));
            }
        }
        catch (Exception ex)
        {
            lock (_gate) SetTerminalLocked(LiveSessionState.Failed, $"No se pudo iniciar PresentMon: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public void CheckStall()
    {
        bool kill;
        lock (_gate)
        {
            kill = _state == LiveSessionState.Running
                && _options.StallGrace is TimeSpan grace
                && _nowMs() - _lastFrameMs > grace.TotalMilliseconds
                && SetTerminalLocked(LiveSessionState.Stalled);
        }
        if (kill) KillOnce();
    }

    /// <summary>Segundos desde el último frame recibido, o null si aún no llegó ninguno.</summary>
    public double? SecondsSinceLastFrame()
    {
        lock (_gate)
            return _lastFramesParsed == 0 ? null : (_nowMs() - _lastFrameMs) / 1000.0;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            var shouldKill = SetTerminalLocked(LiveSessionState.Stopped);
            var pump = _pumpTask;
            _stopTask = Task.Run(() => StopCoreAsync(shouldKill, pump));
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(bool shouldKill, Task? pump)
    {
        if (shouldKill) KillOnce();
        if (pump is not null)
        {
            try { await pump.WaitAsync(StopWaitTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        CompleteIngestOnce();
    }

    private void CompleteIngestOnce()
    {
        if (Interlocked.Exchange(ref _ingestCompleted, 1) == 0) Ingest.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        ILivePresentMonProcess? process;
        lock (_gate) { process = _process; _process = null; }
        process?.Dispose();
    }

    private async Task PumpAsync(ILivePresentMonProcess process)
    {
        var buffer = new char[ReadBufferSize];
        try
        {
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                Ingest.Push(new string(buffer, 0, read));
                var frames = Ingest.Counters.FramesParsed;
                var complete = Ingest.Accumulator.IsComplete;
                bool kill = false;
                lock (_gate)
                {
                    if (frames > _lastFramesParsed)
                    {
                        _lastFramesParsed = frames;
                        _lastFrameMs = _nowMs();
                    }
                    if (!IsTerminal(_state))
                    {
                        if (complete) kill = SetTerminalLocked(LiveSessionState.Completed);
                        else if (frames > 0) _state = LiveSessionState.Running;
                    }
                }
                if (kill) KillOnce();
            }
        }
        catch (Exception ex)
        {
            lock (_gate) SetTerminalLocked(LiveSessionState.Failed, $"Error leyendo PresentMon: {ex.Message}");
            KillOnce();
        }
        finally { CompleteIngestOnce(); }
    }

    private async Task ObserveExitAsync(ILivePresentMonProcess process, Task pump)
    {
        try
        {
            var code = await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // Leer hasta EOF antes de decidir: pueden quedar frames en el pipe tras la salida.
            await pump.ConfigureAwait(false);
            var tail = await process.ReadStandardErrorAsync().ConfigureAwait(false);
            var frames = Ingest.Counters.FramesParsed;
            lock (_gate)
            {
                _exitCode = code;
                _stderrTail = tail;
                if (!IsTerminal(_state))
                {
                    if (code != 0 || frames == 0)
                        SetTerminalLocked(LiveSessionState.Failed, $"PresentMon terminó con código {code}" +
                            (frames == 0 ? " sin frames." : ".") + (tail.Length > 0 ? " " + tail : ""));
                    else SetTerminalLocked(LiveSessionState.ProcessEnded);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate) SetTerminalLocked(LiveSessionState.Failed, $"Error esperando PresentMon: {ex.Message}");
        }
    }

    private void KillOnce()
    {
        ILivePresentMonProcess? process;
        lock (_gate)
        {
            if (_killCalled || _process is null) return;
            _killCalled = true;
            process = _process;
        }
        try { process.Kill(); }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            lock (_gate)
                _killError ??= $"No se pudo cerrar PresentMon: {ex.Message}. Cerralo a mano desde el Administrador de tareas.";
        }
    }

    private static long DefaultNowMs() => SharedClock.ElapsedMilliseconds;
}
