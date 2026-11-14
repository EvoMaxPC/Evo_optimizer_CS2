using System.Security.Cryptography;
using FragForge.Core.Logging;

namespace FragForge.Actions.Measurement;

/// <summary>Entrada de la lista blanca de herramientas externas.</summary>
public sealed record AllowedTool
{
    public required string Key { get; init; }
    public required string FileName { get; init; }
    /// <summary>SHA-256 esperado en hexadecimal minúsculas. VACÍO ⇒ la herramienta no está aprobada todavía.</summary>
    public string? Sha256 { get; init; }
    public string? SourceUrl { get; init; }
    public string? License { get; init; }
    public string? Note { get; init; }
}

public sealed record ToolAllowlist
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<AllowedTool> Tools { get; init; } = [];
    public AllowedTool? Find(string key) => Tools.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

public enum ToolStatus { Approved, Missing, HashPlaceholder, HashMismatch, NotListed }

public sealed record ToolVerification(ToolStatus Status, string ToolKey, string? Path, string? ActualSha256, string? ExpectedSha256, string Message)
{
    public bool IsUsable => Status == ToolStatus.Approved;
}

/// <summary>
/// Verifica herramientas externas contra un manifiesto embebido.
///
/// REGLA: no se inventan hashes. El manifiesto se entrega con <c>sha256 = null</c> y la verificación
/// FALLA con un mensaje explícito hasta que el usuario añada el hash del binario oficial. FragForge
/// NUNCA descarga nada: si el archivo no está en <c>%ProgramData%\FragForge\tools\</c>, no hay herramienta.
/// </summary>
public sealed class ToolVerifier(string toolsDirectory, ToolAllowlist allowlist)
{
    public const string PresentMonKey = "presentmon";

    public ToolVerification Verify(string toolKey)
    {
        var tool = allowlist.Find(toolKey);
        if (tool is null) return new ToolVerification(ToolStatus.NotListed, toolKey, null, null, null, $"La herramienta '{toolKey}' no está en la lista blanca embebida.");
        var path = Path.Combine(toolsDirectory, tool.FileName);
        if (!File.Exists(path)) return new ToolVerification(ToolStatus.Missing, toolKey, path, null, tool.Sha256, $"Falta {tool.FileName} en {toolsDirectory}. Descárgalo manualmente desde el sitio oficial; FragForge no descarga binarios.");
        var actual = ComputeSha256(path);
        if (string.IsNullOrWhiteSpace(tool.Sha256))
            return new ToolVerification(ToolStatus.HashPlaceholder, toolKey, path, actual, null, $"No hay hash aprobado para '{toolKey}'. Añade el SHA-256 oficial ({actual}) a presentmon-allowlist.json antes de usarla. Mientras tanto la captura está deshabilitada.");
        if (!string.Equals(actual, tool.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            return new ToolVerification(ToolStatus.HashMismatch, toolKey, path, actual, tool.Sha256, $"SHA-256 no coincide para '{toolKey}'. Binario sospechoso: no se ejecuta. Esperado {tool.Sha256}, obtenido {actual}.");
        return new ToolVerification(ToolStatus.Approved, toolKey, path, actual, tool.Sha256, $"'{toolKey}' verificado.");
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Manifiesto embebido desde <c>FragForge.Actions.Measurement.presentmon-allowlist.json</c>.</summary>
    public static ToolAllowlist LoadEmbedded()
    {
        using var stream = typeof(ToolVerifier).Assembly.GetManifestResourceStream("FragForge.Actions.Measurement.presentmon-allowlist.json")
            ?? throw new InvalidDataException("Falta el recurso embebido presentmon-allowlist.json.");
        using var reader = new StreamReader(stream);
        return System.Text.Json.JsonSerializer.Deserialize<ToolAllowlist>(reader.ReadToEnd(), JsonDefaults.Options)
            ?? throw new InvalidDataException("El manifiesto de herramientas está vacío.");
    }
}
