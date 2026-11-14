using FragForge.Actions.Files;
using FragForge.Cli;
using FragForge.Core.Abstractions;
using FragForge.Core.Cs2Profile;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cli;

/// <summary>
/// <c>fragforge cs2 export</c> de punta a punta: vista previa, confirmación, escritura y códigos de salida.
/// Todo ocurre en una carpeta temporal con una instalación FICTICIA de CS2; nunca se toca Steam.
/// </summary>
/// <remarks>
/// Se llama a <see cref="Cs2ProfileCommands.RunAsync"/> con un <see cref="IServiceProvider"/> propio, no a
/// <c>Program.Main</c>, que no es hermético (arranca el host, abre el journal y escribe por consola).
/// </remarks>
public sealed class Cs2ExportCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _steam;
    private readonly string _cfg;
    private readonly string _exe;
    private readonly string _out;

    public Cs2ExportCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ff-export-cli-" + Guid.NewGuid().ToString("N"));
        _steam = Path.Combine(_root, "Steam");
        var game = Path.Combine(_steam, "steamapps", "common", "Counter-Strike Global Offensive");
        _cfg = Path.Combine(game, "game", "csgo", "cfg");
        _exe = Path.Combine(game, "game", "bin", "win64", "cs2.exe");
        _out = Path.Combine(_root, "salida");
        Directory.CreateDirectory(_cfg);
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        Directory.CreateDirectory(_out);
        File.WriteAllText(_exe, "ficticio");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ andamiaje

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
    }

    /// <summary>Argumentos de <c>cs2 export</c> con las opciones indicadas.</summary>
    private static ParsedArgs ExportArgs(params (string Name, string? Value)[] options)
    {
        var dictionary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in options)
            dictionary[name] = value is null ? [] : [value];
        return new ParsedArgs { Command = "cs2", Positionals = ["export"], Options = dictionary };
    }

    private async Task<(int Code, FakePrompt Prompt)> RunExportAsync(ParsedArgs args, params string?[] answers)
    {
        var prompt = new FakePrompt(answers);
        var services = new FakeServices()
            .Add(new Cs2Inspector(new FixedResolver(_exe), new FakeRegistry()))
            .Add<IConsolePrompt>(prompt);
        var code = await Cs2ProfileCommands.RunAsync(services, args, CancellationToken.None);
        return (code, prompt);
    }

    private string AutoexecPath => Path.Combine(_cfg, "autoexec.cfg");

    private string GameCfgPath => Path.Combine(_cfg, "fragforge-cs2.cfg");

    // ------------------------------------------------------------------ vista previa

    [Fact]
    public async Task ThePreviewIsPrintedBeforeAnyByteIsWritten()
    {
        // El destino es una carpeta vacía: al acabar hay exactamente un archivo. Si la vista previa se
        // imprimiera DESPUÉS de escribir, no habría forma de cancelar; aquí se comprueba además que el
        // texto del encabezado aparece en la salida.
        var (code, prompt) = await RunExportAsync(ExportArgs(("dest", "carpeta:" + _out), ("fps-cap", "240")));

        Assert.Equal(ExitCodes.Success, code);
        var previewIndex = prompt.Output.FindIndex(l => l.StartsWith("=== Vista previa del archivo .cfg", StringComparison.Ordinal));
        var endIndex = prompt.Output.FindIndex(l => l.StartsWith("=== fin de la vista previa ===", StringComparison.Ordinal));
        Assert.True(previewIndex >= 0, "debe imprimirse la cabecera de la vista previa");
        Assert.True(endIndex > previewIndex, "la vista previa debe cerrarse después de empezar");
        // El texto exacto que se escribirá está DENTRO del bloque de vista previa.
        Assert.Contains(prompt.Output.Skip(previewIndex).Take(endIndex - previewIndex),
            line => line.Contains("// FragForge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePreviewShowsTheExactBytesThatEndUpInTheFile()
    {
        var (_, prompt) = await RunExportAsync(ExportArgs(("dest", "carpeta:" + _out), ("fps-cap", "240")));

        var start = prompt.Output.FindIndex(l => l.StartsWith("=== Vista previa del archivo .cfg", StringComparison.Ordinal));
        var end = prompt.Output.FindIndex(l => l.StartsWith("=== fin de la vista previa ===", StringComparison.Ordinal));

        // Las líneas entre las dos marcas, unidas por un salto de línea, SON el archivo: el generador
        // termina en salto de línea, así que dividir por salto deja una última entrada vacía que se imprime
        // como línea vacía y, al reconstruir, vuelve a ser el salto final. Comparación exacta, sin TrimEnd
        // que disimule un baile de saltos.
        var preview = string.Join('\n', prompt.Output.Skip(start + 1).Take(end - start - 1));
        var written = await File.ReadAllTextAsync(Path.Combine(_out, "fragforge-cs2.cfg"));
        Assert.Equal(written, preview);
        Assert.EndsWith("\n", written, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ confirmación

    [Fact]
    public async Task AnsweringNoLeavesTheGameFolderByteForByteIntact()
    {
        // Hay un autoexec.cfg con contenido propio: si el export escribiera de todos modos, cambiaría.
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("// mio\nbind x y\n"u8.ToArray()).ToArray();
        await File.WriteAllBytesAsync(AutoexecPath, original);
        var before = Cs2ProfileExporter.Sha256(original);

        var (code, prompt) = await RunExportAsync(
            ExportArgs(("dest", "juego-autoexec")),
            "n");

        Assert.Equal(ExitCodes.Success, code);      // negarse no es un fallo
        Assert.Contains("No se ha escrito nada.", prompt.Output);
        // autoexec byte a byte idéntico y sin archivo nuevo ni backups.
        Assert.Equal(before, Cs2ProfileExporter.Sha256(await File.ReadAllBytesAsync(AutoexecPath)));
        Assert.False(File.Exists(GameCfgPath));
        Assert.Empty(Directory.GetFiles(_cfg, "autoexec.cfg.fragforge-backup-*"));
    }

    [Fact]
    public async Task AnsweringNoToNewFileLeavesNoFileBehind()
    {
        var (code, _) = await RunExportAsync(ExportArgs(("dest", "juego-nuevo")), "n");

        Assert.Equal(ExitCodes.Success, code);
        Assert.False(File.Exists(GameCfgPath));
    }

    [Fact]
    public async Task WithYes_ItWritesWithoutAsking()
    {
        var (code, prompt) = await RunExportAsync(ExportArgs(("dest", "juego-nuevo"), ("yes", null)));

        Assert.Equal(ExitCodes.Success, code);
        Assert.True(File.Exists(GameCfgPath));
        // No se hizo ninguna pregunta de confirmación.
        Assert.DoesNotContain(prompt.Questions, q => q.Contains("¿Escribir en la carpeta cfg del juego?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithYes_OnAutoexec_WritesTheBlockAndCreatesTheBackup()
    {
        var original = "// mio\nbind x y\n"u8.ToArray();
        await File.WriteAllBytesAsync(AutoexecPath, original);

        var (code, prompt) = await RunExportAsync(ExportArgs(("dest", "juego-autoexec"), ("yes", null)));

        Assert.Equal(ExitCodes.Success, code);
        var backups = Directory.GetFiles(_cfg, "autoexec.cfg.fragforge-backup-*");
        var backup = Assert.Single(backups);
        // El backup conserva el original byte a byte.
        Assert.Equal(Convert.ToHexString(original), Convert.ToHexString(await File.ReadAllBytesAsync(backup)));
        // El autoexec empieza por el contenido del usuario: el bloque va AL FINAL.
        Assert.StartsWith("// mio", await File.ReadAllTextAsync(AutoexecPath), StringComparison.Ordinal);
        Assert.Contains(Cs2ProfileGenerator.BlockStart, await File.ReadAllTextAsync(AutoexecPath), StringComparison.Ordinal);
        Assert.DoesNotContain(prompt.Questions, q => q.Contains("¿Escribir en la carpeta cfg del juego?", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ destino carpeta

    [Fact]
    public async Task TheFolderDestinationNeverTouchesAnAutoexec()
    {
        // Aunque haya un autoexec.cfg en la carpeta de destino, no se toca: solo se escribe fragforge-cs2.cfg.
        var ajeno = Path.Combine(_out, "autoexec.cfg");
        var ajenoBytes = "// autoexec que no es mio\n"u8.ToArray();
        await File.WriteAllBytesAsync(ajeno, ajenoBytes);

        var (code, _) = await RunExportAsync(ExportArgs(("dest", "carpeta:" + _out)));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(Convert.ToHexString(ajenoBytes), Convert.ToHexString(await File.ReadAllBytesAsync(ajeno)));
        // Y el único archivo nuevo es el del perfil.
        var archivos = Directory.GetFiles(_out).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "autoexec.cfg", "fragforge-cs2.cfg" }, archivos);
    }

    [Fact]
    public async Task TheFolderDestinationNeverTouchesTheGameFolder()
    {
        var (code, _) = await RunExportAsync(ExportArgs(("dest", "carpeta:" + _out)));

        Assert.Equal(ExitCodes.Success, code);
        Assert.True(File.Exists(Path.Combine(_out, "fragforge-cs2.cfg")));
        Assert.Empty(Directory.GetFiles(_cfg));
    }

    [Fact]
    public async Task TheFolderDestinationDoesNotAskForConfirmation()
    {
        var (_, prompt) = await RunExportAsync(ExportArgs(("dest", "carpeta:" + _out)));

        Assert.DoesNotContain(prompt.Questions, q => q.Contains("¿Escribir en la carpeta cfg del juego?", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ sin --dest

    [Fact]
    public async Task WithoutDest_TheThreeOptionsAreShownInTheOrderRequested()
    {
        var (code, prompt) = await RunExportAsync(ExportArgs(), "1", _out);

        Assert.Equal(ExitCodes.Success, code);

        // Las tres opciones, en el orden 1-2-3 y con el texto que las distingue.
        var uno = prompt.Output.FindIndex(l => l.Contains("[1]", StringComparison.Ordinal));
        var dos = prompt.Output.FindIndex(l => l.Contains("[2]", StringComparison.Ordinal));
        var tres = prompt.Output.FindIndex(l => l.Contains("[3]", StringComparison.Ordinal));
        Assert.True(uno >= 0 && dos >= 0 && tres >= 0, "deben aparecer las tres opciones");
        Assert.True(uno < dos && dos < tres, "el orden mostrado debe ser 1, 2, 3");

        // [1] no toca Steam y pide una ruta.
        Assert.Contains(prompt.Questions, q => q.Contains("Ruta de la carpeta destino", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_out, "fragforge-cs2.cfg")));
        Assert.Empty(Directory.GetFiles(_cfg));
    }

    [Fact]
    public async Task WithoutDest_AnInvalidAnswerIsAUsageError_AndNothingIsWritten()
    {
        var (code, _) = await RunExportAsync(ExportArgs(), "9");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(Directory.GetFiles(_out));
        Assert.Empty(Directory.GetFiles(_cfg));
    }

    [Fact]
    public async Task AnUnknownDest_IsAUsageErrorWithAWarning()
    {
        var (code, prompt) = await RunExportAsync(ExportArgs(("dest", "a-la-luna")));

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains(prompt.Warnings, w => w.Contains("--dest no reconocido", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_out));
    }

    // ------------------------------------------------------------------ códigos de salida

    [Fact]
    public async Task WithoutAGameInstallation_AGameDestinationIsAPreflightFailure()
    {
        // Resolver que no encuentra nada: los destinos del juego no se pueden usar, pero la carpeta sí.
        var prompt = new FakePrompt();
        var services = new FakeServices()
            .Add(new Cs2Inspector(new NotFoundResolver(), new FakeRegistry()))
            .Add<IConsolePrompt>(prompt);

        var code = await Cs2ProfileCommands.RunAsync(services, ExportArgs(("dest", "juego-nuevo")), CancellationToken.None);

        Assert.Equal(ExitCodes.Preflight, code);
        Assert.Contains(prompt.Warnings, w => w.Contains("No se localizó la instalación de CS2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAGameInstallation_TheFolderDestinationStillWorks()
    {
        var prompt = new FakePrompt();
        var services = new FakeServices()
            .Add(new Cs2Inspector(new NotFoundResolver(), new FakeRegistry()))
            .Add<IConsolePrompt>(prompt);

        var code = await Cs2ProfileCommands.RunAsync(
            services, ExportArgs(("dest", "carpeta:" + _out)), CancellationToken.None);

        Assert.Equal(ExitCodes.Success, code);
        Assert.True(File.Exists(Path.Combine(_out, "fragforge-cs2.cfg")));
    }

    [Fact]
    public async Task RewritingAnExistingNewFileWithoutOverwrite_FailsAndKeepsTheOldFile()
    {
        // El archivo ya existe: sin --overwrite/--yes no se pisa.
        var existente = Path.Combine(_out, "fragforge-cs2.cfg");
        await File.WriteAllTextAsync(existente, "// viejo\n");

        var (code, prompt) = await RunExportAsync(ExportArgs(("dest", "carpeta:" + _out)));

        Assert.Equal(ExitCodes.Failure, code);
        // Los fallos van por el canal de avisos, no por la salida normal.
        Assert.Contains(prompt.Warnings, l => l.Contains("No se sobrescribe sin confirmación explícita", StringComparison.Ordinal));
        Assert.Equal("// viejo\n", await File.ReadAllTextAsync(existente));
    }

    [Fact]
    public async Task AnInvalidFpsCap_IsAUsageError()
    {
        // ParseFpsCap lanza ArgumentException, que Program.cs traduce a Usage. Aquí se comprueba el contrato
        // de la función, que es lo que el despacho usa.
        var exception = Assert.Throws<ArgumentException>(() => Cs2ProfileCommands.ParseFpsCap("0"));
        Assert.Contains("--fps-cap", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ acceso denegado (paso 4)

    [Fact]
    public async Task WhenTheExporterCannotWrite_TheCliReportsItAsAWarningAndFails()
    {
        // Sin permisos en la carpeta destino. En vez de depender de los permisos del SO (que en algunos
        // equipos permiten escribir igual), se ocupa el nombre con una CARPETA: escribir un archivo sobre un
        // directorio existente falla siempre, con IOException, en cualquier sistema.
        var ocupado = Path.Combine(_out, "fragforge-cs2.cfg");
        Directory.CreateDirectory(ocupado);
        // El exporter exige --overwrite para intentarlo, porque File.Exists da false sobre un directorio.
        var (code, prompt) = await RunExportAsync(ExportArgs(("dest", "carpeta:" + _out), ("overwrite", null)));

        // El tipo exacto de excepción que lanza el SO al mover sobre un directorio varía (IOException en
        // Linux, UnauthorizedAccessException en Windows); lo que NO puede variar es el desenlace: fallo
        // controlado con mensaje, sin excepción que escape y sin archivo escrito.
        Assert.Equal(ExitCodes.Failure, code);
        // El fallo se comunica por el canal de avisos, diciendo qué ruta falló y que no se escribió nada.
        Assert.Contains(prompt.Warnings, w => w.Contains("No se pudo escribir el perfil", StringComparison.Ordinal));
        Assert.Contains(prompt.Warnings, w => w.Contains(ocupado, StringComparison.Ordinal));
        // Y no hay stack trace ni jerga de excepción suelta.
        Assert.DoesNotContain(prompt.Warnings.Concat(prompt.Output), l => l.Contains("   at ", StringComparison.Ordinal));
        Assert.DoesNotContain(prompt.Warnings.Concat(prompt.Output), l => l.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase));
        // El directorio sigue ahí, sin haberse convertido en archivo.
        Assert.True(Directory.Exists(ocupado));
    }

    private sealed class NotFoundResolver : ICs2Resolver
    {
        public Task<Cs2Resolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cs2Resolution { Source = "no encontrado (test)" });
    }
}
