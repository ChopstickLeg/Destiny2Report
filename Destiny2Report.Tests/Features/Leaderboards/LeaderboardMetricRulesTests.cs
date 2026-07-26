using Destiny2Report.API.Features.Leaderboards;

namespace Destiny2Report.Tests.Features.Leaderboards;

public sealed class LeaderboardMetricRulesTests
{
    [Theory]
    [InlineData("time.mode.32", false)]
    [InlineData("time.mode.51", false)]
    [InlineData("combat.kills.mode.54", false)]
    [InlineData("competition.crucible.playlist.57", false)]
    [InlineData("time.mode.5", true)]
    [InlineData("time.mode.58", true)]
    [InlineData("competition.crucible.playlist.31", true)]
    [InlineData("combat.kills.total", true)]
    public void Private_match_metrics_are_not_published(string metricKey, bool expected)
    {
        Assert.Equal(expected, LeaderboardMetricRules.IsPublishedMetric(metricKey));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(51, true)]
    [InlineData(52, true)]
    [InlineData(53, true)]
    [InlineData(54, true)]
    [InlineData(55, true)]
    [InlineData(56, true)]
    [InlineData(57, true)]
    [InlineData(31, false)]
    [InlineData(58, false)]
    public void Identifies_every_private_match_mode(int mode, bool expected)
    {
        Assert.Equal(expected, LeaderboardMetricRules.IsPrivateMatchMode(mode));
    }

    [Fact]
    public void Excluded_metric_keys_cover_every_private_mode_and_specific_metric_family()
    {
        Assert.Equal(24, LeaderboardMetricRules.ExcludedMetricKeys.Count);
        Assert.All(
            LeaderboardMetricRules.ExcludedMetricKeys,
            key => Assert.False(LeaderboardMetricRules.IsPublishedMetric(key)));
    }
}
