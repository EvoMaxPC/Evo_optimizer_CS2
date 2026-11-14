using System.Text.Json;
using System.Text.RegularExpressions;
using FragForge.Core.Logging;
using FragForge.Core.Persistence;

namespace FragForge.Core.Experiments;

public sealed partial class ExperimentStore(FragForgePaths paths)
{
    public string Root => paths.Experiments;

    public string GetSessionDirectory(string sessionId)
    {
        ValidateId(sessionId);
        return Path.Combine(paths.Experiments, sessionId);
    }

    public string GetStatePath(string sessionId) => Path.Combine(GetSessionDirectory(sessionId), "session.json");
    public string GetRunsDirectory(string sessionId) => Path.Combine(GetSessionDirectory(sessionId), "runs");

    public async Task SaveStateAsync(ExperimentState state, CancellationToken cancellationToken = default)
    {
        var directory = GetSessionDirectory(state.Plan.SessionId);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(GetRunsDirectory(state.Plan.SessionId));
        var json = JsonSerializer.Serialize(state with { UpdatedAt = DateTimeOffset.UtcNow }, JsonDefaults.Options);
        await AtomicFile.WriteAllTextAsync(GetStatePath(state.Plan.SessionId), json, cancellationToken);
    }

    public async Task<ExperimentState?> LoadStateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(sessionId);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<ExperimentState>(json, JsonDefaults.Options) ?? throw new InvalidDataException("session.json vacío.");
    }

    public async Task<IReadOnlyList<ExperimentSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.Experiments)) return [];
        var summaries = new List<ExperimentSummary>();
        foreach (var directory in Directory.GetDirectories(paths.Experiments).OrderBy(d => d, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = Path.GetFileName(directory);
            if (!IdRegex().IsMatch(sessionId)) continue;
            ExperimentState? state;
            try { state = await LoadStateAsync(sessionId, cancellationToken); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { continue; }
            if (state is null) continue;
            summaries.Add(new ExperimentSummary(sessionId, state.Plan.Label ?? sessionId, state.Verdict?.Kind, state.Verdict?.Confidence, state.UpdatedAt, state.Runs.Count, state.Completed, state.Invalidated));
        }
        return summaries.OrderByDescending(s => s.UpdatedAt).ToArray();
    }

    public async Task<IReadOnlyList<ExperimentArtifactFile>> ListArtifactsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var directory = GetSessionDirectory(sessionId);
        if (!Directory.Exists(directory)) return [];
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderBy(info => info.FullName, StringComparer.Ordinal)
            .Select(info => new ExperimentArtifactFile(info.Name, Path.GetRelativePath(directory, info.FullName).Replace('\\', '/'), ContentTypeFor(info.Extension), info.Length))
            .ToArray();
    }

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".html" => "text/html",
        _ => "application/octet-stream"
    };

    public static string ValidateId(string id)
    {
        if (!IdRegex().IsMatch(id)) throw new ArgumentException($"Identificador de experimento inválido: '{id}'.", nameof(id));
        return id;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$")]
    private static partial Regex IdRegex();
}

/// <summary>
/// Bloqueo propio de experimentos: distinto del <c>.fragforge.lock</c> del motor para que una medición
/// no impida aplicar/revertir y para que dos mediciones no se solapen.
/// </summary>
public sealed class ExperimentGate(FragForgePaths paths) : IAsyncDisposable
{
    private FileStream? _stream;

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (_stream is not null) return true;
        Directory.CreateDirectory(paths.Root);
        try
        {
            _stream = new FileStream(paths.ExperimentLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null) { await _stream.DisposeAsync(); _stream = null; }
    }
}

public sealed class FileFactStore(FragForgePaths paths) : IFactStore
{
    public async Task<FactsDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.Facts)) return new FactsDocument();
        try
        {
            var json = await File.ReadAllTextAsync(paths.Facts, cancellationToken);
            return JsonSerializer.Deserialize<FactsDocument>(json, JsonDefaults.Options) ?? new FactsDocument();
        }
        catch (JsonException) { return new FactsDocument(); }
    }

    public async Task WriteAsync(MeasuredFact fact, CancellationToken cancellationToken = default)
    {
        var document = await ReadAsync(cancellationToken);
        var facts = document.Facts.Where(f => !f.Key.Equals(fact.Key, StringComparison.OrdinalIgnoreCase)).Append(fact).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var updated = new FactsDocument { SchemaVersion = document.SchemaVersion, Facts = facts };
        await AtomicFile.WriteAllTextAsync(paths.Facts, JsonSerializer.Serialize(updated, JsonDefaults.Options), cancellationToken);
    }
}
