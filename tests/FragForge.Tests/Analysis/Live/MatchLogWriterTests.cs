using System.Text;
using FragForge.Core.Analysis.Live;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

public sealed class MatchLogWriterTests
{
    private static DateTimeOffset Clock() => new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task Save_CreatesDirectoryAndUtf8WithoutBom_AndCollisionPreservesOriginal()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "matches");
        var writer = new MatchLogWriter(directory, Clock);
        var first = await writer.SaveAsync("medición á", CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(first.Path!);
        var second = await writer.SaveAsync("segundo", CancellationToken.None);
        // El primer nombre usa fecha/hora fija; la primera colisión usa sufijo 02.
        Assert.True(first.Saved);
        Assert.Equal("match-20260102-030405.txt", Path.GetFileName(first.Path));
        Assert.Equal(Encoding.UTF8.GetBytes("medición á"), bytes);
        Assert.Equal("medición á", await File.ReadAllTextAsync(first.Path!));
        Assert.True(second.Saved);
        Assert.Equal("match-20260102-030405-02.txt", Path.GetFileName(second.Path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Path!));
        Assert.Equal("segundo", await File.ReadAllTextAsync(second.Path!));
        Assert.Empty(Directory.GetFiles(directory, ".match-*.tmp"));
    }

    [Fact]
    public async Task Save_All99Occupied_DoesNotChangeFilesOrLeaveTemporary()
    {
        using var temp = new TempDirectory();
        var originals = new Dictionary<string, byte[]>();
        for (var i = 1; i <= 99; i++)
        {
            var suffix = i == 1 ? "" : "-" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(temp.Path, "match-20260102-030405" + suffix + ".txt");
            var data = Encoding.UTF8.GetBytes("original " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await File.WriteAllBytesAsync(path, data);
            originals[path] = data;
        }
        var result = await new MatchLogWriter(temp.Path, Clock).SaveAsync("nuevo", CancellationToken.None);
        // 1 nombre base + 98 sufijos (02 a 99) = 99 ocupados.
        Assert.False(result.Saved);
        Assert.Null(result.Path);
        foreach (var pair in originals) Assert.Equal(pair.Value, await File.ReadAllBytesAsync(pair.Key));
        Assert.Empty(Directory.GetFiles(temp.Path, ".match-*.tmp"));
    }

    [Theory]
    [InlineData("steamapps")]
    [InlineData("USERDATA")]
    public async Task Save_RestrictedSegment_DoesNotCreateDirectory(string segment)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, segment, "matches");
        var result = await new MatchLogWriter(path, Clock).SaveAsync("x", CancellationToken.None);
        // Un segmento prohibido rechaza antes de crear cualquier directorio.
        Assert.False(result.Saved);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, segment)));
    }

    [Fact]
    public async Task Save_ParentIsFile_ReturnsFailure()
    {
        using var temp = new TempDirectory();
        var parent = Path.Combine(temp.Path, "archivo");
        await File.WriteAllTextAsync(parent, "original");
        var result = await new MatchLogWriter(Path.Combine(parent, "matches"), Clock).SaveAsync("x", CancellationToken.None);
        // Un archivo no puede ser el padre de un directorio; permanece intacto.
        Assert.False(result.Saved);
        Assert.Equal("original", await File.ReadAllTextAsync(parent));
        Assert.Empty(Directory.GetFiles(temp.Path, ".match-*.tmp"));
    }
}
