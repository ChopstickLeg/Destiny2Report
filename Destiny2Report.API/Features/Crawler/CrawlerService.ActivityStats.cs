using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Crawler.Models.Bungie;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
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
        IReadOnlyCollection<long> characterIds,
        DateTimeOffset? crawlAfter,
        IReadOnlySet<long> recentActivityIds,
        IDictionary<long, string> characterClassById,
        ManifestContext manifest,
        bool resetDerivedAggregates,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var activityDefinitions = await manifest.GetActivityDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var destinationDefinitions = await manifest.GetDestinationDefinitionsAsync(cancellationToken).ConfigureAwait(false);

        await ApplyPgcrAggregatesAsync(
                report,
                accumulator,
                platformId,
                playerMembershipId,
                characterIds,
                crawlAfter,
                recentActivityIds,
                characterClassById,
                activityDefinitions,
                destinationDefinitions,
                manifest,
                resetDerivedAggregates,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ApplyPatrolTime(
        CrawlAccumulator accumulator,
        DestinyHistoricalStatsPeriodGroup activity,
        IReadOnlyDictionary<string, ManifestActivityDefinition> activityDefinitions,
        IReadOnlyDictionary<string, ManifestDestinationDefinition> destinationDefinitions)
    {
        var activityDefinition = GetDefinition(activityDefinitions, activity.ActivityDetails.ReferenceId)
            ?? GetDefinition(activityDefinitions, activity.ActivityDetails.DirectorActivityHash);
        var destinationHash = activityDefinition?.DestinationHash ?? 0;
        var destination = GetDefinition(destinationDefinitions, destinationHash);
        var destinationName = destination?.DisplayProperties?.Name;
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

    private async Task ApplyPgcrAggregatesAsync(
        DestinyReport report,
        CrawlAccumulator accumulator,
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<long> characterIds,
        DateTimeOffset? crawlAfter,
        IReadOnlySet<long> recentActivityIds,
        IDictionary<long, string> characterClassById,
        IReadOnlyDictionary<string, ManifestActivityDefinition> activityDefinitions,
        IReadOnlyDictionary<string, ManifestDestinationDefinition> destinationDefinitions,
        ManifestContext manifest,
        bool resetDerivedAggregates,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var playerEncounterCounts = resetDerivedAggregates
            ? new Dictionary<(int MembershipType, long MembershipId), int>()
            : await LoadPlayerEncounterCountsAsync(platformId, playerMembershipId, cancellationToken).ConfigureAwait(false);
        var pveWeaponDeltas = new Dictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>>();
        var pvpWeaponDeltas = new Dictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>>();
        var gambitWeaponDeltas = new Dictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>>();
        var pveDeathDeltas = new Dictionary<int, long>();
        var pvpDeathDeltas = new Dictionary<int, long>();
        var gambitDeathDeltas = new Dictionary<int, long>();
        var emblemSecondsDeltas = new Dictionary<long, long>();
        var raidCompletions = ToCompletionAggregates(accumulator.RaidCompletions);
        var dungeonCompletions = ToCompletionAggregates(accumulator.DungeonCompletions);
        var conquestCompletions = ToCompletionAggregates(accumulator.ConquestCompletions);
        var playersSherpaed = new Dictionary<string, int>(accumulator.PlayersSherpaed, StringComparer.OrdinalIgnoreCase);
        var pendingSherpaChecks = new List<SherpaCheck>();
        var completedRaidHistoryByPlayer = new ConcurrentDictionary<(int MembershipType, long MembershipId), Lazy<Task<IReadOnlyCollection<CompletedRaidActivity>?>>>();
        var membershipTypeByPlayer = new ConcurrentDictionary<long, Lazy<Task<int?>>>();
        var encounteredPlayerKeys = ReadEncounteredPlayerKeys(accumulator);
        var completedRaidActivities = new List<CompletedRaidActivity>();
        var crawlState = new ActivityCrawlState();
        var discoveredPgcrs = 0L;
        var processedPgcrs = 0L;

        if (progress is not null)
        {
            await progress.StartPhaseAsync("pgcr", "Pulling PGCRs", total: 0, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var activityInstanceIds = FetchActivityHistoryBatchesAsync(
            platformId,
            playerMembershipId,
            characterIds,
            crawlAfter,
            recentActivityIds,
            activity =>
            {
                crawlState.AddFetched(activity);
                return ValueTask.CompletedTask;
            },
            async activity =>
            {
                if (IncludesMode(activity, ActivityModes.Patrol))
                {
                    ApplyPatrolTime(accumulator, activity, activityDefinitions, destinationDefinitions);
                }

                discoveredPgcrs++;
                if (progress is not null)
                {
                    await progress.ReportAsync(processedPgcrs, discoveredPgcrs, cancellationToken).ConfigureAwait(false);
                }
            },
            cancellationToken);

        await foreach (var (instanceId, pgcr) in FetchPgcrBatchAsync(activityInstanceIds, cancellationToken).ConfigureAwait(false))
        {
            processedPgcrs++;
            if (progress is not null)
            {
                await progress.ReportAsync(processedPgcrs, discoveredPgcrs, cancellationToken).ConfigureAwait(false);
            }

            TryFillCharacterClassFromPgcr(characterClassById, pgcr, playerMembershipId);

            var playerEntries = FindPlayerEntries(pgcr, platformId, playerMembershipId);
            if (playerEntries.Length == 0)
            {
                continue;
            }

            AddPlayDate(accumulator, pgcr.Period);

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
            var conquestName = conquests.GetName(
                pgcr.ActivityDetails.ReferenceId,
                pgcr.ActivityDetails.DirectorActivityHash,
                activityCompletedAt);
            var isContest = IsContest(pgcr, activityCompletedAt, isRaid, isDungeon);
            var isSoloFlawless = isSolo && isFlawless;
            var playerActivitySeconds = SumPlayerActivitySeconds(playerEntries);

            accumulator.TotalActivitySeconds += (long)playerActivitySeconds;
            AddActivityModePlaytime(accumulator, pgcr, (long)playerActivitySeconds);
            AddEmblemPlaytime(emblemSecondsDeltas, playerEntries);

            if (isRaid)
            {
                AddActivity(raidCompletions, activityName);
                if (playerCompleted)
                {
                    AddCompletion(raidCompletions, activityName, activityCompletedAt, instanceId, playerActivitySeconds, isContest, isFlawless, isSolo, isSoloFlawless);
                    var normalizedRaidName = ContestModeLookup.NormalizeActivityName(activityName);
                    AddFirstRaidCompletion(accumulator, normalizedRaidName, activityCompletedAt, instanceId);
                    completedRaidActivities.Add(new CompletedRaidActivity(normalizedRaidName, activityCompletedAt, instanceId));
                    pendingSherpaChecks.Add(new SherpaCheck(instanceId, normalizedRaidName, activityCompletedAt, GetCompletedFireteamMembers(pgcr, playerMembershipId).ToArray()));
                }
            }

            if (isDungeon)
            {
                AddActivity(dungeonCompletions, activityName);
                if (playerCompleted)
                {
                    AddCompletion(dungeonCompletions, activityName, activityCompletedAt, instanceId, playerActivitySeconds, isContest, isFlawless, isSolo, isSoloFlawless);
                }
            }

            if (conquestName is not null)
            {
                AddActivity(conquestCompletions, conquestName);
                if (playerCompleted)
                {
                    AddCompletion(
                        conquestCompletions,
                        conquestName,
                        activityCompletedAt,
                        instanceId,
                        playerActivitySeconds,
                        contestClear: false,
                        isFlawless,
                        isSolo,
                        isSoloFlawless);
                }
            }

            var otherPlayers = GetDistinctOtherPlayers(pgcr, playerMembershipId);

            foreach (var otherPlayer in otherPlayers)
            {
                var key = (otherPlayer.MembershipType, otherPlayer.MembershipId);
                playerEncounterCounts[key] = playerEncounterCounts.GetValueOrDefault(key) + 1;
                if (IsCountablePlayerEncounter(key.MembershipType, key.MembershipId, 1))
                {
                    encounteredPlayerKeys.Add(key);
                }
            }

            if (isGambit && hasGambitMoteStats)
            {
                accumulator.GambitMoteMatches++;
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
                    AddPvpPlaylistResult(accumulator, pgcr, playerEntries);
                    AddWeaponsByClassAndMode(pvpWeaponDeltas, pgcr, playerEntries, characterClassById);
                    AddDeathsByMode(pvpDeathDeltas, pgcr, playerEntries);
                }
            }
            else if (isGambit)
            {
                if (!isPrivateGambit)
                {
                    AddWeaponsByClassAndMode(gambitWeaponDeltas, pgcr, playerEntries, characterClassById);
                    AddDeathsByMode(gambitDeathDeltas, pgcr, playerEntries);
                }
            }
            else
            {
                AddWeaponsByClassAndMode(pveWeaponDeltas, pgcr, playerEntries, characterClassById);
                AddDeathsByMode(pveDeathDeltas, pgcr, playerEntries);
            }
        }

        if (progress is not null)
        {
            await progress.CompletePhaseAsync(processedPgcrs, discoveredPgcrs, cancellationToken).ConfigureAwait(false);
        }

        await ApplySherpaChecksAsync(cancellationToken).ConfigureAwait(false);
        UpdateAccumulatorCrawlStateFromState(accumulator, crawlState, [], resetDerivedAggregates);

        var persistedPlayerEncounterCounts = playerEncounterCounts
            .Where(item => IsPersistablePlayerEncounter(item.Key.MembershipType, item.Key.MembershipId, item.Value))
            .ToDictionary(item => item.Key, item => item.Value);

        SaveCompletionAggregates(accumulator.RaidCompletions, raidCompletions);
        SaveCompletionAggregates(accumulator.DungeonCompletions, dungeonCompletions);
        SaveCompletionAggregates(accumulator.ConquestCompletions, conquestCompletions);
        accumulator.PlayersSherpaed = new Dictionary<string, int>(playersSherpaed, StringComparer.OrdinalIgnoreCase);
        SaveEncounteredPlayerKeys(accumulator, encounteredPlayerKeys);

        report.PatrolTimeByPlanet = accumulator.PatrolSecondsByPlanet.ToDictionary(
            item => item.Key,
            item => TimeSpan.FromSeconds(item.Value));
        report.TotalKills = accumulator.TotalKills;
        report.ZeroKillActivities = accumulator.ZeroKillActivities;
        report.CrucibleKills = ToCrucibleKillsReport(accumulator);
        report.GambitMotes = ToGambitMotesReport(accumulator);
        report.RaidCompletions = ToCompletionSummaries(raidCompletions);
        report.DungeonCompletions = ToCompletionSummaries(dungeonCompletions);
        report.ConquestCompletions = ToCompletionSummaries(conquestCompletions);
        report.PvpPlaylists = ToPvpPlaylistReports(accumulator.PvpPlaylists);
        report.PlayersSherpaed = ToSherpaReports(playersSherpaed);
        await ApplyPlayerEncounterCountsAsync(
                report,
                platformId,
                playerMembershipId,
                persistedPlayerEncounterCounts,
                encounteredPlayerKeys,
                accumulator.UniquePlayersPlayedWith,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        if (progress is not null)
        {
            await progress.StartPhaseAsync("weapon-aggregates", "Saving weapon aggregates", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await ApplyWeaponAggregateDeltasAsync(
                platformId,
                playerMembershipId,
                pveWeaponDeltas,
                pvpWeaponDeltas,
                gambitWeaponDeltas,
                resetDerivedAggregates,
                cancellationToken)
            .ConfigureAwait(false);

        await ApplyDeathAggregateDeltasAsync(
                platformId,
                playerMembershipId,
                pveDeathDeltas,
                pvpDeathDeltas,
                gambitDeathDeltas,
                resetDerivedAggregates,
                cancellationToken)
            .ConfigureAwait(false);

        if (progress is not null)
        {
            await progress.StartPhaseAsync("emblem-aggregates", "Saving emblem aggregates", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await ApplyEmblemAggregateDeltasAsync(
                report,
                platformId,
                playerMembershipId,
                emblemSecondsDeltas,
                manifest.Manifest,
                resetDerivedAggregates,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        report.TotalActivityTime = TimeSpan.FromSeconds(accumulator.TotalActivitySeconds);
        report.FirstActivityAtUtc = accumulator.FirstActivityAtUtc;
        report.LongestPlaytimeStreak = GetLongestPlaytimeStreak(accumulator.PlayDates);
        report.CurrentPlaytimeStreak = GetCurrentPlaytimeStreak(accumulator.PlayDates, DateTime.UtcNow.Date);


        async Task<IReadOnlyCollection<CompletedRaidActivity>?> GetCompletedRaidHistoryAsync(
            string normalizedRaidName,
            int membershipType,
            long membershipId)
        {
            var accumulatorHistory = await FetchCompletedRaidHistoryFromAccumulatorAsync(normalizedRaidName, membershipType, membershipId, cancellationToken)
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

            var fetchedHistory = await lazyHistory.Value.ConfigureAwait(false);
            if (fetchedHistory is not null)
            {
                await PersistInferredRaidCompletionsAsync(membershipType, membershipId, fetchedHistory, cancellationToken)
                    .ConfigureAwait(false);
            }

            return fetchedHistory;
        }

        async Task<IReadOnlyCollection<CompletedRaidActivity>?> FetchCompletedRaidHistoryFromAccumulatorAsync(
            string normalizedRaidName,
            int membershipType,
            long membershipId,
            CancellationToken cancellationToken)
        {
            var accumulators = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators");
            var filter = Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, membershipType)
                & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, membershipId);
            var playerAccumulator = await accumulators.Find(filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (playerAccumulator is null)
            {
                return null;
            }

            var firstCompletions = GetFirstRaidCompletions(playerAccumulator).ToArray();
            return firstCompletions.Any(item => string.Equals(item.Key, normalizedRaidName, StringComparison.OrdinalIgnoreCase))
                ? firstCompletions
                    .Select(item => new CompletedRaidActivity(item.Key, item.Value.CompletedAt, item.Value.InstanceId))
                    .ToArray()
                : null;
        }

        async Task PersistInferredRaidCompletionsAsync(
            int membershipType,
            long membershipId,
            IReadOnlyCollection<CompletedRaidActivity> history,
            CancellationToken cancellationToken)
        {
            if (history.Count == 0)
            {
                return;
            }

            var accumulators = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators");
            var filter = Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, membershipType)
                & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, membershipId);
            var updates = new List<UpdateDefinition<CrawlAccumulator>>
            {
                Builders<CrawlAccumulator>.Update.SetOnInsert(item => item.PlatformId, membershipType),
                Builders<CrawlAccumulator>.Update.SetOnInsert(item => item.PlayerMembershipId, membershipId),
                Builders<CrawlAccumulator>.Update.SetOnInsert(item => item.NeedsFullRecrawl, true),
                Builders<CrawlAccumulator>.Update.SetOnInsert(item => item.FullRecrawlReason, "First raid completions inferred from sherpa history.")
            };

            foreach (var raidGroup in history
                .Where(activity => !string.IsNullOrWhiteSpace(activity.RaidName))
                .GroupBy(activity => activity.RaidName, StringComparer.OrdinalIgnoreCase))
            {
                var raidName = raidGroup.Key;
                var firstCompletion = raidGroup
                    .OrderBy(activity => activity.CompletedAt)
                    .ThenBy(activity => activity.InstanceId)
                    .First();
                var firstCompletionRecord = new RaidFirstCompletion
                {
                    CompletedAt = firstCompletion.CompletedAt.UtcDateTime,
                    InstanceId = firstCompletion.InstanceId
                };

                updates.Add(Builders<CrawlAccumulator>.Update.Set($"RaidCompletions.{raidName}.CompletionCount", raidGroup.Count()));
                updates.Add(Builders<CrawlAccumulator>.Update.Set($"RaidCompletions.{raidName}.FirstCompletion", firstCompletionRecord));
            }

            await accumulators.UpdateOneAsync(
                    filter,
                    Builders<CrawlAccumulator>.Update.Combine(updates),
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        async Task ApplySherpaChecksAsync(CancellationToken cancellationToken)
        {
            var unresolvedCandidateChecks = pendingSherpaChecks
                .Where(check =>
                    HasPriorCompletedRaid(accumulator, check.NormalizedRaidName, check.CompletedAt, check.InstanceId)
                    || HasPriorCompletedRaidInHistory(completedRaidActivities, check.NormalizedRaidName, check.CompletedAt, check.InstanceId))
                .SelectMany(check => check.Candidates
                    .Select(player => new SherpaCandidateCheck(
                        check.InstanceId,
                        check.NormalizedRaidName,
                        check.CompletedAt,
                        player.MembershipType,
                        player.MembershipId)))
                .ToArray();

            var resolvedCandidateChecks = new ConcurrentBag<SherpaCandidateCheck>();
            var resolvedCount = 0L;

            if (progress is not null)
            {
                await progress.StartPhaseAsync("sherpa-memberships", "Resolving sherpa candidates", unresolvedCandidateChecks.Length, cancellationToken).ConfigureAwait(false);
            }

            await Parallel.ForEachAsync(
                    unresolvedCandidateChecks,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = sherpaHistoryThrottler.RequestsPerSecond,
                        CancellationToken = cancellationToken
                    },
                    async (check, ct) =>
                    {
                        var resolved = await ResolveCandidateCheckAsync(check).ConfigureAwait(false);
                        if (resolved is not null)
                        {
                            resolvedCandidateChecks.Add(resolved);
                        }

                        var current = Interlocked.Increment(ref resolvedCount);
                        if (progress is not null)
                        {
                            await progress.ReportAsync(current, unresolvedCandidateChecks.Length, ct).ConfigureAwait(false);
                        }
                    })
                .ConfigureAwait(false);

            var candidateChecks = resolvedCandidateChecks.ToArray();

            if (candidateChecks.Length == 0)
            {
                return;
            }

            var candidateRaidPlayers = candidateChecks
                .Select(check => (check.NormalizedRaidName, check.MembershipType, check.MembershipId))
                .Distinct()
                .ToArray();

            var historyByPlayerRaid = new ConcurrentDictionary<(string NormalizedRaidName, int MembershipType, long MembershipId), IReadOnlyCollection<CompletedRaidActivity>?>();
            var historiesFetched = 0L;

            if (progress is not null)
            {
                await progress.StartPhaseAsync("sherpa-histories", "Checking sherpa raid histories", candidateRaidPlayers.Length, cancellationToken).ConfigureAwait(false);
            }

            await Parallel.ForEachAsync(
                    candidateRaidPlayers,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = sherpaHistoryThrottler.RequestsPerSecond,
                        CancellationToken = cancellationToken
                    },
                    async (player, ct) =>
                    {
                        var history = await GetCompletedRaidHistoryAsync(
                                player.NormalizedRaidName,
                                player.MembershipType,
                                player.MembershipId)
                            .ConfigureAwait(false);
                        historyByPlayerRaid[(player.NormalizedRaidName, player.MembershipType, player.MembershipId)] = history;

                        var current = Interlocked.Increment(ref historiesFetched);
                        if (progress is not null)
                        {
                            await progress.ReportAsync(current, candidateRaidPlayers.Length, ct).ConfigureAwait(false);
                        }
                    })
                .ConfigureAwait(false);

            foreach (var check in candidateChecks)
            {
                if (!historyByPlayerRaid.TryGetValue((check.NormalizedRaidName, check.MembershipType, check.MembershipId), out var history)
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
                var operation = $"GetLinkedProfiles:{membershipId}";
                using var lease = await sherpaHistoryThrottler.AcquireAsync(cancellationToken).ConfigureAwait(false);
                var response = await ExecuteBungieOperationAsync(
                        operation,
                        () => bungieClient.Destiny2_GetLinkedProfilesAsync(
                            getAllMemberships: true,
                            membershipId: membershipId,
                            membershipType: AllMembershipTypes,
                            cancellationToken: cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                var payload = EnsureSuccess(response, item => item.Response, operation);
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

    private static void AddPlayDate(CrawlAccumulator accumulator, DateTimeOffset period)
    {
        var playDate = period.UtcDateTime.Date;
        if (!accumulator.PlayDates.Contains(playDate))
        {
            accumulator.PlayDates.Add(playDate);
        }
    }

    private static PlaytimeStreakReport? GetLongestPlaytimeStreak(IEnumerable<DateTime> playDates)
    {
        var dates = playDates
            .Select(date => date.ToUniversalTime().Date)
            .Distinct()
            .OrderBy(date => date)
            .ToArray();
        if (dates.Length == 0)
        {
            return null;
        }

        var longestStart = dates[0];
        var longestEnd = dates[0];
        var currentStart = dates[0];

        for (var index = 1; index < dates.Length; index++)
        {
            if (dates[index] != dates[index - 1].AddDays(1))
            {
                currentStart = dates[index];
            }

            if ((dates[index] - currentStart).Days > (longestEnd - longestStart).Days)
            {
                longestStart = currentStart;
                longestEnd = dates[index];
            }
        }

        return new PlaytimeStreakReport
        {
            StartDate = longestStart,
            EndDate = longestEnd
        };
    }

    private static PlaytimeStreakReport? GetCurrentPlaytimeStreak(IEnumerable<DateTime> playDates, DateTime utcToday)
    {
        var dates = playDates.Select(date => date.ToUniversalTime().Date).Distinct().OrderBy(date => date).ToArray();
        if (dates.Length == 0 || dates[^1] < utcToday.AddDays(-1))
        {
            return null;
        }

        var start = dates[^1];
        for (var index = dates.Length - 2; index >= 0 && dates[index] == start.AddDays(-1); index--)
        {
            start = dates[index];
        }

        return new PlaytimeStreakReport { StartDate = start, EndDate = dates[^1] };
    }

    private static void AddPvpPlaylistResult(
        CrawlAccumulator accumulator,
        DestinyPostGameCarnageReportData pgcr,
        IReadOnlyCollection<DestinyPostGameCarnageReportEntry> playerEntries)
    {
        var key = pgcr.ActivityDetails.Mode.ToString();
        if (!accumulator.PvpPlaylists.TryGetValue(key, out var playlist))
        {
            playlist = new PvpPlaylistAccumulator();
            accumulator.PvpPlaylists[key] = playlist;
        }

        if (playerEntries.Any(entry => entry.Standing == 0)) playlist.Wins++;
        else playlist.Losses++;
    }

    private static List<PvpPlaylistReport> ToPvpPlaylistReports(IReadOnlyDictionary<string, PvpPlaylistAccumulator> playlists)
    {
        return playlists.Select(item =>
            {
                var mode = int.TryParse(item.Key, out var parsed) ? parsed : 0;
                return new PvpPlaylistReport
                {
                    Mode = mode,
                    ModeName = GetSpecificActivityModeName(mode),
                    Wins = item.Value.Wins,
                    Losses = item.Value.Losses
                };
            })
            .OrderByDescending(item => item.Matches)
            .ThenBy(item => item.Mode)
            .ToList();
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
    }

    private static void AddDeathsByMode(
        IDictionary<int, long> deathDeltasByMode,
        DestinyPostGameCarnageReportData pgcr,
        IEnumerable<DestinyPostGameCarnageReportEntry> entries)
    {
        var deaths = (long)SumStats(entries, "deaths");
        if (deaths <= 0)
        {
            return;
        }

        var mode = pgcr.ActivityDetails.Mode;
        deathDeltasByMode.TryGetValue(mode, out var currentDeaths);
        deathDeltasByMode[mode] = currentDeaths + deaths;
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

    private static void AddWeaponsByClassAndMode(
        IDictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>> weaponDeltasByClassAndMode,
        DestinyPostGameCarnageReportData pgcr,
        IEnumerable<DestinyPostGameCarnageReportEntry> entries,
        IDictionary<long, string> characterClassById)
    {
        foreach (var entry in entries)
        {
            var className = characterClassById.TryGetValue(entry.CharacterId, out var knownClass)
                ? ClassName(knownClass)
                : ClassName(entry.Player?.CharacterClass ?? "Unknown");
            AddWeapons(GetWeaponDeltasForClassAndMode(weaponDeltasByClassAndMode, pgcr, className), [entry]);
        }
    }

    private static IDictionary<long, WeaponKillDelta> GetWeaponDeltasForClassAndMode(
        IDictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>> weaponDeltasByClassAndMode,
        DestinyPostGameCarnageReportData pgcr,
        string className)
    {
        var key = (className, pgcr.ActivityDetails.Mode);
        if (!weaponDeltasByClassAndMode.TryGetValue(key, out var weaponDeltas))
        {
            weaponDeltas = new Dictionary<long, WeaponKillDelta>();
            weaponDeltasByClassAndMode[key] = weaponDeltas;
        }

        return weaponDeltas;
    }

    private static double SumStats(
        IEnumerable<DestinyPostGameCarnageReportEntry> playerEntries,
        string statId)
    {
        return playerEntries.Sum(entry => GetStat(entry.Values, statId));
    }

    private static double SumPlayerActivitySeconds(IEnumerable<DestinyPostGameCarnageReportEntry> playerEntries)
    {
        return playerEntries.Sum(GetPlayerActivitySeconds);
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
        IReadOnlyDictionary<string, ManifestActivityModeDefinition> activityModeDefinitions)
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
        IReadOnlyDictionary<string, ManifestActivityModeDefinition> activityModeDefinitions)
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

    private static string GetActivityModeName(IReadOnlyDictionary<string, ManifestActivityModeDefinition> activityModeDefinitions, int mode)
    {
        foreach (var definition in activityModeDefinitions.Values)
        {
            if (definition.ModeType != mode)
            {
                continue;
            }

            var name = definition.DisplayProperties?.Name;
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
            Matches = accumulator.GambitMoteMatches,
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
        IReadOnlyDictionary<string, ManifestActivityDefinition> activityDefinitions,
        IReadOnlySet<long> activityTypeHashes)
    {
        var definition = GetDefinition(activityDefinitions, pgcr.ActivityDetails.ReferenceId)
            ?? GetDefinition(activityDefinitions, pgcr.ActivityDetails.DirectorActivityHash);
        var activityTypeHash = definition?.ActivityTypeHash ?? 0;
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

    private static string ActivityName(IReadOnlyDictionary<string, ManifestActivityDefinition> definitions, int referenceId, int directorActivityHash)
    {
        var definition = GetDefinition(definitions, referenceId) ?? GetDefinition(definitions, directorActivityHash);
        return definition?.DisplayProperties?.Name ?? referenceId.ToString();
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

    private static void AddActivity(IDictionary<string, ActivityCompletionAggregate> completions, string activityName)
    {
        var key = ContestModeLookup.NormalizeActivityName(activityName);
        if (!completions.TryGetValue(key, out var completion))
        {
            completion = new ActivityCompletionAggregate(key);
            completions[key] = completion;
        }
        completion.ActivityCount++;
    }

    private static void AddCompletion(
        IDictionary<string, ActivityCompletionAggregate> completions,
        string activityName,
        DateTimeOffset completedAt,
        long instanceId,
        double durationSeconds,
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
        SetFirstRaidCompletion(completion, completedAt, instanceId);
        if (completion.LastCompletion is null || completedAt.UtcDateTime > completion.LastCompletion.CompletedAt)
        {
            completion.LastCompletion = new RaidFirstCompletion { CompletedAt = completedAt.UtcDateTime, InstanceId = instanceId };
        }
        if (durationSeconds > 0 && (completion.FastestCompletion is null || durationSeconds < completion.FastestCompletion.Duration.TotalSeconds))
        {
            completion.FastestCompletion = new ActivityFastestCompletion
            {
                Duration = TimeSpan.FromSeconds(durationSeconds), CompletedAt = completedAt.UtcDateTime, InstanceId = instanceId
            };
        }
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
        SetFirstRaidCompletion(accumulator.RaidCompletions, normalizedRaidName, completedAt, instanceId);
    }

    private static RaidFirstCompletion? SetFirstRaidCompletion(
        IDictionary<string, ActivityCompletionAggregate> completions,
        string normalizedRaidName,
        DateTimeOffset completedAt,
        long instanceId)
    {
        if (!completions.TryGetValue(normalizedRaidName, out var completion))
        {
            completion = new ActivityCompletionAggregate(normalizedRaidName);
            completions[normalizedRaidName] = completion;
        }

        return SetFirstRaidCompletion(completion, completedAt, instanceId);
    }

    private static RaidFirstCompletion? SetFirstRaidCompletion(
        IDictionary<string, ActivityCompletionAccumulator> completions,
        string normalizedRaidName,
        DateTimeOffset completedAt,
        long instanceId)
    {
        if (!completions.TryGetValue(normalizedRaidName, out var completion))
        {
            completion = new ActivityCompletionAccumulator();
            completions[normalizedRaidName] = completion;
        }

        return SetFirstRaidCompletion(completion, completedAt, instanceId);
    }

    private static RaidFirstCompletion? SetFirstRaidCompletion(
        ActivityCompletionAggregate completion,
        DateTimeOffset completedAt,
        long instanceId)
    {
        if (completion.FirstCompletion is not null && completedAt.UtcDateTime >= completion.FirstCompletion.CompletedAt)
        {
            return null;
        }

        completion.FirstCompletion = new RaidFirstCompletion
        {
            CompletedAt = completedAt.UtcDateTime,
            InstanceId = instanceId
        };
        return completion.FirstCompletion;
    }

    private static RaidFirstCompletion? SetFirstRaidCompletion(
        ActivityCompletionAccumulator completion,
        DateTimeOffset completedAt,
        long instanceId)
    {
        if (completion.FirstCompletion is not null && completedAt.UtcDateTime >= completion.FirstCompletion.CompletedAt)
        {
            return null;
        }

        completion.FirstCompletion = new RaidFirstCompletion
        {
            CompletedAt = completedAt.UtcDateTime,
            InstanceId = instanceId
        };
        return completion.FirstCompletion;
    }

    private static List<ActivityCompletionSummary> ToCompletionSummaries(
        IDictionary<string, ActivityCompletionAggregate> completions)
    {
        return completions.Values
            .OrderBy(completion => completion.ActivityName, StringComparer.OrdinalIgnoreCase)
            .Select(completion => new ActivityCompletionSummary
            {
                ActivityName = completion.ActivityName,
                ActivityCount = completion.ActivityCount,
                CompletionCount = completion.CompletionCount,
                FirstCompletion = completion.FirstCompletion,
                LastCompletion = completion.LastCompletion,
                FastestCompletion = completion.FastestCompletion,
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
        IReadOnlyDictionary<string, ManifestActivityDefinition> activityDefinitions)
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
        return GetFirstRaidCompletions(accumulator).Select(item => new CompletedRaidActivity(
            item.Key,
            item.Value.CompletedAt,
            item.Value.InstanceId));
    }

    private static IEnumerable<KeyValuePair<string, RaidFirstCompletion>> GetFirstRaidCompletions(CrawlAccumulator accumulator)
    {
        return accumulator.RaidCompletions
            .Where(item => item.Value.FirstCompletion is not null)
            .Select(item => new KeyValuePair<string, RaidFirstCompletion>(item.Key, item.Value.FirstCompletion!));
    }

    private static IEnumerable<CompletedRaidActivity> ToCompletedRaidActivities(
        IEnumerable<DestinyHistoricalStatsPeriodGroup> activities,
        IReadOnlyDictionary<string, ManifestActivityDefinition> activityDefinitions)
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
