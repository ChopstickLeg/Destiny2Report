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

    public static async Task<IResult> QueueCrawls(
        IReadOnlyList<AdminCrawlerQueueItem> requests,
        ICrawlerJobQueue queue,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return TypedResults.BadRequest(new ProblemDetails { Title = "Missing crawl requests", Status = StatusCodes.Status400BadRequest });
        }

        var responses = new List<Destiny2Report.API.Features.Reports.ReportQueueResponse>(requests.Count);
        foreach (var request in requests)
        {
            if (request.MembershipTypeId <= 0 || request.MembershipId <= 0)
            {
                return TypedResults.BadRequest(new ProblemDetails { Title = "Invalid membership", Status = StatusCodes.Status400BadRequest });
            }

            responses.Add(await queue.EnqueueAsync(
                    request.MembershipTypeId,
                    request.MembershipId,
                    request.ForceFullCrawl,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return TypedResults.Accepted<IReadOnlyList<Destiny2Report.API.Features.Reports.ReportQueueResponse>>((string?)null, responses);
    }

    public static async Task<Ok<AdminMutationResponse>> FlushRedisQueue(
        IConnectionMultiplexer redis,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        var jobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
        var queuedFilter = Builders<CrawlJob>.Filter.Eq(job => job.State, CrawlJob.StateQueued)
            & Builders<CrawlJob>.Filter.Eq(job => job.DispatchedToRedis, true);
        var queuedPlayers = await jobs.Find(queuedFilter)
            .Project(job => new QueuedPlayer(job.MembershipTypeId, job.MembershipId, job.RunId, job.StreamEntryId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var redisDatabase = redis.GetDatabase();
        var update = Builders<CrawlJob>.Update
            .Set(job => job.State, CrawlJob.StateFailed)
            .Set(job => job.DispatchedToRedis, false)
            .Set(job => job.Error, QueueFlushedError)
            .Set(job => job.LeaseExpiresAtUtc, null)
            .Set(job => job.LeaseOwner, "")
            .Set(job => job.UpdatedAtUtc, DateTime.UtcNow);
        long affectedPlayers = 0;
        foreach (var player in queuedPlayers)
        {
            var playerFilter = queuedFilter
                & Builders<CrawlJob>.Filter.Eq(job => job.PlayerKey, CrawlJob.CreatePlayerKey(player.MembershipTypeId, player.MembershipId))
                & Builders<CrawlJob>.Filter.Eq(job => job.RunId, player.RunId);
            var updateResult = await jobs.UpdateOneAsync(
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
            if (!string.IsNullOrWhiteSpace(player.StreamEntryId))
            {
                await redisDatabase.StreamDeleteAsync(CrawlerQueue.StreamName, [player.StreamEntryId])
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
        var jobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
        var queuedFilter = Builders<CrawlJob>.Filter.Eq(job => job.State, CrawlJob.StateQueued)
            & Builders<CrawlJob>.Filter.Eq(job => job.DispatchedToRedis, false);
        var update = Builders<CrawlJob>.Update
            .Set(job => job.State, CrawlJob.StateFailed)
            .Set(job => job.Error, QueueFlushedError)
            .Set(job => job.LeaseExpiresAtUtc, null)
            .Set(job => job.LeaseOwner, "")
            .Set(job => job.UpdatedAtUtc, DateTime.UtcNow);
        var result = await jobs.UpdateManyAsync(queuedFilter, update, cancellationToken: cancellationToken)
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
        var jobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
        var activeFilter = Builders<CrawlJob>.Filter.In(job => job.State,
            [CrawlJob.StateQueued, CrawlJob.StateRunning, CrawlJob.StateAwaitingFinalization]);

        var activeTask = jobs.Find(activeFilter)
            .SortBy(job => job.StartedAtUtc)
            .Project(job => new ActiveCrawlProjection(
                job.MembershipTypeId,
                job.MembershipId,
                job.DisplayName,
                job.QueuedAtUtc,
                job.StartedAtUtc,
                job.LeaseExpiresAtUtc,
                job.LeaseOwner,
                job.DispatchedToRedis,
                job.RunId,
                job.Fence))
            .ToListAsync(cancellationToken);
        var countTask = jobs.Aggregate()
            .Group(
                job => job.State,
                group => new CrawlStatusCount(group.Key, group.LongCount()))
            .ToListAsync(cancellationToken);

        await Task.WhenAll(activeTask, countTask).ConfigureAwait(false);

        var activeReports = activeTask.Result;
        var displayNames = await LoadDisplayNamesAsync(
                mongoDatabase,
                activeReports,
                cancellationToken)
            .ConfigureAwait(false);
        var activeCrawls = activeReports.Select(report => new AdminActiveCrawlResponse(
            report.MembershipTypeId,
            report.MembershipId,
            string.IsNullOrWhiteSpace(report.DisplayName)
                ? displayNames.GetValueOrDefault((report.MembershipTypeId, report.MembershipId), "")
                : report.DisplayName,
            ToDateTimeOffset(report.QueuedAtUtc),
            ToDateTimeOffset(report.StartedAtUtc),
            ToDateTimeOffset(report.LeaseExpiresAtUtc),
            report.LeaseOwner ?? "",
            report.QueuedInRedis,
            report.RunId,
            report.Fence)).ToArray();

        var countsByStatus = countTask.Result
            .GroupBy(count => count.Status == CrawlJob.StateAwaitingFinalization
                ? CrawlJob.StateRunning
                : string.IsNullOrWhiteSpace(count.Status) ? DestinyReport.CrawlStateCompleted : count.Status,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);
        var statusCounts = CrawlStatuses
            .Select(status => new AdminQueueStatusCountResponse(
                status,
                countsByStatus.GetValueOrDefault(status)))
            .ToArray();

        return new AdminOverviewResponse(now, activeCrawls, statusCounts);
    }

    private static async Task<IReadOnlyDictionary<(int MembershipTypeId, long MembershipId), string>> LoadDisplayNamesAsync(
        IMongoDatabase mongoDatabase,
        IReadOnlyCollection<ActiveCrawlProjection> activeCrawls,
        CancellationToken cancellationToken)
    {
        if (activeCrawls.Count == 0)
        {
            return new Dictionary<(int, long), string>();
        }

        var membershipTypeIds = activeCrawls.Select(crawl => crawl.MembershipTypeId).Distinct();
        var membershipIds = activeCrawls.Select(crawl => crawl.MembershipId).Distinct();
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.In(report => report.PlatformId, membershipTypeIds)
            & Builders<DestinyReport>.Filter.In(report => report.PlayerMembershipId, membershipIds);
        var names = await reports.Find(filter)
            .Project(report => new PlayerDisplayNameProjection(
                report.PlatformId,
                report.PlayerMembershipId,
                report.DisplayName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return names
            .Where(player => !string.IsNullOrWhiteSpace(player.DisplayName))
            .ToDictionary(
                player => (player.MembershipTypeId, player.MembershipId),
                player => player.DisplayName);
    }

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private sealed record QueuedPlayer(int MembershipTypeId, long MembershipId, string RunId, string StreamEntryId);

    private sealed record PlayerDisplayNameProjection(
        int MembershipTypeId,
        long MembershipId,
        string DisplayName);

    private sealed record ActiveCrawlProjection(
        int MembershipTypeId,
        long MembershipId,
        string? DisplayName,
        DateTime? QueuedAtUtc,
        DateTime? StartedAtUtc,
        DateTime? LeaseExpiresAtUtc,
        string? LeaseOwner,
        bool QueuedInRedis,
        string RunId,
        long Fence);
}
