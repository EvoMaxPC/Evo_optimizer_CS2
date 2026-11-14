using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FragForge.Core.Persistence;

namespace FragForge.Core.Cs2Profile;

/// <summary>Dónde se va a escribir.</summary>
public enum ExportDestinationKind
{
    /// <summary>Carpeta cualquiera que elige el usuario. No toca Steam.</summary>
    Folder,

    /// <summary>Carpeta cfg del juego, archivo nuevo <c>fragforge-cs2.cfg</c>.</summary>
    GameNewFile,

    /// <summary>Carpeta cfg del juego, bloque FragForge dentro de <c>autoexec.cfg</c>.</summary>
    GameAutoexecBlock
}

/// <summary>Petición de exportación. Todo lo que el exportador necesita saber, ya resuelto.</summary>
public sealed record ExportRequest
{
    public ExportDestinationKind Kind { get; init; } = ExportDestinationKind.Folder;

    /// <summary>Carpeta destino cuando <see cref="Kind"/> es <see cref="ExportDestinationKind.Folder"/>.</summary>
    public string? FolderPath { get; init; }

    /// <summary>Ruta de <c>cs2.exe</c> si se localizó. Necesaria para los destinos del juego.</summary>
    public string? GameExecutablePath { get; init; }

    /// <summary>Raíz de Steam, para poder rechazar <c>userdata</c> con un mensaje claro.</summary>
    public string? SteamRoot { get; init; }

    /// <summary>Contenido exacto que se escribirá.</summary>
    public string CfgText { get; init; } = "";

    /// <summary>Fecha para el nombre del backup. Inyectable para que el test sea reproducible.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Permite sobrescribir algo existente. Sin esto se pregunta o se aborta.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>Resultado de exportar. <see cref="Written"/> false = no se tocó el disco.</summary>
public sealed record ExportResult
{
    public bool Written { get; init; }
    public string? TargetPath { get; init; }
    public string? BackupPath { get; init; }

    /// <summary>SHA-256 del archivo escrito, en hexadecimal minúscula. Null si no se escribió.</summary>
    public string? WrittenSha256 { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>
/// Escribe el perfil en el destino elegido. La ÚNICA pieza del módulo que toca el disco.
/// </summary>
/// <remarks>
/// <para>
/// <b>Orden de las comprobaciones.</b> Se valida ANTES de escribir y se aborta sin efectos si algo falla:
/// ruta permitida, destino no existente (sin permiso de sobrescritura), backup verificado. Nunca se deja el
/// archivo a medio escribir: se escribe a un temporal y se mueve.
/// </para>
/// <para>
/// <b>Backup verificado.</b> Antes de tocar <c>autoexec.cfg</c> se copia y se comprueba que el SHA-256 del
/// backup coincide con el del original. Si no coincide, se ABORTA y no se escribe nada: un backup que no se
/// puede verificar no es un backup.
/// </para>
/// <para>
/// <b>Bytes intactos.</b> El resto del <c>autoexec.cfg</c> se conserva byte a byte, incluidos el BOM y los
/// finales de línea, porque se trabaja con <see cref="byte"/>[] y no con <see cref="string"/>: decodificar y
/// volver a codificar cambiaría el BOM y podría cambiar los saltos.
/// </para>
/// </remarks>
public static class Cs2ProfileExporter
{
    /// <summary>Codificación sin BOM para los archivos nuevos: el juego no necesita BOM y no lo espera.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default) =>
        ExportAsync(request, verifyBackup: null, cancellationToken);

    /// <summary>
    /// Igual que <see cref="ExportAsync(ExportRequest, CancellationToken)"/> pero con un verificador de backup
    /// inyectable. En producción se pasa null (verificación real por SHA-256); el test lo usa para ejercitar
    /// el camino de ABORTO, que de otro modo exigiría un fallo de disco real.
    /// </summary>
    /// <param name="verifyBackup">Comprueba que la copia coincide con el original. Null = SHA-256 real.</param>
    public static Task<ExportResult> ExportAsync(
        ExportRequest request,
        Func<byte[], byte[], bool>? verifyBackup,
        CancellationToken cancellationToken = default) =>
        ExportAsync(request, verifyBackup, FileWriter, cancellationToken);

    /// <summary>
    /// Igual que las anteriores, con el ESCRITOR inyectable. En producción se usa
    /// <see cref="FileWriter"/>; el test lo sustituye para provocar un fallo de disco (acceso denegado,
    /// archivo en uso, disco lleno) sin depender del sistema operativo ni de sus permisos.
    /// </summary>
    /// <param name="write">Escribe los bytes en la ruta indicada. Puede lanzar E/S o acceso denegado.</param>
    public static async Task<ExportResult> ExportAsync(
        ExportRequest request,
        Func<byte[], byte[], bool>? verifyBackup,
        Func<string, byte[], CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return request.Kind switch
        {
            ExportDestinationKind.Folder => await ExportToFolderAsync(request, write, cancellationToken),
            ExportDestinationKind.GameNewFile => await ExportToGameNewFileAsync(request, write, cancellationToken),
            ExportDestinationKind.GameAutoexecBlock => await ExportToAutoexecAsync(request, verifyBackup, write, cancellationToken),
            _ => Fail("Destino desconocido.")
        };
    }

    /// <summary>Escritura real: atómica (temporal + mover) y sin tocar el archivo si falla.</summary>
    internal static Task FileWriter(string path, byte[] content, CancellationToken cancellationToken) =>
        AtomicFile.WriteAllBytesAsync(path, content, cancellationToken);

    // ------------------------------------------------------------------ destinos

    private static async Task<ExportResult> ExportToFolderAsync(ExportRequest request, Func<string, byte[], CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FolderPath)) return Fail("Falta la carpeta de destino.");
        var normalized = Cs2PathGuard.Normalize(request.FolderPath, Directory.GetCurrentDirectory());
        if (normalized is null) return Fail("La carpeta de destino no es una ruta válida.");

        var target = Path.Combine(normalized, Cs2ProfileGenerator.GameCfgFileName);
        if (File.Exists(target) && !request.Overwrite)
            return Fail($"Ya existe '{target}'. No se sobrescribe sin confirmación explícita.");

        var bytes = Utf8NoBom.GetBytes(NormalizeNewlines(request.CfgText));
        try
        {
            await write(target, bytes, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return FailWrite(ex, "el perfil", target, "No se escribió ningún archivo.");
        }

        // Se hashea lo que QUEDÓ en disco, no lo que creemos haber escrito: si el escritor hiciera otra
        // cosa, el hash lo delataría.
        var written = await File.ReadAllBytesAsync(target, cancellationToken);
        return new ExportResult
        {
            Written = true,
            TargetPath = target,
            WrittenSha256 = Sha256(written),
            Messages = [$"Perfil escrito en '{target}'. Para usarlo, cópialo a la carpeta cfg del juego y ejecuta '{Cs2ProfileGenerator.ExecHint}'."]
        };
    }

    private static async Task<ExportResult> ExportToGameNewFileAsync(ExportRequest request, Func<string, byte[], CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var cfgDirectory = Cs2PathGuard.CfgDirectoryFor(request.GameExecutablePath);
        if (cfgDirectory is null) return Fail("No se pudo derivar la carpeta cfg: ¿está instalado CS2?");

        var target = Path.Combine(cfgDirectory, Cs2ProfileGenerator.GameCfgFileName);
        var check = Cs2PathGuard.CheckGameDestination(target, request.GameExecutablePath!, request.SteamRoot);
        if (!check.IsAllowed) return Fail(check.Reason);

        if (File.Exists(target) && !request.Overwrite)
            return Fail($"Ya existe '{target}'. No se sobrescribe sin confirmación explícita.");

        var bytes = Utf8NoBom.GetBytes(NormalizeNewlines(request.CfgText));
        try
        {
            await write(target, bytes, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return FailWrite(ex, "el perfil", target, "No se escribió ningún archivo.");
        }

        // Se hashea lo que QUEDÓ en disco, no lo que creemos haber escrito: si el escritor hiciera otra
        // cosa, el hash lo delataría.
        var written = await File.ReadAllBytesAsync(target, cancellationToken);
        return new ExportResult
        {
            Written = true,
            TargetPath = target,
            WrittenSha256 = Sha256(written),
            Messages = [$"Perfil escrito en '{target}'. Añade '{Cs2ProfileGenerator.ExecHint}' a tu autoexec (o escríbelo en la consola) para activarlo."]
        };
    }

    private static async Task<ExportResult> ExportToAutoexecAsync(ExportRequest request, Func<byte[], byte[], bool>? verifyBackup, Func<string, byte[], CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var cfgDirectory = Cs2PathGuard.CfgDirectoryFor(request.GameExecutablePath);
        if (cfgDirectory is null) return Fail("No se pudo derivar la carpeta cfg: ¿está instalado CS2?");

        var target = Path.Combine(cfgDirectory, Cs2ProfileGenerator.AutoexecFileName);
        var check = Cs2PathGuard.CheckGameDestination(target, request.GameExecutablePath!, request.SteamRoot);
        if (!check.IsAllowed) return Fail(check.Reason);

        var original = File.Exists(target) ? await File.ReadAllBytesAsync(target, cancellationToken) : [];
        var messages = new List<string>();
        string? backupPath = null;

        // 1) Backup OBLIGATORIO y VERIFICADO antes de tocar nada. Solo si había algo que copiar.
        if (original.Length > 0)
        {
            backupPath = ChooseBackupPath(cfgDirectory, request.Timestamp);
            if (backupPath is null)
            {
                // 99 copias para el mismo segundo: no se pisa ninguna y se aborta sin tocar nada.
                return Fail($"Ya existen {MaxBackupsPerSecond} copias de seguridad con la marca de tiempo " +
                            $"{request.Timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)} en " +
                            $"'{cfgDirectory}'. No se sobrescribe ninguna copia anterior y no se ha modificado autoexec.cfg. " +
                            "Borra alguna copia antigua o espera un segundo y vuelve a intentarlo.");
            }
            try
            {
                await write(backupPath, original, cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Sin copia fiable no hay red de seguridad: se ABORTA sin tocar autoexec.cfg.
                return FailWrite(ex, "la copia de seguridad", backupPath, $"'{target}' no se ha modificado.");
            }

            var backupBytes = await File.ReadAllBytesAsync(backupPath, cancellationToken);
            var originalHash = Sha256(original);
            var backupHash = Sha256(backupBytes);
            var matches = verifyBackup is null
                ? string.Equals(originalHash, backupHash, StringComparison.Ordinal)
                : verifyBackup(original, backupBytes);
            if (!matches)
            {
                // ABORTAR sin escribir: se deja constancia y no se toca el autoexec.
                return Fail($"El backup no coincide con el original (origen {originalHash[..12]}…, copia {backupHash[..12]}…). " +
                            "Se aborta sin modificar autoexec.cfg.");
            }
            messages.Add($"Copia de seguridad verificada: '{backupPath}' (SHA-256 {originalHash[..16]}…).");
        }

        // 2) Componer el archivo: el resto byte a byte + el bloque al final.
        var merged = MergeBlock(original, request.CfgText);
        try
        {
            await write(target, merged, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Si el fallo deja una copia recién creada y sin usar, se retira: no se deja basura que confunda
            // al próximo restore-autoexec (que elige «el último backup» por nombre).
            var cleaned = TryRemoveLeftoverBackup(backupPath);
            return FailWrite(ex, "autoexec.cfg", target,
                cleaned
                    ? $"'{target}' no se ha modificado y se retiró la copia de seguridad que se acababa de crear ('{backupPath}')."
                    : $"'{target}' no se ha modificado." + (backupPath is null ? "" : $" La copia '{backupPath}' se conserva."));
        }

        // 3) Conflicto con líneas del USUARIO fuera del bloque: se informan, NO se tocan.
        var userCommands = ExtractCommands(original);
        foreach (var line in request.CfgText.Split('\n'))
        {
            var command = FirstToken(line);
            if (command is null) continue;
            if (userCommands.Contains(command))
                messages.Add($"'{command}' ya estaba definido fuera del bloque: no se ha modificado esa línea. " +
                             "El bloque va al final del archivo, así que en el juego se aplicará el valor del bloque cuando se ejecute autoexec.");
        }

        messages.Add($"Bloque FragForge escrito al final de '{target}'.");
        return new ExportResult
        {
            Written = true,
            TargetPath = target,
            BackupPath = backupPath,
            WrittenSha256 = Sha256(merged),
            Messages = messages
        };
    }

    // ------------------------------------------------------------------ piezas

    /// <summary>
    /// Devuelve el contenido con el bloque FragForge reemplazado o añadido al final. Lo que está FUERA del
    /// bloque se conserva byte a byte, incluidos BOM y finales de línea.
    /// </summary>
    public static byte[] MergeBlock(byte[] original, string blockContent)
    {
        var blockBytes = Utf8NoBom.GetBytes(Cs2ProfileGenerator.WrapInBlock(NormalizeNewlines(blockContent)));

        if (original.Length == 0) return blockBytes;

        var (start, end) = FindBlock(original);
        if (start < 0)
        {
            // Sin bloque previo: se añade al final, asegurando que el original acaba en salto de línea para
            // no pegar el bloque a la última línea del usuario.
            var needsNewline = original[^1] is not (byte)'\n';
            var suffix = needsNewline ? new byte[] { (byte)'\n' } : [];
            return Concat(original, suffix, blockBytes);
        }

        // Con bloque: se reemplaza SOLO ese tramo. Lo de antes y lo de después queda igual.
        return Concat(original[..start], blockBytes, original[end..]);
    }

    /// <summary>Localiza el bloque por sus marcas. Devuelve (-1,-1) si no hay.</summary>
    private static (int Start, int End) FindBlock(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var startMarker = Cs2ProfileGenerator.BlockStart;
        var endMarker = Cs2ProfileGenerator.BlockEnd;
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return (-1, -1);
        var endIndex = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (endIndex < 0) return (-1, -1);
        var end = endIndex + endMarker.Length;

        // Se traga el salto de línea que sigue al cierre, para no dejar líneas en blanco acumuladas.
        while (end < text.Length && text[end] is '\r' or '\n') end++;

        // Como las marcas son ASCII y la decodificación es UTF-8, los índices de carácter no coinciden con
        // los de byte si hay caracteres multibyte ANTES del bloque. Se recalculan sobre los bytes.
        var startBytes = Encoding.UTF8.GetByteCount(text[..start]);
        var endBytes = Encoding.UTF8.GetByteCount(text[..end]);
        return (startBytes, endBytes);
    }

    /// <summary>Comandos definidos en el texto, en minúsculas, excluyendo comentarios y líneas vacías.</summary>
    private static HashSet<string> ExtractCommands(byte[] bytes)
    {
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var command = FirstToken(line);
            if (command is not null) commands.Add(command);
        }
        return commands;
    }

    private static string? FirstToken(string line)
    {
        var trimmed = line.Trim().TrimStart('\uFEFF').Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) return null;
        var separator = trimmed.IndexOfAny([' ', '\t']);
        return (separator < 0 ? trimmed : trimmed[..separator]).Trim();
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts) { part.CopyTo(result, offset); offset += part.Length; }
        return result;
    }

    /// <summary>SHA-256 en hexadecimal minúscula. Es lo que se compara para verificar un backup.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Cuántas copias se admiten por segundo: la primera sin sufijo y luego <c>-02</c> … <c>-99</c>.
    /// </summary>
    public const int MaxBackupsPerSecond = 99;

    /// <summary>
    /// Elige un nombre de backup que NO exista. La primera copia del segundo usa el nombre pelado; si ya está
    /// ocupado se prueba <c>-02</c>, <c>-03</c>… hasta <c>-99</c>. Devuelve null si están todas ocupadas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Por qué contador y no azar.</b> <c>restore-autoexec</c> elige «el último» ordenando por nombre con
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>. Un sufijo aleatorio haría indeterminado cuál es el último.
    /// Con contador y dos dígitos, el orden ordinal coincide con el orden de creación:
    /// <c>…120000</c> &lt; <c>…120000-02</c> &lt; <c>…120000-03</c> &lt; … &lt; <c>…120001</c>.
    /// El cero a la izquierda es obligatorio: sin él, <c>-10</c> ordenaría ANTES que <c>-9</c>.
    /// </para>
    /// <para>
    /// <b>Ventana de carrera.</b> Entre la comprobación de existencia y la escritura, otro proceso podría crear
    /// el mismo nombre. No se usa creación exclusiva porque <see cref="AtomicFile"/> escribe a un temporal y
    /// mueve con sobreescritura, y no se toca <see cref="AtomicFile"/> porque lo usan otros módulos.
    /// </para>
    /// </remarks>
    internal static string? ChooseBackupPath(string cfgDirectory, DateTimeOffset timestamp)
    {
        var stamp = timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = Cs2ProfileGenerator.BackupPrefix + stamp;

        var first = Path.Combine(cfgDirectory, baseName);
        if (!File.Exists(first)) return first;

        // Cálculo a mano: 02..99 son 98 nombres más el pelado = 99 copias por segundo.
        for (var n = 2; n <= MaxBackupsPerSecond; n++)
        {
            var candidate = Path.Combine(cfgDirectory, baseName + "-" + n.ToString("D2", CultureInfo.InvariantCulture));
            if (!File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Convierte un fallo de disco en un resultado con mensaje en español, sin stack trace. Nombra la ruta
    /// que falló y dice explícitamente que <c>autoexec.cfg</c> no se modificó. Solo sugiere ejecutar como
    /// administrador cuando la causa ES de permisos: un disco lleno o un archivo en uso no se arreglan así.
    /// </summary>
    private static ExportResult FailWrite(Exception ex, string what, string path, string consequence)
    {
        var cause = ex is UnauthorizedAccessException
            ? "no hay permisos para escribir ahí (si el juego o Steam están abiertos, ciérralos; si la carpeta es del sistema, ejecuta FragForge como administrador)"
            : "el sistema no pudo completar la escritura (puede que el archivo esté en uso, que falte espacio o que la ruta no esté disponible)";
        return Fail($"No se pudo escribir {what} en '{path}': {cause}. Detalle: {ex.Message} {consequence}");
    }

    /// <summary>
    /// Retira el backup recién creado si el paso siguiente falló, para no dejar una copia huérfana que
    /// <c>restore-autoexec</c> elegiría como «la última». Devuelve true si se retiró.
    /// </summary>
    /// <remarks>
    /// Si borrar también falla, se responde false y el mensaje dice que la copia se conserva: se informa de
    /// lo que de verdad quedó en disco en vez de suponer que se limpió.
    /// </remarks>
    private static bool TryRemoveLeftoverBackup(string? backupPath)
    {
        if (backupPath is null || !File.Exists(backupPath)) return false;
        try
        {
            File.Delete(backupPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static ExportResult Fail(string message) => new() { Written = false, Messages = [message] };
}
