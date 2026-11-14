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
