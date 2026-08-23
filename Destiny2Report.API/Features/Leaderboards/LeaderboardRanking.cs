namespace Destiny2Report.API.Features.Leaderboards;

public static class LeaderboardRanking
{
    public static List<LeaderboardStoredEntry> SortAndLimit(IEnumerable<LeaderboardStoredEntry> entries) => entries
        .OrderByDescending(entry => entry.Score)
        .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.DisplayCode)
        .ThenBy(entry => entry.MembershipTypeId)
        .ThenBy(entry => entry.MembershipId)
        .Take(LeaderboardBoard.MaximumEntries)
        .ToList();

    public static IReadOnlyList<LeaderboardEntryResponse> Rank(IReadOnlyList<LeaderboardStoredEntry> entries)
    {
        var result = new List<LeaderboardEntryResponse>(entries.Count);
        long? previousScore = null;
        var rank = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (previousScore != entry.Score) rank = index + 1;
            previousScore = entry.Score;
            result.Add(new LeaderboardEntryResponse(rank, entry.MembershipTypeId, entry.MembershipId, entry.DisplayName, entry.DisplayCode, $"{entry.DisplayName}#{entry.DisplayCode:D4}", entry.EmblemBackgroundUrl, entry.Score));
        }
        return result;
    }

    public static (long Score, int Rank)? FindPlayerStanding(
        IReadOnlyList<LeaderboardStoredEntry> entries,
        int membershipTypeId,
        long membershipId)
    {
        var player = entries.FirstOrDefault(entry =>
            entry.MembershipTypeId == membershipTypeId && entry.MembershipId == membershipId);
        if (player is null) return null;

        var rank = 1;
        foreach (var entry in entries)
        {
            if (entry.Score <= player.Score) break;
            rank++;
        }

        return (player.Score, rank);
    }
}
