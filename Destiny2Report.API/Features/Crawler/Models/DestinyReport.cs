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
    public TimeSpan TotalPlaytime { get; set; }
    public Dictionary<string, TimeSpan> PlaytimeByClass { get; set; } = new();
    public Dictionary<string, TimeSpan> PlaytimeByActivity { get; set; } = new();
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
