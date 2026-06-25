using System.Net;
using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record DestinyReport
{
    public const int MostPlayedWithLimit = 10;

    private List<PlayerEncounterReport> _mostPlayedWith = new();
    private List<DestinyTriumphSeal> _triumphSeals = new();

    public int PlatformId { get; init; }
    public long PlayerMembershipId { get; init; }
    public DateTimeOffset CrawledAt { get; init; } = DateTimeOffset.UtcNow;
    public bool NeedsFullRecrawl { get; set; }
    public string FullRecrawlReason { get; set; } = "";
    public TimeSpan TotalPlaytime { get; set; }
    public Dictionary<string, TimeSpan> PlaytimeByClass { get; set; } = new();
    public List<ActivityModePlaytimeReport> PlaytimeByActivityMode { get; set; } = new();
    public Dictionary<string, TimeSpan> PatrolTimeByPlanet { get; set; } = new();
    public int GoodBoyProtocol { get; set; }
    public int FishCaught { get; set; }
    public long TotalKills { get; set; }
    public long TotalDeaths { get; set; }
    public double CrucibleKd { get; set; }
    public double CrucibleKda { get; set; }
    public double GambitKd { get; set; }
    public double GambitKda { get; set; }
    public int CrucibleMatchesPlayed { get; set; }
    public int GambitMatchesPlayed { get; set; }
    public int CrucibleWins { get; set; }
    public int GambitWins { get; set; }
    public DestinyPlayer? CrucibleRival { get; set; }
    public DestinyPlayer? GambitRival { get; set; }
    public double KdAgainstCrucibleRival { get; set; }
    public double KdAgainstGambitRival { get; set; }
    public int GambitMotesBanked { get; set; }
    public int GambitMotesLost { get; set; }
    public List<DestinyTriumphSeal> TriumphSeals
    {
        get => _triumphSeals;
        set => _triumphSeals = value?.Where(IsCompletedSeal).ToList() ?? [];
    }
    public int Misadventures { get; set; }
    public int ZeroKillActivities { get; set; }
    public TimeSpan TotalActivityTime { get; set; }
    public List<ActivityCompletionSummary> RaidCompletions { get; set; } = new();
    public List<ActivityCompletionSummary> DungeonCompletions { get; set; } = new();
    public List<PlayerEncounterReport> MostPlayedWith
    {
        get => _mostPlayedWith;
        set => _mostPlayedWith = value?.Take(MostPlayedWithLimit).ToList() ?? [];
    }
    public int UniquePlayersPlayedWith { get; set; }
    public List<SherpaReport> PlayersSherpaed { get; set; } = new();
    public List<WeaponReport> PvETopWeapons { get; set; } = new();
    public List<WeaponReport> CrucibleTopWeapons { get; set; } = new();
    public List<WeaponReport> GambitTopWeapons { get; set; } = new();

    public static bool IsCompletedSeal(DestinyTriumphSeal seal)
    {
        return seal.IsCompleted;
    }
}

[BsonIgnoreExtraElements]
public record DestinyTriumphSeal
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public bool IsCompleted { get; init; }
}

public record ActivityCompletion
{
    public string ActivityName { get; init; } = "";
    public DateTime CompletionDate { get; init; }
    public bool? IsContest { get; init; }
    public bool? IsDayOne { get; init; }
    public bool? IsFlawless { get; init; }
    public bool? IsSolo { get; init; }
    public long InstanceId { get; init; }
}

public record ActivityCompletionSummary
{
    public string ActivityName { get; init; } = "";
    public int CompletionCount { get; init; }
    public bool ContestClear { get; init; }
    public bool FlawlessClear { get; init; }
    public bool SoloClear { get; init; }
    public bool SoloFlawlessClear { get; init; }
}

public record ActivityModePlaytimeReport
{
    public int Mode { get; init; }
    public string ModeName { get; init; } = "";
    public TimeSpan TotalPlaytime { get; init; }
    public List<ActivityModePlaytimeBreakdown> MostSpecificModes { get; init; } = new();
}

public record ActivityModePlaytimeBreakdown
{
    public int Mode { get; init; }
    public string ModeName { get; init; } = "";
    public TimeSpan Playtime { get; init; }
}

public record DestinyPlayer
{
    public long MembershipId { get; init; }
    public int MembershipType { get; init; }
    public string DisplayName { get; init; } = "";
    public string EmblemUrl { get; init; } = "";
}

public record PlayerEncounterReport
{
    public DestinyPlayer Player { get; init; } = new();
    public int EncounterCount { get; init; }
}

[BsonIgnoreExtraElements]
public record CrawlAccumulator
{
    public int PlatformId { get; set; }
    public long PlayerMembershipId { get; set; }
    public DateTimeOffset LastSuccessfulCrawlAt { get; set; }
    public DateTimeOffset NewestActivityPeriod { get; set; }
    public List<long> RecentActivityInstanceIds { get; set; } = new();
    public bool NeedsFullRecrawl { get; set; }
    public string FullRecrawlReason { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, long> PatrolSecondsByPlanet { get; set; } = new();
    public Dictionary<string, ActivityCompletionAccumulator> RaidCompletions { get; set; } = new();
    public Dictionary<string, ActivityCompletionAccumulator> DungeonCompletions { get; set; } = new();
    public Dictionary<string, RaidFirstCompletion> FirstRaidCompletions { get; set; } = new();
    public Dictionary<string, EncounterAccumulator> EncounterCounts { get; set; } = new();
    public int UniquePlayersPlayedWith { get; set; }
    public Dictionary<string, RivalAccumulator> CrucibleRivals { get; set; } = new();
    public Dictionary<string, RivalAccumulator> GambitRivals { get; set; } = new();
    public int ZeroKillActivities { get; set; }
    public long TotalActivitySeconds { get; set; }
    public Dictionary<string, ActivityModePlaytimeAccumulator> PlaytimeByActivityMode { get; set; } = new();
    public int GambitMotesBanked { get; set; }
    public int GambitMotesLost { get; set; }
    public Dictionary<string, int> PlayersSherpaed { get; set; } = new();
}

public record ActivityModePlaytimeAccumulator
{
    public long TotalSeconds { get; set; }
    public Dictionary<string, long> MostSpecificModeSeconds { get; set; } = new();
}

public record ActivityCompletionAccumulator
{
    public int CompletionCount { get; set; }
    public bool ContestClear { get; set; }
    public bool FlawlessClear { get; set; }
    public bool SoloClear { get; set; }
    public bool SoloFlawlessClear { get; set; }
}

public record RaidFirstCompletion
{
    public DateTimeOffset CompletedAt { get; set; }
    public long InstanceId { get; set; }
}

public record EncounterAccumulator
{
    public int MembershipType { get; set; }
    public long MembershipId { get; set; }
    public int Count { get; set; }
}

public record RivalAccumulator
{
    public DestinyPlayer Player { get; set; } = new();
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double Kills { get; set; }
    public double Deaths { get; set; }
}

[BsonIgnoreExtraElements]
public record WeaponAggregate
{
    public int OwnerMembershipType { get; set; }
    public long OwnerMembershipId { get; set; }
    public string ActivityMode { get; set; } = "";
    public string WeaponKey { get; set; } = "";
    public string WeaponName { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public int TotalKills { get; set; }
}

[BsonIgnoreExtraElements]
public record PlayerEncounterAggregate
{
    public int OwnerMembershipType { get; init; }
    public long OwnerMembershipId { get; init; }
    public int EncounteredMembershipType { get; init; }
    public long EncounteredMembershipId { get; init; }
    public int Count { get; init; }
}

public record WeaponReport
{
    public string Name { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public int TotalKills { get; init; }
}

public record SherpaReport
{
    public string RaidName { get; init; } = "";
    public int PlayerCount { get; init; }
}
