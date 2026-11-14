using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FragForge.Actions.Measurement;

/// <summary>Resultado de localizar PresentMon. <see cref="Path"/> es null cuando no se encontró.</summary>
public sealed record PresentMonLocation(string? Path, string? Version, string Detail, bool IsConfigured)
{
    public bool Found => Path is not null;
    public static PresentMonLocation NotFound(string detail) => new(null, null, detail, false);
}

/// <summary>
/// Localiza el ejecutable de PresentMon. Orden contractual:
/// <list type="number">
/// <item>Ruta de configuración explícita.</item>
/// <item><c>%ProgramFiles%\Intel\PresentMon\PresentMonConsoleApplication\PresentMon-*-x64.exe</c>, eligiendo la versión mayor más alta.</item>
/// <item>Error claro (nunca se busca en PATH).</item>
/// </list>
///
/// La versión se lee de la salida de <c>--help</c> (formato <c>PresentMon 2.6.0</c>). VERIFICADO: ese texto
/// sale por <b>STDERR</b> y el proceso termina con <b>exit code 1</b>, así que se leen ambos flujos y no se
/// usa el código de salida. NO hay gate de versión: el contrato de columnas se valida en runtime con
/// <see cref="PresentMonCsvParser"/>.
/// </summary>
public static partial class PresentMonLocator
{
    /// <summary>Subruta instalada por el instalador oficial de Intel.</summary>
    public const string InstalledSubPath = @"Intel\PresentMon\PresentMonConsoleApplication";

    private const string ProductName = "PresentMon";

    [GeneratedRegex(@"^PresentMon\s+(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"PresentMon-(\d+)\.(\d+)\.(\d+)-x64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InstalledName();

    /// <summary>
    /// Busca PresentMon. <paramref name="configuredPath"/> gana si existe. Devuelve <see cref="PresentMonLocation.NotFound"/>
    /// con el detalle de lo intentado cuando no hay nada.
    /// </summary>
    public static PresentMonLocation Locate(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath)
                ? new PresentMonLocation(configuredPath, null, "ruta configurada explícitamente", true)
                : PresentMonLocation.NotFound($"La ruta configurada no existe: {configuredPath}");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            return PresentMonLocation.NotFound("No se pudo resolver %ProgramFiles%.");

        var directory = Path.Combine(programFiles, InstalledSubPath);
        if (!Directory.Exists(directory))
            return PresentMonLocation.NotFound(
                $"No se encontró {ProductName} en '{directory}'. Instálalo desde el sitio oficial de Intel " +
                $"o configura la ruta del ejecutable explícitamente. FragForge no descarga binarios ni busca en PATH.");

        // Elige la versión mayor más alta (2.6.0 > 2.5.1 > 1.9.3). Instala varias conviven sin problema.
        var candidates = Directory.GetFiles(directory, "PresentMon-*-x64.exe")
            .Select(path => (Path: path, Match: InstalledName().Match(Path.GetFileName(path))))
            .Where(item => item.Match.Success)
            .Select(item => (item.Path, Version: new Version(
                int.Parse(item.Match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(item.Match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(item.Match.Groups[3].Value, CultureInfo.InvariantCulture))))
            .OrderByDescending(item => item.Version)
            .ToArray();

        if (candidates.Length > 0)
            return new PresentMonLocation(candidates[0].Path, candidates[0].Version.ToString(), "instalado por Intel", false);

        return PresentMonLocation.NotFound(
            $"El directorio '{directory}' existe pero no contiene ningún {ProductName}-<versión>-x64.exe.");
    }

    /// <summary>
    /// Lee la versión de la primera línea de la salida de <c>--help</c>.
    ///
    /// VERIFICADO en la PC objetivo: <c>--help</c> escribe por <b>STDERR</b> y termina con <b>exit code 1</b>.
    /// Por eso se leen AMBOS flujos y el texto se concatena, y NO se comprueba el exit code: usarlo como
    /// criterio haría imposible leer la versión. Devuelve null si el proceso falla o si el texto no
    /// empieza por <c>PresentMon &lt;versión&gt;</c>. No lanza: la ausencia de versión no bloquea la captura.
    /// </summary>
    public static string? ReadVersion(string executablePath, TimeSpan? timeout = null)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--help");
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return null;

            // Lectura asíncrona de ambos flujos para no bloquear si uno llena el búfer.
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (NotSupportedException) { }
                return null;
            }

            // WaitForExit(int) puede volver antes de que se vacíen los flujos redirigidos. La sobrecarga
            // sin argumentos espera también a que terminen las lecturas asíncronas.
            process.WaitForExit();

            // --help va por STDERR y sale con código 1: el texto se toma de ambos flujos y no se
            // filtra por exit code (ver ParseVersionOutput).
            return ParseVersionOutput(stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extrae la versión de la salida de <c>--help</c> combinando stdout y stderr.
    ///
    /// VERIFICADO en la PC objetivo: el texto sale por <b>STDERR</b> con <b>exit code 1</b>. Si aquí se
    /// mirase el exit code o solo stdout, la versión sería siempre null. El orden entre flujos no importa:
    /// se devuelve la primera línea que empiece por <c>PresentMon &lt;versión&gt;</c>.
    /// </summary>
    public static string? ParseVersionOutput(string? standardOutput, string? standardError)
    {
        // stderr primero: es el flujo donde realmente aparece (verificado), así no depende del intercalado.
        foreach (var text in new[] { standardError, standardOutput })
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                var match = VersionLine().Match(trimmed);
                if (match.Success) return match.Groups[1].Value;
            }
        }
        return null;
    }
}
