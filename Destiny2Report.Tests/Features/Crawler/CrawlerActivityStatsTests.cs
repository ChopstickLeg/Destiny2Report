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
    public void AddActivityModePlaytime_adds_seconds_to_big_bucket_and_most_specific_mode()
    {
        var accumulator = new CrawlAccumulator();
        var pgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 4, 1);
        pgcr.ActivityDetails.Modes = [7, 4];

        CrawlerReflection.Invoke("AddActivityModePlaytime", accumulator, pgcr, 1800L);

        var report = Assert.Single((List<ActivityModePlaytimeReport>)CrawlerReflection.Invoke(
            "ToActivityModePlaytimeReports",
            accumulator.PlaytimeByActivityMode,
            ActivityModeDefinitions())!);
        Assert.Equal(7, report.Mode);
        Assert.Equal("All PvE Activities", report.ModeName);
        Assert.Equal(TimeSpan.FromMinutes(30), report.TotalPlaytime);
        var specificMode = Assert.Single(report.MostSpecificModes);
        Assert.Equal(4, specificMode.Mode);
        Assert.Equal("Raids", specificMode.ModeName);
        Assert.Equal(TimeSpan.FromMinutes(30), specificMode.Playtime);
    }

    [Fact]
    public void AddActivityModePlaytime_adds_seconds_to_every_applicable_big_bucket()
    {
        var accumulator = new CrawlAccumulator();
        var pgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 63, 1);
        pgcr.ActivityDetails.Modes = [7, 64, 63];

        CrawlerReflection.Invoke("AddActivityModePlaytime", accumulator, pgcr, 900L);

        var reports = (List<ActivityModePlaytimeReport>)CrawlerReflection.Invoke(
            "ToActivityModePlaytimeReports",
            accumulator.PlaytimeByActivityMode,
            ActivityModeDefinitions())!;

        Assert.Equal([7, 64], reports.Select(report => report.Mode));
        Assert.Equal(["All PvE Activities", "All PvE Competitive"], reports.Select(report => report.ModeName));
        Assert.All(reports, report =>
        {
            Assert.Equal(TimeSpan.FromMinutes(15), report.TotalPlaytime);
            var specificMode = Assert.Single(report.MostSpecificModes);
            Assert.Equal(63, specificMode.Mode);
            Assert.Equal("Gambit", specificMode.ModeName);
            Assert.Equal(TimeSpan.FromMinutes(15), specificMode.Playtime);
        });
    }

    [Fact]
    public void AddActivityModePlaytime_is_additive_for_existing_accumulator_seconds()
    {
        var accumulator = new CrawlAccumulator
        {
            PlaytimeByActivityMode =
            {
                ["5"] = new ActivityModePlaytimeAccumulator
                {
                    TotalSeconds = 300,
                    MostSpecificModeSeconds =
                    {
                        ["69"] = 300
                    }
                }
            }
        };
        var pgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 69, 1);
        pgcr.ActivityDetails.Modes = [5, 69];

        CrawlerReflection.Invoke("AddActivityModePlaytime", accumulator, pgcr, 120L);

        var report = Assert.Single((List<ActivityModePlaytimeReport>)CrawlerReflection.Invoke(
            "ToActivityModePlaytimeReports",
            accumulator.PlaytimeByActivityMode,
            new JObject())!);
        Assert.Equal(5, report.Mode);
        Assert.Equal("AllPvP", report.ModeName);
        Assert.Equal(TimeSpan.FromMinutes(7), report.TotalPlaytime);
        var specificMode = Assert.Single(report.MostSpecificModes);
        Assert.Equal(69, specificMode.Mode);
        Assert.Equal("PvPCompetitive", specificMode.ModeName);
        Assert.Equal(TimeSpan.FromMinutes(7), specificMode.Playtime);
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

    private static JObject ActivityModeDefinitions()
    {
        return JObject.Parse(
            """
            {
              "100": {
                "modeType": 4,
                "displayProperties": { "name": "Raids" }
              },
              "101": {
                "modeType": 7,
                "displayProperties": { "name": "All PvE Activities" }
              },
              "102": {
                "modeType": 63,
                "displayProperties": { "name": "Gambit" }
              },
              "103": {
                "modeType": 64,
                "displayProperties": { "name": "All PvE Competitive" }
              }
            }
            """);
    }
}
