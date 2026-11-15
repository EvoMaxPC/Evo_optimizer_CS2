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

    /// <summary>Nombre de la sesión ETW. Por defecto el fijo <see cref="LivePresentMonArguments.DefaultSessionName"/>.</summary>
    public string SessionName { get; init; } = LivePresentMonArguments.DefaultSessionName;

    /// <summary>Gracia ante falta de frames nuevos antes de declarar <see cref="LiveSessionState.Stalled"/>.</summary>
    public TimeSpan StallGrace { get; init; } = DefaultStallGrace;

    /// <summary>Gracia por defecto ante falta de frames, no validada.</summary>
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
    private LiveSessionState _state = LiveSessionState.NotStarted;
    private string? _failureMessage;
    private string _stderrTail = "";
    private int? _exitCode;
    private bool _started;
    private bool _stopRequested;
    private bool _killCalled;
    private long _lastFramesParsed;
    private long _lastFrameMs;

    public LivePresentMonSession(
        ILivePresentMonLauncher launcher,
        Func<bool> isElevated,
        LiveToolResolver toolResolver,
        LiveSessionOptions options,
        Func<long>? nowMs = null)
    {
        _launcher = launcher;
        _isElevated = isElevated;
        _toolResolver = toolResolver;
        _options = options;
        _nowMs = nowMs ?? DefaultNowMs;
        Ingest = new LiveFrameIngest(options.DurationSeconds);
    }

    /// <summary>Ingesta en vivo (acumulador + contadores). Thread-safe.</summary>
    public LiveFrameIngest Ingest { get; }

    /// <summary>Estado actual del ciclo de vida.</summary>
    public LiveSessionState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Motivo del fallo, o <c>null</c> si no falló.</summary>
    public string? FailureMessage
    {
        get { lock (_gate) return _failureMessage; }
    }

    /// <summary>Cola de stderr de PresentMon (vacía hasta que el proceso termina).</summary>
    public string StderrTail
    {
        get { lock (_gate) return _stderrTail; }
    }

    /// <summary>Código de salida de PresentMon, o <c>null</c> si aún no terminó.</summary>
    public int? ExitCode
    {
        get { lock (_gate) return _exitCode; }
    }

    /// <summary>
    /// Arranca la sesión: chequea elevación y herramienta (sin lanzar nada si fallan), lanza PresentMon
    /// y arranca el bombeo de lectura y la espera de salida. Retorna enseguida; el resto es asíncrono.
    /// Esperar a cs2 NO es fallo: sin frames el estado es <see cref="LiveSessionState.WaitingForFrames"/>
    /// indefinidamente (no hay timeout de arranque).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // La cancelación del ciclo de vida la hace StopAsync; el token queda reservado.
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;
        }

        if (!_isElevated())
        {
            SetFailed("PresentMon necesita permisos de administrador; abrí FragForge como administrador.");
            return Task.CompletedTask;
        }

        LiveToolResolution resolution;
        try
        {
            resolution = _toolResolver();
        }
        catch (Exception ex)
        {
            SetFailed($"No se pudo resolver la herramienta: {ex.Message}");
            return Task.CompletedTask;
        }
        if (resolution.ApprovedPath is null)
        {
            SetFailed(resolution.RejectionReason ?? "Herramienta no aprobada.");
            return Task.CompletedTask;
        }

        var arguments = LivePresentMonArguments.Build(_options.ProcessName, _options.SessionName);
        ILivePresentMonProcess process;
        try
        {
            process = _launcher.Start(resolution.ApprovedPath, arguments);
        }
        catch (Exception ex)
        {
            SetFailed($"No se pudo iniciar PresentMon: {ex.Message}");
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            _process = process;
            _state = LiveSessionState.WaitingForFrames;
        }
        _pumpTask = PumpAsync(process);
        _ = WaitForExitAsync(process);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lo llamará el bucle de la UI en cada tick. Si hay frames y pasó más de <c>StallGrace</c> desde el
    /// último frame recibido (según el reloj inyectado), mata el proceso y pasa a
    /// <see cref="LiveSessionState.Stalled"/>. Puro respecto del reloj inyectado.
    /// </summary>
    public void CheckStall()
    {
        bool stalled;
        lock (_gate)
        {
            stalled = _state == LiveSessionState.Running
                      && _nowMs() - _lastFrameMs > (long)_options.StallGrace.TotalMilliseconds;
            if (stalled) _state = LiveSessionState.Stalled;
        }
        if (stalled) KillOnce();
    }

    /// <summary>
    /// Detiene la sesión. Idempotente; nunca lanza por "ya terminó". Mata el proceso, espera al bombeo
    /// con tope de 5 s, completa la ingesta y pasa a <see cref="LiveSessionState.Stopped"/> salvo que ya
    /// sea terminal (Completed, Failed, Stalled, ProcessEnded se conservan).
    /// </summary>
    public async Task StopAsync()
    {
        Task? pump;
        lock (_gate)
        {
            if (_stopRequested) return;
            _stopRequested = true;
            pump = _pumpTask;
        }

        KillOnce();
        if (pump is not null)
        {
            try { await pump.WaitAsync(StopWaitTimeout).ConfigureAwait(false); }
            catch (Exception) { /* tope alcanzado o bombeo terminado con error: se sigue igual */ }
        }
        Ingest.Complete();

        lock (_gate)
        {
            if (_state is not (LiveSessionState.Completed or LiveSessionState.Failed
                    or LiveSessionState.Stalled or LiveSessionState.ProcessEnded))
                _state = LiveSessionState.Stopped;
        }
    }

    /// <summary>Detiene la sesión y libera el proceso.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        ILivePresentMonProcess? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        process?.Dispose();
    }

    private async Task PumpAsync(ILivePresentMonProcess process)
    {
        var buffer = new char[ReadBufferSize];
        try
        {
            int read;
            // ReadAsync por trozos, NO ReadLineAsync: este último devolvería la última línea
            // incompleta al EOF como si estuviera completa.
            while ((read = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                Ingest.Push(new string(buffer, 0, read));
                AfterPush();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            Ingest.Complete();
        }
    }

    private void AfterPush()
    {
        var frames = Ingest.Counters.FramesParsed;
        var complete = Ingest.Accumulator.IsComplete;
        var shouldKill = false;
        lock (_gate)
        {
            if (frames > _lastFramesParsed)
            {
                _lastFramesParsed = frames;
                _lastFrameMs = _nowMs();
            }
            if (_stopRequested) return;
            if (complete && _state is LiveSessionState.WaitingForFrames or LiveSessionState.Running)
            {
                _state = LiveSessionState.Completed;
                shouldKill = true;
            }
            else if (frames > 0 && _state == LiveSessionState.WaitingForFrames)
            {
                _state = LiveSessionState.Running;
            }
        }
        if (shouldKill) KillOnce();
    }

    private async Task WaitForExitAsync(ILivePresentMonProcess process)
    {
        int exitCode;
        try
        {
            exitCode = await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        var tail = "";
        try
        {
            tail = await process.ReadStandardErrorAsync().ConfigureAwait(false);
        }
        catch (Exception) { }

        var frames = Ingest.Counters.FramesParsed;
        lock (_gate)
        {
            _exitCode = exitCode;
            _stderrTail = tail;
            if (_state is LiveSessionState.WaitingForFrames or LiveSessionState.Running)
            {
                if (exitCode != 0 || frames == 0)
                {
                    _state = LiveSessionState.Failed;
                    _failureMessage = exitCode != 0
                        ? $"PresentMon terminó con código {exitCode}." + (tail.Length > 0 ? $" {tail}" : "")
                        : "PresentMon terminó sin entregar frames." + (tail.Length > 0 ? $" {tail}" : "");
                }
                else
                {
                    _state = LiveSessionState.ProcessEnded;
                }
            }
        }
    }

    private void KillOnce()
    {
        ILivePresentMonProcess? process;
        lock (_gate)
        {
            if (_killCalled) return;
            _killCalled = true;
            process = _process;
        }
        process?.Kill();
    }

    private void SetFailed(string message)
    {
        lock (_gate)
        {
            _state = LiveSessionState.Failed;
            _failureMessage = message;
        }
    }

    private static long DefaultNowMs() => SharedClock.ElapsedMilliseconds;
}
