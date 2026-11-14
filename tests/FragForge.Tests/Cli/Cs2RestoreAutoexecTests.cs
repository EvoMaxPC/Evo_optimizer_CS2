using FragForge.Actions.Files;
using FragForge.Cli;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cli;

/// <summary>
/// <c>fragforge cs2 restore-autoexec</c> de punta a punta, con una instalación FICTICIA de CS2 en una
/// carpeta temporal y un <see cref="Cs2Inspector"/> alimentado por resolver y registro falsos. Nunca se
/// lee ni se escribe nada de una instalación real de Steam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Qué se verifica y qué no.</b> El restore comprueba que la copia restaurada es idéntica al backup
/// elegido, pero NO puede detectar un backup alterado DESPUÉS de crearse: no existe ningún hash guardado
/// del original contra el que comparar. Un backup no vacío con el contenido cambiado se restaura sin
/// avisar. Los tests de abajo reflejan exactamente eso y no fingen más de lo que hay.
/// </para>
/// </remarks>
public sealed class Cs2RestoreAutoexecTests : IDisposable
{
    private const string BackupPrefix = "autoexec.cfg.fragforge-backup-";

    private readonly string _root;
    private readonly string _cfg;
    private readonly string _exe;

    public Cs2RestoreAutoexecTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ff-restore-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(_root, "Steam", "steamapps", "common", "Counter-Strike Global Offensive");
        _cfg = Path.Combine(game, "game", "csgo", "cfg");
        _exe = Path.Combine(game, "game", "bin", "win64", "cs2.exe");
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

    // ------------------------------------------------------------------ andamiaje

    /// <summary>Resolver que siempre encuentra el ejecutable ficticio de ESTE test.</summary>
    private sealed class FixedResolver(string executable) : ICs2Resolver
    {
        public Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cs2Resolution { ExecutablePath = executable, Source = "ejecutable ficticio del test" });
    }

    private sealed class FakeServices : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public FakeServices Add<T>(T service) where T : class
        {
            _services[typeof(T)] = service;
            return this;
        }

        public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
    }

    /// <summary>Consola simulada: respuestas preparadas y todo lo escrito capturado.</summary>
    private sealed class FakePrompt(params string?[] answers) : IConsolePrompt
    {
        private readonly Queue<string?> _answers = new(answers);
        public List<string> Output { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Questions { get; } = [];

        public void WriteLine(string text) => Output.Add(text);
        public void WriteWarning(string text) => Warnings.Add(text);

        public string? Ask(string question)
        {
            Questions.Add(question);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }

        public bool? Confirm(string question)
        {
            var answer = Ask(question);
            return answer is null ? null : answer.Trim().Equals("s", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Todo lo impreso, en orden, para buscar la vista previa donde toque.</summary>
        public IReadOnlyList<string> All => [.. Output, .. Warnings];
    }

    /// <summary>Argumentos de <c>cs2 restore-autoexec</c>. Sin <c>--yes</c>, se pregunta.</summary>
    private static ParsedArgs RestoreArgs(bool yes = true)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (yes) options["yes"] = [];
        return new ParsedArgs { Command = "cs2", Positionals = ["restore-autoexec"], Options = options };
    }

    private async Task<(int Code, FakePrompt Prompt)> RunRestoreAsync(ParsedArgs args)
    {
        var prompt = new FakePrompt();
        var services = new FakeServices()
            .Add(new Cs2Inspector(new FixedResolver(_exe), new FakeRegistry()))
            .Add<IConsolePrompt>(prompt);
        var code = await Cs2ProfileCommands.RunAsync(services, args, CancellationToken.None);
        return (code, prompt);
    }

    private string BackupPath(string suffix) => Path.Combine(_cfg, BackupPrefix + suffix);

    private string AutoexecPath => Path.Combine(_cfg, "autoexec.cfg");

    // ------------------------------------------------------------------ camino feliz

    [Fact]
    public async Task HappyPath_RestoresTheBackupByteForByte()
    {
        // Backup con BOM y CRLF: si el restore decodificara y recodificara, se perdería el BOM.
        var backupBytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat("// original\r\nsensitivity 2.5\r\n"u8.ToArray())
            .ToArray();
        var backup = BackupPath("20261113-100000");
        await File.WriteAllBytesAsync(backup, backupBytes);

        // El autoexec actual está modificado y NO coincide con el backup.
        var modificado = "// modificado por FragForge\nfps_max 240\n"u8.ToArray();
        await File.WriteAllBytesAsync(AutoexecPath, modificado);

        var (code, prompt) = await RunRestoreAsync(RestoreArgs());

        Assert.Equal(ExitCodes.Success, code);
        // Byte a byte, incluido el BOM.
        Assert.Equal(Convert.ToHexString(backupBytes), Convert.ToHexString(await File.ReadAllBytesAsync(AutoexecPath)));
        Assert.NotEqual(Convert.ToHexString(modificado), Convert.ToHexString(await File.ReadAllBytesAsync(AutoexecPath)));
        Assert.Contains(prompt.All, line => line.Contains("restaurado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HappyPath_ReportsTheSha256OfTheRestoredBytes()
    {
        // El mensaje imprime el SHA-256 del backup restaurado. Se comprueba con un vector calculado a mano:
        // SHA-256 de "abc" es ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad.
        await File.WriteAllBytesAsync(BackupPath("20261113-100000"), "abc"u8.ToArray());

        var (_, prompt) = await RunRestoreAsync(RestoreArgs());

        Assert.Contains(prompt.All, line => line.Contains(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutYes_RefusesAndLeavesAutoexecUntouched()
    {
        var backupBytes = "// original\n"u8.ToArray();
        await File.WriteAllBytesAsync(BackupPath("20261113-100000"), backupBytes);
        var actual = "// actual\n"u8.ToArray();
        await File.WriteAllBytesAsync(AutoexecPath, actual);

        var args = RestoreArgs(yes: false);
        var prompt = new FakePrompt("n");
        var services = new FakeServices()
            .Add(new Cs2Inspector(new FixedResolver(_exe), new FakeRegistry()))
            .Add<IConsolePrompt>(prompt);

        var code = await Cs2ProfileCommands.RunAsync(services, args, CancellationToken.None);

        // Decir que no no es un error: el usuario decidió, no falló nada.
        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(Convert.ToHexString(actual), Convert.ToHexString(await File.ReadAllBytesAsync(AutoexecPath)));
        Assert.Contains("No se ha restaurado nada.", prompt.Output);
    }

    // ------------------------------------------------------------------ sin backups

    [Fact]
    public async Task WithoutAnyBackup_FailsWithAClearMessage_AndTouchesNothing()
    {
        var actual = "// actual\n"u8.ToArray();
        await File.WriteAllBytesAsync(AutoexecPath, actual);

        var (code, prompt) = await RunRestoreAsync(RestoreArgs());

        Assert.Equal(ExitCodes.Failure, code);
        Assert.Contains(prompt.Warnings, w => w.Contains("No hay ninguna copia de seguridad", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(actual), Convert.ToHexString(await File.ReadAllBytesAsync(AutoexecPath)));
    }

    // ------------------------------------------------------------------ varios backups

    [Fact]
    public async Task SeveralBackups_RestoresTheLastOneByName_WithTheCountWorkedOut()
    {
        // Criterio: OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase) y se toma [0].
        //
        // Cálculo a mano del orden ORDINAL de estos tres nombres, que solo empiezan a diferir justo después
        // del guion final:
        //   1) "…-20261113-100000"        (21 caracteres, termina aquí)
        //   2) "…-20261113-100000-02"     (comparte con (1) y añade "-02")
        //   3) "…-20261113-100000-03"     (comparte con (2) hasta el último carácter)
        // En la comparación, cuando (1) se acaba frente a (2), gana el más largo => (2) > (1).
        // Entre (2) y (3), el último carácter decide: '3' (0x33) > '2' (0x32) => (3) > (2).
        // Orden descendente: (3), (2), (1). El elegido es (3) = "…-100000-03".
        await File.WriteAllBytesAsync(BackupPath("20261113-100000"), "// el mas viejo\n"u8.ToArray());
        await File.WriteAllBytesAsync(BackupPath("20261113-100000-02"), "// intermedio\n"u8.ToArray());
        var esperado = "// el mas reciente\n"u8.ToArray();
        await File.WriteAllBytesAsync(BackupPath("20261113-100000-03"), esperado);

        var (code, prompt) = await RunRestoreAsync(RestoreArgs());

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(Convert.ToHexString(esperado), Convert.ToHexString(await File.ReadAllBytesAsync(AutoexecPath)));
        Assert.Contains(prompt.All, line => line.Contains("100000-03", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALaterSecondWinsOverASuffixOfAnEarlierSecond()
    {
        // Cálculo a mano: "…-100001" contra "…-100000-03".
        // Comparan igual hasta "…-10000"; ahí "1" (0x31) contra "0" (0x30) => 1 > 0.
        // Gana "…-100001" ANTES de llegar al guion del sufijo, así que el del segundo siguiente es el último.
        await File.WriteAllBytesAsync(BackupPath("20261113-100000-03"), "// segundo anterior con sufijo\n"u8.ToArray());
        var esperado = "// segundo siguiente\n"u8.ToArray();
        await File.WriteAllBytesAsync(BackupPath("20261113-100001"), esperado);

        await RunRestoreAsync(RestoreArgs());

        Assert.Equal(Convert.ToHexString(esperado), Convert.ToHexString(await File.ReadAllBytesAsync(AutoexecPath)));
    }

    // ------------------------------------------------------------------ backup inutilizable

    [Fact]
    public async Task AnEmptyBackup_IsRefused_AndAutoexecIsUntouched()
    {
        // El ÚNICO defecto que el restore puede detectar en un backup es que esté vacío: no hay hash
        // guardado del original, así que un backup con contenido distinto pero no vacío pasaría.
        await File.WriteAllBytesAsync(BackupPath("20261113-100000"), []);
        var actual = "// actual\n"u8.ToArray();
        await File.WriteAllBytesAsync(AutoexecPath, actual);

        var (code, prompt) = await RunRestoreAsync(RestoreArgs());

        Assert.Equal(ExitCodes.Failure, code);
        Assert.Contains(prompt.Warnings, w => w.Contains("está vacía", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(actual), Convert.ToHexString(await File.ReadAllBytesAsync(AutoexecPath)));
    }

    [Fact]
    public async Task AnAlteredButNonEmptyBackup_IsRestoredWithoutWarning_ThisIsTheDocumentedGap()
    {
        // Este test FIJA la limitación en vez de disimularla: el restore no puede saber que alguien cambió
        // el backup después de crearlo, porque no existe un hash del original guardado en ningún sitio.
        // Si algún día se añade un manifiesto, este test debe cambiar de expectativa a propósito.
        var alterado = "// esto NO es lo que habia en autoexec.cfg\nunbindall\n"u8.ToArray();
        await File.WriteAllBytesAsync(BackupPath("20261113-100000"), alterado);
        await File.WriteAllBytesAsync(AutoexecPath, "// actual\n"u8.ToArray());

        var (code, _) = await RunRestoreAsync(RestoreArgs());

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(Convert.ToHexString(alterado), Convert.ToHexString(await File.ReadAllBytesAsync(AutoexecPath)));
    }

    // ------------------------------------------------------------------ confinamiento

    [Fact]
    public async Task TheRestoreTargetIsAlwaysAutoexecInsideTheCfgFolder_WhateverTheBackupIsCalled()
    {
        // El destino NO viene del usuario ni del nombre del backup: se deriva del ejecutable con
        // Cs2PathGuard.CfgDirectoryFor y el nombre es la constante AutoexecFileName. Por eso no hay forma
        // de restaurar fuera de la carpeta cfg. Aquí el backup tiene un nombre con ".." dentro: aun así
        // solo se escribe autoexec.cfg en la carpeta cfg.
        var raro = Path.Combine(_cfg, BackupPrefix + "..-..-windows-system32");
        await File.WriteAllBytesAsync(raro, "// contenido del backup\n"u8.ToArray());

        var (code, _) = await RunRestoreAsync(RestoreArgs());

        Assert.Equal(ExitCodes.Success, code);
        Assert.True(File.Exists(AutoexecPath), "debe escribir exactamente autoexec.cfg en la carpeta cfg");
        // Ninguna escritura fuera de la carpeta cfg: la carpeta contenedora solo tiene el autoexec y el backup.
        var archivos = Directory.GetFiles(_cfg).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "autoexec.cfg", Path.GetFileName(raro) }, archivos);
    }

    [Fact]
    public async Task WhenTheGameCannotBeLocated_ItFailsBeforeLookingForBackups()
    {
        // Sin ejecutable no hay carpeta cfg: se corta ANTES de buscar backups, con Preflight (3).
        var prompt = new FakePrompt();
        var services = new FakeServices()
            .Add(new Cs2Inspector(new NotFoundResolver(), new FakeRegistry()))
            .Add<IConsolePrompt>(prompt);

        var code = await Cs2ProfileCommands.RunAsync(services, RestoreArgs(), CancellationToken.None);

        Assert.Equal(ExitCodes.Preflight, code);
        Assert.Contains(prompt.Warnings, w => w.Contains("No se localizó la instalación de CS2", StringComparison.Ordinal));
    }

    private sealed class NotFoundResolver : ICs2Resolver
    {
        public Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cs2Resolution { Source = "no encontrado (test)" });
    }
}
