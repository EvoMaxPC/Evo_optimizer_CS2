using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed class FileJournal(FragForgePaths paths) : IJournal
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Journal)!);
            string? previousHash = null;
            if (File.Exists(paths.Journal))
            {
                var lines = await File.ReadAllLinesAsync(paths.Journal, cancellationToken);
                if (lines.Length > 0)
                {
                    var last = JsonSerializer.Deserialize<JournalEntry>(lines[^1], JsonDefaults.Options) ?? throw new InvalidDataException("Journal corrupto.");
                    if (ComputeHash(last with { Hash = "" }) != last.Hash) throw new InvalidDataException("La última entrada del journal no pasó la verificación de integridad.");
                    previousHash = last.Hash;
                }
            }
            var unsigned = entry with { PreviousHash = previousHash, Hash = "" };
            var signed = unsigned with { Hash = ComputeHash(unsigned) };
            var line = JsonSerializer.Serialize(signed, JsonDefaults.Create(false));
            var stream = new FileStream(paths.Journal, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
                await using (writer.ConfigureAwait(false)) { await writer.WriteLineAsync(line.AsMemory(), cancellationToken); await writer.FlushAsync(cancellationToken); }
                await stream.FlushAsync(cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.Journal)) return [];
            var entries = new List<JournalEntry>();
            foreach (var line in await File.ReadAllLinesAsync(paths.Journal, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Línea vacía en journal.");
                entries.Add(JsonSerializer.Deserialize<JournalEntry>(line, JsonDefaults.Options) ?? throw new InvalidDataException("Entrada de journal nula."));
            }
            return entries;
        }
        finally { _gate.Release(); }
    }
    public async Task<JournalIntegrity> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await ReadAllAsync(cancellationToken);
            string? previous = null;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.PreviousHash != previous || ComputeHash(entry with { Hash = "" }) != entry.Hash) return new JournalIntegrity(false, i, $"Integridad inválida en entrada {i + 1}.");
                previous = entry.Hash;
            }
            return new JournalIntegrity(true, entries.Count, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { return new JournalIntegrity(false, 0, ex.Message); }
    }
    private static string ComputeHash(JournalEntry entry) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonDefaults.Create(false))))).ToLowerInvariant();
}
