using FragForge.Core.Cs2Profile;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Cs2Profile;

public sealed class FolderUserDataGuardTests
{
    private const string Rejection = "No se exporta dentro de la carpeta userdata de Steam. Elegí otra carpeta.";

    private static Task<ExportResult> Export(string folder, string? steamRoot = null) =>
        Cs2ProfileExporter.ExportAsync(new ExportRequest
        {
            Kind = ExportDestinationKind.Folder,
            FolderPath = folder,
            SteamRoot = steamRoot,
            CfgText = "fps_max 240\n"
        });

    [Fact]
    public async Task UserDataFolder_IsRejectedWithoutCreatingFilesOrDirectories()
    {
        using var temp = new TempDirectory();
        var steam = Path.Combine(temp.Path, "Steam");
        var userdata = Path.Combine(steam, "userdata");
        Directory.CreateDirectory(userdata);
        var destination = Path.Combine(userdata, "123", "730");

        var result = await Export(destination, steam);

        // Un destino protegido inexistente: cero archivos y ninguna carpeta nueva.
        Assert.False(result.Written);
        Assert.Equal(Rejection, Assert.Single(result.Messages));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(userdata));
    }

    [Fact]
    public async Task DotDotEndingInUserData_IsRejectedWithoutCreatingFilesOrDirectories()
    {
        using var temp = new TempDirectory();
        var steam = Path.Combine(temp.Path, "Steam");
        var userdata = Path.Combine(steam, "userdata");
        Directory.CreateDirectory(userdata);
        var destination = Path.Combine(steam, "otra", "..", "userdata", "123", "730");

        var result = await Export(destination);

        // Tras resolver .., el destino sigue bajo userdata; 0 entradas creadas.
        Assert.False(result.Written);
        Assert.Equal(Rejection, Assert.Single(result.Messages));
        Assert.False(Directory.Exists(Path.Combine(userdata, "123")));
        Assert.False(Directory.Exists(Path.Combine(steam, "otra")));
        Assert.Empty(Directory.GetFileSystemEntries(userdata));
    }

    [Fact]
    public async Task UserDataWithDifferentCapitalization_IsRejectedEvenWithoutSteamRoot()
    {
        using var temp = new TempDirectory();
        var steam = Path.Combine(temp.Path, "Steam");
        var userdata = Path.Combine(steam, "UsErDaTa");
        Directory.CreateDirectory(userdata);
        var destination = Path.Combine(userdata, "123", "730");

        var result = await Export(destination);

        // Comparación sin distinción de mayúsculas: 0 entradas nuevas.
        Assert.False(result.Written);
        Assert.Equal(Rejection, Assert.Single(result.Messages));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(userdata));
    }

    [Fact]
    public async Task UserDataItself_IsRejectedWithUnrelatedSteamRoot()
    {
        using var temp = new TempDirectory();
        var userdata = Path.Combine(temp.Path, "Steam", "userdata");
        Directory.CreateDirectory(userdata);

        var result = await Export(userdata, Path.Combine(temp.Path, "biblioteca-secundaria"));

        // Raíz errónea y destino userdata mismo: cero archivos escritos.
        Assert.False(result.Written);
        Assert.Equal(Rejection, Assert.Single(result.Messages));
        Assert.Empty(Directory.GetFileSystemEntries(userdata));
    }

    [Fact]
    public async Task NormalFolder_ExportsTheRequestedContent()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "salida");

        var result = await Export(destination);

        // Una carpeta normal produce exactamente un archivo con el texto pedido.
        Assert.True(result.Written);
        Assert.Equal(Path.Combine(destination, "fragforge-cs2.cfg"), result.TargetPath);
        Assert.Equal("fps_max 240\n", await File.ReadAllTextAsync(result.TargetPath!));
        Assert.Single(Directory.GetFileSystemEntries(destination));
    }
}
