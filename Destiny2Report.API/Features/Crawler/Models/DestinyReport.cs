using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record DestinyReport
{
    public const int MostPlayedWithLimit = 10;
    public const int MostUsedEmblemsLimit = 10;
    public const string CrawlStateQueued = "queued";
    public const string CrawlStateRunning = "running";
    public const string CrawlStateCompleted = "completed";
    public const string CrawlStateFailed = "failed";
    public const string CrawlStatePrivate = "private";

    private List<PlayerEncounterReport> _mostPlayedWith = new();
    private List<EmblemReport> _mostUsedEmblems = new();
    private List<DestinyTriumphSeal> _triumphSeals = new();

    public int PlatformId { get; init; }
    public long PlayerMembershipId { get; init; }
    public string DisplayName { get; init; } = "";
    public int DisplayCode { get; init; }
    public string FullDisplayName
    {
        get => $"{DisplayName}#{DisplayCode:D4}";
        init { }
    }
    public DateTime CrawledAt { get; init; } = DateTime.UtcNow;
    [BsonDefaultValue(CrawlStateCompleted)]
    [BsonIgnoreIfDefault]
    public string CrawlState { get; set; } = CrawlStateCompleted;
    public bool QueuedInRedis { get; set; }
    public DateTime? QueuedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? LastCrawledAtUtc { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    [BsonDefaultValue("")]
    [BsonIgnoreIfDefault]
    public string LeaseOwner { get; set; } = "";
    [BsonDefaultValue("")]
    [BsonIgnoreIfDefault]
    public string CrawlError { get; set; } = "";
    [BsonIgnoreIfDefault]
    public bool NeedsFullRecrawl { get; set; }
    [BsonDefaultValue("")]
    [BsonIgnoreIfDefault]
    public string FullRecrawlReason { get; set; } = "";
    [BsonIgnoreIfDefault]
    public TimeSpan TotalPlaytime { get; set; }
    public Dictionary<string, TimeSpan> PlaytimeByClass { get; set; } = new();
    public List<ActivityModePlaytimeReport> PlaytimeByActivityMode { get; set; } = new();
    public Dictionary<string, TimeSpan> PatrolTimeByPlanet { get; set; } = new();
    [BsonIgnoreIfDefault] public int GoodBoyProtocol { get; set; }
    [BsonIgnoreIfDefault] public int FishCaught { get; set; }
    [BsonIgnoreIfDefault] public long TotalKills { get; set; }
    [BsonIgnoreIfDefault] public long TotalDeaths { get; set; }
    [BsonIgnoreIfDefault] public double CrucibleKd { get; set; }
    [BsonIgnoreIfDefault] public double CrucibleKda { get; set; }
    [BsonIgnoreIfDefault] public double GambitKd { get; set; }
    [BsonIgnoreIfDefault] public double GambitKda { get; set; }
    [BsonIgnoreIfDefault] public int CrucibleMatchesPlayed { get; set; }
    [BsonIgnoreIfDefault] public int GambitMatchesPlayed { get; set; }
    [BsonIgnoreIfDefault] public int CrucibleWins { get; set; }
    [BsonIgnoreIfDefault] public int GambitWins { get; set; }
    public CrucibleKillsReport CrucibleKills { get; set; } = new();
    public GambitMotesReport GambitMotes { get; set; } = new();
    public List<DestinyTriumphSeal> TriumphSeals
    {
        get => _triumphSeals;
        set => _triumphSeals = value?.Where(IsCompletedSeal).ToList() ?? [];
    }
    [BsonIgnoreIfDefault] public int Misadventures { get; set; }
    [BsonIgnoreIfDefault] public int ZeroKillActivities { get; set; }
    [BsonIgnoreIfDefault] public TimeSpan TotalActivityTime { get; set; }
    public List<ActivityCompletionSummary> RaidCompletions { get; set; } = new();
    public List<ActivityCompletionSummary> DungeonCompletions { get; set; } = new();
    public List<PlayerEncounterReport> MostPlayedWith
    {
        get => _mostPlayedWith;
        set => _mostPlayedWith = value?.Take(MostPlayedWithLimit).ToList() ?? [];
    }
    [BsonIgnoreIfDefault]
    public int UniquePlayersPlayedWith { get; set; }
    public List<SherpaReport> PlayersSherpaed { get; set; } = new();
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
