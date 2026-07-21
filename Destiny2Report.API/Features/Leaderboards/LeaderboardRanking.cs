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
}
