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
}

public sealed class CrawlerJobQueue(
    IMongoDatabase mongoDatabase,
    IConnectionMultiplexer redis,
    ILogger<CrawlerJobQueue> logger) : ICrawlerJobQueue
{
    internal const string DispatchStatusScript = """
        local currentRun = redis.call('HGET', KEYS[1], 'runId')
        local currentFence = tonumber(redis.call('HGET', KEYS[1], 'fence') or '-1')
        if currentRun == ARGV[1] and currentFence > tonumber(ARGV[2]) then return 0 end
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

        if (job is null || job.RunId != runId)
        {
            job = await jobs.Find(item => item.PlayerKey == playerKey)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
            return ToResponse(job);
        }

        try
        {
            await DispatchAsync(job, cancellationToken).ConfigureAwait(false);
            job = await jobs.Find(item => item.PlayerKey == playerKey)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Crawler run {RunId} for {MembershipType}/{MembershipId} is durable in Mongo but could not be dispatched to Redis.",
                runId,
                membershipTypeId,
                membershipId);
        }

        return ToResponse(job);
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
        var streamId = await database.StreamAddAsync(
                CrawlerQueue.StreamName,
                [
                    new NameValueEntry("protocolVersion", job.ProtocolVersion),
                    new NameValueEntry("runId", job.RunId),
                    new NameValueEntry("membershipTypeId", job.MembershipTypeId),
                    new NameValueEntry("membershipId", job.MembershipId),
                    new NameValueEntry("queuedAtUtc", new DateTimeOffset(job.QueuedAtUtc, TimeSpan.Zero).ToString("O")),
                    new NameValueEntry("forceFullCrawl", job.ForceFullCrawl ? "1" : "0")
                ])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var jobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
        var filter = Builders<CrawlJob>.Filter.Eq(item => item.PlayerKey, job.PlayerKey)
            & Builders<CrawlJob>.Filter.Eq(item => item.RunId, job.RunId);
        var update = Builders<CrawlJob>.Update
            .Set(item => item.DispatchedToRedis, true)
            .Set(item => item.StreamEntryId, streamId.ToString())
            .Set(item => item.UpdatedAtUtc, DateTime.UtcNow);
        var result = await jobs.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.MatchedCount == 0)
        {
            await database.StreamDeleteAsync(CrawlerQueue.StreamName, [streamId]).WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var statusKey = CrawlerQueue.JobStatusKey(job.MembershipTypeId, job.MembershipId);
        var queuedAtUtc = new DateTimeOffset(job.QueuedAtUtc, TimeSpan.Zero).ToString("O");
        var initialized = (long)await database.ScriptEvaluateAsync(
                DispatchStatusScript,
                [statusKey],
                [
                    job.RunId,
                    job.Fence,
                    job.ProtocolVersion,
                    job.MembershipTypeId,
                    job.MembershipId,
                    streamId.ToString(),
                    queuedAtUtc,
                    DateTimeOffset.UtcNow.ToString("O"),
                    (long)CrawlerQueue.ActiveJobStatusTtl.TotalSeconds
                ])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (initialized == 0)
        {
            logger.LogDebug(
                "Skipped stale queued status initialization for crawler run {RunId}; a worker has already advanced its fence.",
                job.RunId);
        }
    }

    private static ReportQueueResponse ToResponse(CrawlJob job) => new(
        job.RunId,
        job.MembershipTypeId,
        job.MembershipId,
        job.State,
        new DateTimeOffset(DateTime.SpecifyKind(job.QueuedAtUtc, DateTimeKind.Utc)));
}
