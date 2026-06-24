using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Models;

public class BrandReputationTests
{
    [Fact]
    public void NoLocalService_FlagsLimitedSupport_RegardlessOfScore()
    {
        var rep = new BrandReputation { Brand = "X", Score = 4.8, HasLocalService = false };
        rep.Flag.Should().Be(ReputationFlag.LimitedLocalSupport);
    }

    [Fact]
    public void HighScoreWithLocalService_FlagsStrongPresence()
    {
        var rep = new BrandReputation { Brand = "X", Score = 4.2, HasLocalService = true };
        rep.Flag.Should().Be(ReputationFlag.StrongLocalPresence);
    }

    [Theory]
    [InlineData(3.0, true)]   // decent but not strong
    [InlineData(4.5, null)]   // unknown service
    public void OtherwiseNoFlag(double score, bool? local)
    {
        var rep = new BrandReputation { Brand = "X", Score = score, HasLocalService = local };
        rep.Flag.Should().Be(ReputationFlag.None);
    }

    [Fact]
    public void HasSignal_IsFalseWhenEmpty()
    {
        new BrandReputation { Brand = "X" }.HasSignal.Should().BeFalse();
        new BrandReputation { Brand = "X", Score = 4 }.HasSignal.Should().BeTrue();
    }
}
