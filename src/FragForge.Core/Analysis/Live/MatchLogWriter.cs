using System.Globalization;
using System.Text;
using FragForge.Core.Persistence;

namespace FragForge.Core.Analysis.Live;

public sealed record MatchLogSaveResult(bool Saved, string? Path, string Message);

public static class MatchLogLocation
{
    public static string DefaultDirectory() => Path.Combine(new FragForgePaths().Root, "matches");
}

/// <summary>Guarda bajo demanda, sin sobrescribir archivos existentes.</summary>
public sealed class MatchLogWriter(string directory, Func<DateTimeOffset> clock)
{
    public async Task<MatchLogSaveResult> SaveAsync(string text, CancellationToken cancellationToken = default)
    {
        string? temporary = null;
        var ownsTemporary = false;
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) return new(false, null, "Directorio vacío; no se guardó el log.");
            // Reconocer ambos separadores incluso en plataformas donde uno no es separador nativo.
            var normalized = directory.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(normalized);
            var segments = fullPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment.Equals("steamapps", StringComparison.OrdinalIgnoreCase) || segment.Equals("userdata", StringComparison.OrdinalIgnoreCase)))
                return new(false, null, "Directorio restringido; no se guardó el log.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(fullPath);
            temporary = Path.Combine(fullPath, ".match-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var stem = "match-" + clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            for (var i = 1; i <= 99; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suffix = i == 1 ? "" : "-" + i.ToString("00", CultureInfo.InvariantCulture);
                var destination = Path.Combine(fullPath, stem + suffix + ".txt");
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    ownsTemporary = false;
                    return new(true, destination, "Log guardado.");
                }
                catch (IOException) when (File.Exists(destination)) { }
            }
            return new(false, null, "No hay nombres disponibles para guardar el log.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, null, "No se pudo guardar el log: " + ex.Message);
        }
        finally
        {
            if (ownsTemporary && temporary is not null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
