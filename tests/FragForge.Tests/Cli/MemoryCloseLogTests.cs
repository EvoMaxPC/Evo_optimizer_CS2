using FragForge.Cli;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class MemoryCloseLogTests
{
    private static readonly DateTimeOffset Now = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private sealed class Temp : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "fragforge-test-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    [Fact]
    public async Task NamesAndSuffixDoNotOverwrite()
    {
        using var temp = new Temp();
        var first = new MemoryCloseLog(temp.DirectoryPath, () => Now);
        var second = new MemoryCloseLog(temp.DirectoryPath, () => Now);
        await first.ReserveAsync("primero\n", CancellationToken.None);
        await second.ReserveAsync("segundo\n", CancellationToken.None);
        await first.AppendAsync("final\n", CancellationToken.None);
        Assert.Equal(Path.Combine(temp.DirectoryPath, "cierre-20250102-030405.txt"), first.Path);
        Assert.Equal(Path.Combine(temp.DirectoryPath, "cierre-20250102-030405-02.txt"), second.Path);
        Assert.Equal("primero\nfinal\n", await File.ReadAllTextAsync(first.Path!));
        Assert.Equal("segundo\n", await File.ReadAllTextAsync(second.Path!));
    }

    [Fact]
    public async Task NinetyNineOccupiedNamesAbortWithoutOverwrite()
    {
        using var temp = new Temp();
        await File.WriteAllTextAsync(Path.Combine(temp.DirectoryPath, "cierre-20250102-030405.txt"), "original");
        for (int i = 2; i <= 99; i++) await File.WriteAllTextAsync(Path.Combine(temp.DirectoryPath, $"cierre-20250102-030405-{i:00}.txt"), "original");
        var log = new MemoryCloseLog(temp.DirectoryPath, () => Now);
        await Assert.ThrowsAsync<IOException>(() => log.ReserveAsync("nuevo", CancellationToken.None));
        Assert.False(log.Reserved); Assert.Null(log.Path);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(temp.DirectoryPath, "cierre-20250102-030405-99.txt")));
        var expected = Enumerable.Range(2, 98).Select(i => $"cierre-20250102-030405-{i:00}.txt")
            .Prepend("cierre-20250102-030405.txt").OrderBy(x => x, StringComparer.Ordinal);
        var actual = Directory.GetFiles(temp.DirectoryPath).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }
    [Fact]
    public async Task Utf8BomAndFlushedLinesSurviveExceptionBeforeSummary()
    {
        using var temp = new Temp();
        var log = new MemoryCloseLog(temp.DirectoryPath, () => Now);
        await log.ReserveAsync("", CancellationToken.None);
        async Task Interrupted()
        {
            await log.AppendAsync("Intención: á\n", CancellationToken.None);
            await log.AppendAsync("Resultado: ok\n", CancellationToken.None);
            throw new InvalidOperationException();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(Interrupted);
        var bytes = await File.ReadAllBytesAsync(log.Path!);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal("Intención: á\nResultado: ok\n", await File.ReadAllTextAsync(log.Path!));
    }

}
