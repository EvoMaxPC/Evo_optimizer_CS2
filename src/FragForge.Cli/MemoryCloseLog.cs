using System.Globalization;
using System.Text;

namespace FragForge.Cli;

public interface IMemoryCloseLog
{
    bool Reserved { get; }
    Task ReserveAsync(string text, CancellationToken cancellationToken);
    Task AppendAsync(string text, CancellationToken cancellationToken);
}

public sealed class MemoryCloseLog(string directory, Func<DateTimeOffset> clock) : IMemoryCloseLog
{
    public string? Path { get; private set; }
    public bool Reserved => Path is not null;

    public async Task ReserveAsync(string text, CancellationToken cancellationToken)
    {
        if (Reserved) throw new InvalidOperationException("El registro ya está reservado.");
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, ".cierre-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool ownsTemporary = false;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                await file.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF }, cancellationToken);
                await using var writer = new StreamWriter(file, new UTF8Encoding(false));
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            var stem = "cierre-" + clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            for (int i = 1; i <= 99; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suffix = i == 1 ? "" : "-" + i.ToString("00", CultureInfo.InvariantCulture);
                var destination = System.IO.Path.Combine(directory, stem + suffix + ".txt");
                try { File.Move(temporary, destination, overwrite: false); ownsTemporary = false; Path = destination; return; }
                catch (IOException) when (File.Exists(destination)) { }
            }
            throw new IOException("No quedan nombres disponibles para el registro.");
        }
        finally { if (ownsTemporary) File.Delete(temporary); }
    }

    public async Task AppendAsync(string text, CancellationToken cancellationToken)
    {
        if (Path is null) throw new InvalidOperationException("No se reservó el registro.");
        await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        stream.Seek(0, SeekOrigin.End);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }
}
