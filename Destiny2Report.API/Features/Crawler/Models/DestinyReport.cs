using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record DestinyReport
{
    public const int MostPlayedWithLimit = 10;
    public const int MostUsedEmblemsLimit = 10;

    private List<PlayerEncounterReport> _mostPlayedWith = new();
    private List<EmblemReport> _mostUsedEmblems = new();
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
    public CrucibleKillsReport CrucibleKills { get; set; } = new();
    public GambitMotesReport GambitMotes { get; set; } = new();
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
    public List<EmblemReport> MostUsedEmblems
    {
        get => _mostUsedEmblems;
        set => _mostUsedEmblems = value?.Take(MostUsedEmblemsLimit).ToList() ?? [];
    }

    public static bool IsCompletedSeal(DestinyTriumphSeal seal)
    {
        return seal.IsCompleted;
    }
}
