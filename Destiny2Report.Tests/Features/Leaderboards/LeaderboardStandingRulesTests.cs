namespace Destiny2Report.Tests.Features.Leaderboards;

using Destiny2Report.API.Features.Leaderboards;

public sealed class LeaderboardStandingRulesTests
{
    [Fact]
    public void Cutoff_Uses_Ceiling_For_The_Inclusive_Percentile()
    {
        var scores = Enumerable.Range(1, 10_000).Reverse().Select(value => (long)value).ToArray();

        Assert.Equal(9_991, LeaderboardStandingRules.Cutoff(scores, .001));
        Assert.Equal(9_901, LeaderboardStandingRules.Cutoff(scores, .01));
        Assert.Equal(9_501, LeaderboardStandingRules.Cutoff(scores, .05));
    }

    [Theory]
    [InlineData(1000, "top-0.1")]
    [InlineData(950, "top-1")]
    [InlineData(850, "top-5")]
    [InlineData(799, null)]
    public void PercentileTier_Returns_The_Highest_Distinction(long score, string? expected)
    {
        var thresholds = new LeaderboardPercentileThresholds(1000, 900, 800, 10_000, DateTimeOffset.UtcNow);

        Assert.Equal(expected, LeaderboardStandingRules.PercentileTier(score, thresholds));
    }
}
