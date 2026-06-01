using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
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
    IHttpClientFactory httpClientFactory) : ICrawlerService
{
    private const string BungieNetBaseUrl = "https://www.bungie.net";
    private const int GeneralStatsGroup = 1;
    private const int ProfileRecordsComponent = 900;
    private const int MetricsComponent = 1100;
    private const int PageSize = 250;
    private const int MaxConcurrentPgcrRequests = 20;

    private static readonly int[] AccountStatGroups = [GeneralStatsGroup];
    private static readonly int[] ProfileComponents = [ProfileRecordsComponent, MetricsComponent];
    private static readonly int[] ModeStatGroups = [GeneralStatsGroup];
    private static readonly TimeSpan ManifestCacheDuration = TimeSpan.FromDays(1);
    private static readonly TimeSpan PgcrCacheDuration = TimeSpan.FromDays(1);

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
            await ApplyActivityDerivedStatsAsync(report, playerMembershipId, activityHistory, pgcrs, manifest, cancellationToken).ConfigureAwait(false);
            ApplyWeaponStats(report, weaponHistoryTask.Result, manifest);
            ApplyTriumphSeals(report, profile, manifest);

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
                    LocalCacheExpiration = TimeSpan.FromDays(30)
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
                    LocalCacheExpiration = TimeSpan.FromDays(30)
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return JObject.Parse(json);
    }

    private static T EnsureSuccess<TResponse, T>(TResponse response, Func<TResponse, T> getPayload, string operation)
    {
        var errorCode = (int?)response?.GetType().GetProperty("ErrorCode")?.GetValue(response) ?? 0;
        if (errorCode != 1)
        {
            var message = (string?)response?.GetType().GetProperty("Message")?.GetValue(response);
            throw new InvalidOperationException($"{operation} failed with Bungie error code {errorCode}: {message}");
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

            return (characterId, mode, EnsureSuccess(response, item => item.Response, $"GetHistoricalStats:{characterId}:{mode}"));
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
            var response = await bungieClient.Destiny2_GetUniqueWeaponHistoryAsync(characterId, playerMembershipId, platformId, cancellationToken)
                .ConfigureAwait(false);

            return (characterId, Weapons: EnsureSuccess(response, item => item.Response, $"GetUniqueWeaponHistory:{characterId}").Weapons ?? []);
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
                var response = await bungieClient.Destiny2_GetActivityHistoryAsync(characterId, PageSize, playerMembershipId, platformId, null, page, cancellationToken)
                    .ConfigureAwait(false);

                var payload = EnsureSuccess(response, item => item.Response, $"GetActivityHistory:{characterId}:{page}");
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
                var pgcr = await cache.GetOrCreateAsync(
                        $"bungie:destiny2:pgcr:{activityId}",
                        async ct =>
                        {
                            var response = await bungieClient.Destiny2_GetPostGameCarnageReportAsync(activityId, ct)
                                .ConfigureAwait(false);

                            return EnsureSuccess(response, item => item.Response, $"GetPostGameCarnageReport:{activityId}");
                        },
                        new HybridCacheEntryOptions
                        {
                            Expiration = PgcrCacheDuration,
                            LocalCacheExpiration = TimeSpan.FromDays(7)
                        },
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

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
        var allTime = accountStats.MergedAllCharacters?.Merged?.AllTime;
        report.TotalPlaytime = TimeSpan.FromSeconds(GetStat(allTime, "secondsPlayed"));
        report.TotalKills = (long)GetStat(allTime, "kills");
        report.TotalDeaths = (long)GetStat(allTime, "deaths");
        report.Misadventures = (int)GetStat(allTime, "suicides");

        report.PlaytimeByClass = BuildPlaytimeByClass(historicalCharacters, characterClassById);
    }

    private static void ApplyProfileStats(DestinyReport report, DestinyProfileResponse profile, ManifestContext manifest)
    {
        var metrics = profile.Metrics?.Data?.Metrics;
        if (metrics is null)
        {
            return;
        }

        report.GoodBoyProtocol = GetMetricProgress(metrics, manifest.FindMetricHash("Good Boy Protocol", "Archie"));
        report.FishCaught = GetMetricProgress(metrics, manifest.FindMetricHash("fish", "caught"));
    }

    private static int GetMetricProgress(IDictionary<string, DestinyMetricComponent> metrics, int? metricHash)
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
        long playerMembershipId,
        IReadOnlyCollection<DestinyHistoricalStatsPeriodGroup> activityHistory,
        IReadOnlyDictionary<long, DestinyPostGameCarnageReportData> pgcrs,
        ManifestContext manifest,
        CancellationToken cancellationToken)
    {
        var activityDefinitions = await manifest.GetTableAsync("DestinyActivityDefinition", cancellationToken).ConfigureAwait(false);
        var destinationDefinitions = await manifest.GetTableAsync("DestinyDestinationDefinition", cancellationToken).ConfigureAwait(false);
        var modifierDefinitions = await manifest.GetTableAsync("DestinyActivityModifierDefinition", cancellationToken).ConfigureAwait(false);
        var inventoryDefinitions = await manifest.GetTableAsync("DestinyInventoryItemDefinition", cancellationToken).ConfigureAwait(false);

        ApplyPatrolTime(report, activityHistory.Where(activity => IncludesMode(activity, ActivityModes.Patrol)), activityDefinitions, destinationDefinitions);
        ApplyPgcrAggregates(report, playerMembershipId, pgcrs, activityDefinitions, modifierDefinitions, inventoryDefinitions);
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
            var destinationHash = activityDefinition?["destinationHash"]?.Value<int>() ?? 0;
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

    private static void ApplyPgcrAggregates(
        DestinyReport report,
        long playerMembershipId,
        IReadOnlyDictionary<long, DestinyPostGameCarnageReportData> pgcrs,
        JObject activityDefinitions,
        JObject modifierDefinitions,
        JObject inventoryDefinitions)
    {
        var allPgcrs = pgcrs.Values.OrderBy(pgcr => pgcr.Period).ToArray();
        var uniquePlayers = new Dictionary<long, ReportPlayer>();
        var teammateCombos = new Dictionary<string, (int Count, List<ReportPlayer> Players)>(StringComparer.Ordinal);
        var raidTeammateCombos = new Dictionary<string, (int Count, List<ReportPlayer> Players)>(StringComparer.Ordinal);
        var pvpOpponents = new Dictionary<long, RivalAggregate>();
        var gambitOpponents = new Dictionary<long, RivalAggregate>();
        var pveWeapons = new Dictionary<int, int>();
        var pvpWeapons = new Dictionary<int, int>();
        var gambitWeapons = new Dictionary<int, int>();

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
            var isFlawless = playerCompleted && playerDeaths <= 0 && (pgcr.ActivityWasStartedFromBeginning ?? true);
            var isSolo = pgcr.Entries?.Select(entry => entry.Player?.DestinyUserInfo?.MembershipId).Where(id => id > 0).Distinct().Count() == 1;
            var isContest = IsContest(pgcr, modifierDefinitions);
            var isDayOne = IsDayOne(pgcr, activityDefinitions);

            var completion = new ActivityCompletion
            {
                RaidName = activityName,
                CompletionDate = pgcr.Period.UtcDateTime,
                IsContest = isContest,
                IsDayOne = isDayOne,
                IsFlawless = isFlawless,
                IsSolo = isSolo,
                InstanceId = pgcr.ActivityDetails.InstanceId
            };

            if (playerCompleted && isRaid)
            {
                report.RaidCompletions.Add(completion);
                if (isDayOne)
                {
                    report.DayOneRaidCompletions.Add(completion);
                }

                if (isFlawless && report.FirstRaidFlawless is null)
                {
                    report.FirstRaidFlawless = completion;
                }
            }

            if (playerCompleted && isDungeon)
            {
                report.DungeonCompletions.Add(completion);
                if (isDayOne)
                {
                    report.DayOneDungeonCompletions.Add(completion);
                }

                if (isFlawless && report.FirstDungeonFlawless is null)
                {
                    report.FirstDungeonFlawless = completion;
                }

                if (isFlawless && isSolo && report.FirstDungeonSoloFlawless is null)
                {
                    report.FirstDungeonSoloFlawless = completion;
                }
            }

            var otherPlayers = (pgcr.Entries ?? [])
                .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId is > 0)
                .Where(entry => entry.Player.DestinyUserInfo.MembershipId != playerMembershipId)
                .Select(entry => ToReportPlayer(entry.Player, entry.Player.EmblemHash))
                .GroupBy(player => player.MembershipId)
                .Select(group => group.First())
                .ToArray();

            foreach (var otherPlayer in otherPlayers)
            {
                uniquePlayers.TryAdd(otherPlayer.MembershipId, otherPlayer);
            }

            TrackCombos(teammateCombos, otherPlayers, 3);
            TrackCombos(teammateCombos, otherPlayers, 6);
            if (isRaid)
            {
                TrackCombos(raidTeammateCombos, otherPlayers, 6);
            }

            if (isPvp)
            {
                TrackRivals(pvpOpponents, pgcr, playerEntry, otherPlayers, playerKills, playerDeaths);
                AddWeapons(pvpWeapons, playerEntry);
            }
            else if (isGambit)
            {
                TrackRivals(gambitOpponents, pgcr, playerEntry, otherPlayers, playerKills, playerDeaths);
                AddWeapons(gambitWeapons, playerEntry);
                report.GambitMotesBanked += (int)GetMoteStat(playerEntry, "bank", "deposit");
                report.GambitMotesLost += (int)GetMoteStat(playerEntry, "lost");
            }
            else
            {
                AddWeapons(pveWeapons, playerEntry);
            }
        }

        report.UniquePlayersPlayedWith = uniquePlayers.Count;
        report.MostPlayedWith[3] = TopCombo(teammateCombos, 3);
        report.MostPlayedWith[6] = TopCombo(teammateCombos, 6);
        report.MostPlayedWithRaid[6] = TopCombo(raidTeammateCombos, 6);

        ApplyRival(report, pvpOpponents, isGambit: false);
        ApplyRival(report, gambitOpponents, isGambit: true);

        report.PvETopWeapons = BuildWeaponReports(pveWeapons, inventoryDefinitions);
        report.CrucibleTopWeapons = BuildWeaponReports(pvpWeapons, inventoryDefinitions);
        report.GambitTopWeapons = BuildWeaponReports(gambitWeapons, inventoryDefinitions);
    }

    private static void ApplyWeaponStats(
        DestinyReport report,
        IReadOnlyDictionary<long, ICollection<DestinyHistoricalWeaponStats>> uniqueWeaponHistory,
        ManifestContext manifest)
    {
        var fallback = uniqueWeaponHistory.Values
            .SelectMany(weapons => weapons)
            .GroupBy(weapon => weapon.ReferenceId)
            .ToDictionary(group => group.Key, group => group.Sum(weapon => (int)GetStat(weapon.Values, "uniqueWeaponKills")));

        if (report.PvETopWeapons.Count == 0)
        {
            report.PvETopWeapons = BuildWeaponReports(fallback, manifest.InventoryItems);
        }
    }

    private static void ApplyTriumphSeals(DestinyReport report, DestinyProfileResponse profile, ManifestContext manifest)
    {
        var profileRecords = profile.ProfileRecords?.Data;
        if (profileRecords?.Records is null || profileRecords.RecordSealsRootNodeHash == 0)
        {
            return;
        }

        var root = GetDefinition(manifest.PresentationNodes, profileRecords.RecordSealsRootNodeHash);
        var sealHashes = root?["children"]?["presentationNodes"]?
            .Select(node => node["presentationNodeHash"]?.Value<int>() ?? 0)
            .Where(hash => hash > 0)
            .ToArray() ?? [];

        foreach (var sealHash in sealHashes)
        {
            var sealNode = GetDefinition(manifest.PresentationNodes, sealHash);
            if (sealNode is null)
            {
                continue;
            }

            var triumphs = sealNode["children"]?["records"]?
                .Select(record => record["recordHash"]?.Value<int>() ?? 0)
                .Where(hash => hash > 0)
                .Select(recordHash => BuildTriumph(recordHash, profileRecords.Records, manifest.Records))
                .OfType<DestinyTriumph>()
                .ToList() ?? [];

            report.TriumphSeals.Add(new DestinyTriumphSeal
            {
                Name = sealNode["displayProperties"]?["name"]?.Value<string>() ?? "",
                Description = sealNode["displayProperties"]?["description"]?.Value<string>() ?? "",
                IconUrl = BungieUrl(sealNode["displayProperties"]?["icon"]?.Value<string>()),
                Triumphs = triumphs
            });
        }
    }

    private static DestinyTriumph? BuildTriumph(
        int recordHash,
        IDictionary<string, DestinyRecordComponent> profileRecords,
        JObject recordDefinitions)
    {
        var definition = GetDefinition(recordDefinitions, recordHash);
        if (definition is null)
        {
            return null;
        }

        profileRecords.TryGetValue(recordHash.ToString(), out var component);
        var isCompleted = component?.CompletedCount > 0 || (component is not null && (component.State & 4) == 0);

        return new DestinyTriumph
        {
            Name = definition["displayProperties"]?["name"]?.Value<string>() ?? "",
            Description = definition["displayProperties"]?["description"]?.Value<string>() ?? "",
            IconUrl = BungieUrl(definition["displayProperties"]?["icon"]?.Value<string>()),
            Points = definition["completionInfo"]?["ScoreValue"]?.Value<int>() ?? definition["completionInfo"]?["scoreValue"]?.Value<int>() ?? 0,
            IsCompleted = isCompleted
        };
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

    private static string ActivityName(JObject definitions, int referenceId, int directorActivityHash)
    {
        var definition = GetDefinition(definitions, referenceId) ?? GetDefinition(definitions, directorActivityHash);
        return definition?["displayProperties"]?["name"]?.Value<string>() ?? referenceId.ToString();
    }

    private static JObject? GetDefinition(JObject table, int hash)
    {
        return table[hash.ToString()] as JObject;
    }

    private static bool IsContest(DestinyPostGameCarnageReportData pgcr, JObject modifierDefinitions)
    {
        return pgcr.SelectedSkullHashes?.Any(hash =>
        {
            var modifier = GetDefinition(modifierDefinitions, hash);
            var name = modifier?["displayProperties"]?["name"]?.Value<string>() ?? "";
            return name.Contains("Contest", StringComparison.OrdinalIgnoreCase);
        }) == true;
    }

    private static bool IsDayOne(DestinyPostGameCarnageReportData pgcr, JObject activityDefinitions)
    {
        var definition = GetDefinition(activityDefinitions, pgcr.ActivityDetails.ReferenceId)
            ?? GetDefinition(activityDefinitions, pgcr.ActivityDetails.DirectorActivityHash);
        var releaseTime = definition?["releaseTime"]?.Value<long>() ?? 0;
        if (releaseTime <= 0)
        {
            return false;
        }

        var release = DateTimeOffset.FromUnixTimeSeconds(releaseTime);
        return pgcr.Period >= release && pgcr.Period <= release.AddDays(1);
    }

    private static ReportPlayer ToReportPlayer(BungiePlayer player, int emblemHash)
    {
        var user = player.DestinyUserInfo;
        return new ReportPlayer
        {
            MembershipId = user?.MembershipId ?? 0,
            MembershipType = user?.MembershipType ?? 0,
            DisplayName = DisplayName(user),
            EmblemUrl = emblemHash > 0 ? emblemHash.ToString() : ""
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

    private static void TrackCombos(
        IDictionary<string, (int Count, List<ReportPlayer> Players)> combos,
        IReadOnlyCollection<ReportPlayer> players,
        int size)
    {
        if (players.Count < size)
        {
            return;
        }

        var topPlayers = players.OrderBy(player => player.MembershipId).Take(size).ToList();
        var key = string.Join('|', topPlayers.Select(player => player.MembershipId));
        combos[key] = combos.TryGetValue(key, out var existing)
            ? (existing.Count + 1, existing.Players)
            : (1, topPlayers);
    }

    private static List<ReportPlayer> TopCombo(IDictionary<string, (int Count, List<ReportPlayer> Players)> combos, int size)
    {
        return combos.Values
            .Where(item => item.Players.Count == size)
            .OrderByDescending(item => item.Count)
            .Select(item => item.Players)
            .FirstOrDefault() ?? [];
    }

    private static void TrackRivals(
        IDictionary<long, RivalAggregate> rivals,
        DestinyPostGameCarnageReportData pgcr,
        DestinyPostGameCarnageReportEntry playerEntry,
        IEnumerable<ReportPlayer> otherPlayers,
        double playerKills,
        double playerDeaths)
    {
        var playerTeam = playerEntry.Values?.TryGetValue("team", out var teamValue) == true ? teamValue.Basic?.Value : null;
        foreach (var opponent in otherPlayers)
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

    private static List<WeaponReport> BuildWeaponReports(IDictionary<int, int> weaponKills, JObject? manifestInventoryDefinitions = null)
    {
        return weaponKills
            .Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value)
            .Take(10)
            .Select(item =>
            {
                var definition = manifestInventoryDefinitions is not null ? GetDefinition(manifestInventoryDefinitions, item.Key) : null;
                return new WeaponReport
                {
                    Name = definition?["displayProperties"]?["name"]?.Value<string>() ?? item.Key.ToString(),
                    IconUrl = BungieUrl(definition?["displayProperties"]?["icon"]?.Value<string>()),
                    TotalKills = item.Value
                };
            })
            .ToList();
    }

    private static string BungieUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{BungieNetBaseUrl}{path}";
    }

    private sealed record RivalAggregate(ReportPlayer Player)
    {
        public int Matches { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public double Kills { get; set; }
        public double Deaths { get; set; }
    }

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

        public JObject PresentationNodes => GetTable("DestinyPresentationNodeDefinition");
        public JObject Records => GetTable("DestinyRecordDefinition");
        public JObject InventoryItems => GetTable("DestinyInventoryItemDefinition");

        public async Task<JObject> GetTableAsync(string tableName, CancellationToken cancellationToken)
        {
            return await _tables.GetOrAdd(tableName, name => new Lazy<Task<JObject>>(() => service.GetManifestTableAsync(manifest, name, cancellationToken)))
                .Value
                .ConfigureAwait(false);
        }

        public int? FindMetricHash(params string[] terms)
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
                    return int.Parse(property.Name);
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
