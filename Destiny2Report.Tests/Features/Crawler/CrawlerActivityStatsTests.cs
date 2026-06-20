using System.Reflection;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerActivityStatsTests
{
    [Fact]
    public void GetActivityWasStartedFromBeginning_uses_pre_beyond_light_starting_phase_rules()
    {
        var scourge = BungieFixture.Pgcr(DateTimeOffset.Parse("2019-01-01T00:00:00Z"), 4, 1);
        scourge.StartingPhaseIndex = 1;
        scourge.ActivityDetails.DirectorActivityHash = 548750096;

        var leviathan = BungieFixture.Pgcr(DateTimeOffset.Parse("2018-01-01T00:00:00Z"), 4, 2);
        leviathan.StartingPhaseIndex = 2;
        leviathan.ActivityDetails.DirectorActivityHash = unchecked((int)2693136600u);

        var generic = BungieFixture.Pgcr(DateTimeOffset.Parse("2019-01-01T00:00:00Z"), 4, 3);
        generic.StartingPhaseIndex = 1;

        Assert.True((bool?)CrawlerReflection.Invoke("GetActivityWasStartedFromBeginning", scourge, Array.Empty<DestinyPostGameCarnageReportEntry>()));
        Assert.True((bool?)CrawlerReflection.Invoke("GetActivityWasStartedFromBeginning", leviathan, Array.Empty<DestinyPostGameCarnageReportEntry>()));
        Assert.False((bool?)CrawlerReflection.Invoke("GetActivityWasStartedFromBeginning", generic, Array.Empty<DestinyPostGameCarnageReportEntry>()));
    }

    [Fact]
    public void GetActivityWasStartedFromBeginning_uses_reported_flag_after_haunted_release()
    {
        var pgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2022-06-01T00:00:00Z"), 82, 1);
        pgcr.ActivityWasStartedFromBeginning = false;

        var result = (bool?)CrawlerReflection.Invoke(
            "GetActivityWasStartedFromBeginning",
            pgcr,
            new[]
            {
                BungieFixture.Entry(1, values: BungieFixture.Stats(("deaths", 0)))
            });

        Assert.False(result);
    }

    [Fact]
    public void AddCompletion_normalizes_variants_and_rolls_up_clear_flags()
    {
        var aggregateType = CrawlerReflection.NestedType("ActivityCompletionAggregate");
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), aggregateType);
        var completions = Activator.CreateInstance(dictionaryType)!;

        CrawlerReflection.Invoke("AddCompletion", completions, "King's Fall: Master", true, false, false, false);
        CrawlerReflection.Invoke("AddCompletion", completions, "King's Fall: Guided Games", false, true, false, false);
        CrawlerReflection.Invoke("AddCompletion", completions, "Spire of the Watcher", false, false, true, true);

        var summaries = (List<ActivityCompletionSummary>)CrawlerReflection.Invoke("ToCompletionSummaries", completions)!;

        Assert.Collection(
            summaries,
            summary =>
            {
                Assert.Equal("King's Fall", summary.ActivityName);
                Assert.Equal(2, summary.CompletionCount);
                Assert.True(summary.ContestClear);
                Assert.True(summary.FlawlessClear);
                Assert.False(summary.SoloClear);
                Assert.False(summary.SoloFlawlessClear);
            },
            summary =>
            {
                Assert.Equal("Spire of the Watcher", summary.ActivityName);
                Assert.Equal(1, summary.CompletionCount);
                Assert.True(summary.SoloClear);
                Assert.True(summary.SoloFlawlessClear);
            });
    }

    [Fact]
    public void HasPriorCompletedRaid_matches_same_normalized_raid_before_current_instance_only()
    {
        var activityDefinitions = JObject.Parse(
            """
            {
              "100": { "displayProperties": { "name": "Root of Nightmares: Master" } },
              "101": { "displayProperties": { "name": "Root of Nightmares" } },
              "102": { "displayProperties": { "name": "King's Fall" } }
            }
            """);
        var currentCompletedAt = DateTimeOffset.Parse("2024-01-02T01:00:00Z");
        var history = new[]
        {
            BungieFixture.Activity(
                DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
                4,
                10,
                100,
                100,
                ("completed", 1),
                ("completionReason", 0),
                ("activityDurationSeconds", 3600)),
            BungieFixture.Activity(
                DateTimeOffset.Parse("2024-01-03T00:00:00Z"),
                4,
                11,
                101,
                101,
                ("completed", 1),
                ("completionReason", 0)),
            BungieFixture.Activity(
                DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
                4,
                12,
                102,
                102,
                ("completed", 1),
                ("completionReason", 0))
        };

        var result = (bool)CrawlerReflection.Invoke(
            "HasPriorCompletedRaid",
            history,
            "Root of Nightmares",
            currentCompletedAt,
            99L,
            activityDefinitions)!;

        Assert.True(result);
    }

    [Fact]
    public void SelectLinkedProfileMembershipType_prefers_cross_save_then_active_then_recent_profile()
    {
        var profiles = new[]
        {
            new DestinyProfileUserInfoCard
            {
                MembershipId = 4611686018463095984,
                MembershipType = 2,
                IsCrossSavePrimary = false,
                IsOverridden = false,
                DateLastPlayed = DateTimeOffset.Parse("2024-02-01T00:00:00Z")
            },
            new DestinyProfileUserInfoCard
            {
                MembershipId = 4611686018463095984,
                MembershipType = 1,
                IsCrossSavePrimary = true,
                IsOverridden = true,
                DateLastPlayed = DateTimeOffset.Parse("2020-02-01T00:00:00Z")
            },
            new DestinyProfileUserInfoCard
            {
                MembershipId = 4611686018463095984,
                MembershipType = 3,
                IsCrossSavePrimary = false,
                IsOverridden = false,
                DateLastPlayed = DateTimeOffset.Parse("2026-02-01T00:00:00Z")
            }
        };

        var result = (int?)CrawlerReflection.Invoke("SelectLinkedProfileMembershipType", profiles, 4611686018463095984L);

        Assert.Equal(1, result);
    }
}
