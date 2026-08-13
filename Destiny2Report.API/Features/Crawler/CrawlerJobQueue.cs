using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Reports;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Destiny2Report.API.Features.Crawler;

public interface ICrawlerJobQueue
{
    Task<ReportQueueResponse> EnqueueAsync(
        int membershipTypeId,
        long membershipId,
        bool forceFullCrawl,
        CancellationToken cancellationToken);

    Task<ReportQueueResponse> EnqueuePriorityAsync(
        int membershipTypeId,
        long membershipId,
        bool forceFullCrawl,
        CancellationToken cancellationToken);

    Task<CrawlerJobEnqueueResult> EnqueueTrackedAsync(
        int membershipTypeId,
        long membershipId,
        bool forceFullCrawl,
        CancellationToken cancellationToken);
}

public sealed record CrawlerJobEnqueueResult(ReportQueueResponse Response, bool CreatedNewJob);

public sealed class CrawlerJobQueue(
    IMongoDatabase mongoDatabase,
    IConnectionMultiplexer redis,
    ILogger<CrawlerJobQueue> logger) : ICrawlerJobQueue
{
    internal const string DispatchStatusScript = """
        local currentRun = redis.call('HGET', KEYS[1], 'runId')
        local currentFence = tonumber(redis.call('HGET', KEYS[1], 'fence') or '-1')
        if currentRun == ARGV[1] and currentFence > tonumber(ARGV[2]) then
            redis.call('HSET', KEYS[1],
                'protocolVersion', ARGV[3],
                'membershipTypeId', ARGV[4],
                'membershipId', ARGV[5],
                'streamEntryId', ARGV[6])
            redis.call('EXPIRE', KEYS[1], ARGV[9])
            return 0
        end
        redis.call('HSET', KEYS[1],
            'runId', ARGV[1],
            'fence', ARGV[2],
            'protocolVersion', ARGV[3],
            'membershipTypeId', ARGV[4],
            'membershipId', ARGV[5],
            'streamEntryId', ARGV[6],
            'status', 'queued',
            'queuedAtUtc', ARGV[7],
            'updatedAtUtc', ARGV[8],
            'error', '',
            'progressPhase', '',
            'progressLabel', '',
            'progressCurrent', '',
            'progressTotal', '',
            'progressStartedAtUtc', '',
            'progressUpdatedAtUtc', '')
        redis.call('EXPIRE', KEYS[1], ARGV[9])
        return 1
        """;

    private static readonly string[] ActiveStates =
    [
        CrawlJob.StateQueued,
        CrawlJob.StateRunning,
        CrawlJob.StateAwaitingFinalization
    ];

    public async Task<ReportQueueResponse> EnqueueAsync(
        int membershipTypeId,
        long membershipId,
        bool forceFullCrawl,
        CancellationToken cancellationToken) =>
        (await EnqueueInternalAsync(
            membershipTypeId,
            membershipId,
            forceFullCrawl,
            priority: false,
            cancellationToken).ConfigureAwait(false)).Response;

    public async Task<ReportQueueResponse> EnqueuePriorityAsync(
        int membershipTypeId,
        long membershipId,
        bool forceFullCrawl,
        CancellationToken cancellationToken) =>
        (await EnqueueInternalAsync(
            membershipTypeId,
            membershipId,
            forceFullCrawl,
            priority: true,
            cancellationToken).ConfigureAwait(false)).Response;

    public async Task<CrawlerJobEnqueueResult> EnqueueTrackedAsync(
        int membershipTypeId,
        long membershipId,
        bool forceFullCrawl,
        CancellationToken cancellationToken) =>
        await EnqueueInternalAsync(
            membershipTypeId,
            membershipId,
            forceFullCrawl,
            priority: false,
            cancellationToken).ConfigureAwait(false);

    private async Task<CrawlerJobEnqueueResult> EnqueueInternalAsync(
        int membershipTypeId,
        long membershipId,
        bool forceFullCrawl,
        bool priority,
        CancellationToken cancellationToken)
    {
        var jobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
        var playerKey = CrawlJob.CreatePlayerKey(membershipTypeId, membershipId);
        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        if (!forceFullCrawl)
        {
            forceFullCrawl = await mongoDatabase.GetCollection<DestinyReport>("destiny_reports")
                .Find(BuildFullRecrawlFilter(membershipTypeId, membershipId))
                .Limit(1)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        var admissionFilter = Builders<CrawlJob>.Filter.Eq(job => job.PlayerKey, playerKey)
            & Builders<CrawlJob>.Filter.Nin(job => job.State, ActiveStates);
        var admissionUpdate = Builders<CrawlJob>.Update
            .SetOnInsert(job => job.PlayerKey, playerKey)
            .SetOnInsert(job => job.MembershipTypeId, membershipTypeId)
            .SetOnInsert(job => job.MembershipId, membershipId)
            .Set(job => job.ProtocolVersion, CrawlerQueue.ProtocolVersion)
            .Set(job => job.RunId, runId)
            .Set(job => job.State, CrawlJob.StateQueued)
            .Set(job => job.DispatchedToRedis, false)
            .Set(job => job.StreamEntryId, "")
            .Set(job => job.IsPriority, priority)
            .Set(job => job.LeaseOwner, "")
            .Set(job => job.LeaseExpiresAtUtc, null)
            .Set(job => job.QueuedAtUtc, now)
            .Set(job => job.StartedAtUtc, null)
            .Set(job => job.UpdatedAtUtc, now)
            .Set(job => job.FinishedAtUtc, null)
            .Set(job => job.ForceFullCrawl, forceFullCrawl)
            .Set(job => job.Error, "")
            .Set(job => job.CandidateGeneration, "")
            .Set(job => job.FinalizerOwner, "")
            .Set(job => job.FinalizerLeaseExpiresAtUtc, null)
            .Set(job => job.FinalizerFence, 0);

        CrawlJob? job;
        try
        {
            job = await jobs.FindOneAndUpdateAsync(
                    admissionFilter,
                    admissionUpdate,
                    new FindOneAndUpdateOptions<CrawlJob>
                    {
                        IsUpsert = true,
                        ReturnDocument = ReturnDocument.After
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            job = null;
        }
        catch (MongoCommandException exception) when (IsDuplicateKeyCommand(exception.Code))
        {
            // findAndModify reports an upsert collision as a command error rather
            // than a MongoWriteException. The collision means another active job
            // already owns this player's deterministic _id, so return that job.
            job = null;
        }

        var createdNewJob = WasCreatedByRun(job, runId);
        if (!createdNewJob)
        {
            job = await jobs.Find(item => item.PlayerKey == playerKey)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
            if (priority && job.State == CrawlJob.StateQueued && !job.IsPriority)
            {
                job = await PromoteAsync(job, forceFullCrawl, cancellationToken).ConfigureAwait(false);
            }
            else if (priority && forceFullCrawl && job.State == CrawlJob.StateQueued && !job.ForceFullCrawl)
            {
                await jobs.UpdateOneAsync(
                        item => item.PlayerKey == playerKey && item.RunId == job.RunId && item.State == CrawlJob.StateQueued,
                        Builders<CrawlJob>.Update
                            .Set(item => item.ForceFullCrawl, true)
                            .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                job = await jobs.Find(item => item.PlayerKey == playerKey)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Once Mongo admission succeeds, the durable job must outlive request cancellation.
        // Dispatch is best-effort and already degrades to the Mongo-backed scheduler on failure.
        job = await DispatchIfNeededAsync(jobs, job!, CancellationToken.None).ConfigureAwait(false);
        return new CrawlerJobEnqueueResult(ToResponse(job), createdNewJob);
    }

    internal static bool WasCreatedByRun(CrawlJob? job, string runId) =>
        job is not null && job.RunId == runId;

    private async Task<CrawlJob> DispatchIfNeededAsync(
        IMongoCollection<CrawlJob> jobs,
        CrawlJob job,
        CancellationToken cancellationToken)
    {
        if (job.State != CrawlJob.StateQueued || job.DispatchedToRedis)
        {
            return job;
        }

        try
        {
            await DispatchAsync(job, cancellationToken).ConfigureAwait(false);
            job = await jobs.Find(item => item.PlayerKey == job.PlayerKey)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Crawler run {RunId} for {MembershipType}/{MembershipId} is durable in Mongo but could not be dispatched to Redis.",
                job.RunId,
                job.MembershipTypeId,
                job.MembershipId);
        }

        return job;
    }

    internal static bool IsDuplicateKeyCommand(int errorCode) => errorCode is 11000 or 11001;

    internal static FilterDefinition<DestinyReport> BuildFullRecrawlFilter(
        int membershipTypeId,
        long membershipId) =>
        Builders<DestinyReport>.Filter.Eq(report => report.PlatformId, membershipTypeId)
        & Builders<DestinyReport>.Filter.Eq(report => report.PlayerMembershipId, membershipId)
        & Builders<DestinyReport>.Filter.Eq(report => report.NeedsFullRecrawl, true);

    private async Task DispatchAsync(CrawlJob job, CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        var streamName = CrawlerQueue.StreamNameFor(job.IsPriority);
        var streamId = await AddStreamEntryAsync(database, streamName, job)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var ownershipEstablished = false;
        try
        {
            var jobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
            var update = Builders<CrawlJob>.Update
                .Set(item => item.DispatchedToRedis, true)
                .Set(item => item.StreamEntryId, streamId.ToString())
                .Set(item => item.UpdatedAtUtc, DateTime.UtcNow);
            var result = await jobs.UpdateOneAsync(
                    BuildDispatchOwnershipFilter(job),
                    update,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            if (result.MatchedCount == 0)
            {
                return;
            }

            ownershipEstablished = true;
            await InitializeStatusAsync(database, job, streamId).ConfigureAwait(false);
        }
        finally
        {
            if (!ownershipEstablished)
            {
                await DeleteUnownedEntryAsync(database, streamName, streamId, job.RunId).ConfigureAwait(false);
            }
        }
    }

    internal static FilterDefinition<CrawlJob> BuildDispatchOwnershipFilter(CrawlJob job) =>
        Builders<CrawlJob>.Filter.Eq(item => item.PlayerKey, job.PlayerKey)
        & Builders<CrawlJob>.Filter.Eq(item => item.RunId, job.RunId)
        & Builders<CrawlJob>.Filter.Eq(item => item.State, CrawlJob.StateQueued)
        & Builders<CrawlJob>.Filter.Eq(item => item.DispatchedToRedis, false);

    private async Task<CrawlJob> PromoteAsync(
        CrawlJob job,
        bool forceFullCrawl,
        CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        job.ForceFullCrawl |= forceFullCrawl;
        var priorityStreamId = await AddStreamEntryAsync(database, CrawlerQueue.PriorityStreamName, job)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var ownershipEstablished = false;
        try
        {
            var jobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
            var filter = Builders<CrawlJob>.Filter.Eq(item => item.PlayerKey, job.PlayerKey)
                & Builders<CrawlJob>.Filter.Eq(item => item.RunId, job.RunId)
                & Builders<CrawlJob>.Filter.Eq(item => item.State, CrawlJob.StateQueued)
                & BuildNormalPriorityFilter();
            var update = Builders<CrawlJob>.Update
                .Set(item => item.IsPriority, true)
                .Set(item => item.DispatchedToRedis, true)
                .Set(item => item.StreamEntryId, priorityStreamId.ToString())
                .Set(item => item.ForceFullCrawl, job.ForceFullCrawl)
                .Set(item => item.UpdatedAtUtc, DateTime.UtcNow);
            var promoted = await jobs.UpdateOneAsync(filter, update, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            if (promoted.ModifiedCount == 0)
            {
                return await jobs.Find(item => item.PlayerKey == job.PlayerKey)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            ownershipEstablished = true;
            if (!string.IsNullOrWhiteSpace(job.StreamEntryId))
            {
                await database.StreamDeleteAsync(CrawlerQueue.StreamName, [job.StreamEntryId])
                    .ConfigureAwait(false);
            }

            var promotedJob = await jobs.Find(item => item.PlayerKey == job.PlayerKey)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
            await InitializeStatusAsync(database, promotedJob, priorityStreamId).ConfigureAwait(false);
            return promotedJob;
        }
        finally
        {
            if (!ownershipEstablished)
            {
                await DeleteUnownedEntryAsync(
                        database,
                        CrawlerQueue.PriorityStreamName,
                        priorityStreamId,
                        job.RunId)
                    .ConfigureAwait(false);
            }
        }
    }

    private static Task<RedisValue> AddStreamEntryAsync(IDatabase database, string streamName, CrawlJob job) =>
        database.StreamAddAsync(
            streamName,
            [
                new NameValueEntry("protocolVersion", job.ProtocolVersion),
                new NameValueEntry("runId", job.RunId),
                new NameValueEntry("membershipTypeId", job.MembershipTypeId),
                new NameValueEntry("membershipId", job.MembershipId),
                new NameValueEntry("queuedAtUtc", new DateTimeOffset(job.QueuedAtUtc, TimeSpan.Zero).ToString("O")),
                new NameValueEntry("forceFullCrawl", job.ForceFullCrawl ? "1" : "0")
            ]);

    private async Task InitializeStatusAsync(IDatabase database, CrawlJob job, RedisValue streamId)
    {
        var statusKey = CrawlerQueue.JobStatusKey(job.MembershipTypeId, job.MembershipId);
        var queuedAtUtc = new DateTimeOffset(job.QueuedAtUtc, TimeSpan.Zero).ToString("O");
        await database.ScriptEvaluateAsync(
                DispatchStatusScript,
                [statusKey],
                [
                    job.RunId,
                    job.Fence,
                    job.ProtocolVersion,
                    job.MembershipTypeId,
                    job.MembershipId,
                    streamId,
                    queuedAtUtc,
                    DateTimeOffset.UtcNow.ToString("O"),
                    (long)CrawlerQueue.ActiveJobStatusTtl.TotalSeconds
                ])
            .ConfigureAwait(false);
    }

    private async Task DeleteUnownedEntryAsync(
        IDatabase database,
        string streamName,
        RedisValue streamId,
        string runId)
    {
        try
        {
            await database.StreamDeleteAsync(streamName, [streamId]).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not remove unowned Redis stream entry {StreamEntryId} for crawler run {RunId}.",
                streamId,
                runId);
        }
    }

    private static ReportQueueResponse ToResponse(CrawlJob job) => new(
        job.RunId,
        job.MembershipTypeId,
        job.MembershipId,
        job.State,
        new DateTimeOffset(DateTime.SpecifyKind(job.QueuedAtUtc, DateTimeKind.Utc)));

    internal static FilterDefinition<CrawlJob> BuildNormalPriorityFilter()
    {
        var filters = Builders<CrawlJob>.Filter;
        return filters.Eq(item => item.IsPriority, false)
            | filters.Exists(item => item.IsPriority, false);
    }
}
