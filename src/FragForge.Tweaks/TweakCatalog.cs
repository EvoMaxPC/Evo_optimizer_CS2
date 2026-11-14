using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Tweaks.Validation;

namespace FragForge.Tweaks;

public sealed class TweakCatalog : ITweakCatalog
{
    private readonly IReadOnlyDictionary<string, TweakDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, string> _hashes;
    public IReadOnlyList<TweakDefinition> Definitions { get; }
    public TweakCatalog(IReadOnlyList<TweakDefinition> definitions)
    {
        Definitions = definitions.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        _definitions = Definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        _hashes = Definitions.ToDictionary(d => d.Id, d => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d, JsonDefaults.Create(false))))).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
    }
    public TweakDefinition Get(string id) => _definitions.TryGetValue(id, out var definition) ? definition : throw new KeyNotFoundException($"Tweak desconocido: {id}");
    public bool TryGet(string id, out TweakDefinition? definition) => _definitions.TryGetValue(id, out definition);
    public string GetDefinitionHash(string id) => _hashes[id];
}
public sealed class JsonTweakCatalogLoader(TweakCatalogValidator validator)
{
    public static string ReadEmbeddedSchema()
    {
        using var stream = typeof(JsonTweakCatalogLoader).Assembly.GetManifestResourceStream("FragForge.Tweaks.Schema.tweak.schema.json") ?? throw new InvalidDataException("Embedded tweak schema is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public async Task<TweakCatalog> LoadEmbeddedAsync(string? additionalDirectory = null, CancellationToken cancellationToken = default)
    {
        var assembly = typeof(JsonTweakCatalogLoader).Assembly;
        var documents = new List<(string Name, string Json)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("FragForge.Tweaks.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            documents.Add((resource, await reader.ReadToEndAsync(cancellationToken)));
        }
        if (additionalDirectory is not null && Directory.Exists(additionalDirectory))
            foreach (var file in Directory.GetFiles(additionalDirectory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)) documents.Add((file, await File.ReadAllTextAsync(file, cancellationToken)));
        return LoadDocuments(documents);
    }
    public TweakCatalog LoadDocuments(IReadOnlyList<(string Name, string Json)> documents)
    {
        var definitions = new List<TweakDefinition>();
        var errors = new List<string>();
        foreach (var (name, json) in documents)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var structural = validator.ValidateStructure(document.RootElement);
                if (structural.Count > 0) { errors.AddRange(structural.Select(e => $"{name}: {e}")); continue; }
                var definition = JsonSerializer.Deserialize<TweakDefinition>(json, JsonDefaults.Options) ?? throw new JsonException("Documento nulo.");
                definitions.Add(definition);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or CatalogValidationException) { errors.Add($"{name}: {ex.Message}"); }
        }
        errors.AddRange(validator.ValidateCatalog(definitions));
        if (errors.Count > 0) throw new CatalogValidationException("El catálogo no pasó la validación de esquema/semántica.", errors);
        return new TweakCatalog(definitions);
    }
}
