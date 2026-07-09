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
    private async Task ApplyPlayerEncounterCountsAsync(
        DestinyReport report,
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<(int MembershipType, long MembershipId), int> encounterCounts,
        IReadOnlyCollection<(int MembershipType, long MembershipId)> playersToQueue,
        int uniquePlayersPlayedWith,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var encounters = mongoDatabase.GetCollection<PlayerEncounterAggregate>("player_encounters");
        var ownerFilter = Builders<PlayerEncounterAggregate>.Filter.Eq(encounter => encounter.OwnerMembershipType, ownerMembershipType)
            & Builders<PlayerEncounterAggregate>.Filter.Eq(encounter => encounter.OwnerMembershipId, ownerMembershipId)
            & Builders<PlayerEncounterAggregate>.Filter.Gt(encounter => encounter.EncounteredMembershipType, 0)
            & Builders<PlayerEncounterAggregate>.Filter.Gt(encounter => encounter.EncounteredMembershipId, 0);

        await encounters.DeleteManyAsync(ownerFilter, cancellationToken).ConfigureAwait(false);

        if (encounterCounts.Count > 0)
        {
            var inserts = encounterCounts
                .Where(item => IsPersistablePlayerEncounter(item.Key.MembershipType, item.Key.MembershipId, item.Value))
                .Select(item =>
                {
                    return new InsertOneModel<PlayerEncounterAggregate>(new PlayerEncounterAggregate
                    {
                        OwnerMembershipType = ownerMembershipType,
                        OwnerMembershipId = ownerMembershipId,
                        EncounteredMembershipType = item.Key.MembershipType,
                        EncounteredMembershipId = item.Key.MembershipId,
                        Count = item.Value
                    });
                })
                .Cast<WriteModel<PlayerEncounterAggregate>>()
                .ToArray();

            if (inserts.Length > 0)
            {
                await encounters.BulkWriteAsync(inserts, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await QueueDiscoveredPlayersAsync(playersToQueue, progress, cancellationToken).ConfigureAwait(false);

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

        report.UniquePlayersPlayedWith = uniquePlayersPlayedWith;
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

    private async Task QueueDiscoveredPlayersAsync(
        IEnumerable<(int MembershipType, long MembershipId)> players,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        const int batchSize = 500;
        var now = DateTimeOffset.UtcNow;
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var validPlayers = players.Where(player => player.MembershipType > 0 && player.MembershipId > 0).ToArray();
        var queued = 0L;

        if (progress is not null)
        {
            await progress.StartPhaseAsync("discovered-players", "Queueing discovered players", validPlayers.Length, cancellationToken).ConfigureAwait(false);
        }

        foreach (var batch in validPlayers.Chunk(batchSize))
        {
            var writes = batch
                .Select(player =>
                {
                    var filter = Builders<DestinyReport>.Filter.Eq(report => report.PlatformId, player.MembershipType)
                        & Builders<DestinyReport>.Filter.Eq(report => report.PlayerMembershipId, player.MembershipId);
                    var update = Builders<DestinyReport>.Update
                        .SetOnInsert(report => report.PlatformId, player.MembershipType)
                        .SetOnInsert(report => report.PlayerMembershipId, player.MembershipId)
                        .SetOnInsert(report => report.CrawlState, DestinyReport.CrawlStateQueued)
                        .SetOnInsert(report => report.QueuedInRedis, false)
                        .SetOnInsert(report => report.QueuedAtUtc, now)
                        .SetOnInsert(report => report.CrawlError, "");

                    return (WriteModel<DestinyReport>)new UpdateOneModel<DestinyReport>(filter, update)
                    {
                        IsUpsert = true
                    };
                })
                .ToArray();

            if (writes.Length > 0)
            {
                await reports.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                    .ConfigureAwait(false);
            }

            queued += batch.Length;
            if (progress is not null)
            {
                await progress.ReportAsync(queued, validPlayers.Length, cancellationToken).ConfigureAwait(false);
            }
        }
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

    private async Task<ReportPlayer> GetPlayerInfoAsync(PlayerEncounterAggregate player, CancellationToken cancellationToken)
    {
        try
        {
            var operation = $"Destiny2_GetProfileAsync:Characters:{player.EncounteredMembershipType}:{player.EncounteredMembershipId}";
            var response = await ExecuteBungieOperationAsync(
                    operation,
                    () => bungieClient.Destiny2_GetProfileAsync(ProfileCharactersComponents, player.EncounteredMembershipId, player.EncounteredMembershipType, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            var characterResponse = EnsureSuccess(response, profile => profile.Response, operation);
            var lastPlayedCharacter = characterResponse?.Characters?.Data?.Values.OrderByDescending(c => c.DateLastPlayed).FirstOrDefault();
            return new ReportPlayer
            {
                MembershipId = player.EncounteredMembershipId,
                MembershipType = player.EncounteredMembershipType,
                DisplayName = characterResponse?.Profile?.Data?.UserInfo?.DisplayName ?? "",
                EmblemUrl = BungieUrl(lastPlayedCharacter?.EmblemPath)
            };
        }
        catch (ApiException ex) when (ex.IsNotFound())
        {
            logger.LogDebug(
                "Skipping profile details for missing encountered player {MembershipType}/{MembershipId}.",
                player.EncounteredMembershipType,
                player.EncounteredMembershipId);

            return new ReportPlayer
            {
                MembershipId = player.EncounteredMembershipId,
                MembershipType = player.EncounteredMembershipType
            };
        }
    }
}
