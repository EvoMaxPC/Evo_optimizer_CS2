using FragForge.Core.Analysis.Live;
using FragForge.Tests.Measurement;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

public sealed class LiveFrameParserPropertiesTests
{
    [Fact]
    public void HeaderPropertiesReflectColumns()
    {
        var parser = new LiveFrameParser();
        Assert.False(parser.HeaderSeen);
        Assert.False(parser.HasFrameTimeColumn);
        Assert.False(parser.HasTimeColumn);
        parser.Feed(PresentMonFixtures.Header260);
        Assert.True(parser.HeaderSeen);
        Assert.True(parser.HasFrameTimeColumn);
        Assert.True(parser.HasTimeColumn);
        var noTime = new LiveFrameParser();
        noTime.Feed("Application,MsBetweenPresents");
        Assert.True(noTime.HeaderSeen);
        Assert.True(noTime.HasFrameTimeColumn);
        Assert.False(noTime.HasTimeColumn);
    }
}
