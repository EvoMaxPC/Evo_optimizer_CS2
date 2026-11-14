using System.Globalization;
using System.Text;
using FragForge.Core.Cs2Profile;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cs2Profile;

/// <summary>
/// Exportador. Todo ocurre en una carpeta temporal con una INSTALACIÓN FICTICIA de CS2 creada por el test:
/// nunca se toca una instalación real ni ninguna carpeta de Steam del usuario.
/// </summary>
public sealed class Cs2ProfileExporterTests : IDisposable
{
    private readonly string _root;
    private readonly string _steam;
    private readonly string _game;
    private readonly string _cfg;
    private readonly string _exe;

    public Cs2ProfileExporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ff-export-" + Guid.NewGuid().ToString("N"));
        _steam = Path.Combine(_root, "Steam");
        _game = Path.Combine(_steam, "steamapps", "common", "Counter-Strike Global Offensive");
        _cfg = Path.Combine(_game, "game", "csgo", "cfg");
        _exe = Path.Combine(_game, "game", "bin", "win64", "cs2.exe");
        Directory.CreateDirectory(_cfg);
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "ficticio");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly DateTimeOffset FixedTime = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    private const string CfgBody = "fps_max 240\nmat_vsync 0\n";

    private ExportRequest GameBlock(bool overwrite = false) => new()
    {
        Kind = ExportDestinationKind.GameAutoexecBlock,
        GameExecutablePath = _exe,
        SteamRoot = _steam,
        CfgText = CfgBody,
        Timestamp = FixedTime,
        Overwrite = overwrite
    };

    // ------------------------------------------------------------------ fallos de disco (paso 4)

    /// <summary>
    /// Escritor inyectable que falla SOLO para una ruta concreta y delega el resto en la escritura real.
    /// Provoca un fallo de disco sin depender del sistema operativo ni de sus permisos.
    /// </summary>
    private static Func<string, byte[], CancellationToken, Task> FailingOn(
        string pathToFail, Exception failure, Func<string, byte[], CancellationToken, Task>? inner = null) =>
        (path, content, token) => string.Equals(
            Path.GetFullPath(path), Path.GetFullPath(pathToFail), StringComparison.OrdinalIgnoreCase)
            ? throw failure
            : (inner ?? ((p, c, ct) => File.WriteAllBytesAsync(p, c, ct)))(path, content, token);

    [Fact]
    public async Task AccessDeniedWhenWritingTheBackup_AbortsAndLeavesAutoexecByteForByteUntouched()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        var originalBytes = new UTF8Encoding(true).GetBytes("// mio\nsensitivity 2.5\n");
        await File.WriteAllBytesAsync(autoexec, originalBytes);
        var before = Cs2ProfileExporter.Sha256(originalBytes);

        // El backup es el PRIMER archivo de ese segundo: su ruta es determinista.
        var backupPath = Path.Combine(_cfg, "autoexec.cfg.fragforge-backup-20261113-100000");
        var writer = FailingOn(backupPath, new UnauthorizedAccessException("acceso denegado (test)"));

        var result = await Cs2ProfileExporter.ExportAsync(GameBlock(), verifyBackup: null, write: writer, CancellationToken.None);

        Assert.False(result.Written);
        var message = Assert.Single(result.Messages);
        Assert.Contains("No se pudo escribir la copia de seguridad", message, StringComparison.Ordinal);
        Assert.Contains(backupPath, message, StringComparison.Ordinal);
        Assert.Contains("no se ha modificado", message, StringComparison.Ordinal);
        // Sugiere administrador SOLO porque la causa es de permisos.
        Assert.Contains("administrador", message, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", message, StringComparison.Ordinal);

        // La excepción no escapó y el autoexec sigue byte a byte igual. Tampoco hay autoexec tocado ni backup.
        Assert.Equal(before, Cs2ProfileExporter.Sha256(await File.ReadAllBytesAsync(autoexec)));
        Assert.False(File.Exists(backupPath));
    }

    /// <summary>Escritor que falla al crear el TEMPORAL (nombre oculto <c>.autoexec.cfg.&lt;guid&gt;.tmp</c>).</summary>
    private static Func<string, byte[], CancellationToken, Task> FailingOnTheTemporary(Exception failure) =>
        (path, content, token) => Path.GetFileName(path).StartsWith(".autoexec.cfg.", StringComparison.Ordinal)
            ? throw failure
            : File.WriteAllBytesAsync(path, content, token);

    [Fact]
    public async Task AccessDeniedWritingTheTemporary_Aborts_SaysAutoexecIsUntouched_AndKeepsTheBackup()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        var originalBytes = new UTF8Encoding(true).GetBytes("// mio\nsensitivity 2.5\n");
        await File.WriteAllBytesAsync(autoexec, originalBytes);
        var before = Cs2ProfileExporter.Sha256(originalBytes);

        var backupPath = Path.Combine(_cfg, "autoexec.cfg.fragforge-backup-20261113-100000");

        var result = await Cs2ProfileExporter.ExportAsync(
            GameBlock(),
            verifyBackup: null,
            write: FailingOnTheTemporary(new UnauthorizedAccessException("acceso denegado (test)")),
            cancellationToken: CancellationToken.None);

        Assert.False(result.Written);
        var message = Assert.Single(result.Messages);
        Assert.Contains("No se pudo escribir el archivo temporal", message, StringComparison.Ordinal);
        // En esta etapa SÍ se puede afirmar que autoexec.cfg no cambió.
        Assert.Contains($"'{autoexec}' no se ha modificado", message, StringComparison.Ordinal);
        // Y se dice dónde queda el backup, que no se borra nunca.
        Assert.Contains("La copia de seguridad", message, StringComparison.Ordinal);
        Assert.Contains(backupPath, message, StringComparison.Ordinal);

        Assert.Equal(before, Cs2ProfileExporter.Sha256(await File.ReadAllBytesAsync(autoexec)));
        // El backup existe y es byte a byte el original: es la única copia recuperable.
        Assert.True(File.Exists(backupPath));
        Assert.Equal(Convert.ToHexString(originalBytes), Convert.ToHexString(await File.ReadAllBytesAsync(backupPath)));
    }

    [Fact]
    public async Task FailureInTheFinalMove_DoesNotClaimAutoexecIsUntouched_AndKeepsTheBackupByteForByte()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        var originalBytes = new UTF8Encoding(true).GetBytes("// mio\nsensitivity 2.5\n");
        await File.WriteAllBytesAsync(autoexec, originalBytes);

        var backupPath = Path.Combine(_cfg, "autoexec.cfg.fragforge-backup-20261113-100000");

        // El temporal se escribe bien; lo que falla es el movimiento final.
        var result = await Cs2ProfileExporter.ExportAsync(
            GameBlock(),
            verifyBackup: null,
            write: (path, content, token) => File.WriteAllBytesAsync(path, content, token),
            move: (_, _) => throw new IOException("el movimiento final falló (test)"),
            cancellationToken: CancellationToken.None);

        Assert.False(result.Written);
        var message = Assert.Single(result.Messages);

        // NO se puede afirmar que autoexec.cfg siga igual: en esta etapa pudo haber cambiado.
        Assert.DoesNotContain("no se ha modificado", message, StringComparison.Ordinal);
        Assert.Contains("No se pudo completar la escritura", message, StringComparison.Ordinal);
        Assert.Contains("VERIFICA", message, StringComparison.Ordinal);
        Assert.Contains(autoexec, message, StringComparison.Ordinal);
        // Y se dice dónde está el backup para poder restaurar.
        Assert.Contains("La copia de seguridad", message, StringComparison.Ordinal);
        Assert.Contains(backupPath, message, StringComparison.Ordinal);

        // El backup SIGUE EXISTIENDO y es byte a byte el original. Nunca se borra.
        Assert.True(File.Exists(backupPath), "el backup no debe borrarse: es el único original recuperable");
        Assert.Equal(Convert.ToHexString(originalBytes), Convert.ToHexString(await File.ReadAllBytesAsync(backupPath)));
        // No quedó temporal huérfano.
        Assert.Empty(Directory.GetFiles(_cfg, ".*.tmp"));
    }

    [Fact]
    public async Task FailureInTheFinalMove_LeavesEveryExistingBackupExactlyAsItWas()
    {
        // Había backups de antes: tras un export fallido en el movimiento final, NINGUNO cambia de contenido.
        // Cálculo a mano: se crean 2 backups previos (el del segundo 10:00:00 y su -02) y el export fallido
        // añade el tercero del MISMO segundo, que es el -03. Antes y después deben ser 3 archivos.
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        await File.WriteAllBytesAsync(autoexec, "// mio\n"u8.ToArray());

        var stem = "autoexec.cfg.fragforge-backup-20261113-100000";
        var previos = new[] { Path.Combine(_cfg, stem), Path.Combine(_cfg, stem + "-02") };
        await File.WriteAllBytesAsync(previos[0], "// copia vieja 1\n"u8.ToArray());
        await File.WriteAllBytesAsync(previos[1], "// copia vieja 2\n"u8.ToArray());
        var hashesAntes = previos.Select(p => Cs2ProfileExporter.Sha256(File.ReadAllBytes(p))).ToArray();

        var result = await Cs2ProfileExporter.ExportAsync(
            GameBlock(),
            verifyBackup: null,
            write: (path, content, token) => File.WriteAllBytesAsync(path, content, token),
            move: (_, _) => throw new IOException("el movimiento final falló (test)"),
            cancellationToken: CancellationToken.None);

        Assert.False(result.Written);

        // Las dos copias previas siguen byte a byte iguales.
        for (var i = 0; i < previos.Length; i++)
            Assert.Equal(hashesAntes[i], Cs2ProfileExporter.Sha256(await File.ReadAllBytesAsync(previos[i])));

        // Y se añadió la tercera (la de este intento), que tampoco se borra: 2 + 1 = 3.
        var todos = Directory.GetFiles(_cfg, Cs2ProfileGenerator.BackupPrefix + "*")
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(3, todos.Length);
        Assert.Equal(new[] { stem, stem + "-02", stem + "-03" }, todos);
    }

    [Fact]
    public async Task FailureInTheFinalMove_WithNoPreviousAutoexec_SaysThereIsNoBackupToRestore()
    {
        // Sin autoexec previo no se crea backup (no hay nada que copiar) y el mensaje debe decirlo en vez de
        // dejar un hueco que se leería como si el backup se hubiera perdido.
        var result = await Cs2ProfileExporter.ExportAsync(
            GameBlock(),
            verifyBackup: null,
            write: (path, content, token) => File.WriteAllBytesAsync(path, content, token),
            move: (_, _) => throw new IOException("el movimiento final falló (test)"),
            cancellationToken: CancellationToken.None);

        Assert.False(result.Written);
        var message = Assert.Single(result.Messages);
        Assert.Contains("No había un autoexec.cfg anterior", message, StringComparison.Ordinal);
        Assert.DoesNotContain("no se ha modificado", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiskFullWhenWritingAutoexec_SaysItIsNotAPermissionsProblem()
    {
        // IOException (disco lleno) al crear el TEMPORAL: NO se sugiere administrador, porque no lo arregla.
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        await File.WriteAllTextAsync(autoexec, "// mio\n");
        var writer = FailingOnTheTemporary(new IOException("no hay espacio en el dispositivo (test)"));

        var result = await Cs2ProfileExporter.ExportAsync(GameBlock(), verifyBackup: null, write: writer, CancellationToken.None);

        Assert.False(result.Written);
        var message = Assert.Single(result.Messages);
        Assert.Contains("el sistema no pudo completar la escritura", message, StringComparison.Ordinal);
        Assert.DoesNotContain("administrador", message, StringComparison.Ordinal);
        Assert.Contains("no se ha modificado", message, StringComparison.Ordinal);
        Assert.Equal("// mio\n", await File.ReadAllTextAsync(autoexec));
    }

    [Fact]
    public async Task AccessDeniedInTheFolderDestination_IsReportedWithoutThrowing()
    {
        var folder = Path.Combine(_root, "salida-denegada");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "fragforge-cs2.cfg");
        var writer = FailingOn(target, new UnauthorizedAccessException("acceso denegado (test)"));

        var request = new ExportRequest
        {
            Kind = ExportDestinationKind.Folder,
            FolderPath = folder,
            CfgText = CfgBody,
            Timestamp = FixedTime
        };

        var result = await Cs2ProfileExporter.ExportAsync(request, verifyBackup: null, write: writer, CancellationToken.None);

        Assert.False(result.Written);
        var message = Assert.Single(result.Messages);
        Assert.Contains("No se pudo escribir el perfil", message, StringComparison.Ordinal);
        Assert.Contains(target, message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ThePublicOverloadsStillWork_AndFileWriterIsTheRealOne()
    {
        // Las tres sobrecargas delegan y la pública sin verificador escribe de verdad.
        var folder = Path.Combine(_root, "salida-normal");
        Directory.CreateDirectory(folder);
        var request = new ExportRequest { Kind = ExportDestinationKind.Folder, FolderPath = folder, CfgText = CfgBody, Timestamp = FixedTime };

        var result = await Cs2ProfileExporter.ExportAsync(request);

        Assert.True(result.Written);
        Assert.Equal(CfgBody.Replace("\r\n", "\n", StringComparison.Ordinal), await File.ReadAllTextAsync(result.TargetPath!));
    }

    // ------------------------------------------------------------------ colisión de backup (mismo segundo)

    private static readonly DateTimeOffset NextSecond = new(2026, 11, 13, 10, 0, 1, TimeSpan.Zero);

    [Fact]
    public async Task TwoExportsInTheSameSecond_ProduceTwoDistinctBackups_AndNeverOverwrite()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        var firstText = "// original del usuario\nsensitivity 2.5\n";
        var firstBytes = new UTF8Encoding(true).GetBytes(firstText);
        await File.WriteAllBytesAsync(autoexec, firstBytes);

        var first = await Cs2ProfileExporter.ExportAsync(GameBlock());
        Assert.True(first.Written);
        // La PRIMERA copia del segundo es la que no lleva sufijo.
        Assert.Equal(Path.Combine(_cfg, "autoexec.cfg.fragforge-backup-20261113-100000"), first.BackupPath);
        // Y es byte a byte el autoexec ORIGINAL (con su BOM).
        Assert.Equal(Convert.ToHexString(firstBytes), Convert.ToHexString(await File.ReadAllBytesAsync(first.BackupPath!)));

        // El primer export ya modificó el autoexec; el segundo copia ESA versión, no la original.
        var afterFirst = await File.ReadAllBytesAsync(autoexec);
        Assert.NotEqual(Convert.ToHexString(firstBytes), Convert.ToHexString(afterFirst));

        var second = await Cs2ProfileExporter.ExportAsync(GameBlock());
        Assert.True(second.Written);
        // Mismo instante inyectado -> el nombre pelado ya está ocupado -> sufijo -02.
        Assert.Equal(Path.Combine(_cfg, "autoexec.cfg.fragforge-backup-20261113-100000-02"), second.BackupPath);
        // El segundo backup es byte a byte el autoexec TAL COMO LO DEJÓ EL PRIMER EXPORT.
        Assert.Equal(Convert.ToHexString(afterFirst), Convert.ToHexString(await File.ReadAllBytesAsync(second.BackupPath!)));
        // Y son dos archivos distintos, con contenidos distintos: no se pisó nada.
        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.NotEqual(Convert.ToHexString(await File.ReadAllBytesAsync(first.BackupPath!)),
                        Convert.ToHexString(await File.ReadAllBytesAsync(second.BackupPath!)));
    }

    [Fact]
    public async Task ThreeExportsInTheSameSecond_AreNumbered_02_And_03()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        await File.WriteAllTextAsync(autoexec, "// original\n");

        var a = await Cs2ProfileExporter.ExportAsync(GameBlock());
        var b = await Cs2ProfileExporter.ExportAsync(GameBlock());
        var c = await Cs2ProfileExporter.ExportAsync(GameBlock());

        var stem = "autoexec.cfg.fragforge-backup-20261113-100000";
        Assert.Equal(Path.Combine(_cfg, stem), a.BackupPath);
        Assert.Equal(Path.Combine(_cfg, stem + "-02"), b.BackupPath);
        Assert.Equal(Path.Combine(_cfg, stem + "-03"), c.BackupPath);

        // Cálculo a mano del criterio de restore (último por nombre, OrderByDescending ordinal):
        //   "…-100000"    vs "…-100000-02" -> los 21 primeros caracteres son iguales; "-" (0x2D) hace que
        //                   "…-100000-02" sea MAYOR y por tanto el último sea -03.
        var presentes = Directory.GetFiles(_cfg, Cs2ProfileGenerator.BackupPrefix + "*")
            .Select(Path.GetFileName)
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(3, presentes.Length);
        Assert.Equal(stem + "-03", presentes[0]);
        Assert.Equal(stem + "-02", presentes[1]);
        Assert.Equal(stem, presentes[2]);
    }

    [Fact]
    public async Task NextSecond_SortsAfterTheNumberedOnesOfThePreviousSecond()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        await File.WriteAllTextAsync(autoexec, "// original\n");

        await Cs2ProfileExporter.ExportAsync(GameBlock());
        await Cs2ProfileExporter.ExportAsync(GameBlock());          // -02
        await Cs2ProfileExporter.ExportAsync(GameBlock() with { Timestamp = NextSecond });

        var stem = "autoexec.cfg.fragforge-backup-20261113-100000";
        var presentes = Directory.GetFiles(_cfg, Cs2ProfileGenerator.BackupPrefix + "*")
            .Select(Path.GetFileName)
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Cálculo a mano carácter a carácter: "…-100001" y "…-100000-02".
        // Comparan igual hasta "…-10000"; luego "1" (0x31) frente a "0" (0x30): 1 > 0, así que el del
        // segundo siguiente gana ANTES de llegar al guion del sufijo. Orden final: 100001 > 100000-02 > 100000.
        Assert.Equal("autoexec.cfg.fragforge-backup-20261113-100001", presentes[0]);
        Assert.Equal(stem + "-02", presentes[1]);
        Assert.Equal(stem, presentes[2]);
    }

    [Fact]
    public async Task WhenAll99SlotsAreTaken_ExportAborts_AndAutoexecAndBackupsAreUntouched()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        var originalBytes = new UTF8Encoding(true).GetBytes("// original intocable\nsensitivity 2.5\n");
        await File.WriteAllBytesAsync(autoexec, originalBytes);

        // Se ocupan las 99 ranuras del segundo 10:00:00: la pelada + -02 … -99.
        // Cálculo a mano: 1 (pelada) + 98 (de 02 a 99 inclusive, porque 99 - 2 + 1 = 98) = 99.
        var stem = "autoexec.cfg.fragforge-backup-20261113-100000";
        var paths = new List<string> { Path.Combine(_cfg, stem) };
        for (var n = 2; n <= 99; n++)
            paths.Add(Path.Combine(_cfg, stem + "-" + n.ToString("D2", CultureInfo.InvariantCulture)));
        Assert.Equal(99, paths.Count);
        foreach (var path in paths) await File.WriteAllTextAsync(path, "copia " + Path.GetFileName(path));

        // Hashes ANTES, para probar después que NADA cambió.
        var hashesBefore = paths.Select(p => (p, Cs2ProfileExporter.Sha256(File.ReadAllBytes(p)))).ToArray();
        var autoexecBefore = Cs2ProfileExporter.Sha256(originalBytes);

        var result = await Cs2ProfileExporter.ExportAsync(GameBlock());

        Assert.False(result.Written);
        var message = Assert.Single(result.Messages);
        Assert.Contains("99 copias de seguridad", message, StringComparison.Ordinal);
        Assert.Contains("No se ha modificado autoexec.cfg", message, StringComparison.Ordinal);
        Assert.Null(result.BackupPath);

        // autoexec byte a byte intacto y las 99 copias con el mismo hash que antes.
        Assert.Equal(autoexecBefore, Cs2ProfileExporter.Sha256(await File.ReadAllBytesAsync(autoexec)));
        foreach (var (path, hashBefore) in hashesBefore)
            Assert.Equal(hashBefore, Cs2ProfileExporter.Sha256(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public void ChooseBackupPath_UsesZeroPaddedCounters_SoOrdinalOrderMatchesCreationOrder()
    {
        var dir = Path.Combine(_root, "contadores");
        Directory.CreateDirectory(dir);
        var stem = "autoexec.cfg.fragforge-backup-20261113-100000";

        // Sin nada: nombre pelado.
        Assert.Equal(Path.Combine(dir, stem), Cs2ProfileExporter.ChooseBackupPath(dir, FixedTime));

        File.WriteAllText(Path.Combine(dir, stem), "x");
        Assert.Equal(Path.Combine(dir, stem + "-02"), Cs2ProfileExporter.ChooseBackupPath(dir, FixedTime));

        // Ocupa -02…-09 y comprueba que el siguiente es -10, no -1.
        // Cálculo a mano: 02,03,04,05,06,07,08,09 son 8 nombres; el que sigue es el 10 -> "-10".
        for (var n = 2; n <= 9; n++)
            File.WriteAllText(Path.Combine(dir, stem + "-" + n.ToString("D2", CultureInfo.InvariantCulture)), "x");
        Assert.Equal(Path.Combine(dir, stem + "-10"), Cs2ProfileExporter.ChooseBackupPath(dir, FixedTime));

        // Orden ordinal de los nombres creados hasta aquí: -10 debe ir DESPUÉS de -09.
        var ordered = Directory.GetFiles(dir, Cs2ProfileGenerator.BackupPrefix + "*")
            .Select(Path.GetFileName)
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(stem + "-10", ordered[0]);
        Assert.Equal(stem + "-09", ordered[1]);
    }

    // ------------------------------------------------------------------ bloque nuevo

    [Fact]
    public async Task NewBlock_IsAppendedAtTheEnd_AndTheOriginalIsByteIdenticalBefore()
    {
        var originalText = "// mi autoexec\nsensitivity 2.5\n";
        var originalBytes = new UTF8Encoding(true).GetBytes(originalText);   // CON BOM
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        await File.WriteAllBytesAsync(autoexec, originalBytes);

        var result = await Cs2ProfileExporter.ExportAsync(GameBlock());

        Assert.True(result.Written);
        var after = await File.ReadAllBytesAsync(autoexec);
        // El prefijo (BOM incluido) debe ser byte a byte el mismo.
        Assert.Equal(originalBytes, after[..originalBytes.Length]);
        // Y el bloque va al final, con sus marcas.
        var afterText = Encoding.UTF8.GetString(after);
        Assert.EndsWith("// >>> FragForge (no editar a mano)\nfps_max 240\nmat_vsync 0\n// <<< FragForge\n", afterText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingBlock_IsReplaced_AndTheRestStaysByteIdentical()
    {
        var before = "// arriba\nbind x \"say hola\"\n" + Cs2ProfileGenerator.WrapInBlock("fps_max 60\n") + "// abajo\n";
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        await File.WriteAllBytesAsync(autoexec, Encoding.UTF8.GetBytes(before));

        var result = await Cs2ProfileExporter.ExportAsync(GameBlock());

        Assert.True(result.Written);
        var afterText = await File.ReadAllTextAsync(autoexec);

        // Lo de antes y lo de después del bloque se conserva; el bloque viejo (fps_max 60) desaparece.
        Assert.StartsWith("// arriba\nbind x \"say hola\"\n", afterText, StringComparison.Ordinal);
        Assert.Contains("fps_max 240", afterText, StringComparison.Ordinal);
        Assert.DoesNotContain("fps_max 60", afterText, StringComparison.Ordinal);
        Assert.Contains("// abajo\n", afterText, StringComparison.Ordinal);
        // Un solo bloque: la marca de apertura aparece una vez.
        Assert.Equal(1, afterText.Split("// >>> FragForge").Length - 1);
    }

    [Fact]
    public async Task Backup_IsCreatedAndVerified_WithTheExpectedName()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        var original = Encoding.UTF8.GetBytes("sensitivity 2.5\n");
        await File.WriteAllBytesAsync(autoexec, original);

        var result = await Cs2ProfileExporter.ExportAsync(GameBlock());

        Assert.True(result.Written);
        // Nombre calculado a mano: prefijo + fecha "yyyyMMdd-HHmmss" de 2026-11-13T10:00:00Z.
        var expectedBackup = Path.Combine(_cfg, "autoexec.cfg.fragforge-backup-20261113-100000");
        Assert.Equal(expectedBackup, result.BackupPath);
        Assert.True(File.Exists(expectedBackup));
        // El backup contiene EXACTAMENTE el original, byte a byte.
        Assert.Equal(original, await File.ReadAllBytesAsync(expectedBackup));
        Assert.Contains(result.Messages, m => m.Contains("Copia de seguridad verificada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackupThatFailsVerification_AbortsWithoutWriting()
    {
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        var original = Encoding.UTF8.GetBytes("sensitivity 2.5\n");
        await File.WriteAllBytesAsync(autoexec, original);

        // Se fuerza el fallo de verificación: es el único modo de ejercitar el ABORTO sin un disco roto.
        var result = await Cs2ProfileExporter.ExportAsync(GameBlock(), verifyBackup: (_, _) => false);

        Assert.False(result.Written);
        // El autoexec queda EXACTAMENTE como estaba: el aborto no lo toca.
        Assert.Equal(original, await File.ReadAllBytesAsync(autoexec));
        Assert.Contains(result.Messages, m => m.Contains("Se aborta sin modificar autoexec.cfg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoExistingAutoexec_MeansNoBackup_AndStillWrites()
    {
        var result = await Cs2ProfileExporter.ExportAsync(GameBlock());

        Assert.True(result.Written);
        Assert.Null(result.BackupPath);   // no había nada que copiar: no se inventa un backup vacío
        Assert.True(File.Exists(Path.Combine(_cfg, "autoexec.cfg")));
        Assert.Empty(Directory.GetFiles(_cfg, "*.fragforge-backup-*"));
    }

    [Fact]
    public async Task ConflictingUserLine_IsReported_AndLeftUntouched()
    {
        // El usuario ya tiene fps_max fuera del bloque: no se modifica esa línea, se informa.
        var autoexec = Path.Combine(_cfg, "autoexec.cfg");
        await File.WriteAllTextAsync(autoexec, "fps_max 999\n");

        var result = await Cs2ProfileExporter.ExportAsync(GameBlock());

        Assert.True(result.Written);
        var afterText = await File.ReadAllTextAsync(autoexec);
        Assert.StartsWith("fps_max 999\n", afterText, StringComparison.Ordinal);
        Assert.Contains(result.Messages, m => m.Contains("'fps_max' ya estaba definido fuera del bloque", StringComparison.Ordinal));
        Assert.Contains(result.Messages, m => m.Contains("no se ha modificado esa línea", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ destinos y guardas

    [Fact]
    public async Task UserDataDestination_IsRejected_AndNothingIsWritten()
    {
        var userDataCfg = Path.Combine(_steam, "userdata", "123", "730", "local", "cfg");
        Directory.CreateDirectory(userDataCfg);
        var target = Path.Combine(userDataCfg, "autoexec.cfg");

        // La guarda deriva la carpeta cfg del EJECUTABLE, así que esta ruta no puede pasar nunca.
        var request = GameBlock() with { GameExecutablePath = _exe };
        var check = Cs2PathGuard.CheckGameDestination(target, _exe, _steam);

        Assert.False(check.IsAllowed);
        Assert.Equal(PathRejection.InsideUserData, check.Rejection);
        Assert.False(File.Exists(target));
        _ = request;
    }

    [Fact]
    public async Task GameNewFile_RefusesToOverwriteWithoutPermission()
    {
        var target = Path.Combine(_cfg, "fragforge-cs2.cfg");
        await File.WriteAllTextAsync(target, "viejo\n");

        var request = new ExportRequest
        {
            Kind = ExportDestinationKind.GameNewFile,
            GameExecutablePath = _exe,
            SteamRoot = _steam,
            CfgText = CfgBody,
            Timestamp = FixedTime,
            Overwrite = false
        };

        var refused = await Cs2ProfileExporter.ExportAsync(request);
        Assert.False(refused.Written);
        Assert.Equal("viejo\n", await File.ReadAllTextAsync(target));   // intacto

        var allowed = await Cs2ProfileExporter.ExportAsync(request with { Overwrite = true });
        Assert.True(allowed.Written);
        // El contenido escrito es EXACTAMENTE el CfgText de la petición (aquí no hay generador de por medio:
        // el exportador no añade ni quita nada). Antes se afirmaba que empezaba por "// FragForge", que solo
        // sería cierto si la petición llevara el encabezado del generador: era el test el que estaba mal.
        Assert.Equal("fps_max 240\nmat_vsync 0\n", await File.ReadAllTextAsync(target));
        // Y el archivo dejó de contener el valor anterior: la sobrescritura fue real, no un no-op.
        Assert.DoesNotContain("viejo", await File.ReadAllTextAsync(target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderDestination_WritesTheNewFileName_NeverAutoexec()
    {
        using var temp = new TempDirectory();

        var result = await Cs2ProfileExporter.ExportAsync(new ExportRequest
        {
            Kind = ExportDestinationKind.Folder,
            FolderPath = temp.Path,
            CfgText = CfgBody,
            Timestamp = FixedTime
        });

        Assert.True(result.Written);
        Assert.Equal(Path.Combine(temp.Path, "fragforge-cs2.cfg"), result.TargetPath);
        Assert.True(File.Exists(result.TargetPath));
        // Un destino "carpeta" no toca nada de Steam y no crea autoexec.cfg.
        Assert.False(File.Exists(Path.Combine(temp.Path, "autoexec.cfg")));
    }

    [Fact]
    public async Task WrittenFile_HasNoBom_AndUsesLfEndings()
    {
        using var temp = new TempDirectory();

        var result = await Cs2ProfileExporter.ExportAsync(new ExportRequest
        {
            Kind = ExportDestinationKind.Folder,
            FolderPath = temp.Path,
            CfgText = "fps_max 240\r\nmat_vsync 0\r\n",   // entrada CRLF
            Timestamp = FixedTime
        });

        var bytes = await File.ReadAllBytesAsync(result.TargetPath!);
        // Sin BOM: los tres primeros bytes no son EF BB BF.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        // Los CRLF de entrada se normalizan a \n: 2 líneas -> exactamente 2 saltos, y ningún \r.
        Assert.Equal(2, bytes.Count(b => b == (byte)'\n'));
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public void Sha256_IsLowercaseHex_OfKnownValue()
    {
        // SHA-256 de "abc" es un vector público conocido: ba7816bf 8f01cfea 414140de 5dae2223 ...
        var hash = Cs2ProfileExporter.Sha256("abc"u8.ToArray());

        Assert.Equal(64, hash.Length);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    [Fact]
    public void MergeBlock_IsPureAndKeepsTheOriginalWhenThereIsNoBlock()
    {
        var original = "a\nb\n"u8.ToArray();
        var merged = Cs2ProfileExporter.MergeBlock(original, "fps_max 1\n");
        var text = Encoding.UTF8.GetString(merged);

        Assert.StartsWith("a\nb\n", text, StringComparison.Ordinal);
        Assert.EndsWith("fps_max 1\n// <<< FragForge\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeBlock_OnEmptyFile_WritesOnlyTheBlock()
    {
        var merged = Cs2ProfileExporter.MergeBlock([], "fps_max 1\n");

        Assert.Equal("// >>> FragForge (no editar a mano)\nfps_max 1\n// <<< FragForge\n", Encoding.UTF8.GetString(merged));
    }
}
