using System.Collections.Concurrent;
using System.Globalization;
using FragForge.Actions.Measurement;
using FragForge.Core.Analysis.Live;
using FragForge.Core.Models;

namespace FragForge.Cli;

/// <summary>Orquesta una medición; dependencias inyectadas para pruebas sin procesos ni Windows.</summary>
public sealed class MatchRunner(
    IConsolePrompt prompt, IConsoleKeyReader keys, IHotkeySource hotkeys,
    Func<LiveSessionOptions, LivePresentMonSession> sessionFactory,
    Func<Cs2WindowObservation> observeWindow, Func<Task<Cs2Inspection>> inspect,
    Func<Task<OrphanPresentMonSearch>> findOrphans, Func<IReadOnlyList<string>> displayModes,
    Func<bool> isElevated, MatchLogWriter? logWriter, Func<DateTimeOffset> clock,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    public async Task<int> RunAsync(MatchOptions options, CancellationToken finishRequested = default)
    {
        var queue = new ConcurrentQueue<HotkeyAction>();
        void Enqueue(HotkeyAction action) => queue.Enqueue(action);
        try
        {
            if (!isElevated())
            {
                prompt.WriteWarning("PresentMon necesita permisos de administrador; abrí FragForge como administrador.");
                return ExitCodes.Failure;
            }
            OrphanPresentMonSearch? orphans = null;
            try { orphans = await findOrphans().ConfigureAwait(false); }
            catch (Exception ex) { prompt.WriteWarning("No se pudieron detectar procesos anteriores: " + ex.Message); }
            if (orphans is not null)
            {
                foreach (var note in orphans.Notes) prompt.WriteWarning(note);
                foreach (var orphan in orphans.Processes) prompt.WriteWarning(OrphanPresentMonAdvice.Describe(orphan));
                if (orphans.Processes.Count > 0 && prompt.Confirm("¿Continuar igual?") != true) return ExitCodes.Cancelled;
            }

            MatchConfigSheet? sheet = null;
            try { sheet = MatchConfigSheetBuilder.From(await inspect().ConfigureAwait(false)); }
            catch (Exception ex) { prompt.WriteWarning("no se pudo leer la configuración de CS2: " + ex.Message); }

            hotkeys.Pressed += Enqueue;
            var hotkeyNotes = new List<string>();
            if (!hotkeys.TryStart(out var hotkeyError))
            {
                hotkeyNotes.Add(hotkeyError ?? "No se pudieron registrar las teclas.");
                prompt.WriteWarning(hotkeyNotes[0]);
            }
            var registrations = hotkeys.Registrations;
            var mode = options.DurationSeconds is double duration
                ? "duración fija " + duration.ToString("0.##", CultureInfo.InvariantCulture) + " s" : "partida completa";
            prompt.WriteLine("Modo: " + mode + ". Teclas registradas: " + (registrations.Count == 0 ? "ninguna" : string.Join(", ", registrations.Select(r => r.Description))) + ". Mientras dure la medición esas teclas no llegan al juego.");
            prompt.WriteLine("F9: segmento; F10: ver ahora; F11: terminar. También podés usar estas teclas en la consola.");
            prompt.WriteLine("Con CS2 en pantalla completa este texto puede quedar tapado; el resumen se ve al terminar o con Alt+Tab. Terminá con F11 o Ctrl+C. CS2 debe estar abierto: PresentMon espera frames.");

            var sessionOptions = new LiveSessionOptions { DurationSeconds = options.DurationSeconds,
                StallGrace = options.DurationSeconds is null ? null : LiveSessionOptions.DefaultStallGrace };
            await using var session = sessionFactory(sessionOptions);
            await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
            if (session.State == LiveSessionState.Failed)
            {
                prompt.WriteWarning(session.FailureMessage ?? "No se pudo iniciar la medición.");
                if (session.StderrTail.Length > 0) prompt.WriteWarning(session.StderrTail);
                return ExitCodes.Failure;
            }

            Cs2WindowObservation? lastObserved = null;
            Cs2WindowObservation Observe()
            {
                Cs2WindowObservation observation;
                try { observation = observeWindow(); }
                catch (Exception ex) { observation = new(Cs2WindowStatus.Unavailable, [], null, null, null, null, null, null, null, ["ventana no disponible: " + ex.Message]); }
                if (observation.Status == Cs2WindowStatus.Observed) lastObserved = observation;
                return observation;
            }
            MatchEnvironment EnvironmentFor(Cs2WindowObservation window)
            {
                IReadOnlyList<string> modes;
                try { modes = displayModes(); }
                catch (Exception ex) { modes = ["No se pudieron leer modos de pantalla: " + ex.Message]; }
                return new MatchEnvironment
                {
                    WindowStatusText = window.Status switch
                    {
                        Cs2WindowStatus.Observed => "observada",
                        Cs2WindowStatus.NotRunning => "cs2 no está corriendo",
                        Cs2WindowStatus.NoWindow => "no se encontró ventana de cs2",
                        Cs2WindowStatus.MinimizedOrHidden => "ventana no disponible (minimizada u oculta)",
                        _ => "ventana no disponible",
                    },
                    ClientWidth = window.ClientWidth, ClientHeight = window.ClientHeight,
                    MonitorWidth = window.MonitorWidth, MonitorHeight = window.MonitorHeight,
                    WindowCoversMonitor = window.WindowCoversMonitor,
                    ResolutionComparisonText = ResolutionComparison.Compare(sheet?.ConfiguredWidth, sheet?.ConfiguredHeight, window.ClientWidth, window.ClientHeight).Text,
                    WindowNotes = window.Notes, DisplayModes = modes, IsElevated = true,
                };
            }

            var segmentOpen = false;
            var segmentClosed = false;
            var announcedRunning = false;
            var stallWarning = false;
            long previousFrames = 0;
            bool finish = false;
            while (!finish && !finishRequested.IsCancellationRequested)
            {
                if (keys.IsAvailable && keys.TryReadKey() is ConsoleKey key)
                {
                    if (key == ConsoleKey.F9) queue.Enqueue(HotkeyAction.ToggleSegment);
                    else if (key == ConsoleKey.F10) queue.Enqueue(HotkeyAction.ShowNow);
                    else if (key == ConsoleKey.F11) queue.Enqueue(HotkeyAction.Finish);
                }
                while (queue.TryDequeue(out var action))
                {
                    if (action == HotkeyAction.Finish) { finish = true; break; }
                    if (action == HotkeyAction.ToggleSegment)
                    {
                        if (!segmentOpen)
                        {
                            session.Ingest.Accumulator.MarkStart(); segmentOpen = true; segmentClosed = false;
                            prompt.WriteLine("Segmento iniciado: desde acá");
                        }
                        else
                        {
                            session.Ingest.Accumulator.MarkEnd(); segmentOpen = false; segmentClosed = true;
                            prompt.WriteLine("Segmento cerrado: hasta acá");
                        }
                    }
                    else if (action == HotkeyAction.ShowNow)
                    {
                        var currentWindow = Observe();
                        if (session.SecondsSinceLastFrame() is null) prompt.WriteLine("Todavía no llegaron frames de cs2 (¿está abierto?)");
                        else
                        {
                            var input = Report(MatchReportKind.Partial, session, mode, sheet, null, EnvironmentFor(currentWindow), segmentClosed,
                                segmentOpen ? hotkeyNotes.Concat(["segmento sin cerrar: no se muestra"]).ToArray() : hotkeyNotes);
                            prompt.WriteLine(MatchReportFormatter.Format(input));
                        }
                    }
                }
                session.CheckStall();
                var state = session.State;
                if (!announcedRunning && (state == LiveSessionState.Running || session.Ingest.Counters.FramesParsed > 0))
                {
                    announcedRunning = true;
                    prompt.WriteLine("Recibiendo frames de cs2.");
                    Observe();
                }
                var frames = session.Ingest.Counters.FramesParsed;
                if (frames > previousFrames) { previousFrames = frames; stallWarning = false; }
                if (options.DurationSeconds is null && !stallWarning && session.SecondsSinceLastFrame() is double elapsed && elapsed > 10)
                {
                    prompt.WriteLine("Sin frames hace " + elapsed.ToString("0.##", CultureInfo.InvariantCulture) + " s (¿cs2 en pausa, minimizado o cerrado?)");
                    stallWarning = true;
                }
                if (finish || state is LiveSessionState.Completed or LiveSessionState.Stopped or LiveSessionState.Stalled or LiveSessionState.ProcessEnded or LiveSessionState.Failed) break;
                try { await delay(Tick, finishRequested).ConfigureAwait(false); }
                catch (OperationCanceledException) when (finishRequested.IsCancellationRequested) { break; }
            }
            await session.StopAsync().ConfigureAwait(false);
            session.Ingest.Complete();
            var finalWindow = Observe();
            if (finalWindow.Status != Cs2WindowStatus.Observed && lastObserved is not null)
                finalWindow = lastObserved with { Notes = lastObserved.Notes.Concat(["ventana observada durante la medición (cs2 ya no estaba disponible al terminar)"]).ToArray() };
            MatchConfigSheet? endSheet = null;
            try { endSheet = MatchConfigSheetBuilder.From(await inspect().ConfigureAwait(false)); }
            catch (Exception ex) { prompt.WriteWarning("no se pudo leer la configuración de CS2 al terminar: " + ex.Message); }
            var final = Report(MatchReportKind.Final, session, mode, sheet, endSheet, EnvironmentFor(finalWindow), segmentClosed,
                segmentOpen ? hotkeyNotes.Concat(["segmento sin cerrar: no se muestra"]).ToArray() : hotkeyNotes);
            var text = MatchReportFormatter.Format(final);
            prompt.WriteLine(text);
            if (!options.SaveLog) prompt.WriteLine("No se guardó ningún log (usá --guardar-log para guardarlo).");
            else if (logWriter is null) prompt.WriteWarning("No se pudo guardar el log: guardado no disponible.");
            else
            {
                try
                {
                    var saved = await logWriter.SaveAsync(text, CancellationToken.None).ConfigureAwait(false);
                    if (saved.Saved) prompt.WriteLine("Log guardado: " + saved.Path);
                    else prompt.WriteWarning(saved.Message);
                }
                catch (Exception ex) { prompt.WriteWarning("No se pudo guardar el log: " + ex.Message); }
            }
            if (session.KillError is { } killError) prompt.WriteWarning(killError);
            if (session.Ingest.Counters.FramesParsed == 0) { prompt.WriteWarning("no llegaron frames"); return ExitCodes.Failure; }
            return session.State == LiveSessionState.Failed ? ExitCodes.Failure : ExitCodes.Success;
        }
        finally
        {
            hotkeys.Pressed -= Enqueue;
            hotkeys.Dispose();
        }
    }

    private MatchReportInput Report(MatchReportKind kind, LivePresentMonSession session, string mode,
        MatchConfigSheet? sheet, MatchConfigSheet? endSheet, MatchEnvironment environment, bool segmentClosed, IReadOnlyList<string> notes)
    {
        var segment = segmentClosed ? session.Ingest.Accumulator.GetSegment() : null;
        if (segment is not null && (segment.FrameCount == 0 || segment.Statistics is null)) segment = null;
        return new MatchReportInput
        {
            Kind = kind, GeneratedAtLocal = clock(), Mode = mode, SessionStateText = StateText(session.State),
            FailureMessage = session.FailureMessage, SecondsSinceLastFrame = session.SecondsSinceLastFrame(),
            Total = session.Ingest.Accumulator.GetTotal(), Last30 = session.Ingest.Accumulator.GetLastWindow(),
            Segment = segment, Counters = session.Ingest.Counters,
            StderrTail = session.State == LiveSessionState.Failed ? session.StderrTail : null,
            Sheet = sheet, SheetAtEnd = endSheet, Environment = environment, HotkeyNotes = notes,
        };
    }
    private static string StateText(LiveSessionState state) => state switch
    {
        LiveSessionState.NotStarted => "no iniciada",
        LiveSessionState.WaitingForFrames => "esperando frames",
        LiveSessionState.Running => "midiendo",
        LiveSessionState.Completed => "completada",
        LiveSessionState.Stopped => "detenida",
        LiveSessionState.Stalled => "sin frames",
        LiveSessionState.ProcessEnded => "PresentMon terminó",
        LiveSessionState.Failed => "fallida",
        _ => "no se sabe",
    };

}
