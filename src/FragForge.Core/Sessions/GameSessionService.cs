using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Core.Sessions;

/// <summary>
/// Modo sesión (Capa 9): aplica un conjunto reversible de tweaks mientras se juega y lo revierte al cerrar
/// CS2, **incluso si la app cae**.
///
/// Diseño, y por qué:
/// <list type="bullet">
/// <item>Todo pasa por <see cref="ITweakEngine"/>: no hay una segunda implementación de apply/revert. El
///       revert es <c>RevertAsync</c> por el snapshot que creó el apply, igual que cualquier otra reversión.</item>
/// <item>El marcador en disco es la memoria que sobrevive a una caída. Sin él, "revertir al cerrar el
///       juego" solo funciona mientras el proceso vive.</item>
/// <item>La vigilancia es POLLING cada 2 s (9.2), no eventos ni WMI: no abre handles a cs2.exe y no puede
///       bloquearse por un proceso protegido por el anticheat.</item>
/// <item>Todo lo que espera recibe un <see cref="CancellationToken"/>: no hay hilos que sobrevivan al
///       servicio.</item>
/// </list>
///
/// Lo que NO hace (9.5): no mata ni prioriza procesos, no abre handles a cs2.exe, no toca Steam y no crea
/// servicios de Windows.
/// </summary>
public sealed class GameSessionService
{
    /// <summary>Intervalo de vigilancia pedido en 9.2.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ITweakEngine _engine;
    private readonly ITweakCatalog _catalog;
    private readonly FragForgePaths _paths;

    public GameSessionService(ITweakEngine engine, ITweakCatalog catalog, FragForgePaths paths)
    {
        _engine = engine;
        _catalog = catalog;
        _paths = paths;
    }

    /// <summary>Archivo del marcador. Expuesto para el CLI y para los tests.</summary>
    public string MarkerPath => GameSessionPaths.MarkerFile(_paths);

    /// <summary>
    /// Tweaks que pueden aplicarse en una sesión AHORA: los marcados <c>sessionEligible</c>, presentes en
    /// el perfil pedido, y que además NO requieran reinicio.
    ///
    /// Lo de <c>requiresReboot</c> es una segunda barrera a propósito: aunque un JSON marque por error
    /// <c>sessionEligible: true</c> en un tweak que necesita reinicio, aquí queda fuera. Un tweak que
    /// necesita reinicio no se puede revertir "al cerrar el juego".
    /// </summary>
    public IReadOnlyList<string> EligibleIds(TweakProfile profile) =>
        Engine.ProfileSelector.Select(_catalog.Definitions, profile)
            // Capa 5.3: un tweak que reinicia el enlace de red NUNCA entra en una sesión. Aunque alguien
            // marque sessionEligible por error, cortar la conexión en mitad de una partida no es admisible.
            //
            // R-J: y tampoco entra ninguno que necesite recuperación FUERA DE BANDA. Se usa el MISMO
            // predicado que el preflight (PreflightService.NeedsOutOfBandRecovery), no una copia: con
            // `!RestartsNetworkLink` a secas, un Bcdedit o un NetAdapterProperty que no declarara el flag
            // entraría en la sesión y el apply moriría con PreflightFailedException a mitad de partida.
            // Hoy el catálogo no tiene ninguno así (los 32: 0 casos), pero la regla no debe depender de eso.
            .Where(d => d.SessionEligible && !d.RequiresReboot && !FragForge.Core.Preflight.PreflightService.NeedsOutOfBandRecovery(d))
            .Select(d => d.Id)
            .ToArray();

    // ------------------------------------------------------------------ Start / Stop / Status

    /// <summary>
    /// Aplica los tweaks elegibles del perfil y escribe el marcador.
    ///
    /// Si ya hay una sesión, NO se apila otra (9.6 "doble Start"): se devuelve la existente con un mensaje,
    /// porque aplicar dos veces dejaría dos snapshots y una reversión incompleta.
    /// </summary>
    public async Task<GameSessionStartResult> StartAsync(TweakProfile profile, CancellationToken cancellationToken = default)
    {
        var existing = await ReadMarkerAsync(cancellationToken);
        if (existing is not null && !existing.Reverted)
        {
            return new GameSessionStartResult
            {
                Started = false,
                Marker = existing,
                Messages = [$"Ya hay una sesión activa ({existing.OperationId}) desde {existing.StartedAt:u}; no se apila otra."]
            };
        }

        var ids = EligibleIds(profile);
        if (ids.Count == 0)
        {
            return new GameSessionStartResult
            {
                Started = false,
                Messages = [$"El perfil {profile} no tiene tweaks elegibles para sesión (sin reinicio y reversibles)."]
            };
        }

        // Sin punto de restauración por sesión (9.2): es una operación corta y totalmente reversible. El
        // snapshot del motor sí se crea, y es lo que permite revertir.
        var report = await _engine.ApplyAsync(ids, new ApplyOptions { Profile = profile, CreateRestorePoint = false }, cancellationToken);
        if (!report.Success)
        {
            return new GameSessionStartResult
            {
                Started = false,
                Messages = [.. report.Errors, .. report.Warnings]
            };
        }

        var marker = new GameSessionMarker
        {
            OperationId = report.OperationId,
            SnapshotId = report.SnapshotId,
            TweakIds = ids,
            Profile = profile.ToString(),
            StartedAt = DateTimeOffset.UtcNow
        };
        await WriteMarkerAsync(marker, cancellationToken);
        return new GameSessionStartResult { Started = true, Marker = marker, Messages = [$"Sesión iniciada: {ids.Count} tweaks aplicados ({string.Join(", ", ids)})."] };
    }

    /// <summary>
    /// Revierte por el snapshot del marcador y borra el marcador.
    ///
    /// Si el revert FALLA (parcial o total), el marcador NO se borra: dejarlo es lo que permite reintentar
    /// en el siguiente arranque. Borrarlo con un revert a medias dejaría el equipo modificado sin memoria de
    /// qué se cambió (9.6).
    /// </summary>
    public async Task<GameSessionStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var marker = await ReadMarkerAsync(cancellationToken);
        if (marker is null)
            return new GameSessionStopResult { AlreadyReverted = true, Reverted = false, Errors = [] };

        if (marker.Reverted)
        {
            // Revert ya hecho y el borrado falló: idempotente, solo se limpia.
            await DeleteMarkerAsync(cancellationToken);
            return new GameSessionStopResult { AlreadyReverted = true, Reverted = true };
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(marker.SnapshotId))
        {
            // Sin snapshot no hay nada reversible de forma segura: se dice y se conserva el marcador para
            // que una persona lo revise. No se inventa un revert por ids.
            errors.Add($"La sesión {marker.OperationId} no tiene snapshot; no se revierte automáticamente.");
            return new GameSessionStopResult { Reverted = false, Errors = errors };
        }

        try
        {
            var report = await _engine.RevertAsync(new RevertRequest { SnapshotId = marker.SnapshotId }, cancellationToken);
            if (!report.Success)
            {
                errors.AddRange(report.Errors);
                if (errors.Count == 0) errors.Add("La reversión no terminó correctamente.");
                return new GameSessionStopResult { Reverted = false, Errors = errors };
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"La reversión falló: {ex.Message}");
            return new GameSessionStopResult { Reverted = false, Errors = errors };
        }

        await DeleteMarkerAsync(cancellationToken);
        return new GameSessionStopResult { Reverted = true };
    }

    public async Task<GameSessionStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var marker = await ReadMarkerAsync(cancellationToken);
        if (marker is null) return new GameSessionStatus { State = GameSessionState.Inactive };

        if (marker.Reverted)
            return new GameSessionStatus { State = GameSessionState.Inactive, Marker = marker, Message = "La sesión ya estaba revertida; solo falta limpiar el marcador." };

        var running = Engine.Cs2Process.IsRunningOnThisMachine();
        return new GameSessionStatus
        {
            State = running switch
            {
                true => GameSessionState.Active,
                false => GameSessionState.WaitingForGame,
                null => GameSessionState.WaitingForGame   // no se pudo comprobar: se informa, no se revierte
            },
            Marker = marker,
            Message = running switch
            {
                true => "Sesión activa: CS2 está en ejecución.",
                false => "Sesión preparada: esperando a que arranque CS2.",
                null => "Sesión preparada: no se pudo comprobar si CS2 corre; no se revierte a ciegas."
            }
        };
    }

    // ------------------------------------------------------------------ Recuperación (9.3)

    /// <summary>
    /// Al arrancar la app o el CLI: si hay marcador y CS2 NO corre, revierte y limpia. Si CS2 sigue
    /// corriendo, no toca nada (la vigilancia se retoma aparte). Idempotente.
    ///
    /// Si ni siquiera se puede saber si CS2 corre, NO se revierte: revertir con el juego abierto sería
    /// sorpresa y podría empeorar el frametime en mitad de una partida. Se informa y se deja el marcador.
    /// </summary>
    public async Task<GameSessionStopResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var marker = await ReadMarkerAsync(cancellationToken);
        if (marker is null) return new GameSessionStopResult { AlreadyReverted = true };

        if (marker.Reverted)
        {
            await DeleteMarkerAsync(cancellationToken);
            return new GameSessionStopResult { AlreadyReverted = true, Reverted = true };
        }

        var running = Engine.Cs2Process.IsRunningOnThisMachine();
        if (running is null)
            return new GameSessionStopResult { Reverted = false, Errors = ["No se pudo comprobar si CS2 está en ejecución; se conserva la sesión para reintentar."] };
        if (running == true)
            return new GameSessionStopResult { Reverted = false, Errors = [] };

        return await StopAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ Vigilancia (9.2)

    /// <summary>
    /// Vigila cs2.exe mientras la sesión está preparada: cuando el juego aparece, la sesión pasa a activa;
    /// cuando TERMINA, revierte y limpia.
    ///
    /// Termina cuando se cancela o cuando revierte con éxito. No se revierte si el revert falla: se informa
    /// y se devuelve el resultado con error, con el marcador intacto.
    ///
    /// El sondeo es inyectable (<paramref name="probe"/>) y el retraso también
    /// (<paramref name="delay"/>): los tests no esperan 2 segundos reales por paso.
    /// </summary>
    public async Task<GameSessionStopResult> WatchAsync(
        Func<bool?>? probe = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        probe ??= Engine.Cs2Process.IsRunningOnThisMachine;
        delay ??= static (interval, token) => Task.Delay(interval, token);

        var marker = await ReadMarkerAsync(cancellationToken);
        if (marker is null || marker.Reverted) return new GameSessionStopResult { AlreadyReverted = true };

        var sawGame = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var running = probe();

            if (running is null)
            {
                // No se pudo comprobar: se espera, no se asume ni que corre ni que no.
                await delay(PollInterval, cancellationToken);
                continue;
            }

            if (running == true)
            {
                sawGame = true;
                await delay(PollInterval, cancellationToken);
                continue;
            }

            // running == false
            if (!sawGame)
            {
                // El juego todavía no ha arrancado: la sesión está esperando, no se revierte.
                await delay(PollInterval, cancellationToken);
                continue;
            }

            // El juego estuvo abierto y ya no está: se revierte.
            return await StopAsync(cancellationToken);
        }
    }

    // ------------------------------------------------------------------ marcador

    /// <summary>Lee el marcador. Un archivo corrupto NO se ignora: se propaga, porque borrarlo a ciegas
    /// podría perder un revert pendiente.</summary>
    public async Task<GameSessionMarker?> ReadMarkerAsync(CancellationToken cancellationToken = default)
    {
        var path = MarkerPath;
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<GameSessionMarker>(json, GameSessionPaths.SerializerOptions)
                   ?? throw new JsonException("Marcador vacío.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"El marcador de sesión '{path}' no se pudo leer: {ex.Message}. No se borra: revisa el archivo.", ex);
        }
    }

    private async Task WriteMarkerAsync(GameSessionMarker marker, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.Root);
        await AtomicFile.WriteAllTextAsync(MarkerPath, JsonSerializer.Serialize(marker, GameSessionPaths.SerializerOptions), cancellationToken);
    }

    /// <summary>
    /// Borra el marcador. El propio revert ya quedó registrado en el journal por el motor: aquí no se añade
    /// una entrada nueva, porque el marcador solo autoriza el revert automático y dejarlo tras un revert
    /// correcto provocaría un revert doble en el siguiente arranque.
    /// </summary>
    private Task DeleteMarkerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = MarkerPath;
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
