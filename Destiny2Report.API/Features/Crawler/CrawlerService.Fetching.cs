using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;
using ReportPlayer = Destiny2Report.API.Features.Crawler.Models.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
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

    private async IAsyncEnumerable<IReadOnlyList<DestinyHistoricalStatsPeriodGroup>> FetchActivityHistoryBatchesAsync(
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<long> characterIds,
        DateTimeOffset? crawlAfter,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seenActivityIds = new HashSet<long>();
        var batch = new List<DestinyHistoricalStatsPeriodGroup>(batchSize);
        var channel = Channel.CreateBounded<DestinyHistoricalStatsPeriodGroup>(
            new BoundedChannelOptions(batchSize)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        var producerTasks = characterIds
            .Select(characterId => FetchPagesAsync(characterId, channel.Writer))
            .ToArray();
        _ = CompleteWhenProducersFinishAsync(producerTasks, channel.Writer);

        await foreach (var activity in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var instanceId = activity.ActivityDetails.InstanceId;
            if (instanceId <= 0 || !seenActivityIds.Add(instanceId))
            {
                continue;
            }

            batch.Add(activity);
            if (batch.Count >= batchSize)
            {
                yield return DrainBatch();
            }
        }

        if (batch.Count > 0)
        {
            yield return DrainBatch();
        }

        async Task FetchPagesAsync(
            long characterId,
            ChannelWriter<DestinyHistoricalStatsPeriodGroup> writer)
        {
            var page = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var operation = $"GetActivityHistory:{characterId}:{page}";
                var response = await bungieClient.Destiny2_GetActivityHistoryAsync(characterId, PageSize, playerMembershipId, platformId, null, page, cancellationToken)
                    .ConfigureAwait(false);

                var payload = EnsureSuccess(response, item => item.Response, operation);
                var activities = payload.Activities;
                if (activities is null || activities.Count == 0)
                {
                    break;
                }

                var reachedCrawlBoundary = false;
                foreach (var activity in activities)
                {
                    var instanceId = activity.ActivityDetails.InstanceId;
                    if (instanceId <= 0)
                    {
                        continue;
                    }

                    if (crawlAfter is null || activity.Period > crawlAfter)
                    {
                        await writer.WriteAsync(activity, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        reachedCrawlBoundary = true;
                    }
                }

                if (activities.Count < PageSize || reachedCrawlBoundary)
                {
                    break;
                }

                page++;
            }
        }

        static async Task CompleteWhenProducersFinishAsync(
            Task[] producers,
            ChannelWriter<DestinyHistoricalStatsPeriodGroup> writer)
        {
            try
            {
                await Task.WhenAll(producers).ConfigureAwait(false);
                writer.TryComplete();
            }
            catch (Exception ex)
            {
                writer.TryComplete(ex);
            }
        }

        List<DestinyHistoricalStatsPeriodGroup> DrainBatch()
        {
            var result = batch
                .OrderBy(activity => activity.Period)
                .ToList();
            batch = new List<DestinyHistoricalStatsPeriodGroup>(batchSize);
            return result;
        }
    }

    private async IAsyncEnumerable<(DestinyHistoricalStatsPeriodGroup Activity, DestinyPostGameCarnageReportData Pgcr)> FetchPgcrBatchAsync(
        IReadOnlyList<DestinyHistoricalStatsPeriodGroup> activities,
        int maxConcurrency,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var nextIndex = 0;
        var pending = new List<Task<(DestinyHistoricalStatsPeriodGroup Activity, DestinyPostGameCarnageReportData Pgcr)>>(Math.Min(maxConcurrency, activities.Count));

        while (nextIndex < activities.Count && pending.Count < maxConcurrency)
        {
            pending.Add(FetchPgcrAsync(activities[nextIndex++]));
        }

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);

            if (nextIndex < activities.Count)
            {
                pending.Add(FetchPgcrAsync(activities[nextIndex++]));
            }

            var result = await completed.ConfigureAwait(false);
            if (result.Pgcr.ActivityDetails.InstanceId > 0)
            {
                yield return result;
            }
        }

        async Task<(DestinyHistoricalStatsPeriodGroup Activity, DestinyPostGameCarnageReportData Pgcr)> FetchPgcrAsync(
            DestinyHistoricalStatsPeriodGroup activity)
        {
            var activityId = activity.ActivityDetails.InstanceId;
            var operation = $"GetPostGameCarnageReport:{activityId}";
            var response = await bungieClient.Destiny2_GetPostGameCarnageReportAsync(activityId, cancellationToken)
                .ConfigureAwait(false);
            var pgcr = EnsureSuccess(response, item => item.Response, operation);

            return (Activity: activity, Pgcr: pgcr);
        }
    }

    private async Task<IReadOnlyCollection<CompletedRaidActivity>?> FetchCompletedRaidHistoryAsync(
        int platformId,
        long playerMembershipId,
        JObject activityDefinitions,
        CancellationToken cancellationToken)
    {
        long[] characterIds;
        try
        {
            characterIds = await FetchHistoricalCharacterIdsAsync(platformId, playerMembershipId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPrivateProfileException(ex))
        {
            logger.LogDebug("Skipping sherpa candidate {MembershipType}/{MembershipId} because their profile is not public.", platformId, playerMembershipId);
            return null;
        }

        if (characterIds.Length == 0)
        {
            return [];
        }

        var results = new ConcurrentDictionary<long, CompletedRaidActivity>();
        var tasks = characterIds.Select(FetchCharacterRaidHistoryAsync);
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPrivateProfileException(ex))
        {
            logger.LogDebug("Skipping sherpa candidate {MembershipType}/{MembershipId} because their activity history is not public.", platformId, playerMembershipId);
            return null;
        }

        return results.Values.ToArray();

        async Task FetchCharacterRaidHistoryAsync(long characterId)
        {
            var page = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var operation = $"GetActivityHistory:Raid:{playerMembershipId}:{characterId}:{page}";
                var response = await bungieClient.Destiny2_GetActivityHistoryAsync(
                        characterId,
                        PageSize,
                        playerMembershipId,
                        platformId,
                        ActivityModes.Raid,
                        page,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (IsPrivateProfileResponse(response))
                {
                    throw new PrivateProfileUnavailableException(operation, response);
                }

                var payload = EnsureSuccess(response, item => item.Response, operation);
                var activities = payload.Activities;
                if (activities is null || activities.Count == 0)
                {
                    break;
                }

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

                    results.TryAdd(
                        activity.ActivityDetails.InstanceId,
                        new CompletedRaidActivity(
                            ContestModeLookup.NormalizeActivityName(raidName),
                            GetActivityCompletedAt(activity),
                            activity.ActivityDetails.InstanceId));
                }

                if (activities.Count < PageSize)
                {
                    break;
                }

                page++;
            }
        }
    }

    private async Task<long[]> FetchHistoricalCharacterIdsAsync(
        int platformId,
        long playerMembershipId,
        CancellationToken cancellationToken)
    {
        var operation = $"GetHistoricalStatsForAccount:Characters:{platformId}:{playerMembershipId}";
        var response = await bungieClient.Destiny2_GetHistoricalStatsForAccountAsync(
                playerMembershipId,
                AccountStatGroups,
                platformId,
                cancellationToken)
            .ConfigureAwait(false);

        if (IsPrivateProfileResponse(response))
        {
            throw new PrivateProfileUnavailableException(operation, response);
        }

        var accountStats = EnsureSuccess(response, item => item.Response, operation);
        return accountStats.Characters?
            .Select(character => character.CharacterId)
            .Where(characterId => characterId > 0)
            .Distinct()
            .ToArray() ?? [];
    }
}
