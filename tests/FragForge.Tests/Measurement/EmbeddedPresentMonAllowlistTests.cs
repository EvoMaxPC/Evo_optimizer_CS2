using FragForge.Actions.Measurement;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Measurement;

public sealed class EmbeddedPresentMonAllowlistTests
{
    [Fact]
    public void EmbeddedPresentMonHash_IsExactLowercaseHex()
    {
        var tool = ToolVerifier.LoadEmbedded().Find("presentmon");
        Assert.NotNull(tool);
        var hash = tool.Sha256;
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal("b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af", hash);
    }

    [Fact]
    public void EmbeddedAllowlist_RejectsDummyBinary()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "PresentMon.exe");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);
        var verification = new ToolVerifier(directory.Path, ToolVerifier.LoadEmbedded()).Verify(ToolVerifier.PresentMonKey);
        Assert.Equal(ToolStatus.HashMismatch, verification.Status);
        Assert.False(verification.IsUsable);
        Assert.Equal(ToolVerifier.ComputeSha256(path), verification.ActualSha256);
    }
}
