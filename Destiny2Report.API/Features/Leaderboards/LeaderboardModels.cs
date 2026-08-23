using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Leaderboards;

[BsonIgnoreExtraElements]
public sealed record LeaderboardBoard
{
    public const int MaximumEntries = 1000;
    [BsonId] public string MetricKey { get; init; } = "";
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Unit { get; init; } = "count";
    public int DisplayOrder { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public bool IsRepairing { get; init; }
    public string RepairError { get; init; } = "";
    public List<LeaderboardStoredEntry> Entries { get; init; } = [];
}

public sealed record LeaderboardStoredEntry
{
    public int MembershipTypeId { get; init; }
    public long MembershipId { get; init; }
    public string DisplayName { get; init; } = "";
    public int DisplayCode { get; init; }
    public string EmblemBackgroundUrl { get; init; } = "";
    public long Score { get; init; }
    public DateTime SourceCrawledAtUtc { get; init; }
}

public sealed record LeaderboardMetric(string Key, string Category, string Title, string Description, string Unit, int DisplayOrder, long Score);
public sealed record LeaderboardDefinitionResponse(string Key, string Category, string Title, string Description, string Unit, int DisplayOrder, int RankedPlayerCount, bool IsRepairing);
public sealed record LeaderboardCatalogResponse(bool IsReady, long CompletedPlayerCount, int MinimumCompletedPlayers, IReadOnlyList<LeaderboardDefinitionResponse> Leaderboards);
public sealed record LeaderboardEntryResponse(int Rank, int MembershipTypeId, long MembershipId, string DisplayName, int DisplayCode, string FullDisplayName, string EmblemBackgroundUrl, long Score);
public sealed record LeaderboardPageResponse(string Key, string Category, string Title, string Description, string Unit, int Offset, int Limit, int RetainedEntryCount, DateTimeOffset UpdatedAtUtc, bool IsRepairing, IReadOnlyList<LeaderboardEntryResponse> Entries);

public sealed record PlayerLeaderboardSnapshot
{
    [BsonId] public string PlayerKey { get; init; } = "";
    public int MembershipTypeId { get; init; }
    public long MembershipId { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public List<PlayerLeaderboardScore> Scores { get; init; } = [];
}

public sealed record PlayerLeaderboardScore(string MetricKey, long Score);
public sealed record LeaderboardPercentileThresholds(long TopPointOnePercent, long TopOnePercent, long TopFivePercent, int PlayerCount, DateTimeOffset UpdatedAtUtc);
public sealed record PlayerLeaderboardStanding(string MetricKey, string Category, string Title, string Unit, long Score, string Tier, int? Rank);
public sealed record PlayerLeaderboardStandingsResponse(DateTimeOffset? ThresholdsUpdatedAtUtc, IReadOnlyList<PlayerLeaderboardStanding> Standings);
