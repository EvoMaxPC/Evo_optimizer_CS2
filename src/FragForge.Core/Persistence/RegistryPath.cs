namespace FragForge.Core.Persistence;

public static class RegistryPath
{
    public static string Canonicalize(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0') || key.Length > 32767) throw new ArgumentException("Invalid registry key.", nameof(key));
        var parts = key.Trim().TrimEnd('\\').Split('\\');
        parts[0] = parts[0].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => "HKLM",
            "HKEY_CURRENT_USER" or "HKCU" => "HKCU",
            "HKEY_USERS" or "HKU" => "HKU",
            "HKEY_CLASSES_ROOT" or "HKCR" => "HKCR",
            "HKEY_CURRENT_CONFIG" or "HKCC" => "HKCC",
            _ => throw new ArgumentException($"Unsupported registry root '{parts[0]}'.", nameof(key))
        };
        if (parts.Any(p => p is "." or "..")) throw new ArgumentException("Relative registry segments are not allowed.", nameof(key));
        return string.Join('\\', parts);
    }
    public static string ToFullRoot(string key)
    {
        var canonical = Canonicalize(key);
        return canonical.StartsWith("HKLM\\", StringComparison.Ordinal) ? "HKEY_LOCAL_MACHINE" + canonical[4..]
            : canonical.StartsWith("HKCU\\", StringComparison.Ordinal) ? "HKEY_CURRENT_USER" + canonical[4..]
            : canonical.StartsWith("HKU\\", StringComparison.Ordinal) ? "HKEY_USERS" + canonical[3..]
            : canonical.StartsWith("HKCR\\", StringComparison.Ordinal) ? "HKEY_CLASSES_ROOT" + canonical[4..]
            : "HKEY_CURRENT_CONFIG" + canonical[4..];
    }
    public static string ResolveUserHive(string key, string? userSid)
    {
        var canonical = Canonicalize(key);
        if (!canonical.StartsWith("HKCU\\", StringComparison.Ordinal)) return canonical;
        if (string.IsNullOrWhiteSpace(userSid)) throw new InvalidOperationException("The interactive user SID is required for HKCU actions.");
        if (!userSid.StartsWith("S-1-", StringComparison.Ordinal) || userSid.Contains('\\') || userSid.Contains('/') || userSid.Contains("..")) throw new ArgumentException("Invalid user SID.", nameof(userSid));
        return "HKU\\" + userSid + canonical[4..];
    }
    public static IEnumerable<string> AncestorsFromRoot(string key)
    {
        var canonical = Canonicalize(key);
        var parts = canonical.Split('\\');
        for (var i = parts.Length; i >= 2; i--) yield return string.Join('\\', parts.Take(i));
    }
}
