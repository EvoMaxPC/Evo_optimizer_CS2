using System.Text.Json.Serialization;

namespace FragForge.Core.Experiments;

/// <summary>Veredicto de validez de una captura de frames.</summary>
public enum CaptureValidity
{
    /// <summary>La captura sirve como evidencia.</summary>
    Valid,
    /// <summary>Sirve, pero hay señales de que no es representativa. Se guarda y se informa; no bloquea.</summary>
    Suspect,
    /// <summary>No sirve: no hay encabezado obligatorio o no hay ninguna fila. Nunca entra en un veredicto.</summary>
    Invalid
}

/// <summary>
/// Puerta de validez de una corrida. Campo ADITIVO del resultado (R-B): un JSON anterior sin este campo
/// se deserializa como <see cref="CaptureValidity.Valid"/> + lista vacía, que es el comportamiento previo.
/// </summary>
public sealed record CaptureValidityReport
{
    /// <summary>
    /// Umbrales de la heurística. No son verdades físicas: son cotas para detectar capturas que no
    /// representan una partida real. Documentados aquí a propósito para que se puedan discutir.
    /// </summary>
    /// <summary>Por debajo de esta mediana de FPS la captura se marca sospechosa.</summary>
    public const double SuspectMedianFpsBelow = 30.0;
    /// <summary>Fracción mínima de frames capturados respecto de lo esperado por duración × refresco.</summary>
    public const double SuspectFrameRatioBelow = 0.5;

    public CaptureValidity Validity { get; init; } = CaptureValidity.Valid;
    /// <summary>Motivos legibles. Para <see cref="CaptureValidity.Invalid"/> explica qué faltó; para Suspect, qué se observó.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    [JsonIgnore] public bool IsUsable => Validity != CaptureValidity.Invalid;
    [JsonIgnore] public bool IsSuspect => Validity == CaptureValidity.Suspect;

    public static CaptureValidityReport Ok() => new();

    public static CaptureValidityReport Invalid(params string[] reasons) =>
        new() { Validity = CaptureValidity.Invalid, Reasons = reasons };

    public static CaptureValidityReport Suspect(params string[] reasons) =>
        new() { Validity = CaptureValidity.Suspect, Reasons = reasons };
}

/// <summary>
/// Evalúa si una captura es válida, sospechosa o inválida a partir de la serie ya parseada.
///
/// Reglas (todas son heurísticas salvo las dos de Invalid, que son duras):
/// <list type="bullet">
/// <item><b>Invalid</b>: sin encabezado obligatorio (la serie no tiene frametimes por falta de columna) o cero filas.</item>
/// <item><b>Suspect</b>: mediana de FPS &lt; 30; todas las filas PresentMode=Other o todas las columnas de display NA
/// ("sin seguimiento de display"); GPU no disponible; más de una swapchain; frames &lt; 50 % de lo esperado.</item>
/// </list>
/// Una captura Empty pero con encabezado correcto (CSV sólo cabecera) es Invalid, no Suspect: no hay evidencia.
/// </summary>
public static class CaptureValidityGate
{
    public static CaptureValidityReport Evaluate(FrameSeries series, TimeSpan? requestedDuration = null, int? refreshHz = null)
    {
        if (series is null) return CaptureValidityReport.Invalid("serie nula");

        // --- Invalid (duro) ---
        if (series.FrameCount == 0)
        {
            if (!string.IsNullOrWhiteSpace(series.SourceColumns) && series.SourceColumns.StartsWith("missing-frame-time", StringComparison.Ordinal))
                return CaptureValidityReport.Invalid(
                    $"Falta la columna obligatoria {RequiredFrameTimeColumn}. Columnas encontradas: {string.Join(",", series.HeaderColumns)}");
            return CaptureValidityReport.Invalid("La captura no tiene ninguna fila de frames.");
        }

        var reasons = new List<string>();

        // --- Suspect (heurístico) ---
        var positive = series.FrameTimesMs.Where(v => v > 0).ToArray();
        var medianFrameTime = positive.Length == 0 ? 0 : Statistics.Median(positive);
        var medianFps = medianFrameTime > 0 ? 1000.0 / medianFrameTime : (double?)null;
        if (medianFps is not null && medianFps < CaptureValidityReport.SuspectMedianFpsBelow)
            reasons.Add($"mediana de FPS {medianFps.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} < {CaptureValidityReport.SuspectMedianFpsBelow.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}");

        if (series.PresentModes.Count > 0 && series.PresentModes.All(mode => mode.Equals("Other", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("todas las filas PresentMode=Other");

        var displayAllNa = series.DisplayChangeMs.Count > 0 && series.DisplayChangeMs.All(v => v is null)
            && series.UntilDisplayedMs.Count > 0 && series.UntilDisplayedMs.All(v => v is null);
        if (displayAllNa) reasons.Add("sin seguimiento de display (MsBetweenDisplayChange y MsUntilDisplayed en NA)");

        if (series.GpuUnavailable) reasons.Add("GPU no disponible (todas las filas de MsGPUBusy son 0 o NA)");

        if (series.Warnings.Any(w => w.StartsWith("varias swapchains", StringComparison.Ordinal)))
            reasons.Add("más de una swapchain en la captura");

        if (requestedDuration is { } duration && duration > TimeSpan.Zero && refreshHz is > 0)
        {
            var framesPerSecond = refreshHz!.Value;
            var expected = duration.TotalSeconds * framesPerSecond;
            if (expected > 0 && series.FrameCount < expected * CaptureValidityReport.SuspectFrameRatioBelow)
                reasons.Add($"solo {series.FrameCount} frames de ~{expected.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} esperados");
        }

        return reasons.Count == 0
            ? CaptureValidityReport.Ok()
            : CaptureValidityReport.Suspect([.. reasons]);
    }

    /// <summary>
    /// Columna obligatoria. El literal se repite aquí a propósito: el parser vive en Actions y Core no puede
    /// referenciarlo. Está verificado contra PresentMon 2.6.0.
    /// </summary>
    public const string RequiredFrameTimeColumn = "MsBetweenPresents";
}
