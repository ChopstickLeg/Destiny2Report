using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

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
    public long TotalKills { get; set; }
    public long TotalDeaths { get; set; }
    public Dictionary<string, long> PatrolSecondsByPlanet { get; set; } = new();
    public Dictionary<string, ActivityCompletionAccumulator> RaidCompletions { get; set; } = new();
    public Dictionary<string, ActivityCompletionAccumulator> DungeonCompletions { get; set; } = new();
    public Dictionary<string, RaidFirstCompletion> FirstRaidCompletions { get; set; } = new();
    public Dictionary<string, EncounterAccumulator> EncounterCounts { get; set; } = new();
    public byte[] EncounteredPlayerKeys { get; set; } = [];
    public int UniquePlayersPlayedWith { get; set; }
    public int ZeroKillActivities { get; set; }
    public long TotalActivitySeconds { get; set; }
    public Dictionary<string, ActivityModePlaytimeAccumulator> PlaytimeByActivityMode { get; set; } = new();
    public int GambitMotesBanked { get; set; }
    public int GambitMotesLost { get; set; }
    public int GambitMotesDenied { get; set; }
    public Dictionary<string, int> GambitMotesBankedByMode { get; set; } = new();
    public Dictionary<string, int> GambitMotesLostByMode { get; set; } = new();
    public Dictionary<string, int> GambitMotesDeniedByMode { get; set; } = new();
    public int GambitBankOverage { get; set; }
    public Dictionary<string, int> GambitBankOverageByMode { get; set; } = new();
    public long CrucibleKills { get; set; }
    public Dictionary<string, long> CrucibleKillsByMode { get; set; } = new();
    public Dictionary<string, int> PlayersSherpaed { get; set; } = new();
}
