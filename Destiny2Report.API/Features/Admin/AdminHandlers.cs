using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

namespace Destiny2Report.API.Features.Admin;

public static class AdminHandlers
{
    private const string QueueFlushedError = "Removed from the crawl queue by an administrator.";
    private static readonly TimeSpan OverviewInterval = TimeSpan.FromSeconds(3);
    private static readonly string[] CrawlStatuses =
    [
        DestinyReport.CrawlStateQueued,
        DestinyReport.CrawlStateRunning,
        DestinyReport.CrawlStateCompleted,
        DestinyReport.CrawlStateFailed,
        DestinyReport.CrawlStatePrivate
    ];

    public static IResult StreamOverview(
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        return TypedResults.ServerSentEvents(StreamOverviewEvents(mongoDatabase, cancellationToken));
    }

    public static async Task<Ok<AdminMutationResponse>> FlushRedisQueue(
        IConnectionMultiplexer redis,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var queuedFilter = Builders<DestinyReport>.Filter.Eq(report => report.CrawlState, DestinyReport.CrawlStateQueued)
            & Builders<DestinyReport>.Filter.Eq(report => report.QueuedInRedis, true);
        var queuedPlayers = await reports.Find(queuedFilter)
            .Project(report => new QueuedPlayer(report.PlatformId, report.PlayerMembershipId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var redisDatabase = redis.GetDatabase();
        var update = Builders<DestinyReport>.Update
            .Set(report => report.CrawlState, DestinyReport.CrawlStateFailed)
            .Set(report => report.QueuedInRedis, false)
            .Set(report => report.CrawlError, QueueFlushedError)
            .Set(report => report.LeaseExpiresAtUtc, null)
            .Set(report => report.LeaseOwner, "");
        long affectedPlayers = 0;
        foreach (var player in queuedPlayers)
        {
            var playerFilter = queuedFilter
                & Builders<DestinyReport>.Filter.Eq(report => report.PlatformId, player.MembershipTypeId)
                & Builders<DestinyReport>.Filter.Eq(report => report.PlayerMembershipId, player.MembershipId);
            var updateResult = await reports.UpdateOneAsync(
                    playerFilter,
                    update,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (updateResult.ModifiedCount == 0)
            {
                // A worker claimed the entry after the initial read. Leave its stream state intact.
                continue;
            }

            affectedPlayers++;
            var statusKey = CrawlerQueue.JobStatusKey(player.MembershipTypeId, player.MembershipId);
            var streamEntryId = await redisDatabase.HashGetAsync(statusKey, "streamEntryId")
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (streamEntryId.HasValue)
            {
                await redisDatabase.StreamDeleteAsync(CrawlerQueue.StreamName, [streamEntryId])
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await redisDatabase.KeyDeleteAsync(statusKey)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return TypedResults.Ok(new AdminMutationResponse(
            affectedPlayers,
            $"Removed {affectedPlayers} queued player(s) from the Redis crawl queue."));
    }

    public static async Task<Ok<AdminMutationResponse>> FlushMongoQueue(
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var queuedFilter = Builders<DestinyReport>.Filter.Eq(report => report.CrawlState, DestinyReport.CrawlStateQueued)
            & Builders<DestinyReport>.Filter.Eq(report => report.QueuedInRedis, false);
        var update = Builders<DestinyReport>.Update
            .Set(report => report.CrawlState, DestinyReport.CrawlStateFailed)
            .Set(report => report.CrawlError, QueueFlushedError)
            .Set(report => report.LeaseExpiresAtUtc, null)
            .Set(report => report.LeaseOwner, "");
        var result = await reports.UpdateManyAsync(queuedFilter, update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new AdminMutationResponse(
            result.ModifiedCount,
            $"Removed {result.ModifiedCount} queued player(s) from the Mongo crawl queue."));
    }

    public static async Task<Results<Ok<AdminMutationResponse>, BadRequest<ProblemDetails>>> SetFullRecrawl(
        AdminFullRecrawlRequest request,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Invalid recrawl reason",
                Detail = "A reason between 1 and 500 characters is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var update = Builders<DestinyReport>.Update
            .Set(report => report.NeedsFullRecrawl, true)
            .Set(report => report.FullRecrawlReason, reason);
        var result = await reports.UpdateManyAsync(
                Builders<DestinyReport>.Filter.Empty,
                update,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new AdminMutationResponse(
            result.MatchedCount,
            $"Marked {result.MatchedCount} player(s) for a full recrawl."));
    }

    private static async IAsyncEnumerable<SseItem<AdminOverviewResponse>> StreamOverviewEvents(
        IMongoDatabase mongoDatabase,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var overview = await BuildOverviewAsync(mongoDatabase, cancellationToken).ConfigureAwait(false);
            yield return new SseItem<AdminOverviewResponse>(overview, "overview");
            await Task.Delay(OverviewInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AdminOverviewResponse> BuildOverviewAsync(
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var activeFilter = Builders<DestinyReport>.Filter.Eq(report => report.CrawlState, DestinyReport.CrawlStateRunning)
            & (Builders<DestinyReport>.Filter.Eq(report => report.QueuedInRedis, true)
                | Builders<DestinyReport>.Filter.Gt(report => report.LeaseExpiresAtUtc, now.UtcDateTime));

        var activeTask = reports.Find(activeFilter)
            .SortBy(report => report.StartedAtUtc)
            .Project(report => new ActiveCrawlProjection(
                report.PlatformId,
                report.PlayerMembershipId,
                report.DisplayName,
                report.QueuedAtUtc,
                report.StartedAtUtc,
                report.LeaseExpiresAtUtc,
                report.LeaseOwner,
                report.QueuedInRedis))
            .ToListAsync(cancellationToken);
        var countTask = reports.Aggregate()
            .Group(
                report => report.CrawlState,
                group => new CrawlStatusCount(group.Key, group.LongCount()))
            .ToListAsync(cancellationToken);

        await Task.WhenAll(activeTask, countTask).ConfigureAwait(false);

        var activeCrawls = activeTask.Result.Select(report => new AdminActiveCrawlResponse(
            report.MembershipTypeId,
            report.MembershipId,
            report.DisplayName,
            ToDateTimeOffset(report.QueuedAtUtc),
            ToDateTimeOffset(report.StartedAtUtc),
            ToDateTimeOffset(report.LeaseExpiresAtUtc),
            report.LeaseOwner,
            report.QueuedInRedis)).ToArray();

        var countsByStatus = countTask.Result
            .GroupBy(count => string.IsNullOrWhiteSpace(count.Status)
                ? DestinyReport.CrawlStateCompleted
                : count.Status,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);
        var statusCounts = CrawlStatuses
            .Select(status => new AdminQueueStatusCountResponse(
                status,
                countsByStatus.GetValueOrDefault(status)))
            .ToArray();

        return new AdminOverviewResponse(now, activeCrawls, statusCounts);
    }

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private sealed record QueuedPlayer(int MembershipTypeId, long MembershipId);

    private sealed record ActiveCrawlProjection(
        int MembershipTypeId,
        long MembershipId,
        string DisplayName,
        DateTime? QueuedAtUtc,
        DateTime? StartedAtUtc,
        DateTime? LeaseExpiresAtUtc,
        string LeaseOwner,
        bool QueuedInRedis);
}
