using FragForge.Actions.Files;
using FragForge.Core.Engine;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cs2;

/// <summary>
/// Capa 8.2 — resolución de cs2.exe por la cadena de Steam.
///
/// Los fixtures son los archivos REALES de Steam (libraryfolders.vdf y appmanifest_730.acf) con datos
/// anonimizados. Se prueban los casos que importan: rutas con espacios, varias bibliotecas, barras
/// escapadas y dobles, y el caso ambiguo (dos instalaciones) que debe devolver <c>null</c> en vez de
/// elegir una.
///
/// El sistema de archivos real se sustituye por una carpeta temporal: los parsers son puros, pero la
/// resolución comprueba <c>Directory.Exists</c>/<c>File.Exists</c>, así que se ejercita de verdad.
/// </summary>
public sealed class Cs2ResolutionTests : IDisposable
{
    private readonly string _temp;

    public Cs2ResolutionTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ff-cs2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- parsers

    [Fact]
    public void LibraryFolders_ParsesPathsWithSpacesAndDoubleBackslashes()
    {
        // Forma real de Steam: la barra invertida va escapada en el VDF.
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Program Files (x86)\\Steam"
                    "label"     ""
                    "apps"      { "730" "1234567890" }
                }
                "1"
                {
                    "path"      "D:\\SteamLibrary"
                    "apps"      { "730" "9876543210" }
                }
            }
            """;

        var paths = SteamCs2Resolver.ParseLibraryFolders(vdf);

        Assert.Equal(2, paths.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", paths[0]);
        Assert.Equal(@"D:\SteamLibrary", paths[1]);
    }

    [Fact]
    public void InstallDir_ComesFromTheManifest_NotFromAFixedFolderName()
    {
        const string acf = """
            "AppState"
            {
                "appid"       "730"
                "name"        "Counter-Strike 2"
                "installdir"  "Counter-Strike Global Offensive"
                "StateFlags"  "4"
            }
            """;
        Assert.Equal("Counter-Strike Global Offensive", SteamCs2Resolver.ParseInstallDir(acf));
    }

    [Fact]
    public void InstallDir_RejectsAPathThatEscapesCommon()
    {
        // Un manifiesto manipulado no puede sacar el ejecutable de steamapps\common.
        const string acf = """
            "AppState" { "installdir"  "..\\..\\Windows" }
            """;
        Assert.Throws<InvalidDataException>(() => SteamCs2Resolver.ParseInstallDir(acf));
    }

    [Theory]
    [InlineData(@"C:\Steam", @"C:\Steam")]
    [InlineData(@"C:/Steam", @"C:\Steam")]
    [InlineData(@"C:\\Steam\\\\Library", @"C:\Steam\Library")]
    [InlineData(@"C:\Program Files (x86)\Steam\", @"C:\Program Files (x86)\Steam")]
    public void NormalizeDirectory_CollapsesSeparators(string raw, string expected)
    {
        Assert.Equal(expected, SteamCs2Resolver.NormalizeDirectory(raw));
    }

    // ---------------------------------------------------------------- resolucion real sobre disco temporal

    [Fact]
    public async Task Resolve_FindsTheExecutableThroughTheManifest()
    {
        var steam = Path.Combine(_temp, "Steam");
        var library = Path.Combine(_temp, "Library With Spaces");
        WriteFile(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), LibraryVdf((0, steam), (1, library)));
        var executable = WriteManifestAndExecutable(library, "Counter-Strike Global Offensive");

        var registry = new FakeRegistry();
        registry.Set(@"HKCU\Software\Valve\Steam", "SteamPath", "String", steam);

        var resolution = await new SteamCs2Resolver(registry).ResolveAsync();

        Assert.True(resolution.Found, resolution.Source);
        Assert.Equal(executable, resolution.ExecutablePath);
        Assert.True(!resolution.Ambiguous);
    }

    [Fact]
    public async Task Resolve_UsesTheInstallDirFromTheManifest_EvenIfTheFolderWasRenamed()
    {
        var steam = Path.Combine(_temp, "Steam");
        WriteFile(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), LibraryVdf((0, steam)));
        // Carpeta renombrada: con la ruta fija del locator viejo no se encontraría.
        var executable = WriteManifestAndExecutable(steam, "MiCarpetaDeCS2");

        var registry = new FakeRegistry();
        registry.Set(@"HKCU\Software\Valve\Steam", "SteamPath", "String", steam);

        var resolution = await new SteamCs2Resolver(registry).ResolveAsync();

        Assert.True(resolution.Found, resolution.Source);
        Assert.Equal(executable, resolution.ExecutablePath);
    }

    [Fact]
    public async Task Resolve_WithoutSteam_ReturnsNotFoundWithAReason_NotAGuess()
    {
        // Sin registro y sin carpeta por omision: no hay nada que probar.
        var resolution = await new SteamCs2Resolver(new FakeRegistry()).ResolveAsync();

        Assert.True(!resolution.Found);
        Assert.Null(resolution.ExecutablePath);
        Assert.True(!string.IsNullOrWhiteSpace(resolution.Source), "si no se encuentra, el motivo es obligatorio");
        Assert.Empty(resolution.Candidates);
    }

    [Fact]
    public async Task Resolve_WithTwoInstallations_IsAmbiguousAndDoesNotPickOne()
    {
        var steam = Path.Combine(_temp, "Steam");
        var other = Path.Combine(_temp, "Other");
        WriteFile(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), LibraryVdf((0, steam), (1, other)));
        WriteManifestAndExecutable(steam, "Counter-Strike Global Offensive");
        WriteManifestAndExecutable(other, "Counter-Strike Global Offensive");

        var registry = new FakeRegistry();
        registry.Set(@"HKCU\Software\Valve\Steam", "SteamPath", "String", steam);

        var resolution = await new SteamCs2Resolver(registry).ResolveAsync();

        Assert.True(!resolution.Found, "con dos instalaciones no se elige una: se pide seleccion manual");
        Assert.True(resolution.Ambiguous);
        Assert.Equal(2, resolution.Candidates.Count);
    }

    [Fact]
    public async Task Resolve_WithManifestButNoExecutable_ReturnsNotFound()
    {
        var steam = Path.Combine(_temp, "Steam");
        WriteFile(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), LibraryVdf((0, steam)));
        // Manifiesto presente pero el ejecutable no: instalacion a medias.
        WriteFile(Path.Combine(steam, "steamapps", "appmanifest_730.acf"), Manifest("Counter-Strike Global Offensive"));

        var registry = new FakeRegistry();
        registry.Set(@"HKCU\Software\Valve\Steam", "SteamPath", "String", steam);

        var resolution = await new SteamCs2Resolver(registry).ResolveAsync();

        Assert.True(!resolution.Found);
        Assert.Empty(resolution.Candidates);
        Assert.Contains("cs2.exe", resolution.Source, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- helpers

    private static string LibraryVdf(params (int Index, string Path)[] libraries)
    {
        // Raw interpolado con DOS $, igual que Manifest() unas lineas mas abajo: "{{...}}" interpola y las
        // llaves sueltas son contenido literal. Con un solo $ la cadena no compila de ninguna forma: "{{" no
        // es un escape en un raw (CS9006, y de ahi el CS1733 en cascada) y "{" suelto abre un hueco de
        // interpolacion, asi que el texto de dentro se intenta leer como expresion.
        var entries = libraries.Select(l => $$"""
                "{{l.Index}}"
                {
                    "path"  "{{l.Path.Replace(@"\", @"\\")}}"
                }
            """);
        return "\"libraryfolders\"\n{\n" + string.Join("\n", entries) + "\n}\n";
    }

    private static string Manifest(string installDir) => $$"""
        "AppState"
        {
            "appid"      "730"
            "installdir" "{{installDir}}"
        }
        """;

    private string WriteManifestAndExecutable(string library, string installDir)
    {
        WriteFile(Path.Combine(library, "steamapps", "appmanifest_730.acf"), Manifest(installDir));
        return WriteFile(Path.Combine(library, "steamapps", "common", installDir, @"game\bin\win64\cs2.exe"), "stub");
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}

/// <summary>
/// Capa 8.7 — detección de CS2 en ejecución. Función pura: no toca el sistema.
/// </summary>
public sealed class Cs2ProcessTests
{
    [Theory]
    [InlineData("cs2", true)]
    [InlineData("CS2", true)]
    [InlineData("Cs2", true)]
    [InlineData("csgo", false)]
    [InlineData("cs2.exe", false)]   // el nombre de imagen SIN extension es "cs2"
    [InlineData("steam", false)]
    public void IsRunning_ComparesTheProcessNameWithoutExtension(string name, bool expected)
    {
        Assert.Equal(expected, Cs2Process.IsRunning([name]));
    }

    [Fact]
    public void IsRunning_FindsCs2AmongManyProcesses()
    {
        Assert.True(Cs2Process.IsRunning(["explorer", "steam", "cs2", "discord"]));
    }

    [Fact]
    public void IsRunning_WithEmptyList_IsFalse()
    {
        Assert.False(Cs2Process.IsRunning([]));
    }

    /// <summary>
    /// No poder enumerar procesos NO es "no corre". Si devolviera false, el modo sesión revertiría con el
    /// juego abierto.
    /// </summary>
    [Fact]
    public void IsRunning_WithNullSource_IsUnknown_NotFalse()
    {
        Assert.Null(Cs2Process.IsRunning(null));
    }

    [Fact]
    public void ProcessNameConstant_IsTheImageNameWithoutExtension()
    {
        Assert.Equal("cs2", Cs2Process.ProcessName);
    }
}
