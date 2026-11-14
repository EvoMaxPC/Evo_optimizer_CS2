using System.Text.Json;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed partial class FileSnapshotStore(FragForgePaths paths) : ISnapshotStore
{
    public string RootPath => paths.Snapshots;
    public string GetArtifactDirectory(string snapshotId) => Path.Combine(paths.Snapshots, ValidateId(snapshotId), "artifacts");
    public async Task SaveAsync(SystemSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateId(snapshot.Id);
        if (snapshot.SchemaVersion != 1) throw new InvalidDataException($"Unsupported snapshot schema {snapshot.SchemaVersion}.");
        var directory = Path.Combine(paths.Snapshots, snapshot.Id);
        Directory.CreateDirectory(directory);
        var artifacts = Directory.Exists(GetArtifactDirectory(snapshot.Id))
            ? Directory.GetFiles(GetArtifactDirectory(snapshot.Id), "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : [];
        var final = snapshot with { Artifacts = artifacts };
        var json = JsonSerializer.Serialize(final, JsonDefaults.Options);
        await AtomicFile.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), json, cancellationToken);
    }
    public async Task<SystemSnapshot> GetAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.Snapshots, ValidateId(snapshotId), "snapshot.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"No existe el snapshot '{snapshotId}'.", path);
        var snapshot = JsonSerializer.Deserialize<SystemSnapshot>(await File.ReadAllTextAsync(path, cancellationToken), JsonDefaults.Options) ?? throw new InvalidDataException("Snapshot vacío.");
        if (snapshot.SchemaVersion != 1 || snapshot.Id != snapshotId) throw new InvalidDataException("Snapshot version/identity mismatch.");
        foreach (var artifact in snapshot.Artifacts)
        {
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, artifact));
            if (!full.StartsWith(Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Artifact path escapes snapshot directory.");
        }
        return snapshot;
    }
    public async Task<IReadOnlyList<SystemSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.Snapshots)) return [];
        var result = new List<SystemSnapshot>();
        foreach (var directory in Directory.GetDirectories(paths.Snapshots).OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, "snapshot.json"))) result.Add(await GetAsync(Path.GetFileName(directory), cancellationToken));
        }
        return result.OrderByDescending(s => s.CreatedAt).ToArray();
    }
    private static string ValidateId(string id)
    {
        if (!IdRegex().IsMatch(id)) throw new ArgumentException("Identificador de snapshot inválido.", nameof(id));
        return id;
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$")]
    private static partial Regex IdRegex();
}
