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
                var activities = payload.Activities?.ToArray() ?? [];
                if (activities.Length == 0)
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

                if (activities.Length < PageSize)
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
