using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;
using ReportPlayer = Destiny2Report.API.Features.Crawler.Models.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public class CrawlerService(
    ILogger<CrawlerService> logger,
    IMongoDatabase mongoDatabase,
    ID2ReportClient bungieClient,
    HybridCache cache,
    IHttpClientFactory httpClientFactory,
    IOptions<ContestModeOptions> contestModeOptions,
    IOptions<ActivityTriumphRecordOptions> activityTriumphRecordOptions) : ICrawlerService
{
    private const string BungieNetBaseUrl = "https://www.bungie.net";
    private const int GeneralStatsGroup = 1;
    private const int BasicProfileComponent = 100;
    private const int ProfileCharactersComponent = 200;
    private const int ProfileRecordsComponent = 900;
    private const int MetricsComponent = 1100;
    private const int PageSize = 250;
    private const int MaxConcurrentPgcrRequests = 45;
    private const string InventoryItemDefinitionType = "DestinyInventoryItemDefinition";

    private static readonly int[] AccountStatGroups = [GeneralStatsGroup];
    private static readonly int[] ProfileComponents = [ProfileRecordsComponent, MetricsComponent];
    private static readonly int[] ProfileCharactersComponents = [BasicProfileComponent, ProfileCharactersComponent];
    private static readonly int[] ModeStatGroups = [GeneralStatsGroup];
    private static readonly long[] TriumphSealRootPresentationNodeHashes = [616318467, 1881970629];
    private static readonly TimeSpan ManifestCacheDuration = TimeSpan.FromDays(1);
    private static readonly HashSet<long> PrivateGambitActivityTypeHashes = [146907730, 2516284680];
    private static readonly HashSet<long> PrivateCrucibleActivityTypeHashes = [4260058063];
    private static readonly DateTimeOffset BeyondLightRelease = new(2020, 11, 10, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WitchQueenRelease = new(2022, 2, 22, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SeasonOfTheHauntedRelease = new(2022, 5, 24, 17, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<long> ScourgeOfThePastActivityHashes = [548750096, 2812525063];
    private static readonly HashSet<long> LeviathanActivityHashes =
    [
        2693136600, 2693136601, 2693136602, 2693136603, 2693136604, 2693136605,
        89727599, 287649202, 1699948563, 1875726950, 3916343513, 4039317196,
        417231112, 508802457, 757116822, 771164842, 1685065161, 1800508819,
        2449714930, 3446541099, 4206123728, 3912437239, 3879860661, 3857338478
    ];
    private readonly ContestModeLookup contestMode = ContestModeLookup.FromOptions(contestModeOptions.Value);
    private readonly ActivityTriumphRecordOptions activityTriumphRecords = activityTriumphRecordOptions.Value;

    public async Task CrawlAsync(int platformId, long playerMembershipId, CancellationToken cancellationToken)
    {
        using var activity = AppTelemetry.ActivitySource.StartActivity("crawler.player.crawl", ActivityKind.Internal);
        activity?.SetTag("destiny.membership_type_id", platformId);
        activity?.SetTag("destiny.membership_id", playerMembershipId);

        logger.LogInformation("Crawling Destiny report for membership {MembershipType}/{MembershipId}.", platformId, playerMembershipId);

        try
        {
            var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
            var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);

            var profileTask = bungieClient.Destiny2_GetProfileAsync(ProfileComponents, playerMembershipId, platformId, cancellationToken);
            var accountStatsTask = bungieClient.Destiny2_GetHistoricalStatsForAccountAsync(playerMembershipId, AccountStatGroups, platformId, cancellationToken);

            await Task.WhenAll(profileTask, accountStatsTask).ConfigureAwait(false);

            var profile = EnsureSuccess(profileTask.Result, response => response.Response, "GetProfile");
            var accountStats = EnsureSuccess(accountStatsTask.Result, response => response.Response, "GetHistoricalStatsForAccount");
            var historicalCharacters = accountStats.Characters?.ToArray() ?? [];

            var characterIds = historicalCharacters.Select(character => character.CharacterId).ToArray();

            var historicalStatsTask = FetchModeStatsAsync(platformId, playerMembershipId, characterIds, cancellationToken);
            var weaponHistoryTask = FetchUniqueWeaponHistoryAsync(platformId, playerMembershipId, characterIds, cancellationToken);
            var activityHistoryTask = FetchActivityHistoriesAsync(platformId, playerMembershipId, characterIds, cancellationToken);

            await Task.WhenAll(historicalStatsTask, weaponHistoryTask, activityHistoryTask).ConfigureAwait(false);

            var activityHistory = activityHistoryTask.Result;
            var pgcrs = await FetchPgcrsAsync(activityHistory, cancellationToken).ConfigureAwait(false);
            var characterClassById = BuildCharacterClassMap(historicalCharacters, pgcrs.Values, playerMembershipId, characterIds);

            var report = new DestinyReport
            {
                PlatformId = platformId,
                PlayerMembershipId = playerMembershipId
            };

            ApplyAccountStats(report, accountStats, historicalCharacters, characterClassById);
            ApplyProfileStats(report, profile, manifest);
            ApplyModeStats(report, historicalStatsTask.Result);
            await ApplyActivityDerivedStatsAsync(report, platformId, playerMembershipId, activityHistory, pgcrs, manifest, cancellationToken).ConfigureAwait(false);
            await ApplyWeaponStatsAsync(report, weaponHistoryTask.Result, manifest, cancellationToken).ConfigureAwait(false);
            ApplyTriumphSeals(report, profile, manifest);
            ApplyActivityTriumphRecords(report, profile);

            var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, platformId)
                & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, playerMembershipId);

            await reports.ReplaceOneAsync(filter, report, new ReplaceOptions { IsUpsert = true }, cancellationToken)
                .ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private async Task<ManifestContext> GetManifestAsync(CancellationToken cancellationToken)
    {
        var manifest = await cache.GetOrCreateAsync(
                "bungie:destiny2:manifest",
                async ct =>
                {
                    var response = await bungieClient.Destiny2_GetDestinyManifestAsync(ct).ConfigureAwait(false);
                    return EnsureSuccess(response, item => item.Response, "GetDestinyManifest");
                },
                new HybridCacheEntryOptions
                {
                    Expiration = ManifestCacheDuration,
                    LocalCacheExpiration = ManifestCacheDuration
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ManifestContext(manifest, this);
    }

    private async Task<JObject> GetManifestTableAsync(DestinyManifest manifest, string tableName, CancellationToken cancellationToken)
    {
        var path = manifest.JsonWorldComponentContentPaths["en"][tableName];
        var cacheKey = $"bungie:destiny2:manifest:{manifest.Version}:{tableName}";
        var json = await cache.GetOrCreateAsync(
                cacheKey,
                async ct =>
                {
                    var httpClient = httpClientFactory.CreateClient();
                    return await httpClient.GetStringAsync(new Uri($"{BungieNetBaseUrl}{path}"), ct).ConfigureAwait(false);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = ManifestCacheDuration,
                    LocalCacheExpiration = ManifestCacheDuration
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return JObject.Parse(json);
    }

    private async Task<WeaponDefinitionSummary?> GetInventoryItemSummaryAsync(
        DestinyManifest manifest,
        int itemHash,
        CancellationToken cancellationToken)
    {
        try
        {
            var hashIdentifier = ToUnsignedHashIdentifier(itemHash);
            var cacheKey = $"bungie:destiny2:manifest:{manifest.Version}:{InventoryItemDefinitionType}:{hashIdentifier}";
            return await cache.GetOrCreateAsync(
                    cacheKey,
                    async ct =>
                    {
                        var operation = $"GetDestinyEntityDefinition:{InventoryItemDefinitionType}:{hashIdentifier}";
                        var response = await bungieClient.Destiny2_GetDestinyEntityDefinitionAsync(
                                InventoryItemDefinitionType,
                                hashIdentifier,
                                ct)
                            .ConfigureAwait(false);
                        var definition = EnsureSuccess(response, item => item.Response, operation);
                        return ToWeaponDefinitionSummary(definition);
                    },
                    new HybridCacheEntryOptions
                    {
                        Expiration = ManifestCacheDuration,
                        LocalCacheExpiration = ManifestCacheDuration
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve Destiny inventory item definition {ItemHash}.", itemHash);
            return null;
        }
    }

    private static string ToUnsignedHashIdentifier(int hash)
    {
        return unchecked((uint)hash).ToString();
    }

    private async Task<Dictionary<int, WeaponDefinitionSummary>> GetInventoryItemSummariesAsync(
        DestinyManifest manifest,
        IEnumerable<int> itemHashes,
        CancellationToken cancellationToken)
    {
        var tasks = itemHashes
            .Distinct()
            .Select(async itemHash => new
            {
                ItemHash = itemHash,
                Summary = await GetInventoryItemSummaryAsync(manifest, itemHash, cancellationToken).ConfigureAwait(false)
            });

        var summaries = await Task.WhenAll(tasks).ConfigureAwait(false);
        return summaries
            .Where(item => item.Summary is not null)
            .ToDictionary(item => item.ItemHash, item => item.Summary!);
    }

    private static T EnsureSuccess<TResponse, T>(TResponse response, Func<TResponse, T> getPayload, string operation)
        where TResponse : BungieResponse
    {
        if (response.ErrorCode != 1)
        {
            throw new InvalidOperationException($"{operation} failed with Bungie error code {response.ErrorCode}: {response.Message}");
        }

        return getPayload(response) ?? throw new InvalidOperationException($"{operation} returned an empty response.");
    }

    private static Dictionary<long, string> BuildCharacterClassMap(
        IEnumerable<DestinyHistoricalStatsPerCharacter> historicalCharacters,
        IEnumerable<DestinyPostGameCarnageReportData> pgcrs,
        long playerMembershipId,
        IReadOnlyCollection<long> historicalCharacterIds)
    {
        var characterClasses = historicalCharacterIds.ToDictionary(characterId => characterId, _ => "Unknown");
        foreach (var character in historicalCharacters)
        {
            if (TryReadHistoricalCharacterClass(character, out var className))
            {
                characterClasses[character.CharacterId] = className;
            }
        }

        foreach (var entry in pgcrs
                     .SelectMany(pgcr => pgcr.Entries ?? [])
                     .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId == playerMembershipId)
                     .Where(entry => characterClasses.ContainsKey(entry.CharacterId))
                     .Where(entry => characterClasses[entry.CharacterId] == "Unknown")
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Player.CharacterClass)))
        {
            characterClasses[entry.CharacterId] = entry.Player.CharacterClass;
        }

        return characterClasses;
    }

    private static bool TryReadHistoricalCharacterClass(DestinyHistoricalStatsPerCharacter character, out string className)
    {
        className = "Unknown";
        foreach (var key in new[] { "characterClass", "className", "class", "classType" })
        {
            if (!character.AdditionalProperties.TryGetValue(key, out var value))
            {
                continue;
            }

            className = value switch
            {
                string text => ClassName(text),
                int classType => ClassName(classType),
                long classType => ClassName((int)classType),
                JValue { Value: string text } => ClassName(text),
                JValue { Value: long classType } => ClassName((int)classType),
                JValue { Value: int classType } => ClassName(classType),
                _ => "Unknown"
            };

            if (className != "Unknown")
            {
                return true;
            }
        }

        return false;
    }

    private static string ClassName(string className)
    {
        if (className.Equals("Titan", StringComparison.OrdinalIgnoreCase))
        {
            return "Titan";
        }

        if (className.Equals("Hunter", StringComparison.OrdinalIgnoreCase))
        {
            return "Hunter";
        }

        if (className.Equals("Warlock", StringComparison.OrdinalIgnoreCase))
        {
            return "Warlock";
        }

        return "Unknown";
    }

    private static string ClassName(int classType) => classType switch
    {
        0 => "Titan",
        1 => "Hunter",
        2 => "Warlock",
        _ => "Unknown"
    };

    private static Dictionary<long, string> NormalizeCharacterClassMap(IReadOnlyDictionary<long, string> characterClassById)
    {
        return characterClassById.ToDictionary(
            item => item.Key,
            item => ClassName(item.Value));
    }

    private static Dictionary<string, TimeSpan> BuildPlaytimeByClass(
        IEnumerable<DestinyHistoricalStatsPerCharacter> historicalCharacters,
        IReadOnlyDictionary<long, string> characterClassById)
    {
        var playtimeByClass = new Dictionary<string, TimeSpan>();
        var normalizedClassById = NormalizeCharacterClassMap(characterClassById);

        foreach (var character in historicalCharacters)
        {
            var className = normalizedClassById.GetValueOrDefault(character.CharacterId, "Unknown");
            var seconds = GetStat(character.Merged?.AllTime, "secondsPlayed");
            playtimeByClass[className] = playtimeByClass.GetValueOrDefault(className) + TimeSpan.FromSeconds(seconds);
        }

        return playtimeByClass;
    }

    private async Task<Dictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>>> FetchModeStatsAsync(
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<long> characterIds,
        CancellationToken cancellationToken)
    {
        var modes = new[] { ActivityModes.AllPvP, ActivityModes.AllPvE, ActivityModes.Gambit, ActivityModes.GambitPrime };
        var tasks = characterIds
            .SelectMany(characterId => modes.Select(mode => FetchModeStatsAsync(characterId, mode)))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(item => (item.CharacterId, item.Mode), item => item.Response);

        async Task<(long CharacterId, int Mode, IDictionary<string, DestinyHistoricalStatsByPeriod> Response)> FetchModeStatsAsync(long characterId, int mode)
        {
            var operation = $"GetHistoricalStats:{characterId}:{mode}";
            var response = await bungieClient.Destiny2_GetHistoricalStatsAsync(
                            characterId,
                            null,
                            null,
                            playerMembershipId,
                            ModeStatGroups,
                            platformId,
                            [mode],
                            null,
                            cancellationToken)
                .ConfigureAwait(false);

            return (characterId, mode, EnsureSuccess(response, item => item.Response, operation));
        }
    }

    private async Task<Dictionary<long, ICollection<DestinyHistoricalWeaponStats>>> FetchUniqueWeaponHistoryAsync(
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<long> characterIds,
        CancellationToken cancellationToken)
    {
        var tasks = characterIds.Select(async characterId =>
        {
            var operation = $"GetUniqueWeaponHistory:{characterId}";
            var response = await bungieClient.Destiny2_GetUniqueWeaponHistoryAsync(characterId, playerMembershipId, platformId, cancellationToken)
                .ConfigureAwait(false);

            return (characterId, Weapons: EnsureSuccess(response, item => item.Response, operation).Weapons ?? []);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(item => item.characterId, item => item.Weapons);
    }

    private async Task<List<DestinyHistoricalStatsPeriodGroup>> FetchActivityHistoriesAsync(
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<long> characterIds,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<long, DestinyHistoricalStatsPeriodGroup>();

        var tasks = characterIds.Select(FetchPagesAsync);
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.Values.OrderBy(activity => activity.Period).ToList();

        async Task FetchPagesAsync(long characterId)
        {
            var page = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var operation = $"GetActivityHistory:{characterId}:{page}";
                var response = await bungieClient.Destiny2_GetActivityHistoryAsync(characterId, PageSize, playerMembershipId, platformId, null, page, cancellationToken)
                    .ConfigureAwait(false);

                var payload = EnsureSuccess(response, item => item.Response, operation);
                var activities = payload.Activities?.ToArray() ?? [];
                if (activities.Length == 0)
                {
                    break;
                }

                foreach (var activity in activities)
                {
                    results.TryAdd(activity.ActivityDetails.InstanceId, activity);
                }

                if (activities.Length < PageSize)
                {
                    break;
                }

                page++;
            }
        }
    }

    private async Task<Dictionary<long, DestinyPostGameCarnageReportData>> FetchPgcrsAsync(
        IEnumerable<DestinyHistoricalStatsPeriodGroup> activities,
        CancellationToken cancellationToken)
    {
        var activityIds = activities
            .Select(activity => activity.ActivityDetails.InstanceId)
            .Where(instanceId => instanceId > 0)
            .Distinct()
            .ToArray();

        var results = new ConcurrentDictionary<long, DestinyPostGameCarnageReportData>();
        using var throttler = new SemaphoreSlim(MaxConcurrentPgcrRequests);
        var tasks = activityIds.Select(async activityId =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var operation = $"GetPostGameCarnageReport:{activityId}";
                var response = await bungieClient.Destiny2_GetPostGameCarnageReportAsync(activityId, cancellationToken)
                    .ConfigureAwait(false);
                var pgcr = EnsureSuccess(response, item => item.Response, operation);

                results.TryAdd(activityId, pgcr);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(item => item.Key, item => item.Value);
    }

    private static void ApplyAccountStats(
        DestinyReport report,
        DestinyHistoricalStatsAccountResult accountStats,
        IEnumerable<DestinyHistoricalStatsPerCharacter> historicalCharacters,
        IReadOnlyDictionary<long, string> characterClassById)
    {
        report.TotalPlaytime = TimeSpan.FromSeconds(accountStats.Characters.Sum(c => c.Results.Sum(a => a.Value.AllTime?.TryGetValue("secondsPlayed", out var stat) ?? false ? stat?.Basic.Value ?? 0 : 0)));
        report.TotalKills = (long)accountStats.Characters.Sum(c => c.Results.Sum(a => a.Value.AllTime?.TryGetValue("kills", out var stat) ?? false ? stat?.Basic.Value ?? 0 : 0));
        report.TotalDeaths = (long)accountStats.Characters.Sum(c => c.Results.Sum(a => a.Value.AllTime?.TryGetValue("deaths", out var stat) ?? false ? stat?.Basic.Value ?? 0 : 0));
        report.Misadventures = (int)accountStats.Characters.Sum(c => c.Results.Sum(a => a.Value.AllTime?.TryGetValue("suicides", out var stat) ?? false ? stat?.Basic.Value ?? 0 : 0));

        report.PlaytimeByClass = BuildPlaytimeByClass(historicalCharacters, characterClassById);
    }

    private static void ApplyProfileStats(DestinyReport report, DestinyProfileResponse profile, ManifestContext manifest)
    {
        var metrics = profile.Metrics?.Data?.Metrics;
        if (metrics is null)
        {
            return;
        }

        report.GoodBoyProtocol = GetMetricProgress(metrics, manifest.FindMetricHash("Good Boy Protocol"));
        report.FishCaught = GetMetricProgress(metrics, manifest.FindMetricHash("Total Fish Caught"));
    }

    private static int GetMetricProgress(IDictionary<string, DestinyMetricComponent> metrics, uint? metricHash)
    {
        return metricHash is not null && metrics.TryGetValue(metricHash.Value.ToString(), out var metric)
            ? metric.ObjectiveProgress?.Progress ?? 0
            : 0;
    }

    private static void ApplyModeStats(
        DestinyReport report,
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats)
    {
        var pveSeconds = SumModeStat(modeStats, ActivityModes.AllPvE, "secondsPlayed");
        var pvpSeconds = SumModeStat(modeStats, ActivityModes.AllPvP, "secondsPlayed");
        var gambitSeconds = SumModeStat(modeStats, ActivityModes.Gambit, "secondsPlayed")
            + SumModeStat(modeStats, ActivityModes.GambitPrime, "secondsPlayed");

        report.PlaytimeByActivity["PvE"] = TimeSpan.FromSeconds(pveSeconds);
        report.PlaytimeByActivity["Crucible"] = TimeSpan.FromSeconds(pvpSeconds);
        report.PlaytimeByActivity["Gambit"] = TimeSpan.FromSeconds(gambitSeconds);

        report.CrucibleKd = WeightedRatio(modeStats, ActivityModes.AllPvP, "kills", "deaths", "killsDeathsRatio");
        report.CrucibleKda = AverageModeStat(modeStats, ActivityModes.AllPvP, "killsDeathsAssists");
        report.GambitKd = WeightedRatio(modeStats, [ActivityModes.Gambit, ActivityModes.GambitPrime], "kills", "deaths", "killsDeathsRatio");
        report.GambitKda = AverageModeStat(modeStats, [ActivityModes.Gambit, ActivityModes.GambitPrime], "killsDeathsAssists");
        report.CrucibleMatchesPlayed = (int)SumModeStat(modeStats, ActivityModes.AllPvP, "activitiesEntered");
        report.GambitMatchesPlayed = (int)(SumModeStat(modeStats, ActivityModes.Gambit, "activitiesEntered") + SumModeStat(modeStats, ActivityModes.GambitPrime, "activitiesEntered"));
        report.CrucibleWins = (int)SumModeStat(modeStats, ActivityModes.AllPvP, "activitiesWon");
        report.GambitWins = (int)(SumModeStat(modeStats, ActivityModes.Gambit, "activitiesWon") + SumModeStat(modeStats, ActivityModes.GambitPrime, "activitiesWon"));
    }

    private async Task ApplyActivityDerivedStatsAsync(
        DestinyReport report,
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<DestinyHistoricalStatsPeriodGroup> activityHistory,
        IReadOnlyDictionary<long, DestinyPostGameCarnageReportData> pgcrs,
        ManifestContext manifest,
        CancellationToken cancellationToken)
    {
        var activityDefinitions = await manifest.GetTableAsync("DestinyActivityDefinition", cancellationToken).ConfigureAwait(false);
        var destinationDefinitions = await manifest.GetTableAsync("DestinyDestinationDefinition", cancellationToken).ConfigureAwait(false);

        ApplyPatrolTime(report, activityHistory.Where(activity => IncludesMode(activity, ActivityModes.Patrol)), activityDefinitions, destinationDefinitions);
        await ApplyPgcrAggregatesAsync(
                report,
                platformId,
                playerMembershipId,
                pgcrs,
                activityDefinitions,
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ApplyPatrolTime(
        DestinyReport report,
        IEnumerable<DestinyHistoricalStatsPeriodGroup> patrolActivities,
        JObject activityDefinitions,
        JObject destinationDefinitions)
    {
        foreach (var activity in patrolActivities)
        {
            var activityDefinition = GetDefinition(activityDefinitions, activity.ActivityDetails.ReferenceId)
                ?? GetDefinition(activityDefinitions, activity.ActivityDetails.DirectorActivityHash);
            var destinationHash = activityDefinition?["destinationHash"]?.Value<long>() ?? 0;
            var destination = GetDefinition(destinationDefinitions, destinationHash);
            var destinationName = destination?["displayProperties"]?["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(destinationName))
            {
                destinationName = destinationHash > 0 ? destinationHash.ToString() : "Unknown";
            }

            var seconds = GetStat(activity.Values, "timePlayedSeconds");
            if (seconds <= 0)
            {
                seconds = GetStat(activity.Values, "activityDurationSeconds");
            }

            report.PatrolTimeByPlanet[destinationName] = report.PatrolTimeByPlanet.GetValueOrDefault(destinationName) + TimeSpan.FromSeconds(seconds);
        }
    }

    private async Task ApplyPgcrAggregatesAsync(
        DestinyReport report,
        int platformId,
        long playerMembershipId,
        IReadOnlyDictionary<long, DestinyPostGameCarnageReportData> pgcrs,
        JObject activityDefinitions,
        ManifestContext manifest,
        CancellationToken cancellationToken)
    {
        var allPgcrs = pgcrs.Values.OrderBy(pgcr => pgcr.Period).ToArray();
        var playerEncounterIncrements = new Dictionary<(int MembershipType, long MembershipId), int>();
        var pvpOpponents = new Dictionary<long, RivalAggregate>();
        var gambitOpponents = new Dictionary<long, RivalAggregate>();
        var pveWeapons = new Dictionary<int, int>();
        var pvpWeapons = new Dictionary<int, int>();
        var gambitWeapons = new Dictionary<int, int>();
        var raidCompletions = new Dictionary<string, ActivityCompletionAggregate>(StringComparer.OrdinalIgnoreCase);
        var dungeonCompletions = new Dictionary<string, ActivityCompletionAggregate>(StringComparer.OrdinalIgnoreCase);
        var activityTime = new TimeSpan();

        foreach (var pgcr in allPgcrs)
        {
            var playerEntry = FindPlayerEntry(pgcr, playerMembershipId);
            if (playerEntry is null)
            {
                continue;
            }

            var playerCompleted = GetStat(playerEntry.Values, "completed") > 0;
            var playerKills = GetStat(playerEntry.Values, "kills");
            var playerDeaths = GetStat(playerEntry.Values, "deaths");
            if (playerKills <= 0)
            {
                report.ZeroKillActivities++;
            }

            var activityName = ActivityName(activityDefinitions, pgcr.ActivityDetails.ReferenceId, pgcr.ActivityDetails.DirectorActivityHash);
            var isRaid = IncludesMode(pgcr, ActivityModes.Raid);
            var isDungeon = IncludesMode(pgcr, ActivityModes.Dungeon);
            var isPvp = IncludesMode(pgcr, ActivityModes.AllPvP);
            var isGambit = IncludesMode(pgcr, ActivityModes.Gambit) || IncludesMode(pgcr, ActivityModes.GambitPrime);
            var isPrivateCrucible = isPvp && HasActivityTypeHash(pgcr, activityDefinitions, PrivateCrucibleActivityTypeHashes);
            var isPrivateGambit = isGambit && HasActivityTypeHash(pgcr, activityDefinitions, PrivateGambitActivityTypeHashes);
            var activityPlayerEntries = GetActivityPlayerEntries(pgcr);
            var activityWasStartedFromBeginning = GetActivityWasStartedFromBeginning(pgcr, activityPlayerEntries);
            var wasStartedFromBeginning = activityWasStartedFromBeginning == true;
            var isFlawless = (isRaid || isDungeon) && playerCompleted && wasStartedFromBeginning && activityPlayerEntries.Length > 0 && activityPlayerEntries.All(entry => GetStat(entry.Values, "deaths") <= 0);
            var isSolo = (isRaid || isDungeon) && playerCompleted && wasStartedFromBeginning && activityPlayerEntries.Select(entry => entry.Player?.DestinyUserInfo?.MembershipId).Distinct().Count() == 1;
            var activityCompletedAt = GetActivityCompletedAt(pgcr, playerEntry);
            var isContest = IsContest(pgcr, activityCompletedAt, isRaid, isDungeon);
            var isSoloFlawless = isSolo && isFlawless;
            activityTime += TimeSpan.FromSeconds(GetStat(playerEntry.Values, "activityDurationSeconds"));

            if (playerCompleted && isRaid)
            {
                AddCompletion(raidCompletions, activityName, isContest, isFlawless, isSolo, isSoloFlawless);
            }

            if (playerCompleted && isDungeon)
            {
                AddCompletion(dungeonCompletions, activityName, isContest, isFlawless, isSolo, isSoloFlawless);
            }

            var otherPlayers = (pgcr.Entries ?? [])
                .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId is > 0)
                .Where(entry => entry.Player.DestinyUserInfo.MembershipId != playerMembershipId)
                .Select(entry => entry.Player.DestinyUserInfo)
                .GroupBy(player => (player.MembershipType, player.MembershipId))
                .Select(group => group.First())
                .ToArray();

            foreach (var otherPlayer in otherPlayers)
            {
                var key = (otherPlayer.MembershipType, otherPlayer.MembershipId);
                playerEncounterIncrements[key] = playerEncounterIncrements.GetValueOrDefault(key) + 1;
            }

            if (isPvp)
            {
                if (!isPrivateCrucible)
                {
                    TrackRivals(pvpOpponents, pgcr, playerEntry, playerMembershipId, playerKills, playerDeaths);
                    AddWeapons(pvpWeapons, playerEntry);
                }               
            }
            else if (isGambit)
            {
                if (!isPrivateGambit)
                {
                    TrackRivals(gambitOpponents, pgcr, playerEntry, playerMembershipId, playerKills, playerDeaths);
                    AddWeapons(gambitWeapons, playerEntry);
                    report.GambitMotesBanked += (int)GetMoteStat(playerEntry, "bank", "deposit");
                    report.GambitMotesLost += (int)GetMoteStat(playerEntry, "lost");
                }
            }
            else
            {
                AddWeapons(pveWeapons, playerEntry);
            }
        }

        report.RaidCompletions = ToCompletionSummaries(raidCompletions);
        report.DungeonCompletions = ToCompletionSummaries(dungeonCompletions);
        await ApplyPlayerEncounterIncrementsAsync(
                report,
                platformId,
                playerMembershipId,
                playerEncounterIncrements,
                cancellationToken)
            .ConfigureAwait(false);

        ApplyRival(report, pvpOpponents, isGambit: false);
        ApplyRival(report, gambitOpponents, isGambit: true);

        var weaponDefinitions = await GetInventoryItemSummariesAsync(
                manifest.Manifest,
                TopWeaponHashes(pveWeapons).Concat(TopWeaponHashes(pvpWeapons)).Concat(TopWeaponHashes(gambitWeapons)),
                cancellationToken)
            .ConfigureAwait(false);

        report.PvETopWeapons = BuildWeaponReports(pveWeapons, weaponDefinitions);
        report.CrucibleTopWeapons = BuildWeaponReports(pvpWeapons, weaponDefinitions);
        report.GambitTopWeapons = BuildWeaponReports(gambitWeapons, weaponDefinitions);

        report.TotalActivityTime = activityTime;
    }

    private async Task ApplyPlayerEncounterIncrementsAsync(
        DestinyReport report,
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<(int MembershipType, long MembershipId), int> encounterIncrements,
        CancellationToken cancellationToken)
    {
        var encounters = mongoDatabase.GetCollection<PlayerEncounterAggregate>("player_encounters");
        if (encounterIncrements.Count > 0)
        {
            var updates = encounterIncrements
                .Where(item => item.Key.MembershipType > 0 && item.Key.MembershipId > 0 && item.Value > 0)
                .Select(item =>
                {
                    var filter = Builders<PlayerEncounterAggregate>.Filter.Eq(encounter => encounter.OwnerMembershipType, ownerMembershipType)
                        & Builders<PlayerEncounterAggregate>.Filter.Eq(encounter => encounter.OwnerMembershipId, ownerMembershipId)
                        & Builders<PlayerEncounterAggregate>.Filter.Eq(encounter => encounter.EncounteredMembershipType, item.Key.MembershipType)
                        & Builders<PlayerEncounterAggregate>.Filter.Eq(encounter => encounter.EncounteredMembershipId, item.Key.MembershipId);
                    var update = Builders<PlayerEncounterAggregate>.Update.Inc(encounter => encounter.Count, item.Value);

                    return new UpdateOneModel<PlayerEncounterAggregate>(filter, update)
                    {
                        IsUpsert = true
                    };
                })
                .Cast<WriteModel<PlayerEncounterAggregate>>()
                .ToArray();

            if (updates.Length > 0)
            {
                await encounters.BulkWriteAsync(updates, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var ownerFilter = Builders<PlayerEncounterAggregate>.Filter.Eq(encounter => encounter.OwnerMembershipType, ownerMembershipType)
            & Builders<PlayerEncounterAggregate>.Filter.Eq(encounter => encounter.OwnerMembershipId, ownerMembershipId)
            & Builders<PlayerEncounterAggregate>.Filter.Gt(encounter => encounter.EncounteredMembershipType, 0)
            & Builders<PlayerEncounterAggregate>.Filter.Gt(encounter => encounter.EncounteredMembershipId, 0);
        var uniquePlayers = await encounters.CountDocumentsAsync(ownerFilter, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var mostPlayedWith = await encounters
            .Find(ownerFilter)
            .SortByDescending(encounter => encounter.Count)
            .Limit(DestinyReport.MostPlayedWithLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var populateMostPlayedWithTasks = mostPlayedWith
            .Select(async encounter =>
            {
                return await GetPlayerInfoAsync(encounter, cancellationToken).ConfigureAwait(false);
            })
            .ToArray();
        var mostPlayedWithInfo = await Task.WhenAll(populateMostPlayedWithTasks).ConfigureAwait(false);

        report.UniquePlayersPlayedWith = uniquePlayers > int.MaxValue ? int.MaxValue : (int)uniquePlayers;
        var mostPlayedWithInfoByMembershipId = mostPlayedWithInfo.ToDictionary(player => (player.MembershipType, player.MembershipId));
        report.MostPlayedWith = mostPlayedWith
            .Select(encounter => new PlayerEncounterReport
            {
                Player = mostPlayedWithInfoByMembershipId.GetValueOrDefault((encounter.EncounteredMembershipType, encounter.EncounteredMembershipId))
                    ?? new ReportPlayer
                    {
                        MembershipId = encounter.EncounteredMembershipId,
                        MembershipType = encounter.EncounteredMembershipType
                    },
                EncounterCount = encounter.Count
            })
            .ToList();
    }

    private async Task ApplyWeaponStatsAsync(
        DestinyReport report,
        IReadOnlyDictionary<long, ICollection<DestinyHistoricalWeaponStats>> uniqueWeaponHistory,
        ManifestContext manifest,
        CancellationToken cancellationToken)
    {
        var fallback = uniqueWeaponHistory.Values
            .SelectMany(weapons => weapons)
            .GroupBy(weapon => weapon.ReferenceId)
            .ToDictionary(group => group.Key, group => group.Sum(weapon => (int)GetStat(weapon.Values, "uniqueWeaponKills")));

        if (report.PvETopWeapons.Count == 0)
        {
            var weaponDefinitions = await GetInventoryItemSummariesAsync(manifest.Manifest, TopWeaponHashes(fallback), cancellationToken)
                .ConfigureAwait(false);

            report.PvETopWeapons = BuildWeaponReports(fallback, weaponDefinitions);
        }
    }

    private static void ApplyTriumphSeals(DestinyReport report, DestinyProfileResponse profile, ManifestContext manifest)
    {
        var profileRecords = profile.ProfileRecords?.Data;
        if (profileRecords?.Records is null)
        {
            return;
        }

        var seenCompletionRecordHashes = new HashSet<long>();
        foreach (var sealPresentationNodeHash in GetSealPresentationNodeHashes(manifest.PresentationNodes))
        {
            var sealNode = GetDefinition(manifest.PresentationNodes, sealPresentationNodeHash);
            var completionRecordHash = sealNode?["completionRecordHash"]?.Value<long>() ?? 0;
            if (completionRecordHash <= 0 || !seenCompletionRecordHashes.Add(completionRecordHash))
            {
                continue;
            }

            var definition = GetDefinition(manifest.Records, completionRecordHash);
            if (definition is null)
            {
                continue;
            }

            TryGetProfileRecord(profileRecords.Records, completionRecordHash, out var component);
            if (component is null || !IsRecordCompleted(component))
            {
                continue;
            }

            report.TriumphSeals.Add(new DestinyTriumphSeal
            {
                Name = definition["displayProperties"]?["name"]?.Value<string>() ?? "",
                Description = definition["displayProperties"]?["description"]?.Value<string>() ?? "",
                IconUrl = BungieUrl(sealNode?["displayProperties"]?["icon"]?.Value<string>()),
                IsCompleted = true
            });
        }
    }

    private void ApplyActivityTriumphRecords(DestinyReport report, DestinyProfileResponse profile)
    {
        var profileRecords = profile.ProfileRecords?.Data;
        if (profileRecords?.Records is null)
        {
            return;
        }

        foreach (var raid in activityTriumphRecords.Raids)
        {
            var flawlessClear = IsProfileRecordCompleted(profileRecords.Records, raid.RecordId);
            if (!flawlessClear)
            {
                continue;
            }

            UpdateActivityCompletionSummary(
                report.RaidCompletions,
                raid.ActivityName,
                summary => summary with { FlawlessClear = true });
        }

        foreach (var dungeon in activityTriumphRecords.Dungeons)
        {
            var soloClear = IsProfileRecordCompleted(profileRecords.Records, dungeon.SoloRecordId);
            var flawlessClear = IsProfileRecordCompleted(profileRecords.Records, dungeon.FlawlessRecordId);
            var soloFlawlessClear = IsProfileRecordCompleted(profileRecords.Records, dungeon.SoloFlawlessRecordId);
            if (!soloClear && !flawlessClear && !soloFlawlessClear)
            {
                continue;
            }

            UpdateActivityCompletionSummary(
                report.DungeonCompletions,
                dungeon.ActivityName,
                summary => summary with
                {
                    SoloClear = summary.SoloClear || soloClear || soloFlawlessClear,
                    FlawlessClear = summary.FlawlessClear || flawlessClear || soloFlawlessClear,
                    SoloFlawlessClear = summary.SoloFlawlessClear || soloFlawlessClear
                });
        }
    }

    private static bool IsProfileRecordCompleted(
        IDictionary<string, DestinyRecordComponent> profileRecords,
        long recordHash)
    {
        return recordHash > 0
            && TryGetProfileRecord(profileRecords, recordHash, out var component)
            && component is not null
            && IsRecordCompleted(component);
    }

    private static void UpdateActivityCompletionSummary(
        IList<ActivityCompletionSummary> completions,
        string activityName,
        Func<ActivityCompletionSummary, ActivityCompletionSummary> update)
    {
        if (string.IsNullOrWhiteSpace(activityName))
        {
            return;
        }

        var normalizedName = ContestModeLookup.NormalizeActivityName(activityName);
        for (var i = 0; i < completions.Count; i++)
        {
            if (!string.Equals(completions[i].ActivityName, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            completions[i] = update(completions[i]);
            return;
        }
    }

    private static IEnumerable<long> GetSealPresentationNodeHashes(JObject presentationNodes)
    {
        return TriumphSealRootPresentationNodeHashes
            .Select(rootHash => GetDefinition(presentationNodes, rootHash))
            .SelectMany(GetChildPresentationNodeHashes);
    }

    private static IEnumerable<long> GetChildPresentationNodeHashes(JObject? presentationNode)
    {
        return presentationNode?["children"]?["presentationNodes"]?
            .OrderBy(node => node["nodeDisplayPriority"]?.Value<int>() ?? int.MaxValue)
            .Select(node => node["presentationNodeHash"]?.Value<long>() ?? 0)
            .Where(hash => hash > 0) ?? [];
    }

    private static bool IsRecordCompleted(DestinyRecordComponent component)
    {
        const int objectiveNotCompleted = 4;
        return component.CompletedCount > 0 || (component.State & objectiveNotCompleted) == 0;
    }

    private static bool TryGetProfileRecord(
        IDictionary<string, DestinyRecordComponent> profileRecords,
        long recordHash,
        out DestinyRecordComponent? component)
    {
        return TryGetHashValue(profileRecords, recordHash, out component);
    }

    private static bool TryGetHashValue<T>(
        IDictionary<string, T> values,
        long hash,
        out T? value)
    {
        if (values.TryGetValue(hash.ToString(), out value))
        {
            return true;
        }

        if (hash is >= int.MinValue and <= int.MaxValue)
        {
            return values.TryGetValue(unchecked((uint)(int)hash).ToString(), out value);
        }

        if (hash is > int.MaxValue and <= uint.MaxValue)
        {
            return values.TryGetValue(unchecked((int)(uint)hash).ToString(), out value);
        }

        value = default;
        return false;
    }

    private static double SumModeStat(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        int mode,
        string statId)
    {
        return modeStats
            .Where(item => item.Key.Mode == mode)
            .Sum(item => GetStat(GetPreferredStatsBucket(item.Value, mode)?.AllTime, statId));
    }

    private static double AverageModeStat(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        int mode,
        string statId)
    {
        return AverageModeStat(modeStats, [mode], statId);
    }

    private static double AverageModeStat(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        IReadOnlyCollection<int> modes,
        string statId)
    {
        var values = modeStats
            .Where(item => modes.Contains(item.Key.Mode))
            .Select(item => GetStat(GetPreferredStatsBucket(item.Value, item.Key.Mode)?.AllTime, statId))
            .Where(value => value > 0)
            .ToArray();

        return values.Length == 0 ? 0 : Math.Round(values.Average(), 3);
    }

    private static double WeightedRatio(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        int mode,
        string numeratorStat,
        string denominatorStat,
        string fallbackStat)
    {
        return WeightedRatio(modeStats, [mode], numeratorStat, denominatorStat, fallbackStat);
    }

    private static double WeightedRatio(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        IReadOnlyCollection<int> modes,
        string numeratorStat,
        string denominatorStat,
        string fallbackStat)
    {
        var values = modeStats
            .Where(item => modes.Contains(item.Key.Mode))
            .Select(item => GetPreferredStatsBucket(item.Value, item.Key.Mode)?.AllTime)
            .Where(allTime => allTime is not null)
            .ToArray();

        var numerator = values.Sum(allTime => GetStat(allTime, numeratorStat));
        var denominator = values.Sum(allTime => GetStat(allTime, denominatorStat));
        if (denominator > 0)
        {
            return Math.Round(numerator / denominator, 3);
        }

        var fallbackValues = values.Select(allTime => GetStat(allTime, fallbackStat)).Where(value => value > 0).ToArray();
        return fallbackValues.Length == 0 ? 0 : Math.Round(fallbackValues.Average(), 3);
    }

    private static double GetStat(IDictionary<string, DestinyHistoricalStatsValue>? stats, string statId)
    {
        return stats is not null && stats.TryGetValue(statId, out var value)
            ? value.Basic?.Value ?? 0
            : 0;
    }

    private static DestinyHistoricalStatsByPeriod? GetPreferredStatsBucket(
        IDictionary<string, DestinyHistoricalStatsByPeriod> stats,
        int mode)
    {
        foreach (var key in PreferredStatsKeys(mode))
        {
            if (stats.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return stats.Values.FirstOrDefault();
    }

    private static IEnumerable<string> PreferredStatsKeys(int mode)
    {
        return mode switch
        {
            ActivityModes.AllPvE => ["allPvE", "allTime"],
            ActivityModes.AllPvP => ["allPvP", "allTime"],
            ActivityModes.Gambit => ["gambit", "allTime"],
            ActivityModes.GambitPrime => ["gambitPrime", "allTime"],
            _ => ["allTime"]
        };
    }

    private static DestinyPostGameCarnageReportEntry? FindPlayerEntry(DestinyPostGameCarnageReportData pgcr, long membershipId)
    {
        return pgcr.Entries?.FirstOrDefault(entry => entry.Player?.DestinyUserInfo?.MembershipId == membershipId);
    }

    private static bool IncludesMode(DestinyPostGameCarnageReportData pgcr, int mode)
    {
        return pgcr.ActivityDetails.Mode == mode || (pgcr.ActivityDetails.Modes?.Contains(mode) ?? false);
    }

    private static bool IncludesMode(DestinyHistoricalStatsPeriodGroup activity, int mode)
    {
        return activity.ActivityDetails.Mode == mode || (activity.ActivityDetails.Modes?.Contains(mode) ?? false);
    }

    private static bool HasActivityTypeHash(
        DestinyPostGameCarnageReportData pgcr,
        JObject activityDefinitions,
        IReadOnlySet<long> activityTypeHashes)
    {
        var definition = GetDefinition(activityDefinitions, pgcr.ActivityDetails.ReferenceId)
            ?? GetDefinition(activityDefinitions, pgcr.ActivityDetails.DirectorActivityHash);
        var activityTypeHash = definition?["activityTypeHash"]?.Value<long>() ?? 0;
        return activityTypeHashes.Contains(activityTypeHash);
    }

    private static bool? GetActivityWasStartedFromBeginning(
        DestinyPostGameCarnageReportData pgcr,
        IReadOnlyCollection<DestinyPostGameCarnageReportEntry> activityPlayerEntries)
    {
        if (pgcr.Period >= SeasonOfTheHauntedRelease)
        {
            return pgcr.ActivityWasStartedFromBeginning;
        }

        if (pgcr.Period < BeyondLightRelease)
        {
            if (pgcr.StartingPhaseIndex is not { } startingPhaseIndex)
            {
                return null;
            }

            var activityHash = ToUnsignedHash(pgcr.ActivityDetails.DirectorActivityHash);
            if (ScourgeOfThePastActivityHashes.Contains(activityHash))
            {
                return startingPhaseIndex <= 1;
            }

            if (LeviathanActivityHashes.Contains(activityHash))
            {
                return startingPhaseIndex is 0 or 2;
            }

            return startingPhaseIndex == 0;
        }

        if (pgcr.Period >= WitchQueenRelease)
        {
            var deathless = activityPlayerEntries.Count > 0
                && activityPlayerEntries.All(entry => GetStat(entry.Values, "deaths") <= 0);
            if (pgcr.ActivityWasStartedFromBeginning == true || deathless)
            {
                return pgcr.ActivityWasStartedFromBeginning;
            }
        }

        return null;
    }

    private static string ActivityName(JObject definitions, int referenceId, int directorActivityHash)
    {
        var definition = GetDefinition(definitions, referenceId) ?? GetDefinition(definitions, directorActivityHash);
        return definition?["displayProperties"]?["name"]?.Value<string>() ?? referenceId.ToString();
    }

    private static long ToUnsignedHash(int hash)
    {
        return unchecked((uint)hash);
    }

    private static JObject? GetDefinition(JObject table, long hash)
    {
        if (table[hash.ToString()] is JObject definition)
        {
            return definition;
        }

        if (hash is >= int.MinValue and <= int.MaxValue)
        {
            var unsignedHash = unchecked((uint)(int)hash).ToString();
            if (table[unsignedHash] is JObject unsignedDefinition)
            {
                return unsignedDefinition;
            }
        }

        if (hash is > int.MaxValue and <= uint.MaxValue)
        {
            var signedHash = unchecked((int)(uint)hash).ToString();
            if (table[signedHash] is JObject signedDefinition)
            {
                return signedDefinition;
            }
        }

        return null;
    }

    private bool IsContest(
        DestinyPostGameCarnageReportData pgcr,
        DateTimeOffset activityCompletedAt,
        bool isRaid,
        bool isDungeon)
    {
        var contestWindows = isRaid
            ? contestMode.Raids
            : isDungeon
                ? contestMode.Dungeons
                : null;
        if (contestWindows is null)
        {
            return false;
        }

        return IsContestActivityHash(pgcr.ActivityDetails.ReferenceId, activityCompletedAt, contestWindows)
            || IsContestActivityHash(pgcr.ActivityDetails.DirectorActivityHash, activityCompletedAt, contestWindows);
    }

    private static bool IsContestActivityHash(
        long activityHash,
        DateTimeOffset activityCompletedAt,
        IReadOnlyDictionary<long, IReadOnlyCollection<ContestModeActivityWindow>> contestWindows)
    {
        return contestWindows.TryGetValue(activityHash, out var windows)
            && windows.Any(window => activityCompletedAt >= window.Start && activityCompletedAt < window.End);
    }

    private static DateTimeOffset GetActivityCompletedAt(
        DestinyPostGameCarnageReportData pgcr,
        DestinyPostGameCarnageReportEntry playerEntry)
    {
        var durationSeconds = GetStat(playerEntry.Values, "activityDurationSeconds");
        return durationSeconds > 0
            ? pgcr.Period.AddSeconds(durationSeconds)
            : pgcr.Period;
    }

    private static DestinyPostGameCarnageReportEntry[] GetActivityPlayerEntries(DestinyPostGameCarnageReportData pgcr)
    {
        return (pgcr.Entries ?? [])
            .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId > 0)
            .ToArray();
    }

    private static void AddCompletion(
        IDictionary<string, ActivityCompletionAggregate> completions,
        string activityName,
        bool contestClear,
        bool flawlessClear,
        bool soloClear,
        bool soloFlawlessClear)
    {
        var key = ContestModeLookup.NormalizeActivityName(activityName);
        if (!completions.TryGetValue(key, out var completion))
        {
            completion = new ActivityCompletionAggregate(key);
            completions[key] = completion;
        }

        completion.CompletionCount++;
        completion.ContestClear |= contestClear;
        completion.FlawlessClear |= flawlessClear;
        completion.SoloClear |= soloClear;
        completion.SoloFlawlessClear |= soloFlawlessClear;
    }

    private static List<ActivityCompletionSummary> ToCompletionSummaries(
        IDictionary<string, ActivityCompletionAggregate> completions)
    {
        return completions.Values
            .OrderBy(completion => completion.ActivityName, StringComparer.OrdinalIgnoreCase)
            .Select(completion => new ActivityCompletionSummary
            {
                ActivityName = completion.ActivityName,
                CompletionCount = completion.CompletionCount,
                ContestClear = completion.ContestClear,
                FlawlessClear = completion.FlawlessClear,
                SoloClear = completion.SoloClear,
                SoloFlawlessClear = completion.SoloFlawlessClear
            })
            .ToList();
    }

    private static ReportPlayer ToReportPlayer(BungiePlayer player, string emblemUrl)
    {
        var user = player.DestinyUserInfo;
        return new ReportPlayer
        {
            MembershipId = user?.MembershipId ?? 0,
            MembershipType = user?.MembershipType ?? 0,
            DisplayName = DisplayName(user),
            EmblemUrl = emblemUrl
        };
    }

    private static string DisplayName(UserInfoCard? user)
    {
        if (user is null)
        {
            return "";
        }

        if (!string.IsNullOrWhiteSpace(user.BungieGlobalDisplayName))
        {
            return user.BungieGlobalDisplayNameCode is > 0
                ? $"{user.BungieGlobalDisplayName}#{user.BungieGlobalDisplayNameCode:0000}"
                : user.BungieGlobalDisplayName;
        }

        return user.DisplayName ?? "";
    }

    private static void TrackRivals(
        IDictionary<long, RivalAggregate> rivals,
        DestinyPostGameCarnageReportData pgcr,
        DestinyPostGameCarnageReportEntry playerEntry,
        long playerMembershipId,
        double playerKills,
        double playerDeaths)
    {
        var playerTeam = GetTeam(playerEntry);
        if (playerTeam is null)
        {
            return;
        }

        var opponents = (pgcr.Entries ?? [])
            .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId is > 0)
            .Where(entry => entry.Player.DestinyUserInfo.MembershipId != playerMembershipId)
            .Where(entry => GetTeam(entry) is { } team && team != playerTeam)
            .Select(entry => ToReportPlayer(entry.Player, ""))
            .GroupBy(player => player.MembershipId)
            .Select(group => group.First());

        foreach (var opponent in opponents)
        {
            var aggregate = rivals.TryGetValue(opponent.MembershipId, out var existing)
                ? existing
                : rivals[opponent.MembershipId] = new RivalAggregate(opponent);

            aggregate.Matches++;
            aggregate.Kills += playerKills;
            aggregate.Deaths += playerDeaths;
            aggregate.Wins += playerEntry.Standing == 0 ? 1 : 0;
            aggregate.Losses += playerEntry.Standing > 0 ? 1 : 0;
        }
    }

    private static double? GetTeam(DestinyPostGameCarnageReportEntry entry)
    {
        return entry.Values?.TryGetValue("team", out var teamValue) == true
            ? teamValue.Basic?.Value
            : null;
    }

    private static void ApplyRival(DestinyReport report, IDictionary<long, RivalAggregate> rivals, bool isGambit)
    {
        var rival = rivals.Values.OrderByDescending(item => item.Matches).FirstOrDefault();
        if (rival is null)
        {
            return;
        }

        var kd = rival.Deaths > 0 ? Math.Round(rival.Kills / rival.Deaths, 3) : rival.Kills;
        if (isGambit)
        {
            report.GambitRival = rival.Player;
            report.KdAgainstGambitRival = kd;
        }
        else
        {
            report.CrucibleRival = rival.Player;
            report.KdAgainstCrucibleRival = kd;
        }
    }

    private static void AddWeapons(IDictionary<int, int> weapons, DestinyPostGameCarnageReportEntry entry)
    {
        foreach (var weapon in entry.Extended?.Weapons ?? [])
        {
            var kills = (int)GetStat(weapon.Values, "uniqueWeaponKills");
            if (kills <= 0)
            {
                kills = (int)GetStat(weapon.Values, "kills");
            }

            weapons.TryGetValue(weapon.ReferenceId, out var currentKills);
            weapons[weapon.ReferenceId] = currentKills + kills;
        }
    }

    private static double GetMoteStat(DestinyPostGameCarnageReportEntry entry, params string[] needles)
    {
        return EnumerateValueDictionaries(entry)
            .SelectMany(dictionary => dictionary)
            .Where(item => needles.Any(needle => item.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .Sum(item => item.Value.Basic?.Value ?? 0);
    }

    private static IEnumerable<IDictionary<string, DestinyHistoricalStatsValue>> EnumerateValueDictionaries(DestinyPostGameCarnageReportEntry entry)
    {
        if (entry.Values is not null)
        {
            yield return entry.Values;
        }

        if (entry.Extended?.Values is not null)
        {
            yield return entry.Extended.Values;
        }

        if (entry.Extended?.ScoreboardValues is not null)
        {
            yield return entry.Extended.ScoreboardValues;
        }
    }

    private static IEnumerable<int> TopWeaponHashes(IDictionary<int, int> weaponKills)
    {
        return weaponKills
            .Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value)
            .Take(10)
            .Select(item => item.Key);
    }

    private static List<WeaponReport> BuildWeaponReports(
        IDictionary<int, int> weaponKills,
        IReadOnlyDictionary<int, WeaponDefinitionSummary>? weaponDefinitions = null)
    {
        return weaponKills
            .Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value)
            .Take(10)
            .Select(item =>
            {
                WeaponDefinitionSummary? definition = null;
                weaponDefinitions?.TryGetValue(item.Key, out definition);
                return new WeaponReport
                {
                    Name = definition?.Name ?? item.Key.ToString(),
                    IconUrl = definition?.IconUrl ?? "",
                    TotalKills = item.Value
                };
            })
            .ToList();
    }

    private static WeaponDefinitionSummary ToWeaponDefinitionSummary(DestinyDefinition definition)
    {
        var displayProperties = TryGetJObject(definition.AdditionalProperties, "displayProperties");
        return new WeaponDefinitionSummary(
            displayProperties?["name"]?.Value<string>() ?? definition.Hash.ToString(),
            BungieUrl(displayProperties?["icon"]?.Value<string>()));
    }

    private static JObject? TryGetJObject(IDictionary<string, object> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value as JObject ?? JObject.FromObject(value);
    }

    private static string BungieUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{BungieNetBaseUrl}{path}";
    }

    private async Task<ReportPlayer> GetPlayerInfoAsync(PlayerEncounterAggregate player, CancellationToken cancellationToken)
    {
        var response = await bungieClient.Destiny2_GetProfileAsync(ProfileCharactersComponents, player.EncounteredMembershipId, player.EncounteredMembershipType, cancellationToken).ConfigureAwait(false);
        var characterResponse = EnsureSuccess(response, profile => profile.Response, $"Destiny2_GetProfileAsync:Characters:{player.EncounteredMembershipType}:{player.EncounteredMembershipId}");
        var lastPlayedCharacter = characterResponse?.Characters?.Data?.Values.OrderByDescending(c => c.DateLastPlayed).FirstOrDefault();
        return new ReportPlayer
        {
            MembershipId = player.EncounteredMembershipId,
            MembershipType = player.EncounteredMembershipType,
            DisplayName = characterResponse?.Profile?.Data?.UserInfo?.DisplayName ?? "",
            EmblemUrl = BungieUrl(lastPlayedCharacter?.EmblemPath)
        };
    }

    private sealed record RivalAggregate(ReportPlayer Player)
    {
        public int Matches { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public double Kills { get; set; }
        public double Deaths { get; set; }
    }

    private sealed record ActivityCompletionAggregate(string ActivityName)
    {
        public int CompletionCount { get; set; }
        public bool ContestClear { get; set; }
        public bool FlawlessClear { get; set; }
        public bool SoloClear { get; set; }
        public bool SoloFlawlessClear { get; set; }
    }

    private sealed record WeaponDefinitionSummary(string Name, string IconUrl);

    private static class ActivityModes
    {
        public const int Raid = 4;
        public const int AllPvP = 5;
        public const int Patrol = 6;
        public const int AllPvE = 7;
        public const int Gambit = 63;
        public const int GambitPrime = 75;
        public const int Dungeon = 82;
    }

    private sealed class ManifestContext(DestinyManifest manifest, CrawlerService service)
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<JObject>>> _tables = new(StringComparer.Ordinal);

        public DestinyManifest Manifest => manifest;

        public JObject PresentationNodes => GetTable("DestinyPresentationNodeDefinition");
        public JObject Records => GetTable("DestinyRecordDefinition");

        public async Task<JObject> GetTableAsync(string tableName, CancellationToken cancellationToken)
        {
            return await _tables.GetOrAdd(tableName, name => new Lazy<Task<JObject>>(() => service.GetManifestTableAsync(manifest, name, cancellationToken)))
                .Value
                .ConfigureAwait(false);
        }

        public uint? FindMetricHash(params string[] terms)
        {
            var metrics = GetTable("DestinyMetricDefinition");
            foreach (var property in metrics.Properties())
            {
                var name = property.Value["displayProperties"]?["name"]?.Value<string>() ?? "";
                var description = property.Value["displayProperties"]?["description"]?.Value<string>() ?? "";
                if (terms.All(term =>
                        name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || description.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    return uint.Parse(property.Name);
                }
            }

            return null;
        }

        private JObject GetTable(string tableName)
        {
            return GetTableAsync(tableName, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
