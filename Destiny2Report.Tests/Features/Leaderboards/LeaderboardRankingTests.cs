using Destiny2Report.API.Features.Leaderboards;

namespace Destiny2Report.Tests.Features.Leaderboards;

public sealed class LeaderboardRankingTests
{
    [Fact]
    public void SortAndLimit_never_persists_more_than_one_thousand_entries()
    {
        var entries = Enumerable.Range(1, 1_050).Select(index => Entry(index, index));
        var result = LeaderboardRanking.SortAndLimit(entries);
        Assert.Equal(1_000, result.Count);
        Assert.Equal(1_050, result[0].Score);
        Assert.Equal(51, result[^1].Score);
    }

    [Fact]
    public void SortAndLimit_uses_identity_as_a_deterministic_tie_breaker()
    {
        var result = LeaderboardRanking.SortAndLimit([
            Entry(2, 10, "Zavala"),
            Entry(1, 10, "Ikora")
        ]);
        Assert.Equal(["Ikora", "Zavala"], result.Select(entry => entry.DisplayName));
    }

    [Fact]
    public void Rank_assigns_competition_ranks_to_equal_scores()
    {
        var ranked = LeaderboardRanking.Rank(LeaderboardRanking.SortAndLimit([
            Entry(1, 20, "A"), Entry(2, 20, "B"), Entry(3, 10, "C")
        ]));
        Assert.Equal([1, 1, 3], ranked.Select(entry => entry.Rank));
    }

    [Fact]
    public void Rank_includes_the_players_emblem_background()
    {
        var ranked = LeaderboardRanking.Rank([Entry(1, 20, "Ikora") with { EmblemBackgroundUrl = "/common/emblem.jpg" }]);

        Assert.Equal("/common/emblem.jpg", ranked[0].EmblemBackgroundUrl);
    }

    [Fact]
    public void FindPlayerStanding_returns_the_same_competition_rank_without_materializing_responses()
    {
        var entries = LeaderboardRanking.SortAndLimit([
            Entry(1, 20, "A"), Entry(2, 20, "B"), Entry(3, 10, "C")
        ]);

        var tiedStanding = LeaderboardRanking.FindPlayerStanding(entries, 3, 2);
        var lowerStanding = LeaderboardRanking.FindPlayerStanding(entries, 3, 3);

        Assert.Equal((20L, 1), tiedStanding);
        Assert.Equal((10L, 3), lowerStanding);
    }

    [Fact]
    public void FindPlayerStanding_returns_null_when_the_player_is_not_on_the_board()
    {
        var standing = LeaderboardRanking.FindPlayerStanding([Entry(1, 20)], 3, 2);

        Assert.Null(standing);
    }

    private static LeaderboardStoredEntry Entry(int id, long score, string? name = null) => new()
    {
        MembershipTypeId = 3,
        MembershipId = id,
        DisplayName = name ?? $"Guardian{id:D4}",
        DisplayCode = id,
        Score = score
    };
}
