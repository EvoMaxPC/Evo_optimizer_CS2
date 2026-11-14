using System.Diagnostics;
using FragForge.Core.Abstractions;
using FragForge.Core.Experiments;
using FragForge.Core.Models;

namespace FragForge.Actions.Measurement;

public sealed record ToolProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);
    public string? ExpectedOutputFile { get; init; }
}

public sealed record ToolProcessResult(int ExitCode, bool TimedOut, bool Killed, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut && !Killed;
}

/// <summary>Lanzador de procesos de medición. Abstraído para poder simular PresentMon en tests sin Windows.</summary>
public interface IToolProcessLauncher
{
    Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lanzador real. Usa <see cref="ProcessStartInfo.ArgumentList"/> (nunca una cadena concatenada) y
/// <c>Kill(entireProcessTree: true)</c> en cancelación o timeout, para no dejar procesos ETW huérfanos.
/// </summary>
public sealed class ProcessTreeLauncher : IToolProcessLauncher
{
    public async Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(request.FileName) ?? Environment.CurrentDirectory
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) return new ToolProcessResult(-1, false, false, "", "No se pudo iniciar el proceso.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(request.MaxDuration);
        var timedOut = false;
        var killed = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            killed = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (NotSupportedException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut)
            {
                await stdout.WaitAsync(CancellationToken.None);
                await stderr.WaitAsync(CancellationToken.None);
                throw;
            }
        }
        var output = await stdout.WaitAsync(CancellationToken.None);
        var error = await stderr.WaitAsync(CancellationToken.None);
        return new ToolProcessResult(process.ExitCode, timedOut, killed, output, error);
    }
}

/// <summary>
/// Captura con Intel PresentMon.
///
/// Argumentos de PresentMon 2.x usados (verificados por nombre documentado, no por posición):
/// <c>--process_name</c>, <c>--output_file</c>, <c>--session_name</c>, <c>--timed</c>,
/// <c>--terminate_after_timed</c>, <c>--no_console_stats</c>.
/// NO se usa la opción de "detener sesión existente": mataría sesiones ETW de otras herramientas.
/// </summary>
public sealed class PresentMonFrameCapture(IToolProcessLauncher launcher, ToolVerifier verifier) : IFrameCapture
{
    public string Name => "PresentMon";

    /// <summary>Nombre de sesión propio y único: nunca colisiona con otra instancia ni con LatencyMon.</summary>
    public static string SessionName(string runId)
    {
        var sanitized = new string(runId.Where(char.IsLetterOrDigit).Take(48).ToArray());
        return "FragForge-" + (sanitized.Length == 0 ? "session" : sanitized);
    }

    /// <summary>
    /// Ruta de configuración explícita del ejecutable. Se consulta ANTES de la allowlist. Es una propiedad
    /// mutable del componente (no de un servicio compartido) para no cambiar firmas públicas de otros tipos.
    /// </summary>
    public string? ConfiguredExecutablePath { get; set; }

    /// <summary>
    /// Dónde está PresentMon. Orden: configuración explícita → instalación de Intel → no encontrado.
    /// No ejecuta nada ni valida hash: eso lo decide <see cref="CheckAvailabilityAsync"/>.
    /// </summary>
    public PresentMonLocation LocateTool() => PresentMonLocator.Locate(ConfiguredExecutablePath);

    public Task<CaptureAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var verification = verifier.Verify(ToolVerifier.PresentMonKey);
        var availability = verification.Status switch
        {
            ToolStatus.Approved => new CaptureAvailability(true, verification.Message, verification.Path, verification.ActualSha256),
            ToolStatus.HashPlaceholder => new CaptureAvailability(false, verification.Message, verification.Path, verification.ActualSha256, IsPlaceholderHash: true),
            _ => new CaptureAvailability(false, verification.Message, verification.Path, verification.ActualSha256)
        };

        // Si la allowlist no tiene la herramienta, se intenta el instalador de Intel para dar un mensaje
        // accionable (ruta y versión) en vez de "falta PresentMon.exe en la carpeta de herramientas".
        // El hash sigue siendo obligatorio: esta rama NO habilita la captura por sí sola.
        if (!availability.IsAvailable && verification.Status == ToolStatus.Missing)
        {
            var location = LocateTool();
            if (location.Found)
                return Task.FromResult(new CaptureAvailability(
                    false,
                    $"{verification.Message} Se encontró una instalación de Intel en '{location.Path}'" +
                    (location.Version is null ? "." : $" (versión {location.Version}).") +
                    " Añade su SHA-256 a la lista blanca para habilitar la captura."));
        }

        return Task.FromResult(availability);
    }

    public async Task<FrameSeries> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        var verification = verifier.Verify(ToolVerifier.PresentMonKey);
        if (verification.Status != ToolStatus.Approved)
            return new FrameSeries { IsPartial = true, SourceColumns = "tool-not-approved: " + verification.Message };

        Directory.CreateDirectory(request.OutputDirectory);
        var csvPath = Path.Combine(request.OutputDirectory, request.RunId + ".csv");
        if (File.Exists(csvPath)) File.Delete(csvPath);

        var arguments = new List<string>
        {
            "--process_name", request.ProcessName + ".exe",
            "--output_file", csvPath,
            "--session_name", SessionName(request.RunId),
            "--timed", request.Duration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "--terminate_after_timed",
            "--no_console_stats"
        };

        var processRequest = new ToolProcessRequest
        {
            FileName = verification.Path!,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(verification.Path),
            MaxDuration = request.Duration + TimeSpan.FromSeconds(30),
            ExpectedOutputFile = csvPath
        };

        FrameSeries series;
        try
        {
            var result = await launcher.RunAsync(processRequest, cancellationToken);
            if (result.TimedOut || result.Killed)
            {
                series = File.Exists(csvPath) ? PresentMonCsvParser.ParseFile(csvPath) with { IsPartial = true } : new FrameSeries { IsPartial = true };
                return series;
            }
            // Éxito = archivo existe + encabezado con la columna obligatoria + >=1 fila.
            // NO se depende del exit code ni del texto de stdout: PresentMon no documenta su exit code
            // (no observado) y stdout sólo emite el warning inocuo de --no_console_stats.
            if (!File.Exists(csvPath)) return new FrameSeries { IsPartial = false, SourceColumns = $"sin CSV; exit={result.ExitCode}; stderr={Trim(result.StandardError)}" };
            series = PresentMonCsvParser.ParseFile(csvPath);
        }
        catch (OperationCanceledException)
        {
            // Cancelación: el lanzador ya mató el árbol. Se conserva el CSV parcial, marcado como parcial.
            series = File.Exists(csvPath) ? PresentMonCsvParser.ParseFile(csvPath) with { IsPartial = true } : new FrameSeries { IsPartial = true };
            return series;
        }

        if (series.FrameCount < request.MinimumFrames) return series with { SourceColumns = (series.SourceColumns ?? "") + $";frames={series.FrameCount}<min{request.MinimumFrames}" };
        return series;
    }

    public FrameSeries ParseCsv(string path) => PresentMonCsvParser.ParseFile(path);

    private static string Trim(string value) => value.Length <= 200 ? value.Trim() : value[..200].Trim();
}

/// <summary>Implementación para plataformas sin PresentMon: nunca finge datos.</summary>
public sealed class UnsupportedFrameCapture : IFrameCapture
{
    public string Name => "Unsupported";
    public Task<CaptureAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CaptureAvailability.Unsupported("La captura de frames requiere Windows con PresentMon verificado."));
    public Task<FrameSeries> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FrameSeries { IsPartial = true, SourceColumns = "unsupported-platform" });
    public FrameSeries ParseCsv(string path) => PresentMonCsvParser.ParseFile(path);
}
