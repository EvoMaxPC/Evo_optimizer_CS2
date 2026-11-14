using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Myths;

public enum MythVerdict
{
    /// <summary>Falso: no hay mecanismo plausible y/o está medido en contra.</summary>
    False,
    /// <summary>Parcialmente cierto en otras condiciones, irrelevante o contraproducente aquí.</summary>
    Misleading,
    /// <summary>Sin evidencia suficiente para afirmarlo ni negarlo.</summary>
    Unproven
}

public sealed record MythEntry
{
    public required string Id { get; init; }
    public required LocalizedText Title { get; init; }
    public required MythVerdict Verdict { get; init; }
    public required LocalizedText Why { get; init; }
    public required string Reference { get; init; }
    public LocalizedText? WhatHelpsInstead { get; init; }
}

public sealed record MythDocument
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<MythEntry> Myths { get; init; } = [];
}

/// <summary>
/// Catálogo de mitos embebido. Se valida al cargar: sin referencia, sin los dos idiomas o con un
/// veredicto ilegible, la carga falla entera. La referencia es OBLIGATORIA: un mito sin fuente es
/// otra opinión más.
/// </summary>
public sealed class MythCatalog
{
    private readonly IReadOnlyList<MythEntry> _entries;
    private MythCatalog(IReadOnlyList<MythEntry> entries) => _entries = entries;

    public IReadOnlyList<MythEntry> Entries => _entries;
    public MythEntry? Find(string id) => _entries.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static MythCatalog LoadEmbedded()
    {
        using var stream = typeof(MythCatalog).Assembly.GetManifestResourceStream("FragForge.Core.Myths.myths.json")
            ?? throw new InvalidDataException("Falta el recurso embebido myths.json.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static MythCatalog Parse(string json)
    {
        MythDocument? document;
        try { document = JsonSerializer.Deserialize<MythDocument>(json, JsonDefaults.Options); }
        catch (JsonException ex) { throw new InvalidDataException("myths.json no es JSON válido: " + ex.Message, ex); }
        if (document is null) throw new InvalidDataException("myths.json está vacío.");
        if (document.SchemaVersion != 1) throw new InvalidDataException($"schemaVersion {document.SchemaVersion} no soportada.");

        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var myth in document.Myths)
        {
            if (string.IsNullOrWhiteSpace(myth.Id)) { errors.Add("Mito sin id."); continue; }
            if (!ids.Add(myth.Id)) errors.Add($"{myth.Id}: duplicado.");
            if (string.IsNullOrWhiteSpace(myth.Title.EsMx) || string.IsNullOrWhiteSpace(myth.Title.EnUs)) errors.Add($"{myth.Id}: título sin los dos idiomas.");
            if (string.IsNullOrWhiteSpace(myth.Why.EsMx) || string.IsNullOrWhiteSpace(myth.Why.EnUs)) errors.Add($"{myth.Id}: explicación sin los dos idiomas.");
            if (string.IsNullOrWhiteSpace(myth.Reference) || !myth.Reference.StartsWith("http", StringComparison.OrdinalIgnoreCase)) errors.Add($"{myth.Id}: falta una referencia verificable.");
            if (!Enum.IsDefined(myth.Verdict)) errors.Add($"{myth.Id}: veredicto ilegible.");
        }
        if (errors.Count > 0) throw new InvalidDataException("myths.json inválido: " + string.Join(" | ", errors));
        return new MythCatalog(document.Myths.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
