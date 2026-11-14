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

    /// <summary>
    /// Qué se rechazó al cargar y por qué (Capa 10.4). Las entradas rechazadas NO están en el catálogo: se
    /// quedan fuera con su motivo visible, en vez de tumbar la carga entera.
    /// </summary>
    public CatalogLoadReport LoadReport { get; init; } = CatalogLoadReport.Empty;
}
public sealed class JsonTweakCatalogLoader(TweakCatalogValidator validator)
{
    public static string ReadEmbeddedSchema()
    {
        using var stream = typeof(JsonTweakCatalogLoader).Assembly.GetManifestResourceStream("FragForge.Tweaks.Schema.tweak.schema.json") ?? throw new InvalidDataException("Embedded tweak schema is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    /// <summary>
    /// Los documentos JSON incrustados en el binario, en orden estable. Se extrajo de
    /// <see cref="LoadEmbeddedAsync"/> para que la carga resiliente pueda saber qué ids ya están ocupados por
    /// el catálogo embebido sin duplicar la lectura de recursos.
    /// </summary>
    private static async Task<List<(string Name, string Json)>> ReadEmbeddedDocumentsAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(JsonTweakCatalogLoader).Assembly;
        var embedded = new List<(string Name, string Json)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(EmbeddedResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            embedded.Add((resource, await reader.ReadToEndAsync(cancellationToken)));
        }
        return embedded;
    }

    private const string EmbeddedResourcePrefix = "FragForge.Tweaks.Catalog.";

    /// <summary>
    /// Ids que ya ocupa el catálogo EMBEBIDO, con el nombre del recurso que los aporta. Se leen del binario,
    /// no de una lista paralela que se pudiera quedar desactualizada. Un documento ilegible se ignora aquí: su
    /// validación es asunto de la ruta estricta, que es la que debe lanzar si el catálogo de la entrega se rompe.
    /// </summary>
    private static Dictionary<string, string> EmbeddedIdsByResource()
    {
        var byResource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, json) in ReadEmbeddedDocumentsAsync(CancellationToken.None).GetAwaiter().GetResult())
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
                    byResource[name] = value;
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* la ruta estricta es la que valida */ }
        }
        return byResource;
    }

    /// <summary>
    /// Carga el catálogo embebido y, si se indica, el EXTERNO del usuario.
    ///
    /// <para>
    /// Los dos se validan por separado a propósito (Capa 10.4): un archivo externo roto no puede tumbar el
    /// catálogo del binario. Los embebidos pasan por la ruta ESTRICTA (un fallo ahí es un bug de la entrega y
    /// debe lanzar); los externos, por la RESILIENTE (la entrada mala se descarta con motivo visible y el resto
    /// carga). Antes se concatenaban y un solo JSON inválido del usuario abortaba TODO el catálogo.
    /// </para>
    /// </summary>
    public async Task<TweakCatalog> LoadEmbeddedAsync(string? additionalDirectory = null, CancellationToken cancellationToken = default)
    {
        var embedded = await ReadEmbeddedDocumentsAsync(cancellationToken);

        var external = new List<(string Name, string Json)>();
        if (additionalDirectory is not null && Directory.Exists(additionalDirectory))
            foreach (var file in Directory.GetFiles(additionalDirectory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                external.Add((file, await File.ReadAllTextAsync(file, cancellationToken)));

        // Sin externos no hay nada que pueda degradarse: se usa la ruta estricta y punto.
        if (external.Count == 0) return LoadDocuments(embedded);

        // Los embebidos se validan solos (estricto). Después se cargan embebidos + externos en modo resiliente:
        // los embebidos ya están validados como conjunto, así que lo único que puede caerse son los externos y,
        // como mucho, un externo que declare un ID que choque con uno embebido (y ese es justo el caso 10.4).
        _ = LoadDocuments(embedded);
        return LoadDocumentsResilient([.. embedded, .. external]);
    }
    /// <summary>
    /// Carga ESTRICTA de un conjunto de documentos: si algo no valida, se lanza y no hay catálogo.
    ///
    /// <para>
    /// Es el comportamiento que usan los tests de esquema y el que debe tener el catálogo EMBEBIDO: un
    /// catálogo del binario que no valida es un bug de la entrega y tiene que verse, no degradarse.
    /// </para>
    ///
    /// <para>Capa 10.4: para el catálogo EXTERNO del usuario está <see cref="LoadDocumentsResilient"/>, que
    /// descarta la entrada mala y conserva el resto.</para>
    /// </summary>
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

    /// <summary>
    /// Carga RESILIENTE (Capa 10.4): una entrada mala se queda fuera con su motivo y **el resto del catálogo
    /// carga**.
    ///
    /// <para>
    /// Esto es para el catálogo EXTERNO del usuario (<c>%ProgramData%\FragForge\catalog</c>). Un archivo con
    /// JSON roto, un ID que choca con uno embebido o un tweak que apunta a un destino protegido por la denylist
    /// no puede dejar la app sin arrancar: se rechaza con <see cref="CatalogRejection"/> y el motivo se muestra.
    /// </para>
    ///
    /// <para>
    /// El rechazo por denylist ocurre AL CARGAR, no al aplicar. <see cref="TweakActionFactory"/> sigue siendo
    /// la última línea: esto adelanta el aviso, no relaja la política.
    /// </para>
    /// </summary>
    public TweakCatalog LoadDocumentsResilient(IReadOnlyList<(string Name, string Json)> documents)
    {
        var accepted = new List<TweakDefinition>();
        var rejected = new List<CatalogRejection>();
        // El conjunto de ids arranca con los del catálogo EMBEBIDO, no vacío (Capa 10.4,
        // docs/seguridad.md:87 "Un id que choque con uno embebido se rechaza" y :179 "la primera definición
        // gana"). Antes solo se reservaban los ids que iban llegando en `documents`: por la ruta de la app
        // (LoadEmbeddedAsync) los embebidos van DELANTE y quedaban reservados de rebote, pero llamada con solo
        // externos —que es justo como la ejercita el test— no había nada reservado, así que un externo con el id
        // de un tweak auditado se ACEPTABA en vez de rechazarse.
        //
        // Se excluye el embebido cuyo documento ya viene en `documents`: en la ruta de la app los embebidos
        // están en la lista y son ellos los que reservan su id al pasar. Si se reservaran aquí, se rechazarían
        // a sí mismos. En las dos situaciones la regla es la misma: el PRIMERO que llega se queda el id, y un
        // externo que lo repita se cae.
        var present = documents.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(
            EmbeddedIdsByResource().Where(pair => !present.Contains(pair.Key)).Select(pair => pair.Value),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (name, json) in documents)
        {
            TweakDefinition definition;
            try
            {
                using var document = JsonDocument.Parse(json);
                var structural = validator.ValidateStructure(document.RootElement);
                if (structural.Count > 0)
                {
                    rejected.AddRange(structural.Select(e => new CatalogRejection(name, null, e)));
                    continue;
                }
                definition = JsonSerializer.Deserialize<TweakDefinition>(json, JsonDefaults.Options) ?? throw new JsonException("Documento nulo.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or CatalogValidationException)
            {
                rejected.Add(new CatalogRejection(name, null, ex.Message));
                continue;
            }

            // El id se reserva ANTES de validar: un duplicado se descarta aunque lo demás sea correcto, y la
            // primera definición gana. Así el usuario nunca ve dos tweaks distintos con el mismo identificador.
            if (!ids.Add(definition.Id))
            {
                rejected.Add(new CatalogRejection(name, definition.Id, $"ID duplicado: '{definition.Id}' ya existe en el catálogo. Se conserva la primera definición."));
                continue;
            }

            var problems = validator.ValidateSingle(definition).Concat(validator.ValidatePolicy(definition)).ToArray();
            if (problems.Length > 0)
            {
                rejected.AddRange(problems.Select(e => new CatalogRejection(name, definition.Id, e)));
                continue;
            }
            accepted.Add(definition);
        }

        // Dependencias y conflictos se comprueban contra lo que SÍ entró, y se vuelve a filtrar hasta que el
        // conjunto se estabiliza: si A depende de B y B se cae por depender de C (rechazada), A también se cae
        // y se dice por qué.
        var final = accepted;
        while (true)
        {
            var next = new List<TweakDefinition>();
            var removedAny = false;
            foreach (var definition in final)
            {
                var errors = validator.ValidateReferences(definition, final);
                if (errors.Count > 0)
                {
                    rejected.AddRange(errors.Select(e => new CatalogRejection(definition.Id + ".json", definition.Id, e)));
                    removedAny = true;
                    continue;
                }
                next.Add(definition);
            }
            final = next;
            if (!removedAny || final.Count == 0) break;
        }

        return new TweakCatalog(final) { LoadReport = new CatalogLoadReport(rejected) };
    }
}


/// <summary>Una entrada del catálogo que se descartó y por qué (Capa 10.4). Se muestra; no se oculta.</summary>
public sealed record CatalogRejection(string Name, string? Id, string Reason);

/// <summary>Resultado de la carga: qué se rechazó, con motivo, para poder enseñarlo.</summary>
public sealed record CatalogLoadReport(IReadOnlyList<CatalogRejection> Rejected)
{
    public static CatalogLoadReport Empty { get; } = new([]);
    public bool HasRejections => Rejected.Count > 0;
}
