using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.Tests.TestSupport;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerAccumulatorTests
{
    [Fact]
    public void AddFirstRaidCompletion_keeps_earliest_normalized_raid_clear()
    {
        var accumulator = new CrawlAccumulator();

        CrawlerReflection.Invoke(
            "AddFirstRaidCompletion",
            accumulator,
            "Root of Nightmares",
            DateTimeOffset.Parse("2024-02-01T01:00:00Z"),
            20L);
        CrawlerReflection.Invoke(
            "AddFirstRaidCompletion",
            accumulator,
            "Root of Nightmares",
            DateTimeOffset.Parse("2024-01-01T01:00:00Z"),
            10L);
        CrawlerReflection.Invoke(
            "AddFirstRaidCompletion",
            accumulator,
            "Root of Nightmares",
            DateTimeOffset.Parse("2024-03-01T01:00:00Z"),
            30L);

        var firstClear = Assert.Single(accumulator.RaidCompletions).Value.FirstCompletion;
        Assert.NotNull(firstClear);
        Assert.Equal(DateTime.Parse("2024-01-01T01:00:00Z").ToUniversalTime(), firstClear.CompletedAt);
        Assert.Equal(10L, firstClear.InstanceId);
    }

    [Fact]
    public void HasPriorCompletedRaid_uses_accumulator_first_clear_instance_to_avoid_current_run()
    {
        var accumulator = new CrawlAccumulator
        {
            RaidCompletions =
            {
                ["King's Fall"] = new ActivityCompletionAccumulator
                {
                    FirstCompletion = new RaidFirstCompletion
                    {
                        CompletedAt = DateTime.Parse("2024-01-02T01:00:00Z").ToUniversalTime(),
                        InstanceId = 99L
                    }
                }
            }
        };

        var sameRun = (bool)CrawlerReflection.Invoke(
            "HasPriorCompletedRaid",
            accumulator,
            "King's Fall",
            DateTimeOffset.Parse("2024-01-02T01:00:00Z"),
            99L)!;
        var laterRun = (bool)CrawlerReflection.Invoke(
            "HasPriorCompletedRaid",
            accumulator,
            "King's Fall",
            DateTimeOffset.Parse("2024-01-03T01:00:00Z"),
            100L)!;

        Assert.False(sameRun);
        Assert.True(laterRun);
    }

    [Fact]
    public void UpdateAccumulatorCrawlState_advances_watermark_and_keeps_recent_ids_distinct()
    {
        var accumulator = new CrawlAccumulator
        {
            NeedsFullRecrawl = true,
            FullRecrawlReason = "stat migration",
            NewestActivityPeriod = DateTime.Parse("2024-01-01T00:00:00Z").ToUniversalTime(),
            RecentActivityInstanceIds = [1, 2, 3]
        };
        var fetchedActivities = new[]
        {
            BungieFixture.Activity(DateTimeOffset.Parse("2024-01-03T00:00:00Z"), 7, 4, 100, 100),
            BungieFixture.Activity(DateTimeOffset.Parse("2024-01-02T00:00:00Z"), 7, 2, 100, 100)
        };

        CrawlerReflection.Invoke("UpdateAccumulatorCrawlState", accumulator, fetchedActivities, new[] { 4L, 5L });

        Assert.False(accumulator.NeedsFullRecrawl);
        Assert.Empty(accumulator.FullRecrawlReason);
        Assert.Equal(DateTime.Parse("2024-01-03T00:00:00Z").ToUniversalTime(), accumulator.NewestActivityPeriod);
        Assert.Equal([4, 2, 5, 1, 3], accumulator.RecentActivityInstanceIds);
        Assert.True(accumulator.LastSuccessfulCrawlAt > DateTime.MinValue);
    }

    [Theory]
    [InlineData(1, 100, 1, false)]
    [InlineData(1, 100, 2, true)]
    [InlineData(0, 100, 2, false)]
    [InlineData(1, 0, 2, false)]
    public void IsPersistablePlayerEncounter_requires_valid_player_seen_more_than_once(
        int membershipType,
        long membershipId,
        int count,
        bool expected)
    {
        var actual = (bool)CrawlerReflection.Invoke(
            "IsPersistablePlayerEncounter",
            membershipType,
            membershipId,
            count)!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SaveEncounteredPlayerKeys_deduplicates_players_and_updates_unique_count()
    {
        var accumulator = new CrawlAccumulator();

        CrawlerReflection.Invoke(
            "SaveEncounteredPlayerKeys",
            accumulator,
            new[]
            {
                (MembershipType: 1, MembershipId: 100L),
                (MembershipType: 1, MembershipId: 100L),
                (MembershipType: 2, MembershipId: 200L)
            });

        var keys = (HashSet<(int MembershipType, long MembershipId)>)CrawlerReflection.Invoke(
            "ReadEncounteredPlayerKeys",
            accumulator)!;

        Assert.Equal(2, accumulator.UniquePlayersPlayedWith);
        Assert.Equal(18, accumulator.EncounteredPlayerKeys.Length);
        Assert.Contains((1, 100L), keys);
        Assert.Contains((2, 200L), keys);
    }

    [Fact]
    public void EncounteredPlayerKeys_round_trip_large_destiny_membership_ids()
    {
        var accumulator = new CrawlAccumulator();
        const long membershipId = 4611686018463095984L;

        CrawlerReflection.Invoke(
            "SaveEncounteredPlayerKeys",
            accumulator,
            new[] { (MembershipType: 3, MembershipId: membershipId) });

        var keys = (HashSet<(int MembershipType, long MembershipId)>)CrawlerReflection.Invoke(
            "ReadEncounteredPlayerKeys",
            accumulator)!;

        var key = Assert.Single(keys);
        Assert.Equal(3, key.MembershipType);
        Assert.Equal(membershipId, key.MembershipId);
        Assert.Equal(1, accumulator.UniquePlayersPlayedWith);
    }

    [Theory]
    [InlineData(1, 100, 1, true)]
    [InlineData(1, 100, 2, true)]
    [InlineData(0, 100, 1, false)]
    [InlineData(256, 100, 1, false)]
    [InlineData(1, 0, 1, false)]
    [InlineData(1, 100, 0, false)]
    public void IsCountablePlayerEncounter_includes_valid_one_time_encounters(
        int membershipType,
        long membershipId,
        int count,
        bool expected)
    {
        var actual = (bool)CrawlerReflection.Invoke(
            "IsCountablePlayerEncounter",
            membershipType,
            membershipId,
            count)!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DestinyReport_can_be_flagged_for_full_recrawl()
    {
        var report = new DestinyReport
        {
            NeedsFullRecrawl = true,
            FullRecrawlReason = "recompute gambit mote stats"
        };

        Assert.True(report.NeedsFullRecrawl);
        Assert.Equal("recompute gambit mote stats", report.FullRecrawlReason);
    }
}
