using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.Tests.TestSupport;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerModeStatsTests
{
    [Fact]
    public void ApplyModeStats_aggregates_activity_playtime_matches_and_wins()
    {
        var report = new DestinyReport();
        var modeStats = ModeStats(
            ((1, 7), new Dictionary<string, DestinyHistoricalStatsByPeriod>
            {
                ["allPvE"] = BungieFixture.Bucket(("secondsPlayed", 7200))
            }),
            ((1, 5), new Dictionary<string, DestinyHistoricalStatsByPeriod>
            {
                ["allPvP"] = BungieFixture.Bucket(
                    ("secondsPlayed", 1800),
                    ("kills", 60),
                    ("deaths", 30),
                    ("killsDeathsAssists", 2.25),
                    ("activitiesEntered", 6),
                    ("activitiesWon", 4))
            }),
            ((2, 63), new Dictionary<string, DestinyHistoricalStatsByPeriod>
            {
                ["gambit"] = BungieFixture.Bucket(
                    ("secondsPlayed", 900),
                    ("kills", 45),
                    ("deaths", 15),
                    ("killsDeathsAssists", 3.5),
                    ("activitiesEntered", 3),
                    ("activitiesWon", 2))
            }),
            ((2, 75), new Dictionary<string, DestinyHistoricalStatsByPeriod>
            {
                ["gambitPrime"] = BungieFixture.Bucket(
                    ("secondsPlayed", 600),
                    ("kills", 10),
                    ("deaths", 5),
                    ("killsDeathsAssists", 2.5),
                    ("activitiesEntered", 2),
                    ("activitiesWon", 1))
            }));

        CrawlerReflection.Invoke("ApplyModeStats", report, modeStats);

        Assert.Equal(2.0, report.CrucibleKd);
        Assert.Equal(2.25, report.CrucibleKda);
        Assert.Equal(2.75, report.GambitKd);
        Assert.Equal(3.0, report.GambitKda);
        Assert.Equal(6, report.CrucibleMatchesPlayed);
        Assert.Equal(5, report.GambitMatchesPlayed);
        Assert.Equal(4, report.CrucibleWins);
        Assert.Equal(3, report.GambitWins);
    }

    [Fact]
    public void ApplyModeStats_uses_fallback_ratio_average_when_deaths_are_zero()
    {
        var report = new DestinyReport();
        var modeStats = ModeStats(
            ((1, 5), new Dictionary<string, DestinyHistoricalStatsByPeriod>
            {
                ["allPvP"] = BungieFixture.Bucket(("kills", 40), ("deaths", 0), ("killsDeathsRatio", 4.0))
            }),
            ((2, 5), new Dictionary<string, DestinyHistoricalStatsByPeriod>
            {
                ["allPvP"] = BungieFixture.Bucket(("kills", 10), ("deaths", 0), ("killsDeathsRatio", 2.0))
            }));

        CrawlerReflection.Invoke("ApplyModeStats", report, modeStats);

        Assert.Equal(3.0, report.CrucibleKd);
    }

    [Fact]
    public void ApplyModeStats_prefers_mode_specific_bucket_over_allTime()
    {
        var report = new DestinyReport();
        var modeStats = ModeStats(
            ((1, 5), new Dictionary<string, DestinyHistoricalStatsByPeriod>
            {
                ["allTime"] = BungieFixture.Bucket(("kills", 999), ("deaths", 1), ("secondsPlayed", 999)),
                ["allPvP"] = BungieFixture.Bucket(("kills", 12), ("deaths", 6), ("secondsPlayed", 120))
            }));

        CrawlerReflection.Invoke("ApplyModeStats", report, modeStats);

        Assert.Equal(2.0, report.CrucibleKd);
    }

    private static IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> ModeStats(
        params ((long CharacterId, int Mode) Key, IDictionary<string, DestinyHistoricalStatsByPeriod> Value)[] values)
    {
        return values.ToDictionary(item => item.Key, item => item.Value);
    }
}
