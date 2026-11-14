using System.Text.Json;
using System.Text.Json.Serialization;
using FragForge.Core.Logging;

namespace FragForge.Core.Sessions;

/// <summary>Estado visible del modo sesión (Capa 9).</summary>
public enum GameSessionState
{
    /// <summary>No hay sesión: no existe marcador.</summary>
    Inactive,
    /// <summary>Sesión creada, esperando a que arranque cs2.exe.</summary>
    WaitingForGame,
    /// <summary>Sesión aplicada y con el juego en ejecución.</summary>
    Active,
    /// <summary>Se está revirtiendo (o se intentó y falló: se ve en <see cref="GameSessionStatus.Error"/>).</summary>
    Reverting
}

/// <summary>
/// Marcador persistido de la sesión activa: <c>%ProgramData%\FragForge\session.json</c>.
///
/// Es lo único que permite RECUPERAR tras una caída de la app: si la app muere con los tweaks aplicados,
/// al volver a arrancar lee este archivo y revierte. Por eso se escribe de forma atómica
/// (<see cref="Persistence.AtomicFile"/>) y se borra solo cuando el revert ya se hizo.
///
/// También se guarda el <c>SnapshotId</c>: revertir por snapshot es lo que hace que el revert sea el mismo
/// que el de cualquier otra operación, no una segunda implementación que pueda divergir.
///
/// R-N: el esquema es ADITIVO. Se puede añadir un campo al final; nunca se renombra ni se cambia el
/// significado de los existentes, porque un marcador viejo tiene que seguir siendo legible.
/// </summary>
public sealed record GameSessionMarker
{
    /// <summary>Versión del marcador, para poder leer marcadores antiguos.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Id de la operación de apply que creó la sesión.</summary>
    public required string OperationId { get; init; }

    /// <summary>Snapshot del apply: es lo que se revierte. <c>null</c> solo si el apply no lo creó.</summary>
    public string? SnapshotId { get; init; }

    /// <summary>Tweaks que se aplicaron en esta sesión, en orden.</summary>
    public IReadOnlyList<string> TweakIds { get; init; } = [];

    /// <summary>Cuándo empezó la sesión (UTC).</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Perfil con el que se aplicó. Se guarda para poder reportar con qué se revirtió, no para reaplicar.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Último estado conocido. <c>true</c> cuando el revert terminó bien pero el borrado del marcador
    /// falló: así el siguiente arranque sabe que no hay nada que revertir (idempotencia, 9.3).
    /// </summary>
    public bool Reverted { get; init; }
}

/// <summary>Estado calculado del modo sesión, listo para mostrar en CLI o UI.</summary>
public sealed record GameSessionStatus
{
    public GameSessionState State { get; init; }
    public GameSessionMarker? Marker { get; init; }
    /// <summary>Mensaje para el usuario. Nunca vacío salvo <see cref="GameSessionState.Inactive"/>.</summary>
    public string? Message { get; init; }
    /// <summary>Error del último revert, si lo hubo. Con esto presente el marcador NO se borra.</summary>
    public string? Error { get; init; }
}

/// <summary>Resultado de <c>Start</c>: o hay marcador, o hay un motivo.</summary>
public sealed record GameSessionStartResult
{
    public bool Started { get; init; }
    public GameSessionMarker? Marker { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>Resultado de <c>Stop</c>.</summary>
public sealed record GameSessionStopResult
{
    public bool Reverted { get; init; }
    /// <summary><c>true</c> si no había nada que hacer: ya estaba revertido y el marcador se limpió.</summary>
    public bool AlreadyReverted { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Rutas del modo sesión. Se calcula sobre <see cref="Persistence.FragForgePaths"/> para respetar
/// <c>--data-root</c>: los tests usan una carpeta temporal y nunca tocan <c>%ProgramData%</c>.
/// </summary>
public static class GameSessionPaths
{
    /// <summary><c>&lt;root&gt;\session.json</c>.</summary>
    public static string MarkerFile(Persistence.FragForgePaths paths) => Path.Combine(paths.Root, "session.json");

    /// <summary>
    /// Opciones de serialización del marcador. Propias (no <see cref="JsonDefaults"/>) porque el marcador
    /// puede evolucionar: se ignoran campos desconocidos para que un marcador de una versión nueva no
    /// rompa la recuperación de una vieja (R-N, aditivo).
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };
}
