using D2Report.BungieClient;
using Microsoft.Extensions.Caching.Hybrid;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerReadService(
    ILogger<CrawlerReadService> logger,
    IMongoDatabase mongoDatabase,
    ID2ReportClient bungieClient,
    HybridCache cache,
    IHttpClientFactory httpClientFactory) : ICrawlerReadService
{
    private const string BungieNetBaseUrl = "https://www.bungie.net";
    private const string InventoryItemDefinitionType = "DestinyInventoryItemDefinition";
    private static readonly TimeSpan ManifestCacheDuration = TimeSpan.FromDays(1);
    private static readonly TimeSpan ManifestTableCacheDuration = TimeSpan.FromDays(365);

    private static readonly IReadOnlyDictionary<int, string> ActivityModeTypeNames = new Dictionary<int, string>
    {
        [0] = "None",
        [2] = "Story",
        [3] = "Strike",
        [4] = "Raid",
        [5] = "AllPvP",
        [6] = "Patrol",
        [7] = "AllPvE",
        [9] = "Reserved9",
        [10] = "Control",
        [11] = "Reserved11",
        [12] = "Clash",
        [13] = "Reserved13",
        [15] = "CrimsonDoubles",
        [16] = "Nightfall",
        [17] = "HeroicNightfall",
        [18] = "AllStrikes",
        [19] = "IronBanner",
        [20] = "Reserved20",
        [21] = "Reserved21",
        [22] = "Reserved22",
        [24] = "Reserved24",
        [25] = "AllMayhem",
        [26] = "Reserved26",
        [27] = "Reserved27",
        [28] = "Reserved28",
        [29] = "Reserved29",
        [30] = "Reserved30",
        [31] = "Supremacy",
        [32] = "PrivateMatchesAll",
        [37] = "Survival",
        [38] = "Countdown",
        [39] = "TrialsOfTheNine",
        [40] = "Social",
        [41] = "TrialsCountdown",
        [42] = "TrialsSurvival",
        [43] = "IronBannerControl",
        [44] = "IronBannerClash",
        [45] = "IronBannerSupremacy",
        [46] = "ScoredNightfall",
        [47] = "ScoredHeroicNightfall",
        [48] = "Rumble",
        [49] = "AllDoubles",
        [50] = "Doubles",
        [51] = "PrivateMatchesClash",
        [52] = "PrivateMatchesControl",
        [53] = "PrivateMatchesSupremacy",
        [54] = "PrivateMatchesCountdown",
        [55] = "PrivateMatchesSurvival",
        [56] = "PrivateMatchesMayhem",
        [57] = "PrivateMatchesRumble",
        [58] = "HeroicAdventure",
        [59] = "Showdown",
        [60] = "Lockdown",
        [61] = "Scorched",
        [62] = "ScorchedTeam",
        [63] = "Gambit",
        [64] = "AllPvECompetitive",
        [65] = "Breakthrough",
        [66] = "BlackArmoryRun",
        [67] = "Salvage",
        [68] = "IronBannerSalvage",
        [69] = "PvPCompetitive",
        [70] = "PvPQuickplay",
        [71] = "ClashQuickplay",
        [72] = "ClashCompetitive",
        [73] = "ControlQuickplay",
        [74] = "ControlCompetitive",
        [75] = "GambitPrime",
        [76] = "Reckoning",
        [77] = "Menagerie",
        [78] = "VexOffensive",
        [79] = "NightmareHunt",
        [80] = "Elimination",
        [81] = "Momentum",
        [82] = "Dungeon",
        [83] = "Sundial",
        [84] = "TrialsOfOsiris",
        [85] = "Dares",
        [86] = "Offensive",
        [87] = "LostSector",
        [88] = "Rift",
        [89] = "ZoneControl",
        [90] = "IronBannerRift",
        [91] = "IronBannerZoneControl",
        [92] = "Relic",
        [93] = "LawlessFrontier",
        [94] = "SparrowRacingLeague"
    };

    public static string GetSpecificActivityModeName(int mode) =>
        ActivityModeTypeNames.GetValueOrDefault(mode) ?? $"Mode {mode}";
}
