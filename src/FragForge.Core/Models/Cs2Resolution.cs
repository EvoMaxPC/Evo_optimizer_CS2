namespace FragForge.Core.Models;

/// <summary>
/// Resultado de resolver cs2.exe por la vía de Steam (Capa 8.2).
///
/// Es explícito sobre POR QUÉ no hay ruta: un <c>null</c> seco obligaría al llamador a adivinar entre
/// "no hay Steam", "no hay librería", "el manifiesto no está" y "el ejecutable no existe". Con esto, el
/// tweak puede decir <c>Unknown</c> con motivo (R-L) y quedar No aplicable sin inventar una ruta.
/// </summary>
public sealed record Cs2Resolution
{
    /// <summary>Ruta completa del ejecutable, o <c>null</c> si no se pudo resolver con certeza.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Cómo se llegó a la respuesta. Se escribe siempre, también cuando falla: es el texto que se muestra
    /// en el informe y en el detalle del tweak.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>Carpetas de Steam y bibliotecas que se inspeccionaron, en orden. Diagnóstico.</summary>
    public IReadOnlyList<string> ProbedPaths { get; init; } = [];

    /// <summary>
    /// Rutas candidatas válidas encontradas. Más de una = ambiguo: se devuelve <c>null</c> arriba y se
    /// exige selección manual, en vez de elegir la primera por orden alfabético.
    /// </summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];

    public bool Found => ExecutablePath is not null;
    public bool Ambiguous => Candidates.Count > 1;
}
