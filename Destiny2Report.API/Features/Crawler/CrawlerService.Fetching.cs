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
            var response = await ExecuteBungieOperationAsync(
                    operation,
                    () => bungieClient.Destiny2_GetHistoricalStatsAsync(
                        characterId,
                        null,
                        null,
                        playerMembershipId,
                        ModeStatGroups,
                        platformId,
                        [mode],
                        null,
                        cancellationToken),
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
            var response = await ExecuteBungieOperationAsync(
                    operation,
                    () => bungieClient.Destiny2_GetUniqueWeaponHistoryAsync(characterId, playerMembershipId, platformId, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            return (characterId, Weapons: EnsureSuccess(response, item => item.Response, operation).Weapons ?? []);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(item => item.characterId, item => item.Weapons);
    }

    private async IAsyncEnumerable<long> FetchActivityHistoryBatchesAsync(
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<long> characterIds,
        DateTimeOffset? crawlAfter,
        IReadOnlySet<long> recentActivityIds,
        Func<DestinyHistoricalStatsPeriodGroup, ValueTask>? onFetchedActivity,
        Func<DestinyHistoricalStatsPeriodGroup, ValueTask>? onActivityToCrawl,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seenActivityIds = new HashSet<long>();
        var seenActivityIdsLock = new object();
        var discoveryCallbackLock = new SemaphoreSlim(1, 1);
        var channel = Channel.CreateUnbounded<long>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        var producerTasks = characterIds
            .Select(characterId => FetchPagesAsync(characterId, channel.Writer))
            .ToArray();
        _ = CompleteWhenProducersFinishAsync(producerTasks, channel.Writer);

        await foreach (var activityInstanceId in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return activityInstanceId;
        }

        async Task FetchPagesAsync(
            long characterId,
            ChannelWriter<long> writer)
        {
            var page = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var operation = $"GetActivityHistory:{characterId}:{page}";
                var response = await ExecuteBungieOperationAsync(
                        operation,
                        () => bungieClient.Destiny2_GetActivityHistoryAsync(characterId, PageSize, playerMembershipId, platformId, null, page, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                EnsurePublicActivityHistoryResponse(response, operation);
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
                        if (await TryDiscoverActivityAsync(activity, instanceId).ConfigureAwait(false))
                        {
                            await writer.WriteAsync(instanceId, cancellationToken).ConfigureAwait(false);
                        }
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

        async ValueTask<bool> TryDiscoverActivityAsync(DestinyHistoricalStatsPeriodGroup activity, long instanceId)
        {
            lock (seenActivityIdsLock)
            {
                if (!seenActivityIds.Add(instanceId))
                {
                    return false;
                }
            }

            await discoveryCallbackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (onFetchedActivity is not null)
                {
                    await onFetchedActivity(activity).ConfigureAwait(false);
                }

                if (recentActivityIds.Contains(instanceId))
                {
                    return false;
                }

                if (onActivityToCrawl is not null)
                {
                    await onActivityToCrawl(activity).ConfigureAwait(false);
                }

                return true;
            }
            finally
            {
                discoveryCallbackLock.Release();
            }
        }

        static async Task CompleteWhenProducersFinishAsync(
            Task[] producers,
            ChannelWriter<long> writer)
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
    }

    private async IAsyncEnumerable<(long ActivityInstanceId, DestinyPostGameCarnageReportData Pgcr)> FetchPgcrBatchAsync(
        IAsyncEnumerable<long> activityInstanceIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maxConcurrency = pgcrThrottler.RequestsPerSecond;
        var pending = new List<Task<(long ActivityInstanceId, DestinyPostGameCarnageReportData Pgcr)>>(maxConcurrency);
        var source = activityInstanceIds
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false)
            .GetAsyncEnumerator();

        try
        {
            while (pending.Count < maxConcurrency && await source.MoveNextAsync())
            {
                pending.Add(FetchPgcrAsync(source.Current));
            }

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);

                if (await source.MoveNextAsync())
                {
                    pending.Add(FetchPgcrAsync(source.Current));
                }

                var result = await completed.ConfigureAwait(false);
                if (result.Pgcr.ActivityDetails.InstanceId > 0)
                {
                    yield return result;
                }
            }
        }
        finally
        {
            await source.DisposeAsync();
        }

        async Task<(long ActivityInstanceId, DestinyPostGameCarnageReportData Pgcr)> FetchPgcrAsync(
            long activityId)
        {
            var operation = $"GetPostGameCarnageReport:{activityId}";
            using var lease = await pgcrThrottler.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var response = await ExecuteBungieOperationAsync(
                    operation,
                    () => bungieClient.Destiny2_GetPostGameCarnageReportAsync(activityId, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            var pgcr = EnsureSuccess(response, item => item.Response, operation);

            return (ActivityInstanceId: activityId, Pgcr: pgcr);
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
        catch (ApiException ex) when (ex.IsNotFound())
        {
            logger.LogDebug("Skipping sherpa candidate {MembershipType}/{MembershipId} because their account was not found.", platformId, playerMembershipId);
            return null;
        }
        catch (Exception ex) when (IsPrivateProfileException(ex))
        {
            logger.LogDebug("Skipping sherpa candidate {MembershipType}/{MembershipId} because their profile is not public.", platformId, playerMembershipId);
            return null;
        }
        catch (Exception ex) when (IsBungieOperationFailure(ex))
        {
            logger.LogWarning(
                ex,
                "Skipping sherpa candidate {MembershipType}/{MembershipId} because their account history could not be fetched.",
                platformId,
                playerMembershipId);
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
        catch (ApiException ex) when (ex.IsNotFound())
        {
            logger.LogDebug("Skipping sherpa candidate {MembershipType}/{MembershipId} because their account was not found while reading activity history.", platformId, playerMembershipId);
            return null;
        }
        catch (Exception ex) when (IsPrivateProfileException(ex))
        {
            logger.LogDebug("Skipping sherpa candidate {MembershipType}/{MembershipId} because their activity history is not public.", platformId, playerMembershipId);
            return null;
        }
        catch (Exception ex) when (IsBungieOperationFailure(ex))
        {
            logger.LogWarning(
                ex,
                "Skipping sherpa candidate {MembershipType}/{MembershipId} because their raid history could not be fetched.",
                platformId,
                playerMembershipId);
            return null;
        }

        return results.Values.ToArray();

        async Task FetchCharacterRaidHistoryAsync(long characterId)
        {
            var page = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var operation = $"GetActivityHistory:Raid:{playerMembershipId}:{characterId}:{page}";
                using var lease = await sherpaHistoryThrottler.AcquireAsync(cancellationToken).ConfigureAwait(false);
                var response = await ExecuteBungieOperationAsync(
                        operation,
                        () => bungieClient.Destiny2_GetActivityHistoryAsync(
                            characterId,
                            PageSize,
                            playerMembershipId,
                            platformId,
                            ActivityModes.Raid,
                            page,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                EnsurePublicActivityHistoryResponse(response, operation);
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
        using var lease = await sherpaHistoryThrottler.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var response = await ExecuteBungieOperationAsync(
                operation,
                () => bungieClient.Destiny2_GetHistoricalStatsForAccountAsync(
                    playerMembershipId,
                    AccountStatGroups,
                    platformId,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        if (IsPrivateProfileResponse(response))
        {
            throw new PrivatePlayerUnavailableException(operation, "profile", response);
        }

        var accountStats = EnsureSuccess(response, item => item.Response, operation);
        return accountStats.Characters?
            .Select(character => character.CharacterId)
            .Where(characterId => characterId > 0)
            .Distinct()
            .ToArray() ?? [];
    }

    private static void EnsurePublicActivityHistoryResponse(BungieResponse response, string operation)
    {
        if (IsPrivateProfileResponse(response))
        {
            throw new PrivatePlayerUnavailableException(operation, "activity history", response);
        }
    }
}
