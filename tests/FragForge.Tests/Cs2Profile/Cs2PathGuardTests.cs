using FragForge.Core.Cs2Profile;
using Xunit;

namespace FragForge.Tests.Cs2Profile;

/// <summary>
/// Guardas de ruta. Las rutas se construyen con <see cref="Path.Combine"/> sobre una base TEMPORAL real en
/// vez de escribir literales <c>C:\...</c>: así la misma prueba vale en Windows y en Linux, y no depende de
/// que exista una instalación de Steam. No se toca ninguna carpeta de Steam: solo se comparan cadenas.
/// </summary>
public sealed class Cs2PathGuardTests
{
    // Base con espacios y paréntesis, como la ruta real de Program Files.
    private static readonly string Steam = Path.Combine(Path.GetTempPath(), "ff cs2 tests", "Steam (x86)");
    private static readonly string GameRoot = Path.Combine(Steam, "steamapps", "common", "Counter-Strike Global Offensive");
    private static readonly string Exe = Path.Combine(GameRoot, "game", "bin", "win64", "cs2.exe");
    private static readonly string Cfg = Path.Combine(GameRoot, "game", "csgo", "cfg");

    [Fact]
    public void CfgDirectory_IsDerivedFromTheExecutable()
    {
        Assert.Equal(Cfg, Cs2PathGuard.CfgDirectoryFor(Exe));
        Assert.Null(Cs2PathGuard.CfgDirectoryFor(null));
        Assert.Null(Cs2PathGuard.CfgDirectoryFor(""));
    }

    [Fact]
    public void StartWithBypass_SteamVsSteam2_IsRejected()
    {
        // "Steam (x86) 2" empieza por "Steam (x86)" como TEXTO. Si la comparación fuera StartsWith, la
        // userdata de la segunda instalación pasaría por la de la primera. Debe rechazarse.
        Assert.False(Cs2PathGuard.AreSamePath(Path.Combine(Steam, "game", "csgo", "cfg"), Path.Combine(Steam + " 2", "game", "csgo", "cfg")));
        Assert.False(Cs2PathGuard.IsInsideUserData(Path.Combine(Steam + " 2", "userdata", "123", "730", "local", "cfg", "autoexec.cfg"), Steam));
    }

    [Fact]
    public void AllowedGamePaths_AreAccepted_WithMixedCaseAndSeparators()
    {
        // Ruta con separadores mezclados y mayúsculas cambiadas.
        var messy = Cfg.Replace('\\', '/').ToUpperInvariant() + "/autoexec.cfg";
        var check = Cs2PathGuard.CheckGameDestination(messy, Exe, Steam);

        Assert.True(check.IsAllowed, check.Reason);
        Assert.Equal(PathRejection.None, check.Rejection);
        Assert.EndsWith("autoexec.cfg", check.NormalizedPath, StringComparison.Ordinal);
    }

    [Fact]
    public void DotDot_IsResolved_AndCannotEscapeTheCfgFolder()
    {
        // Se intenta salir de cfg con "..": tras normalizar cae en game\csgo, que no es la carpeta cfg.
        var escape = Cs2PathGuard.CheckGameDestination(Path.Combine(Cfg, "..", "autoexec.cfg"), Exe, Steam);
        Assert.False(escape.IsAllowed);
        Assert.Equal(PathRejection.NotTheCfgFolder, escape.Rejection);

        // Y un ".." que sube y vuelve a entrar SÍ es válido: la ruta final es la misma.
        var roundTrip = Cs2PathGuard.CheckGameDestination(Path.Combine(Cfg, "..", "cfg", "autoexec.cfg"), Exe, Steam);
        Assert.True(roundTrip.IsAllowed, roundTrip.Reason);
    }

    [Fact]
    public void UserData_IsRejected_EvenWithAnAllowedFileName()
    {
        var check = Cs2PathGuard.CheckGameDestination(
            Path.Combine(Steam, "userdata", "12345678", "730", "local", "cfg", "autoexec.cfg"), Exe, Steam);

        Assert.False(check.IsAllowed);
        // Se rechaza por userdata ANTES de quejarse de la carpeta: el motivo debe ser el correcto.
        Assert.Equal(PathRejection.InsideUserData, check.Rejection);
        Assert.Contains("userdata", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherFilesInTheCfgFolder_AreRejected()
    {
        var check = Cs2PathGuard.CheckGameDestination(Path.Combine(Cfg, "cs2_video.txt"), Exe, Steam);

        Assert.False(check.IsAllowed);
        Assert.Equal(PathRejection.NotAnAllowedFileName, check.Rejection);
    }

    [Fact]
    public void AnotherInstallation_IsRejected()
    {
        // Un cs2.exe distinto implica otra carpeta cfg esperada: la ruta pedida deja de ser válida.
        var otherExe = Path.Combine(Path.GetTempPath(), "otra-biblioteca", "game", "bin", "win64", "cs2.exe");

        var check = Cs2PathGuard.CheckGameDestination(Path.Combine(Cfg, "autoexec.cfg"), otherExe, Steam);

        Assert.False(check.IsAllowed);
        Assert.Equal(PathRejection.NotTheCfgFolder, check.Rejection);
    }

    [Fact]
    public void RelativePath_NeedsABase_AndThenWorks()
    {
        // Sin base no se puede resolver: se rechaza en vez de adivinar el directorio actual.
        var withoutBase = Cs2PathGuard.CheckGameDestination("autoexec.cfg", Exe, Steam, baseDirectory: null);
        Assert.False(withoutBase.IsAllowed);
        Assert.Equal(PathRejection.NotAbsolute, withoutBase.Rejection);

        // Con base = la carpeta cfg, la relativa apunta al sitio correcto.
        var withBase = Cs2PathGuard.CheckGameDestination("autoexec.cfg", Exe, Steam, Cfg);
        Assert.True(withBase.IsAllowed, withBase.Reason);
    }

    [Fact]
    public void AllowedFileNames_AreExactlyTwo()
    {
        Assert.True(Cs2PathGuard.IsAllowedFileName("fragforge-cs2.cfg"));
        Assert.True(Cs2PathGuard.IsAllowedFileName("AUTOEXEC.CFG"));
        Assert.False(Cs2PathGuard.IsAllowedFileName("autoexec.cfg.bak"));
        Assert.False(Cs2PathGuard.IsAllowedFileName("fragforge-cs2.cfg.tmp"));
        Assert.False(Cs2PathGuard.IsAllowedFileName(null));
    }
}
