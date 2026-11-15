using System.Diagnostics;
using System.Text;

namespace FragForge.Actions.Measurement;

/// <summary>
/// Proceso PresentMon en vivo, abstraído para poder testear la sesión sin proceso real.
/// El stream de salida se expone MIENTRAS corre (a diferencia de <see cref="ProcessTreeLauncher"/>,
/// que solo junta stdout/stderr hasta el final).
/// </summary>
public interface ILivePresentMonProcess : IDisposable
{
    /// <summary>Salida estándar (el CSV), legible mientras el proceso corre.</summary>
    TextReader StandardOutput { get; }

    /// <summary>
    /// Cola de stderr (últimas ~20 líneas / 8 KiB). Se lee en paralelo desde el arranque para evitar
    /// deadlock por pipe lleno; la tarea completa cuando stderr se cierra.
    /// </summary>
    Task<string> ReadStandardErrorAsync();

    /// <summary>Espera a que el proceso termine y devuelve su código de salida.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Mata solo el árbol de PresentMon (hijo propio). Tolera "ya terminó".</summary>
    void Kill();
}

/// <summary>Lanza PresentMon en vivo. Abstraído para tests sin proceso real.</summary>
public interface ILivePresentMonLauncher
{
    ILivePresentMonProcess Start(string exePath, IReadOnlyList<string> arguments);
}

/// <summary>
/// Lanzador real: <see cref="ProcessStartInfo"/> con pipes anónimos (<c>RedirectStandardOutput</c>),
/// codificación UTF8 (la salida medida es ASCII sin BOM) y argumentos por <c>ArgumentList</c>.
/// NO se testea: ningún test lanza procesos reales.
/// </summary>
public sealed class LivePresentMonLauncher : ILivePresentMonLauncher
{
    public ILivePresentMonProcess Start(string exePath, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("No se pudo iniciar PresentMon.");
        }
        return new LivePresentMonProcess(process);
    }
}

/// <summary>Envoltorio real sobre <see cref="Process"/> para <see cref="ILivePresentMonProcess"/>.</summary>
public sealed class LivePresentMonProcess : ILivePresentMonProcess
{
    private const int MaxStderrLines = 20;
    private const int MaxStderrChars = 8 * 1024;

    private readonly Process _process;
    private readonly Task<string> _stderrTail;
    private bool _disposed;

    internal LivePresentMonProcess(Process process)
    {
        _process = process;
        // stderr se bombea en paralelo desde ya: si nadie lo lee y el pipe se llena, PresentMon se bloquea.
        _stderrTail = PumpStderrAsync();
    }

    public TextReader StandardOutput => _process.StandardOutput;

    public Task<string> ReadStandardErrorAsync() => _stderrTail;

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _process.Dispose();
    }

    private async Task<string> PumpStderrAsync()
    {
        // Capacidad acotada incluso si stderr contiene una línea sin terminador.
        var tail = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > MaxStderrChars) tail.Remove(0, tail.Length - MaxStderrChars);
                var newlines = 0;
                for (var i = tail.Length - 1; i >= 0; i--)
                {
                    if (tail[i] == '\n' && ++newlines > MaxStderrLines)
                    {
                        tail.Remove(0, i + 1);
                        break;
                    }
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return tail.ToString();
    }
}
