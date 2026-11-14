namespace FragForge.Core.Abstractions;

public interface IRegistry
{
    Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default);
    Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default);
    Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default);
    Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default);
    Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default);
}
public sealed record RegistryReadResult(bool KeyExists, bool ValueExists, string? Kind, object? Data);
public sealed record RegistryWriteValue(string Kind, object Data);
