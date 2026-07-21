using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record CrawlAccumulator
{
    public int PlatformId { get; set; }
    public long PlayerMembershipId { get; set; }
    public DateTime LastSuccessfulCrawlAt { get; set; }
    public DateTime NewestActivityPeriod { get; set; }
    public DateTime? FirstActivityAtUtc { get; set; }
    [BsonIgnoreIfDefault]
    public bool FirstActivityDiscoveryCompleted { get; set; }
    public List<long> RecentActivityInstanceIds { get; set; } = new();
    [BsonIgnoreIfDefault]
    public bool NeedsFullRecrawl { get; set; }
    [BsonDefaultValue("")]
    [BsonIgnoreIfDefault]
    public string FullRecrawlReason { get; set; } = "";
    [BsonIgnoreIfDefault]
    public long TotalKills { get; set; }
    public Dictionary<string, long> PatrolSecondsByPlanet { get; set; } = new();
    public Dictionary<string, ActivityCompletionAccumulator> RaidCompletions { get; set; } = new();
    public Dictionary<string, ActivityCompletionAccumulator> DungeonCompletions { get; set; } = new();
    public Dictionary<string, ActivityCompletionAccumulator> ConquestCompletions { get; set; } = new();
    public byte[] EncounteredPlayerKeys { get; set; } = [];
    [BsonIgnoreIfDefault]
    public int UniquePlayersPlayedWith { get; set; }
    [BsonIgnoreIfDefault]
    public int ZeroKillActivities { get; set; }
    [BsonIgnoreIfDefault]
    public long TotalActivitySeconds { get; set; }
    public List<DateTime> PlayDates { get; set; } = new();
    public Dictionary<string, ActivityModePlaytimeAccumulator> PlaytimeByActivityMode { get; set; } = new();
    [BsonIgnoreIfDefault]
    public int GambitMotesBanked { get; set; }
    [BsonIgnoreIfDefault]
    public int GambitMotesLost { get; set; }
    [BsonIgnoreIfDefault]
    public int GambitMotesDenied { get; set; }
    public Dictionary<string, int> GambitMotesBankedByMode { get; set; } = new();
    public Dictionary<string, int> GambitMotesLostByMode { get; set; } = new();
    public Dictionary<string, int> GambitMotesDeniedByMode { get; set; } = new();
    [BsonIgnoreIfDefault]
    public int GambitBankOverage { get; set; }
    public Dictionary<string, int> GambitBankOverageByMode { get; set; } = new();
    [BsonIgnoreIfDefault]
    public int GambitMoteMatches { get; set; }
    public Dictionary<string, PvpPlaylistAccumulator> PvpPlaylists { get; set; } = new();
    [BsonIgnoreIfDefault]
    public long CrucibleKills { get; set; }
    public Dictionary<string, long> CrucibleKillsByMode { get; set; } = new();
    public Dictionary<string, int> PlayersSherpaed { get; set; } = new();
}
