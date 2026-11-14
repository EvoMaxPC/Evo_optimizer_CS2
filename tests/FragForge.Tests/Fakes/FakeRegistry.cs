using FragForge.Core.Abstractions;

namespace FragForge.Tests.Fakes;

public sealed class FakeRegistry : IRegistry
{
    public Dictionary<string, Dictionary<string, (string Kind, object Data)>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Writes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Deletes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ReadCount { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Action<string, string>? BeforeWrite { get; set; }
    public bool ThrowOnRead { get; set; }
    private static string Normalize(string key) => key.Trim().TrimEnd('\\');
    private static string Path(string key, string name) => Normalize(key) + "::" + name;
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Keys.ContainsKey(Normalize(key)));
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        if (ThrowOnRead) throw new IOException("Fake registry read failure.");
        var canonical = Normalize(key);
        ReadCount[Path(canonical, name)] = ReadCount.GetValueOrDefault(Path(canonical, name)) + 1;
        if (!Keys.TryGetValue(canonical, out var values)) return Task.FromResult(new RegistryReadResult(false, false, null, null));
        return Task.FromResult(values.TryGetValue(name, out var value) ? new RegistryReadResult(true, true, value.Kind, value.Data) : new RegistryReadResult(true, false, null, null));
    }
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Keys.TryGetValue(Normalize(key), out var values) ? values.Keys.ToArray() : []);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        var canonical = Normalize(key) + "\\";
        return Task.FromResult<IReadOnlyList<string>>(Keys.Keys.Where(k => k.StartsWith(canonical, StringComparison.OrdinalIgnoreCase) && !k[canonical.Length..].Contains('\\')).Select(k => k[canonical.Length..]).ToArray());
    }
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default)
    {
        BeforeWrite?.Invoke(key, name);
        var canonical = Normalize(key);
        if (!Keys.TryGetValue(canonical, out var values)) { values = new Dictionary<string, (string, object)>(StringComparer.OrdinalIgnoreCase); Keys[canonical] = values; }
        values[name] = (value.Kind, value.Data);
        var path = Path(canonical, name);
        Writes[path] = Writes.GetValueOrDefault(path) + 1;
        return Task.CompletedTask;
    }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        var canonical = Normalize(key);
        if (Keys.TryGetValue(canonical, out var values)) values.Remove(name);
        var path = Path(canonical, name);
        Deletes[path] = Deletes.GetValueOrDefault(path) + 1;
        return Task.CompletedTask;
    }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default)
    {
        var child = Normalize(parentKey) + "\\" + childName;
        if (Keys.TryGetValue(child, out var values) && values.Count == 0) Keys.Remove(child);
        return Task.CompletedTask;
    }
    public void Set(string key, string name, string kind, object data)
    {
        var canonical = Normalize(key);
        if (!Keys.TryGetValue(canonical, out var values)) { values = new Dictionary<string, (string, object)>(StringComparer.OrdinalIgnoreCase); Keys[canonical] = values; }
        values[name] = (kind, data);
    }
    public string? GetString(string key, string name) => Keys.TryGetValue(Normalize(key), out var values) && values.TryGetValue(name, out var value) ? value.Data as string : null;
    public int? GetInt(string key, string name) => Keys.TryGetValue(Normalize(key), out var values) && values.TryGetValue(name, out var value) ? value.Data as int? : null;
}
