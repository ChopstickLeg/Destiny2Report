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

public partial class CrawlerService
{
    private async Task ApplyActivityDerivedStatsAsync(
        DestinyReport report,
        CrawlAccumulator accumulator,
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<DestinyHistoricalStatsPeriodGroup> activityHistory,
        IDictionary<long, string> characterClassById,
        ManifestContext manifest,
        bool resetWeaponAggregates,
        CancellationToken cancellationToken)
    {
        var activityDefinitions = await manifest.GetTableAsync("DestinyActivityDefinition", cancellationToken).ConfigureAwait(false);
        var activityModeDefinitions = await manifest.GetTableAsync("DestinyActivityModeDefinition", cancellationToken).ConfigureAwait(false);
        var destinationDefinitions = await manifest.GetTableAsync("DestinyDestinationDefinition", cancellationToken).ConfigureAwait(false);

        ApplyPatrolTime(accumulator, activityHistory.Where(activity => IncludesMode(activity, ActivityModes.Patrol)), activityDefinitions, destinationDefinitions);
        await ApplyPgcrAggregatesAsync(
                report,
                accumulator,
                platformId,
                playerMembershipId,
                activityHistory,
                characterClassById,
                activityDefinitions,
                activityModeDefinitions,
                manifest,
                resetWeaponAggregates,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ApplyPatrolTime(
        CrawlAccumulator accumulator,
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

            accumulator.PatrolSecondsByPlanet[destinationName] = accumulator.PatrolSecondsByPlanet.GetValueOrDefault(destinationName) + (long)seconds;
        }
    }

    private async Task ApplyPgcrAggregatesAsync(
        DestinyReport report,
        CrawlAccumulator accumulator,
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<DestinyHistoricalStatsPeriodGroup> activityHistory,
        IDictionary<long, string> characterClassById,
        JObject activityDefinitions,
        JObject activityModeDefinitions,
        ManifestContext manifest,
        bool resetWeaponAggregates,
        CancellationToken cancellationToken)
    {
        var playerEncounterCounts = accumulator.EncounterCounts.Values
            .ToDictionary(item => (item.MembershipType, item.MembershipId), item => item.Count);
        var pveWeaponDeltas = new Dictionary<long, WeaponKillDelta>();
        var pvpWeaponDeltas = new Dictionary<long, WeaponKillDelta>();
        var gambitWeaponDeltas = new Dictionary<long, WeaponKillDelta>();
        var raidCompletions = ToCompletionAggregates(accumulator.RaidCompletions);
        var dungeonCompletions = ToCompletionAggregates(accumulator.DungeonCompletions);
        var playersSherpaed = new Dictionary<string, int>(accumulator.PlayersSherpaed, StringComparer.OrdinalIgnoreCase);
        var pendingSherpaChecks = new List<SherpaCheck>();
        var completedRaidHistoryByPlayer = new ConcurrentDictionary<(int MembershipType, long MembershipId), Lazy<Task<IReadOnlyCollection<CompletedRaidActivity>?>>>();
        var membershipTypeByPlayer = new ConcurrentDictionary<long, Lazy<Task<int?>>>();
        var currentCrawlEncounteredPlayers = new HashSet<(int MembershipType, long MembershipId)>();
        var completedRaidActivities = ToCompletedRaidActivities(activityHistory, activityDefinitions).ToArray();

        foreach (var activityBatch in activityHistory.Chunk(MaxBufferedPgcrs))
        {
            var pgcrBatch = await FetchPgcrBatchAsync(activityBatch, cancellationToken).ConfigureAwait(false);
            foreach (var (activity, pgcr) in pgcrBatch)
            {
                var instanceId = activity.ActivityDetails.InstanceId;
                TryFillCharacterClassFromPgcr(characterClassById, pgcr, playerMembershipId);

                var playerEntries = FindPlayerEntries(pgcr, platformId, playerMembershipId);
                if (playerEntries.Length == 0)
                {
                    continue;
                }

                var completionReason = GetPgcrCompletionReason(pgcr);
                var playerCompleted = playerEntries.Any(entry => IsNormallyCompleted(entry.Values, completionReason));
                var playerKills = SumStats(playerEntries, "kills");
                AddPgcrPlayerStats(accumulator, playerEntries);
                if (playerKills <= 0)
                {
                    accumulator.ZeroKillActivities++;
                }

                var activityName = ActivityName(activityDefinitions, pgcr.ActivityDetails.ReferenceId, pgcr.ActivityDetails.DirectorActivityHash);
                var isRaid = IncludesMode(pgcr, ActivityModes.Raid);
                var isDungeon = IncludesMode(pgcr, ActivityModes.Dungeon);
                var isPvp = IncludesMode(pgcr, ActivityModes.AllPvP);
                var isGambit = IncludesMode(pgcr, ActivityModes.Gambit) || IncludesMode(pgcr, ActivityModes.GambitPrime);
                var hasGambitMoteStats = IncludesMode(pgcr, ActivityModes.AllPvECompetitive);
                var isPrivateCrucible = isPvp && HasActivityTypeHash(pgcr, activityDefinitions, PrivateCrucibleActivityTypeHashes);
                var isPrivateGambit = isGambit && HasActivityTypeHash(pgcr, activityDefinitions, PrivateGambitActivityTypeHashes);
                var activityPlayerEntries = GetActivityPlayerEntries(pgcr);
                var activityWasStartedFromBeginning = GetActivityWasStartedFromBeginning(pgcr, activityPlayerEntries);
                var wasStartedFromBeginning = activityWasStartedFromBeginning == true;
                var isFlawless = (isRaid || isDungeon) && playerCompleted && wasStartedFromBeginning && activityPlayerEntries.Length > 0 && activityPlayerEntries.All(entry => GetStat(entry.Values, "deaths") <= 0);
                var isSolo = (isRaid || isDungeon) && playerCompleted && wasStartedFromBeginning && IsSoloActivity(activityPlayerEntries);
                var activityCompletedAt = GetActivityCompletedAt(pgcr, playerEntries);
                var isContest = IsContest(pgcr, activityCompletedAt, isRaid, isDungeon);
                var isSoloFlawless = isSolo && isFlawless;
                var playerActivitySeconds = SumPlayerActivitySeconds(playerEntries);

                accumulator.TotalActivitySeconds += (long)playerActivitySeconds;
                AddActivityModePlaytime(accumulator, pgcr, (long)playerActivitySeconds);

                if (playerCompleted && isRaid)
                {
                    AddCompletion(raidCompletions, activityName, isContest, isFlawless, isSolo, isSoloFlawless);
                    var normalizedRaidName = ContestModeLookup.NormalizeActivityName(activityName);
                    AddFirstRaidCompletion(accumulator, normalizedRaidName, activityCompletedAt, instanceId);
                    if (HasPriorCompletedRaid(accumulator, normalizedRaidName, activityCompletedAt, instanceId)
                        || HasPriorCompletedRaidInHistory(completedRaidActivities, normalizedRaidName, activityCompletedAt, instanceId))
                    {
                        pendingSherpaChecks.Add(new SherpaCheck(
                            instanceId,
                            normalizedRaidName,
                            activityCompletedAt,
                            GetCompletedFireteamMembers(pgcr, playerMembershipId).ToArray()));
                    }
                }

                if (playerCompleted && isDungeon)
                {
                    AddCompletion(dungeonCompletions, activityName, isContest, isFlawless, isSolo, isSoloFlawless);
                }

                var otherPlayers = GetDistinctOtherPlayers(pgcr, playerMembershipId);

                foreach (var otherPlayer in otherPlayers)
                {
                    var key = (otherPlayer.MembershipType, otherPlayer.MembershipId);
                    playerEncounterCounts[key] = playerEncounterCounts.GetValueOrDefault(key) + 1;
                    if (IsCountablePlayerEncounter(key.MembershipType, key.MembershipId, 1))
                    {
                        currentCrawlEncounteredPlayers.Add(key);
                    }
                }

                if (hasGambitMoteStats)
                {
                    foreach (var playerEntry in playerEntries)
                    {
                        AddGambitMoteStats(accumulator, pgcr, playerEntry);
                    }
                }

                if (isPvp)
                {
                    AddCrucibleKills(accumulator, pgcr, (long)playerKills);
                    if (!isPrivateCrucible)
                    {
                        AddWeapons(pvpWeaponDeltas, playerEntries);
                    }
                }
                else if (isGambit)
                {
                    if (!isPrivateGambit)
                    {
                        AddWeapons(gambitWeaponDeltas, playerEntries);
                    }
                }
                else
                {
                    AddWeapons(pveWeaponDeltas, playerEntries);
                }
            }
        }

        await ApplySherpaChecksAsync(cancellationToken).ConfigureAwait(false);

        var persistedPlayerEncounterCounts = playerEncounterCounts
            .Where(item => IsPersistablePlayerEncounter(item.Key.MembershipType, item.Key.MembershipId, item.Value))
            .ToDictionary(item => item.Key, item => item.Value);

        SaveCompletionAggregates(accumulator.RaidCompletions, raidCompletions);
        SaveCompletionAggregates(accumulator.DungeonCompletions, dungeonCompletions);
        accumulator.PlayersSherpaed = new Dictionary<string, int>(playersSherpaed, StringComparer.OrdinalIgnoreCase);
        accumulator.EncounterCounts = persistedPlayerEncounterCounts.ToDictionary(
            item => PlayerKey(item.Key.MembershipType, item.Key.MembershipId),
            item => new EncounterAccumulator
            {
                MembershipType = item.Key.MembershipType,
                MembershipId = item.Key.MembershipId,
                Count = item.Value
            });
        accumulator.UniquePlayersPlayedWith += currentCrawlEncounteredPlayers.Count;

        report.PatrolTimeByPlanet = accumulator.PatrolSecondsByPlanet.ToDictionary(
            item => item.Key,
            item => TimeSpan.FromSeconds(item.Value));
        report.TotalKills = accumulator.TotalKills;
        report.TotalDeaths = accumulator.TotalDeaths;
        report.PlaytimeByActivityMode = ToActivityModePlaytimeReports(accumulator.PlaytimeByActivityMode, activityModeDefinitions);
        report.ZeroKillActivities = accumulator.ZeroKillActivities;
        report.CrucibleKills = ToCrucibleKillsReport(accumulator);
        report.GambitMotes = ToGambitMotesReport(accumulator);
        report.RaidCompletions = ToCompletionSummaries(raidCompletions);
        report.DungeonCompletions = ToCompletionSummaries(dungeonCompletions);
        report.PlayersSherpaed = ToSherpaReports(playersSherpaed);
        await ApplyPlayerEncounterCountsAsync(
                report,
                platformId,
                playerMembershipId,
                persistedPlayerEncounterCounts,
                accumulator.UniquePlayersPlayedWith,
                cancellationToken)
            .ConfigureAwait(false);

        await ApplyWeaponAggregateDeltasAsync(
                report,
                platformId,
                playerMembershipId,
                pveWeaponDeltas,
                pvpWeaponDeltas,
                gambitWeaponDeltas,
                manifest.Manifest,
                resetWeaponAggregates,
                cancellationToken)
            .ConfigureAwait(false);

        report.TotalActivityTime = TimeSpan.FromSeconds(accumulator.TotalActivitySeconds);

        async Task<IReadOnlyCollection<CompletedRaidActivity>?> GetCompletedRaidHistoryAsync(int membershipType, long membershipId)
        {
            var accumulatorHistory = await FetchCompletedRaidHistoryFromAccumulatorAsync(membershipType, membershipId, cancellationToken)
                .ConfigureAwait(false);
            if (accumulatorHistory is not null)
            {
                return accumulatorHistory;
            }

            var lazyHistory = completedRaidHistoryByPlayer.GetOrAdd(
                (membershipType, membershipId),
                key => new Lazy<Task<IReadOnlyCollection<CompletedRaidActivity>?>>(
                    () => FetchCompletedRaidHistoryAsync(
                        key.MembershipType,
                        key.MembershipId,
                        activityDefinitions,
                        cancellationToken)));

            return await lazyHistory.Value.ConfigureAwait(false);
        }

        async Task<IReadOnlyCollection<CompletedRaidActivity>?> FetchCompletedRaidHistoryFromAccumulatorAsync(
            int membershipType,
            long membershipId,
            CancellationToken cancellationToken)
        {
            var accumulators = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators");
            var filter = Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, membershipType)
                & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, membershipId)
                & Builders<CrawlAccumulator>.Filter.Eq(item => item.NeedsFullRecrawl, false);
            var playerAccumulator = await accumulators.Find(filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (playerAccumulator is null || playerAccumulator.FirstRaidCompletions.Count == 0)
            {
                return null;
            }

            return ToCompletedRaidActivities(playerAccumulator).ToArray();
        }

        async Task ApplySherpaChecksAsync(CancellationToken cancellationToken)
        {
            var unresolvedCandidateChecks = pendingSherpaChecks
                .SelectMany(check => check.Candidates
                    .Select(player => new SherpaCandidateCheck(
                        check.InstanceId,
                        check.NormalizedRaidName,
                        check.CompletedAt,
                        player.MembershipType,
                        player.MembershipId)))
                .ToArray();

            var resolvedCandidateChecks = await Task.WhenAll(unresolvedCandidateChecks.Select(ResolveCandidateCheckAsync))
                .ConfigureAwait(false);
            var candidateChecks = resolvedCandidateChecks.OfType<SherpaCandidateCheck>().ToArray();

            if (candidateChecks.Length == 0)
            {
                return;
            }

            var candidatePlayers = candidateChecks
                .Select(check => (check.MembershipType, check.MembershipId))
                .Distinct()
                .ToArray();

            using var throttler = new SemaphoreSlim(MaxConcurrentSherpaHistoryRequests);
            var historyTasks = candidatePlayers.Select(async player =>
            {
                await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var history = await GetCompletedRaidHistoryAsync(player.MembershipType, player.MembershipId).ConfigureAwait(false);
                    return (player.MembershipType, player.MembershipId, History: history);
                }
                finally
                {
                    throttler.Release();
                }
            });

            var histories = await Task.WhenAll(historyTasks).ConfigureAwait(false);
            var historyByPlayer = histories.ToDictionary(
                item => (item.MembershipType, item.MembershipId),
                item => item.History);

            foreach (var check in candidateChecks)
            {
                if (!historyByPlayer.TryGetValue((check.MembershipType, check.MembershipId), out var history)
                    || history is null
                    || history.Any(activity => IsPriorCompletedRaid(
                        activity,
                        check.NormalizedRaidName,
                        check.CompletedAt,
                        check.InstanceId)))
                {
                    continue;
                }

                playersSherpaed[check.NormalizedRaidName] = playersSherpaed.GetValueOrDefault(check.NormalizedRaidName) + 1;
            }
        }

        async Task<SherpaCandidateCheck?> ResolveCandidateCheckAsync(SherpaCandidateCheck check)
        {
            if (check.MembershipType > 0)
            {
                return check;
            }

            var resolvedMembershipType = await ResolveMembershipTypeAsync(check.MembershipId).ConfigureAwait(false);
            if (resolvedMembershipType is not > 0)
            {
                logger.LogDebug(
                    "Skipping sherpa candidate {MembershipId} because their membership type could not be resolved.",
                    check.MembershipId);
                return null;
            }

            return check with { MembershipType = resolvedMembershipType.Value };
        }

        async Task<int?> ResolveMembershipTypeAsync(long membershipId)
        {
            var lazyMembershipType = membershipTypeByPlayer.GetOrAdd(
                membershipId,
                key => new Lazy<Task<int?>>(() => FetchMembershipTypeAsync(key, cancellationToken)));

            return await lazyMembershipType.Value.ConfigureAwait(false);
        }

        async Task<int?> FetchMembershipTypeAsync(long membershipId, CancellationToken cancellationToken)
        {
            try
            {
                var response = await bungieClient.Destiny2_GetLinkedProfilesAsync(
                        getAllMemberships: true,
                        membershipId: membershipId,
                        membershipType: AllMembershipTypes,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var payload = EnsureSuccess(response, item => item.Response, $"GetLinkedProfiles:{membershipId}");
                return SelectLinkedProfileMembershipType(payload.Profiles, membershipId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not resolve membership type for sherpa candidate {MembershipId}.", membershipId);
                return null;
            }
        }

        IEnumerable<SherpaCandidate> GetCompletedFireteamMembers(
            DestinyPostGameCarnageReportData raidPgcr,
            long ownerMembershipId)
        {
            return GetActivityPlayerEntries(raidPgcr)
                .Where(entry => IsNormallyCompleted(entry.Values, GetPgcrCompletionReason(raidPgcr)))
                .Select(entry => entry.Player?.DestinyUserInfo)
                .Where(player => player?.MembershipId > 0)
                .Where(player => player!.MembershipId != ownerMembershipId)
                .GroupBy(player => player!.MembershipId)
                .Select(group => new SherpaCandidate(
                    group.Select(player => player!.MembershipType).FirstOrDefault(membershipType => membershipType > 0),
                    group.Key));
        }
    }

    private static DestinyPostGameCarnageReportEntry[] FindPlayerEntries(
        DestinyPostGameCarnageReportData pgcr,
        int membershipType,
        long membershipId)
    {
        return (pgcr.Entries ?? [])
            .Where(entry =>
            {
                var player = entry.Player?.DestinyUserInfo;
                return player?.MembershipId == membershipId
                    && (membershipType <= 0 || player.MembershipType <= 0 || player.MembershipType == membershipType);
            })
            .ToArray();
    }

    private static void AddPgcrPlayerStats(
        CrawlAccumulator accumulator,
        IReadOnlyCollection<DestinyPostGameCarnageReportEntry> playerEntries)
    {
        accumulator.TotalKills += (long)SumStats(playerEntries, "kills");
        accumulator.TotalDeaths += (long)SumStats(playerEntries, "deaths");
    }

    private static void AddCrucibleKills(
        CrawlAccumulator accumulator,
        DestinyPostGameCarnageReportData pgcr,
        long kills)
    {
        var modeKey = pgcr.ActivityDetails.Mode.ToString();
        accumulator.CrucibleKills += kills;
        accumulator.CrucibleKillsByMode[modeKey] = accumulator.CrucibleKillsByMode.GetValueOrDefault(modeKey) + kills;
    }

    private static double SumStats(
        IEnumerable<DestinyPostGameCarnageReportEntry> playerEntries,
        string statId)
    {
        return playerEntries.Sum(entry => GetStat(entry.Values, statId));
    }

    private static double SumPlayerActivitySeconds(IEnumerable<DestinyPostGameCarnageReportEntry> playerEntries)
    {
        return playerEntries.Sum(entry =>
        {
            var seconds = GetStat(entry.Values, "timePlayedSeconds");
            return seconds > 0
                ? seconds
                : GetStat(entry.Values, "activityDurationSeconds");
        });
    }

    private static bool IncludesMode(DestinyPostGameCarnageReportData pgcr, int mode)
    {
        return pgcr.ActivityDetails.Mode == mode || (pgcr.ActivityDetails.Modes?.Contains(mode) ?? false);
    }

    private static bool IncludesMode(DestinyHistoricalStatsPeriodGroup activity, int mode)
    {
        return activity.ActivityDetails.Mode == mode || (activity.ActivityDetails.Modes?.Contains(mode) ?? false);
    }

    private static void AddActivityModePlaytime(
        CrawlAccumulator accumulator,
        DestinyPostGameCarnageReportData pgcr,
        long seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        foreach (var broadMode in EnumerateActivityPlaytimeBroadModes(pgcr))
        {
            var broadModeKey = broadMode.ToString();
            if (!accumulator.PlaytimeByActivityMode.TryGetValue(broadModeKey, out var playtime))
            {
                playtime = new ActivityModePlaytimeAccumulator();
                accumulator.PlaytimeByActivityMode[broadModeKey] = playtime;
            }

            playtime.TotalSeconds += seconds;
            var mostSpecificModeKey = pgcr.ActivityDetails.Mode.ToString();
            playtime.MostSpecificModeSeconds[mostSpecificModeKey] =
                playtime.MostSpecificModeSeconds.GetValueOrDefault(mostSpecificModeKey) + seconds;
        }
    }

    private static IEnumerable<int> EnumerateActivityPlaytimeBroadModes(DestinyPostGameCarnageReportData pgcr)
    {
        foreach (var mode in ActivityPlaytimeBroadModes)
        {
            if (IncludesMode(pgcr, mode))
            {
                yield return mode;
            }
        }
    }

    private static List<ActivityModePlaytimeReport> ToActivityModePlaytimeReports(
        IReadOnlyDictionary<string, ActivityModePlaytimeAccumulator> playtimeByActivityMode,
        JObject activityModeDefinitions)
    {
        return ActivityPlaytimeBroadModes
            .Select(mode => (Mode: mode, Playtime: GetActivityModePlaytime(playtimeByActivityMode, mode)))
            .Where(item => item.Playtime is not null)
            .Select(item => new ActivityModePlaytimeReport
            {
                Mode = item.Mode,
                ModeName = GetActivityModeName(activityModeDefinitions, item.Mode),
                TotalPlaytime = TimeSpan.FromSeconds(item.Playtime!.TotalSeconds),
                MostSpecificModes = item.Playtime.MostSpecificModeSeconds
                    .Select(modePlaytime => ToActivityModePlaytimeBreakdown(modePlaytime, activityModeDefinitions))
                    .OrderByDescending(modePlaytime => modePlaytime.Playtime)
                    .ThenBy(modePlaytime => modePlaytime.Mode)
                    .ToList()
            })
            .ToList();
    }

    private static ActivityModePlaytimeBreakdown ToActivityModePlaytimeBreakdown(
        KeyValuePair<string, long> modePlaytime,
        JObject activityModeDefinitions)
    {
        var mode = int.TryParse(modePlaytime.Key, out var parsedMode) ? parsedMode : 0;
        return new ActivityModePlaytimeBreakdown
        {
            Mode = mode,
            ModeName = GetActivityModeName(activityModeDefinitions, mode),
            Playtime = TimeSpan.FromSeconds(modePlaytime.Value)
        };
    }

    private static ActivityModePlaytimeAccumulator? GetActivityModePlaytime(
        IReadOnlyDictionary<string, ActivityModePlaytimeAccumulator> playtimeByActivityMode,
        int mode)
    {
        return playtimeByActivityMode.TryGetValue(mode.ToString(), out var playtime)
            ? playtime
            : null;
    }

    private static string GetActivityModeName(JObject activityModeDefinitions, int mode)
    {
        foreach (var definition in activityModeDefinitions.Properties())
        {
            if (definition.Value["modeType"]?.Value<int>() != mode)
            {
                continue;
            }

            var name = definition.Value["displayProperties"]?["name"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return ActivityModeTypeNames.GetValueOrDefault(mode) ?? $"Mode {mode}";
    }

    private static GambitMotesReport ToGambitMotesReport(CrawlAccumulator accumulator)
    {
        return new GambitMotesReport
        {
            MotesBanked = ToGambitMoteStatReport(
                accumulator.GambitMotesBanked + accumulator.GambitBankOverage,
                SumModeTotals(accumulator.GambitMotesBankedByMode, accumulator.GambitBankOverageByMode)),
            MotesLost = ToGambitMoteStatReport(accumulator.GambitMotesLost, accumulator.GambitMotesLostByMode),
            MotesDenied = ToGambitMoteStatReport(accumulator.GambitMotesDenied, accumulator.GambitMotesDeniedByMode)
        };
    }

    private static CrucibleKillsReport ToCrucibleKillsReport(CrawlAccumulator accumulator)
    {
        return new CrucibleKillsReport
        {
            Total = accumulator.CrucibleKills,
            ByMode = accumulator.CrucibleKillsByMode
                .GroupBy(item => GetActivityModeName(item.Key), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static GambitMoteStatReport ToGambitMoteStatReport(
        int total,
        IReadOnlyDictionary<string, int> totalsByMode)
    {
        return new GambitMoteStatReport
        {
            Total = total,
            ByMode = totalsByMode
                .GroupBy(item => GetActivityModeName(item.Key), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static Dictionary<string, int> SumModeTotals(
        IReadOnlyDictionary<string, int> first,
        IReadOnlyDictionary<string, int> second)
    {
        var result = new Dictionary<string, int>(first, StringComparer.OrdinalIgnoreCase);
        foreach (var item in second)
        {
            result[item.Key] = result.GetValueOrDefault(item.Key) + item.Value;
        }

        return result;
    }

    private static string GetActivityModeName(string modeKey)
    {
        return int.TryParse(modeKey, out var mode)
            ? ActivityModeTypeNames.GetValueOrDefault(mode) ?? $"Mode {mode}"
            : modeKey;
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
        IEnumerable<DestinyPostGameCarnageReportEntry> playerEntries)
    {
        var durationSeconds = playerEntries.Max(entry => GetStat(entry.Values, "activityDurationSeconds"));
        return durationSeconds > 0
            ? pgcr.Period.AddSeconds(durationSeconds)
            : pgcr.Period;
    }

    private static DateTimeOffset GetActivityCompletedAt(DestinyHistoricalStatsPeriodGroup activity)
    {
        var durationSeconds = GetStat(activity.Values, "activityDurationSeconds");
        return durationSeconds > 0
            ? activity.Period.AddSeconds(durationSeconds)
            : activity.Period;
    }

    private static bool IsNormallyCompleted(IDictionary<string, DestinyHistoricalStatsValue>? values)
    {
        return GetStat(values, "completed") > 0
            && GetStat(values, "completionReason") == 0;
    }

    private static bool IsNormallyCompleted(
        IDictionary<string, DestinyHistoricalStatsValue>? values,
        double completionReason)
    {
        return GetStat(values, "completed") > 0
            && completionReason == 0;
    }

    private static double GetPgcrCompletionReason(DestinyPostGameCarnageReportData pgcr)
    {
        return GetStat(pgcr.Entries?.FirstOrDefault()?.Values, "completionReason");
    }

    private static int? SelectLinkedProfileMembershipType(
        IEnumerable<DestinyProfileUserInfoCard>? profiles,
        long membershipId)
    {
        var matchingProfiles = (profiles ?? [])
            .Where(profile => profile.MembershipId == membershipId && profile.MembershipType > 0)
            .OrderByDescending(profile => profile.IsCrossSavePrimary)
            .ThenBy(profile => profile.IsOverridden)
            .ThenByDescending(profile => profile.DateLastPlayed)
            .ToArray();

        return matchingProfiles.Length > 0
            ? matchingProfiles[0].MembershipType
            : null;
    }

    private static DestinyPostGameCarnageReportEntry[] GetActivityPlayerEntries(DestinyPostGameCarnageReportData pgcr)
    {
        return (pgcr.Entries ?? [])
            .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId > 0)
            .ToArray();
    }

    private static bool IsSoloActivity(IReadOnlyCollection<DestinyPostGameCarnageReportEntry> activityPlayerEntries)
    {
        long? membershipId = null;
        foreach (var entry in activityPlayerEntries)
        {
            var entryMembershipId = entry.Player?.DestinyUserInfo?.MembershipId;
            if (entryMembershipId is not > 0)
            {
                continue;
            }

            if (membershipId is null)
            {
                membershipId = entryMembershipId;
                continue;
            }

            if (membershipId.Value != entryMembershipId.Value)
            {
                return false;
            }
        }

        return membershipId is not null;
    }

    private static IEnumerable<DestinyUserInfo> GetDistinctOtherPlayers(
        DestinyPostGameCarnageReportData pgcr,
        long playerMembershipId)
    {
        var seen = new HashSet<(int MembershipType, long MembershipId)>();
        foreach (var entry in pgcr.Entries ?? [])
        {
            var player = entry.Player?.DestinyUserInfo;
            if (player?.MembershipId is not > 0 || player.MembershipId == playerMembershipId)
            {
                continue;
            }

            if (seen.Add((player.MembershipType, player.MembershipId)))
            {
                yield return player;
            }
        }
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

    private static void AddFirstRaidCompletion(
        CrawlAccumulator accumulator,
        string normalizedRaidName,
        DateTimeOffset completedAt,
        long instanceId)
    {
        if (!accumulator.FirstRaidCompletions.TryGetValue(normalizedRaidName, out var existing)
            || completedAt < existing.CompletedAt)
        {
            accumulator.FirstRaidCompletions[normalizedRaidName] = new RaidFirstCompletion
            {
                CompletedAt = completedAt,
                InstanceId = instanceId
            };
        }
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

    private static bool HasPriorCompletedRaidInHistory(
        IEnumerable<CompletedRaidActivity> activities,
        string normalizedRaidName,
        DateTimeOffset activityCompletedAt,
        long activityInstanceId)
    {
        return activities.Any(activity => IsPriorCompletedRaid(activity, normalizedRaidName, activityCompletedAt, activityInstanceId));
    }

    private static bool HasPriorCompletedRaid(
        IEnumerable<DestinyHistoricalStatsPeriodGroup> activities,
        string normalizedRaidName,
        DateTimeOffset activityCompletedAt,
        long activityInstanceId,
        JObject activityDefinitions)
    {
        return HasPriorCompletedRaidInHistory(
            ToCompletedRaidActivities(activities, activityDefinitions),
            normalizedRaidName,
            activityCompletedAt,
            activityInstanceId);
    }

    private static bool HasPriorCompletedRaid(
        CrawlAccumulator accumulator,
        string normalizedRaidName,
        DateTimeOffset activityCompletedAt,
        long activityInstanceId)
    {
        return ToCompletedRaidActivities(accumulator)
            .Any(activity => IsPriorCompletedRaid(activity, normalizedRaidName, activityCompletedAt, activityInstanceId));
    }

    private static IEnumerable<CompletedRaidActivity> ToCompletedRaidActivities(CrawlAccumulator accumulator)
    {
        return accumulator.FirstRaidCompletions.Select(item => new CompletedRaidActivity(
            item.Key,
            item.Value.CompletedAt,
            item.Value.InstanceId));
    }

    private static IEnumerable<CompletedRaidActivity> ToCompletedRaidActivities(
        IEnumerable<DestinyHistoricalStatsPeriodGroup> activities,
        JObject activityDefinitions)
    {
        foreach (var activity in activities)
        {
            if (!IsNormallyCompleted(activity.Values) || !IncludesMode(activity, ActivityModes.Raid))
            {
                continue;
            }

            var raidName = ActivityName(
                activityDefinitions,
                activity.ActivityDetails.ReferenceId,
                activity.ActivityDetails.DirectorActivityHash);

            yield return new CompletedRaidActivity(
                ContestModeLookup.NormalizeActivityName(raidName),
                GetActivityCompletedAt(activity),
                activity.ActivityDetails.InstanceId);
        }
    }

    private static bool IsPriorCompletedRaid(
        CompletedRaidActivity completedRaid,
        string normalizedRaidName,
        DateTimeOffset activityCompletedAt,
        long activityInstanceId)
    {
        return string.Equals(completedRaid.RaidName, normalizedRaidName, StringComparison.OrdinalIgnoreCase)
            && completedRaid.InstanceId != activityInstanceId
            && completedRaid.CompletedAt <= activityCompletedAt;
    }

    private static List<SherpaReport> ToSherpaReports(IDictionary<string, int> playersSherpaed)
    {
        return playersSherpaed
            .Where(item => item.Value > 0)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SherpaReport
            {
                RaidName = item.Key,
                PlayerCount = item.Value
            })
            .ToList();
    }
}
