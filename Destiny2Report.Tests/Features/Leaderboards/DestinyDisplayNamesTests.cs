using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Leaderboards;

namespace Destiny2Report.Tests.Features.Leaderboards;

public sealed class DestinyDisplayNamesTests
{
    [Theory]
    [InlineData("New Pacific Arcology", "Titan")]
    [InlineData("Arcadian Valley", "Nessus")]
    [InlineData("Echo Mesa", "IO")]
    [InlineData("Hellas Basin", "Mars")]
    public void Patrol_aliases_resolve_to_canonical_destinations(string source, string expected)
    {
        Assert.True(DestinyDisplayNames.TryCanonicalPatrolDestination(source, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void Mode_94_is_sparrow_racing_league()
    {
        Assert.Equal("SparrowRacingLeague", CrawlerService.GetSpecificActivityModeName(94));
        Assert.Equal("Sparrow Racing League", DestinyDisplayNames.HumanizeIdentifier(CrawlerService.GetSpecificActivityModeName(94)));
    }

    [Fact]
    public void Unknown_patrol_destinations_are_rejected()
    {
        Assert.False(DestinyDisplayNames.TryCanonicalPatrolDestination("Unknown", out _));
    }

    [Theory]
    [InlineData(-1, "Abilities")]
    [InlineData(-4, "Unknown")]
    [InlineData(123, "Unknown")]
    [InlineData(123, "")]
    public void Unknown_and_synthetic_weapon_categories_are_rejected(long hash, string category)
    {
        Assert.False(LeaderboardMetricRules.IsRecognizedWeapon(hash, category));
        Assert.True(LeaderboardMetricRules.IsRecognizedWeapon(123, "Auto Rifle"));
    }
}
