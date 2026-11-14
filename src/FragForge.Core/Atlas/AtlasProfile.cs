using FragForge.Core.Models;

namespace FragForge.Core.Atlas;

/// <summary>Un marcador concreto: se cumple, no se cumple, o no se pudo leer. Nunca se asume.</summary>
public sealed record AtlasMarker(string Name, string Description, bool? Detected, string Evidence)
{
    /// <summary>Marcador que por sí solo sostiene la detección (organización o carpeta de datos).</summary>
    public bool IsStrong => Detected == true && (Name.StartsWith("organization", StringComparison.Ordinal) || Name.StartsWith("folder", StringComparison.Ordinal));
}

/// <summary>Certeza de la detección. Sin firma verificable de Atlas, nunca se pasa de Medium.</summary>
public enum Confidence { None, Low, Medium, High }

/// <summary>
/// Perfil de Atlas construido SOLO con marcadores que se pueden verificar en el sistema.
///
/// La regla de honestidad: los servicios parados NO son un marcador. Cualquier usuario puede haber
/// parado SysMain o DiagTrack por su cuenta, así que ese indicio se guarda como evidencia
/// <c>Detected = null</c> (desconocido) y nunca cuenta como detección.
///
/// Con un solo marcador fuerte y todos los leídos, el máximo es <see cref="Confidence.Medium"/>:
/// no hay forma local de distinguir "Atlas estuvo aquí" de "alguien puso este valor a mano".
/// </summary>
public sealed record AtlasProfile
{
    public required Confidence Confidence { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<AtlasMarker> Markers { get; init; }

    /// <summary>Convención del perfil de Atlas: ejecutarse desde una carpeta con este nombre.</summary>
    public static string ExpectedFolderName => "Atlas";

    /// <summary>Hay al menos un marcador fuerte y ningún marcador ilegible contradiciéndolo.</summary>
    public bool Detected => Confidence >= Confidence.Low;

    /// <summary>
    /// Clasificación conservadora: si algo no se pudo leer, como máximo Low. Si nada fuerte se
    /// cumplió, None. Nunca High: no hay firma criptográfica en juego.
    /// </summary>
    public static Confidence Classify(int strongDetected, int strongTotal, int unknownTotal) =>
        // Sin ningún marcador FUERTE no hay detección, aunque haya marcadores ilegibles: "no pude leer
        // esto" nunca equivale a "esto es Atlas".
        strongDetected == 0 ? Confidence.None
        : unknownTotal > 0 || strongDetected < strongTotal ? Confidence.Low
        : Confidence.Medium;

    public static AtlasProfile FromMarkers(IReadOnlyList<AtlasMarker> markers, string summary)
    {
        var strong = markers.Where(m => m.Name.StartsWith("organization", StringComparison.Ordinal) || m.Name.StartsWith("folder", StringComparison.Ordinal)).ToArray();
        var unknown = markers.Count(m => m.Detected is null);
        var confidence = Classify(strong.Count(m => m.Detected == true), strong.Length, unknown);
        return new AtlasProfile { Confidence = confidence, Summary = summary, Markers = markers };
    }

    /// <summary>
    /// Deriva el perfil de los marcadores ya recogidos por el sondeo de hardware. Un playbook sin
    /// evidencia no es un marcador: no se rellena con falsos positivos.
    /// </summary>
    public static AtlasProfile FromHardware(HardwareProfile profile)
    {
        var playbooks = profile.Playbooks;
        var markers = new List<AtlasMarker>();
        foreach (var playbook in playbooks)
        {
            foreach (var evidence in playbook.Evidence)
            {
                var (name, description, detected) = Classify(evidence);
                markers.Add(new AtlasMarker(name, description, detected, evidence));
            }
        }
        return FromMarkers(markers, markers.Count == 0
            ? "Sin marcadores de Atlas verificables: el perfil no se aplica."
            : "Detección por marcadores locales. La coincidencia no demuestra que Atlas siga instalado.");
    }

    private static (string Name, string Description, bool? Detected) Classify(string evidence)
    {
        if (evidence.StartsWith("RegisteredOrganization", StringComparison.OrdinalIgnoreCase))
            return ("organization.registered", "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\RegisteredOrganization contiene 'Atlas'.", true);
        if (evidence.StartsWith("Organization", StringComparison.OrdinalIgnoreCase))
            return ("organization.owner", "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Organization contiene 'Atlas'.", true);
        if (evidence.StartsWith("ProgramDataAtlas", StringComparison.OrdinalIgnoreCase))
            return ("folder.programdata", "Existe %ProgramData%\\Atlas.", true);
        if (evidence.StartsWith("Servicios no corriendo", StringComparison.OrdinalIgnoreCase))
            return ("services.stopped", "Servicios parados: NO es concluyente, cualquier usuario puede haberlos parado.", null);
        return ("other", "Indicio del sondeo sin clasificar.", null);
    }
}
