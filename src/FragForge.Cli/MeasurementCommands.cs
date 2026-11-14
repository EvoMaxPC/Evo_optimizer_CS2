using System.Globalization;
using FragForge.Actions.Measurement;
using FragForge.Core.Abstractions;
using FragForge.Core.Experiments;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Cli;

/// <summary>
/// Comandos de la Capa 1. Se mantienen en un archivo aparte para no engordar <c>Program.cs</c>.
/// Cada método devuelve el código de salida que corresponde al contrato del CLI.
/// </summary>
public static class MeasurementCommands
{
    public static async Task<int> ToolsVerifyAsync(IServiceProvider services, ParsedArgs parsed)
    {
        var verifier = services.GetRequiredService<ToolVerifier>();
        var paths = services.GetRequiredService<FragForgePaths>();
        var verification = verifier.Verify(ToolVerifier.PresentMonKey);
        if (parsed.Has("json")) { ConsoleOutput.Json(new { verification.Status, verification.ToolKey, verification.Path, verification.ActualSha256, verification.ExpectedSha256, verification.Message }); }
        else
        {
            Console.WriteLine($"Herramienta : {verification.ToolKey}");
            Console.WriteLine($"Estado      : {verification.Status}");
            Console.WriteLine($"Ruta        : {verification.Path ?? "(no configurada)"}");
            Console.WriteLine($"SHA-256     : {verification.ActualSha256 ?? "(no calculado)"}");
            Console.WriteLine($"Esperado    : {verification.ExpectedSha256 ?? "(sin aprobar)"}");
            Console.WriteLine($"Mensaje     : {verification.Message}");
            Console.WriteLine($"Carpeta     : {paths.Tools}");
        }
        return verification.IsUsable ? ExitCodes.Success : ExitCodes.MeasurementInvalid;
    }

    public static async Task<int> ProbeAsync(IServiceProvider services, ParsedArgs parsed)
    {
        var key = parsed.Positionals.FirstOrDefault() ?? parsed.Value("probe-key");
        var probes = services.GetRequiredService<IEnumerable<IDiagnosticProbe>>().ToArray();
        if (key is null)
        {
            if (parsed.Has("json")) ConsoleOutput.Json(probes.Select(p => p.Key).ToArray());
            else { Console.WriteLine("Sondas disponibles:"); foreach (var probe in probes) Console.WriteLine("  " + probe.Key); }
            return ExitCodes.Success;
        }
        var probe = probes.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (probe is null)
        {
            Console.Error.WriteLine($"Sonda desconocida: {key}. Disponibles: {string.Join(", ", probes.Select(p => p.Key))}");
            return ExitCodes.Usage;
        }
        var hardware = await services.GetRequiredService<IHardwareProfiler>().ProfileAsync();
        var observation = await probe.ReadAsync(hardware);
        if (parsed.Has("json")) ConsoleOutput.Json(observation);
        else Console.WriteLine($"{observation.Key}: {observation.Value}  [{observation.Confidence}]  {observation.Source}{(observation.Note is null ? "" : "\n  " + observation.Note)}");
        return ExitCodes.Success;
    }

    public static async Task<int> MouseTestAsync(IServiceProvider services, ParsedArgs parsed)
    {
        var duration = ParseDouble(parsed.Value("duration"), 10);
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("mouse-test requiere Windows (Raw Input).");
            return ExitCodes.MeasurementInvalid;
        }
        Console.Error.WriteLine($"Mueve el ratón durante {duration:0} s. Solo se registran timestamps, nunca coordenadas.");
        var experiment = services.GetRequiredService<MouseRateExperiment>();
        var result = await experiment.RunAsync(TimeSpan.FromSeconds(duration));
        if (parsed.Has("json")) ConsoleOutput.Json(result);
        else
        {
            Console.WriteLine($"Muestras    : {result.SampleCount}");
            Console.WriteLine($"Hz mediana  : {F(result.MedianHz)}");
            Console.WriteLine($"Hz p5       : {F(result.P5Hz)}");
            Console.WriteLine($"Jitter (P99-P50) : {F(result.JitterMs)} ms");
            for (var i = 0; i < result.HistogramBins.Count && i < result.BinLabels.Count; i++) Console.WriteLine($"  {result.BinLabels[i],-12} {result.HistogramBins[i]}");
            Console.WriteLine($"Método      : {result.Method}");
            foreach (var note in result.Notes) Console.WriteLine("  " + note);
        }
        return result.IsValid ? ExitCodes.Success : ExitCodes.MeasurementInvalid;
    }

    public static async Task<int> MemBenchAsync(IServiceProvider services, ParsedArgs parsed)
    {
        var hardware = await services.GetRequiredService<IHardwareProfiler>().ProfileAsync();
        var experiment = services.GetRequiredService<MemoryBandwidthExperiment>();
        var result = await experiment.RunAsync(hardware);
        await MemoryBandwidthExperiment.RecordAsync(services.GetRequiredService<IFactStore>(), hardware, result);
        if (parsed.Has("json")) ConsoleOutput.Json(result);
        else
        {
            Console.WriteLine($"Lectura     : {result.ReadGigabytesPerSecond:F1} GB/s (máximo entre configuraciones)");
            Console.WriteLine($"Mediana     : {result.MedianThreadGbs:F1} GB/s");
            Console.WriteLine($"Hilos       : {result.Threads}");
            Console.WriteLine($"Dispersión  : {result.SpreadPercent:F1} %");
            Console.WriteLine($"Canal       : {result.ChannelVerdict} (confianza {result.Confidence})");
            foreach (var note in result.Notes) Console.WriteLine("  " + note);
        }
        return ExitCodes.Success;
    }

    public static async Task<int> BaselineAsync(IServiceProvider services, ParsedArgs parsed)
    {
        var capture = services.GetRequiredService<IFrameCapture>();
        var sensors = services.GetRequiredService<ISensorSampler>();
        var duration = ParseDouble(parsed.Value("duration"), 120);
        var runs = (int)ParseDouble(parsed.Value("runs"), 3);
        var warmup = ParseDouble(parsed.Value("warmup"), 10);
        var store = services.GetRequiredService<ExperimentStore>();

        var availability = await capture.CheckAvailabilityAsync();
        if (!availability.IsAvailable)
        {
            Console.Error.WriteLine("Captura no disponible: " + availability.Reason);
            return ExitCodes.MeasurementInvalid;
        }

        var sessionId = parsed.Value("session") ?? "baseline-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = store.GetRunsDirectory(sessionId);
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        for (var i = 0; i < Math.Clamp(runs, 1, 20); i++)
        {
            Console.Error.WriteLine($"Corrida {i + 1}/{runs}…");
            var series = await capture.CaptureAsync(new CaptureRequest { ProcessName = "cs2", OutputDirectory = directory, RunId = $"{sessionId}-run{i + 1:00}", Duration = TimeSpan.FromSeconds(duration) });
            var metrics = FrameMetricsCalculator.Compute(series, warmup);
            var sensorSeries = await sensors.SampleAsync(new SensorSamplingRequest { OutputDirectory = directory, RunId = $"{sessionId}-run{i + 1:00}", Duration = TimeSpan.FromSeconds(duration) });
            results.Add(new { run = i + 1, metrics.FramesUsed, metrics.AvgFps, metrics.P1Low, metrics.FrameTimeP50, metrics.FrameTimeP99, metrics.JitterMs, metrics.StutterPct, metrics.PresentModePrimary, sensors = sensorSeries.Samples.Count });
            if (!parsed.Has("json"))
                Console.WriteLine($"Run {i + 1}: avgFps={F(metrics.AvgFps)} p1Low={F(metrics.P1Low)} P50={F(metrics.FrameTimeP50)} P99={F(metrics.FrameTimeP99)} jitter={F(metrics.JitterMs)} stutter={F(metrics.StutterPct)}% mode={metrics.PresentModePrimary ?? "Unknown"}");
        }
        if (parsed.Has("json")) ConsoleOutput.Json(new { sessionId, results });
        return ExitCodes.Success;
    }

    public static async Task<int> AbAsync(IServiceProvider services, ParsedArgs parsed)
    {
        var ids = parsed.Positionals.ToArray();
        if (ids.Length == 0) { Console.Error.WriteLine("ab requiere al menos un ID de tweak."); return ExitCodes.Usage; }
        var runner = services.GetRequiredService<ExperimentRunner>();
        var store = services.GetRequiredService<ExperimentStore>();
        var request = new ExperimentRequest
        {
            SessionId = parsed.Value("session") ?? "ab-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            TweakIds = ids,
            RunsPerArm = (int)ParseDouble(parsed.Value("runs"), 5),
            DurationSeconds = ParseDouble(parsed.Value("duration"), 120),
            WarmupSeconds = ParseDouble(parsed.Value("warmup"), 10),
            Seed = (int)ParseDouble(parsed.Value("seed"), 20260101),
            AaCalibration = parsed.Has("aa"),
            Mode = parsed.Value("mode") is { } mode && mode.Equals("blocked", StringComparison.OrdinalIgnoreCase) ? ExperimentMode.Blocked : ExperimentMode.Interleaved,
            Label = parsed.Value("label")
        };
        var result = await runner.StartAsync(request);
        return await FinishExperimentAsync(services, store, result, parsed);
    }

    public static async Task<int> ExperimentAsync(IServiceProvider services, ParsedArgs parsed)
    {
        var sub = parsed.Positionals.FirstOrDefault() ?? "list";
        var store = services.GetRequiredService<ExperimentStore>();
        switch (sub)
        {
            case "list":
            {
                var items = await store.ListAsync();
                if (parsed.Has("json")) ConsoleOutput.Json(items);
                else if (items.Count == 0) Console.WriteLine("No hay experimentos.");
                else foreach (var item in items) Console.WriteLine($"{item.SessionId}  {item.UpdatedAt:O}  runs={item.Runs}  veredicto={item.Verdict?.ToString() ?? "-"}  {item.Label}");
                return ExitCodes.Success;
            }
            case "show":
            {
                var id = parsed.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("experiment show requiere ID.");
                var state = await store.LoadStateAsync(id) ?? throw new ArgumentException($"No existe el experimento '{id}'.");
                if (parsed.Has("json")) ConsoleOutput.Json(state);
                else
                {
                    Console.WriteLine($"Sesión   : {state.Plan.SessionId} ({state.Plan.Label ?? "-"})");
                    Console.WriteLine($"Corridas : {state.Runs.Count}");
                    Console.WriteLine($"Veredicto: {state.Verdict?.Kind.ToString() ?? (state.Completed ? "-" : "pendiente")} / {state.Verdict?.Confidence.ToString() ?? "-"}");
                    if (state.Verdict is not null) foreach (var reason in state.Verdict.Reasons) Console.WriteLine("  · " + reason);
                }
                return ExitCodes.Success;
            }
            case "resume":
            {
                var id = parsed.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("experiment resume requiere ID.");
                var state = await store.LoadStateAsync(id) ?? throw new ArgumentException($"No existe el experimento '{id}'.");
                var runner = services.GetRequiredService<ExperimentRunner>();
                var request = new ExperimentRequest
                {
                    SessionId = id,
                    TweakIds = state.Plan.TweakIds,
                    RunsPerArm = state.Plan.RunsPerArm,
                    DurationSeconds = state.Plan.DurationSeconds,
                    WarmupSeconds = state.Plan.WarmupSeconds,
                    Seed = state.Plan.Seed,
                    Mode = state.Plan.Mode,
                    AaCalibration = state.Plan.IsAaCalibration,
                    Label = state.Plan.Label
                };
                var result = await runner.ResumeAsync(id, request);
                return await FinishExperimentAsync(services, store, result, parsed);
            }
            case "report":
            {
                var id = parsed.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("experiment report requiere ID.");
                var state = await store.LoadStateAsync(id) ?? throw new ArgumentException($"No existe el experimento '{id}'.");
                var html = ExperimentReportRenderer.Render(state);
                var outPath = parsed.Value("out") ?? Path.Combine(store.GetSessionDirectory(id), "report.html");
                await AtomicFile.WriteAllTextAsync(outPath, html);
                Console.WriteLine("Informe: " + Path.GetFullPath(outPath));
                return ExitCodes.Success;
            }
            default:
                Console.Error.WriteLine("experiment admite list|show|resume|report.");
                return ExitCodes.Usage;
        }
    }

    private static async Task<int> FinishExperimentAsync(IServiceProvider services, ExperimentStore store, ExperimentResult result, ParsedArgs parsed)
    {
        if (result.State.Verdict is not null) await services.GetRequiredService<LocalEvidenceStore>().RecordAsync(result.State);
        if (parsed.Has("json")) ConsoleOutput.Json(result.State);
        else
        {
            Console.WriteLine($"Sesión   : {result.State.Plan.SessionId}");
            Console.WriteLine($"Resultado: {result.Outcome} — {result.Message}");
            if (result.State.Verdict is not null)
            {
                foreach (var reason in result.State.Verdict.Reasons) Console.WriteLine("  · " + reason);
            }
        }
        return result.Outcome switch
        {
            ExperimentRunOutcome.Completed => ExitCodes.Success,
            ExperimentRunOutcome.NeedsResume => ExitCodes.Success,
            ExperimentRunOutcome.Invalid => ExitCodes.MeasurementInvalid,
            _ => ExitCodes.Failure
        };
    }

    private static double ParseDouble(string? raw, double fallback) => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static string F(double? value) => value is null ? "Unknown" : value.Value.ToString("0.00", CultureInfo.InvariantCulture);
}
