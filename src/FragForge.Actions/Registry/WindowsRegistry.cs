using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Persistence;
using Microsoft.Win32;

namespace FragForge.Actions.Registry;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : IRegistry
{
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult(subkey is not null);
    }
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        if (subkey is null) return Task.FromResult(new RegistryReadResult(false, false, null, null));
        var data = subkey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return Task.FromResult(data is null ? new RegistryReadResult(true, false, null, null) : new RegistryReadResult(true, true, subkey.GetValueKind(name).ToString(), data));
    }
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult<IReadOnlyList<string>>(subkey?.GetValueNames() ?? []);
    }
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult<IReadOnlyList<string>>(subkey?.GetSubKeyNames() ?? []);
    }
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.CreateSubKey(path, true);
        subkey.SetValue(name, value.Data, Enum.Parse<RegistryValueKind>(value.Kind));
        subkey.Flush();
        return Task.CompletedTask;
    }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path, true);
        subkey?.DeleteValue(name, false);
        subkey?.Flush();
        return Task.CompletedTask;
    }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(parentKey);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var parent = root.OpenSubKey(path, true);
        if (parent is null) return Task.CompletedTask;
        using var child = parent.OpenSubKey(childName);
        if (child is null || child.SubKeyCount != 0 || child.ValueCount != 0) return Task.CompletedTask;
        parent.DeleteSubKey(childName, false);
        parent.Flush();
        return Task.CompletedTask;
    }
    private static (RegistryHive Hive, string Path) Split(string key)
    {
        var canonical = RegistryPath.Canonicalize(key);
        var index = canonical.IndexOf('\\');
        var root = index < 0 ? canonical : canonical[..index];
        var path = index < 0 ? "" : canonical[(index + 1)..];
        return (root switch { "HKLM" => RegistryHive.LocalMachine, "HKU" => RegistryHive.Users, "HKCU" => RegistryHive.CurrentUser, "HKCR" => RegistryHive.ClassesRoot, "HKCC" => RegistryHive.CurrentConfig, _ => throw new ArgumentException("Unsupported registry root.") }, path);
    }
}
