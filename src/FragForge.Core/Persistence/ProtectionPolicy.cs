using System.Text.Json;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public sealed partial class ProtectionPolicy : IProtectionPolicy
{
    private readonly PolicyDocument _document;
    private ProtectionPolicy(PolicyDocument document) => _document = document;
    public static ProtectionPolicy CreateDefault()
    {
        using var stream = typeof(ProtectionPolicy).Assembly.GetManifestResourceStream("FragForge.Core.Policy.denylist.json") ?? throw new InvalidDataException("Embedded denylist is missing.");
        using var reader = new StreamReader(stream);
        var document = JsonSerializer.Deserialize<PolicyDocument>(reader.ReadToEnd(), JsonDefaults.Options) ?? throw new InvalidDataException("Embedded denylist is empty.");
        return new ProtectionPolicy(document);
    }
    public static async Task<ProtectionPolicy> LoadAsync(string? additionalPolicyPath, CancellationToken cancellationToken = default)
    {
        var policy = CreateDefault();
        if (string.IsNullOrWhiteSpace(additionalPolicyPath) || !File.Exists(additionalPolicyPath)) return policy;
        var extra = JsonSerializer.Deserialize<PolicyDocument>(await File.ReadAllTextAsync(additionalPolicyPath, cancellationToken), JsonDefaults.Options) ?? throw new InvalidDataException("Additional denylist is empty.");
        if (extra.SchemaVersion != 1) throw new InvalidDataException("Unsupported denylist schema version.");
        policy._document.Services.AddRange(extra.Services);
        policy._document.RegistryKeyPrefixes.AddRange(extra.RegistryKeyPrefixes);
        policy._document.RegistryValueNames.AddRange(extra.ValueNames());
        policy._document.TaskPrefixes.AddRange(extra.TaskPrefixes);
        policy._document.BcdDenyElements.AddRange(extra.BcdDenyElements);
        policy._document.FilePrefixes.AddRange(extra.FilePrefixes);
        policy._document.NetworkPropertyKeywords.AddRange(extra.NetworkPropertyKeywords);
        policy._document.PowerShellDeniedTokens.AddRange(extra.PowerShellDeniedTokens);
        return policy;
    }
    public void EnsureRegistryAllowed(string key, string? valueName = null)
    {
        var canonical = RegistryPath.Canonicalize(key);
        foreach (var prefix in _document.RegistryKeyPrefixes)
        {
            var normalized = NormalizePrefix(prefix);
            if (MatchesPrefix(canonical, normalized)) throw new ProtectedTargetException(canonical, "prefijo de clave protegida");
        }
        var serviceMatch = ServiceKeyRegex().Match(canonical);
        if (serviceMatch.Success) EnsureServiceAllowed(serviceMatch.Groups[1].Value);
        if (valueName is not null && _document.RegistryValueNames.Contains(valueName, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException($"{canonical}\\{valueName}", "valor de ejecución/seguridad protegido");
        if (canonical.Contains("\\Image File Execution Options\\", StringComparison.OrdinalIgnoreCase))
        {
            var image = canonical.Split("\\Image File Execution Options\\", StringSplitOptions.None)[1].Split('\\')[0];
            if (image.Equals("csrss.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("lsass.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("services.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("MsMpEng.exe", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(canonical, "imagen crítica de Windows");
        }
    }
    public void EnsureServiceAllowed(string name)
    {
        if (_document.Services.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException(name, "servicio esencial, de seguridad o protegido por el perfil Intel");
    }
    public void EnsureTaskAllowed(string path)
    {
        var normalized = "\\" + path.Trim().Trim('\\').Replace('/', '\\');
        if (_document.TaskPrefixes.Any(p => MatchesPrefix(normalized, "\\" + p.Trim('\\')))) throw new ProtectedTargetException(normalized, "tarea de seguridad/mantenimiento protegida");
    }
    public void EnsureBcdAllowed(string element)
    {
        if (_document.BcdDenyElements.Contains(element, StringComparer.OrdinalIgnoreCase) || !_document.BcdAllowedElements.Contains(element, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException(element, "elemento BCD no permitido por la política");
    }
    public void EnsureFileAllowed(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var key = PolicyPath(path);
        foreach (var prefix in _document.FilePrefixes)
        {
            var normalized = PolicyPath(prefix);
            if (key.Equals(normalized, StringComparison.OrdinalIgnoreCase) || key.StartsWith(normalized.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "ruta de sistema protegida");
            var expandedPrefix = Path.GetFullPath(Environment.ExpandEnvironmentVariables(prefix));
            if (full.Equals(expandedPrefix, StringComparison.OrdinalIgnoreCase) || full.StartsWith(expandedPrefix.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "ruta de sistema protegida");
        }
        var reduced = key.ToLowerInvariant();
        if (reduced.StartsWith("%systemroot%\\", StringComparison.OrdinalIgnoreCase) && !reduced.StartsWith("%systemroot%\\system32\\config\\systemprofile", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "solo se permiten archivos de configuración de usuario/juego, nunca rutas de Windows");
        if (full.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException(full, "FileEdit solo admite archivos de configuración de texto");
    }
    public void EnsurePowerShellAllowed(string script)
    {
        foreach (var token in _document.PowerShellDeniedTokens)
            if (script.Contains(token, StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException("PowerShell", $"token bloqueado '{token}'");
        if (script.Contains("http://", StringComparison.OrdinalIgnoreCase) || script.Contains("https://", StringComparison.OrdinalIgnoreCase)) throw new ProtectedTargetException("PowerShell", "acceso de red prohibido");
    }
    public void EnsureNetworkPropertyAllowed(string adapterName, string keyword)
    {
        if (_document.NetworkPropertyKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)) throw new ProtectedTargetException($"{adapterName}:{keyword}", "propiedad de red fuera del alcance seguro de Capa 0");
        if (keyword.Length is 0 or > 128) throw new ProtectedTargetException(keyword, "se requiere una palabra clave exacta");
        var stripped = keyword.StartsWith('*') ? keyword[1..] : keyword;
        if (stripped.Contains('*') || stripped.Contains('?') || stripped.Length == 0) throw new ProtectedTargetException(keyword, "solo se admite el prefijo '*' del controlador; sin otros comodines");
    }
    private static string PolicyPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path).Replace('/', '\\');
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows)?.Replace('/', '\\');
        if (!string.IsNullOrEmpty(systemRoot)) expanded = expanded.Replace(systemRoot.TrimEnd('\\'), "%SystemRoot%", StringComparison.OrdinalIgnoreCase);
        var marker = expanded.IndexOf("\\windows\\", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) expanded = "%SystemRoot%" + expanded[(marker + "\\windows".Length)..];
        return expanded.Replace("%SYSTEMROOT%", "%SystemRoot%", StringComparison.OrdinalIgnoreCase).Replace("\\", "\\");
    }
    private static string NormalizePrefix(string prefix) => RegistryPath.Canonicalize(Environment.ExpandEnvironmentVariables(prefix));
    private static bool MatchesPrefix(string value, string prefix) => value.Equals(prefix, StringComparison.OrdinalIgnoreCase) || value.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex(@"^HKLM\\SYSTEM\\(?:CurrentControlSet|ControlSet\d{3})\\SERVICES\\([^\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceKeyRegex();
}

public sealed record PolicyDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<string> Services { get; init; } = [];
    public List<string> RegistryKeyPrefixes { get; init; } = [];
    public List<string> RegistryValueNames { get; init; } = [];
    public List<string> TaskPrefixes { get; init; } = [];
    public List<string> BcdDenyElements { get; init; } = [];
    public List<string> BcdAllowedElements { get; init; } = ["disabledynamictick"];
    public List<string> FilePrefixes { get; init; } = [];
    public List<string> NetworkPropertyKeywords { get; init; } = [];
    public List<string> PowerShellDeniedTokens { get; init; } = [];
    public IEnumerable<string> ValueNames() => RegistryValueNames;
}
