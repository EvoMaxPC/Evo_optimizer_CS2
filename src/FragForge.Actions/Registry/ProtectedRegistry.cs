using FragForge.Core.Abstractions;

namespace FragForge.Actions.Registry;

public sealed class ProtectedRegistry(IRegistry inner, IProtectionPolicy policy) : IRegistry
{
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => inner.KeyExistsAsync(key, cancellationToken);
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default) => inner.ReadValueAsync(key, name, cancellationToken);
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => inner.GetValueNamesAsync(key, cancellationToken);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default) => inner.GetSubKeyNamesAsync(key, cancellationToken);
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(key, name); return inner.WriteValueAsync(key, name, value, cancellationToken); }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(key, name); return inner.DeleteValueAsync(key, name, cancellationToken); }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(parentKey + "\\" + childName); return inner.DeleteEmptySubKeyAsync(parentKey, childName, cancellationToken); }
}
