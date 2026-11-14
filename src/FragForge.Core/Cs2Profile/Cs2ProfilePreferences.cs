using System.Text.Json.Serialization;

namespace FragForge.Core.Cs2Profile;

/// <summary>Qué busca el usuario. Cambia el punto de partida de los valores, no promete resultados.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Cs2ProfileObjective>))]
public enum Cs2ProfileObjective
{
    /// <summary>Menos trabajo en cola antes de mostrar cada imagen.</summary>
    LatenciaMinima,

    /// <summary>Frametimes más parecidos entre sí, a costa de latencia.</summary>
    FrametimeEstable,

    /// <summary>Sin sesgo declarado hacia latencia ni hacia estabilidad.</summary>
    Equilibrado
}

/// <summary>Tope de FPS. Es un tipo cerrado: no hay "más o menos" un número inventado.</summary>
public enum FpsCapMode { SinTope, Fijo, DerivadoDelRefresco }

/// <summary>
/// Tope de FPS. Con <see cref="FpsCapMode.DerivadoDelRefresco"/> el número sale del refresco MEDIDO; si no
/// se pudo medir, no se inventa uno (R-L): el generador dará una advertencia y no emitirá fps_max.
/// </summary>
public sealed record FpsCap
{
    public FpsCapMode Mode { get; init; } = FpsCapMode.SinTope;

    /// <summary>Valor exacto cuando <see cref="Mode"/> es Fijo. Null en los demás modos.</summary>
    public int? FixedFps { get; init; }

    /// <summary>Factor sobre el refresco cuando el modo es DerivadoDelRefresco. 1.0 = igual al refresco.</summary>
    public double? RefreshFactor { get; init; }

    public static FpsCap None => new() { Mode = FpsCapMode.SinTope };
    public static FpsCap Fixed(int fps) => new() { Mode = FpsCapMode.Fijo, FixedFps = fps };
    public static FpsCap DerivedFromRefresh(double factor) => new() { Mode = FpsCapMode.DerivadoDelRefresco, RefreshFactor = factor };
}

/// <summary>Una opción tildada, con su valor editable. <see cref="Value"/> null = usar el default del catálogo.</summary>
public sealed record SelectedOption
{
    public string Id { get; init; } = "";
    public string? Value { get; init; }
}

/// <summary>
/// Preferencias del usuario. Todos los campos tienen valor por defecto: un JSON viejo sin un campo nuevo
/// sigue cargando (campos aditivos).
/// </summary>
public sealed record Cs2ProfilePreferences
{
    /// <summary>Versión del formato del perfil importable/exportable.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public Cs2ProfileObjective Objective { get; init; } = Cs2ProfileObjective.Equilibrado;

    /// <summary>Opciones tildadas, con su valor. El orden no importa; el generador ordena por el catálogo.</summary>
    public IReadOnlyList<SelectedOption> Options { get; init; } = [];

    public FpsCap FpsCap { get; init; } = FpsCap.None;

    /// <summary>Pide VSync explícitamente. Null = no se toca (no se emite mat_vsync).</summary>
    public bool? VSync { get; init; }

    /// <summary>Pide dormir el motor fuera de foco. Null = no se toca.</summary>
    public bool? SleepWhenUnfocused { get; init; }

    /// <summary>
    /// Por defecto FALSE: las opciones <see cref="Cs2Evidence.Unverified"/> vienen destildadas. Se incluyen
    /// solo si el usuario lo pide de forma explícita.
    /// </summary>
    public bool IncludeUnverified { get; init; }

    /// <summary>Refresco medido en Hz, o null si no se pudo medir. Null NO se sustituye por 60.</summary>
    public int? MeasuredRefreshHz { get; init; }

    public static Cs2ProfilePreferences Empty => new();
}
