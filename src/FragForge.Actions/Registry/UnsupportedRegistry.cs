using FragForge.Core.Abstractions;

namespace FragForge.Actions.Registry;

public sealed class UnsupportedRegistry : IRegistry
{
    private static T Fail<T>() => throw new PlatformNotSupportedException("El registro de Windows no está disponible en esta plataforma.");
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default) => Task.FromResult(new RegistryReadResult(false, false, null, null));
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default) => Fail<Task>();
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default) => Fail<Task>();
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default) => Fail<Task>();
}
