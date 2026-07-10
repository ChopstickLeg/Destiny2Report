using D2Report.BungieClient;
using Destiny2Report.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerCharacterStatsTests
{
    [Fact]
    public void BuildCharacterClassMap_prefers_historical_classes_and_fills_unknown_from_pgcr()
    {
        var characters = new[]
        {
            BungieFixture.HistoricalCharacter(11),
            BungieFixture.HistoricalCharacter(22),
            BungieFixture.HistoricalCharacter(33)
        };
        characters[0].AdditionalProperties["classType"] = 0;
        characters[1].AdditionalProperties["characterClass"] = JToken.FromObject("hunter");

        var pgcrs = new[]
        {
            BungieFixture.Pgcr(
                DateTimeOffset.Parse("2024-06-01T00:00:00Z"),
                4,
                100,
                BungieFixture.Entry(4611686018463095984, characterId: 33, characterClass: "Warlock"),
                BungieFixture.Entry(99, characterId: 44, characterClass: "Titan"))
        };

        var result = (Dictionary<long, string>)CrawlerReflection.Invoke(
            "BuildCharacterClassMap",
            characters,
            pgcrs,
            4611686018463095984L,
            new long[] { 11, 22, 33, 44 })!;

        Assert.Equal("Titan", result[11]);
        Assert.Equal("Hunter", result[22]);
        Assert.Equal("Warlock", result[33]);
        Assert.Equal("Unknown", result[44]);
    }

    [Fact]
    public void BuildPlaytimeByClass_aggregates_seconds_by_normalized_class()
    {
        var characters = new[]
        {
            BungieFixture.HistoricalCharacter(11, merged: BungieFixture.Bucket(("secondsPlayed", 3600))),
            BungieFixture.HistoricalCharacter(22, merged: BungieFixture.Bucket(("secondsPlayed", 1800))),
            BungieFixture.HistoricalCharacter(33, merged: BungieFixture.Bucket(("secondsPlayed", 600)))
        };

        var result = (Dictionary<string, TimeSpan>)CrawlerReflection.Invoke(
            "BuildPlaytimeByClass",
            characters,
            new Dictionary<long, string>
            {
                [11] = "titan",
                [22] = "Hunter",
                [33] = "Bogus"
            })!;

        Assert.Equal(TimeSpan.FromMinutes(60), result["Titan"]);
        Assert.Equal(TimeSpan.FromMinutes(30), result["Hunter"]);
        Assert.Equal(TimeSpan.FromMinutes(10), result["Unknown"]);
    }
}
