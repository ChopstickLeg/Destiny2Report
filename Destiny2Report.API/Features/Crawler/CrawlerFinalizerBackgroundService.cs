using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Leaderboards;
using Destiny2Report.API.Features.PushNotifications;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Destiny2Report.API.Features.Crawler;

public sealed class CrawlerFinalizerBackgroundService(
    ILogger<CrawlerFinalizerBackgroundService> logger,
    IServiceProvider serviceProvider,
    IMongoDatabase mongoDatabase,
    IConnectionMultiplexer redis,
    ICrawlGenerationStore generationStore,
    IReportPushNotificationService notifications) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private readonly string _owner = $"{Environment.MachineName}-{Environment.ProcessId}-finalizer-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await TryClaimAsync(stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }
                await FinalizeAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Crawler finalizer loop failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<CrawlJob?> TryClaimAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var filter = BuildClaimFilter(now);
        var update = Builders<CrawlJob>.Update
            .Set(item => item.FinalizerOwner, _owner)
            .Set(item => item.FinalizerLeaseExpiresAtUtc, now.Add(LeaseDuration))
            .Set(item => item.UpdatedAtUtc, now)
            .Inc(item => item.FinalizerFence, 1);
        return await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs")
            .FindOneAndUpdateAsync(
                filter,
                update,
                new FindOneAndUpdateOptions<CrawlJob>
                {
                    ReturnDocument = ReturnDocument.After,
                    Sort = Builders<CrawlJob>.Sort.Ascending(item => item.QueuedAtUtc)
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static FilterDefinition<CrawlJob> BuildClaimFilter(DateTime now)
    {
        var filters = Builders<CrawlJob>.Filter;
        var unowned = filters.Eq(item => item.FinalizerOwner, "")
            | filters.Exists(item => item.FinalizerOwner, false);
        var expired = filters.Lt(item => item.FinalizerLeaseExpiresAtUtc, now);
        return filters.Eq(item => item.State, CrawlJob.StateAwaitingFinalization)
            & (unowned | expired);
    }

    private async Task FinalizeAsync(CrawlJob job, CancellationToken cancellationToken)
    {
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewLeaseAsync(job, ownership);
        try
        {
            var (report, state) = await generationStore.MaterializeAsync(job, job.CandidateGeneration, ownership.Token)
                .ConfigureAwait(false);
            using var scope = serviceProvider.CreateScope();
            var crawler = scope.ServiceProvider.GetRequiredService<ICrawlerReadService>();
            var leaderboards = scope.ServiceProvider.GetRequiredService<ILeaderboardService>();

            var terminalState = report.CrawlState switch
            {
                DestinyReport.CrawlStatePrivate => CrawlJob.StatePrivate,
                DestinyReport.CrawlStateFailed => CrawlJob.StateFailed,
                _ => CrawlJob.StateCompleted
            };
            if (terminalState == CrawlJob.StateCompleted)
            {
                var metrics = await crawler.GetLeaderboardMetricsAsync(
                        job.MembershipTypeId,
                        job.MembershipId,
                        ownership.Token)
                    .ConfigureAwait(false);
                await leaderboards.PublishPlayerAsync(report, metrics, ownership.Token).ConfigureAwait(false);
            }
            else
            {
                await leaderboards.RemovePlayerAsync(job.MembershipTypeId, job.MembershipId, ownership.Token).ConfigureAwait(false);
            }

            if (!await TryPromoteAsync(job, terminalState, report.CrawlError, ownership.Token).ConfigureAwait(false))
            {
                logger.LogWarning("Finalizer {Owner} lost fence {Fence} for run {RunId}.", _owner, job.FinalizerFence, job.RunId);
                return;
            }

            if (terminalState == CrawlJob.StateCompleted && job.NotifiedRunId != job.RunId)
            {
                try
                {
                    await notifications.NotifyReportCompletedAsync(job.MembershipTypeId, job.MembershipId, ownership.Token).ConfigureAwait(false);
                    await MarkNotifiedAsync(job, ownership.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Notification failed for finalized crawler run {RunId}.", job.RunId);
                }
            }

            await PublishTerminalStatusAsync(job, terminalState, report.CrawlError).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Finalizing crawler run {RunId} failed; its lease will expire for retry.", job.RunId);
        }
        finally
        {
            ownership.Cancel();
            await renewal.ConfigureAwait(false);
        }
    }

    private async Task RenewLeaseAsync(CrawlJob job, CancellationTokenSource ownership)
    {
        using var timer = new PeriodicTimer(LeaseDuration / 3);
        try
        {
            while (await timer.WaitForNextTickAsync(ownership.Token).ConfigureAwait(false))
            {
                var result = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs").UpdateOneAsync(
                    FinalizerOwnershipFilter(job)
                    & Builders<CrawlJob>.Filter.Eq(item => item.State, CrawlJob.StateAwaitingFinalization),
                    Builders<CrawlJob>.Update
                        .Set(item => item.FinalizerLeaseExpiresAtUtc, DateTime.UtcNow.Add(LeaseDuration))
                        .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                    cancellationToken: ownership.Token).ConfigureAwait(false);
                if (result.ModifiedCount != 1)
                {
                    logger.LogWarning("Finalizer {Owner} lost lease fence {Fence} for run {RunId}.", _owner, job.FinalizerFence, job.RunId);
                    ownership.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryPromoteAsync(CrawlJob job, string terminalState, string? error, CancellationToken cancellationToken)
    {
        var filter = FinalizerOwnershipFilter(job)
            & Builders<CrawlJob>.Filter.Eq(item => item.State, CrawlJob.StateAwaitingFinalization)
            & Builders<CrawlJob>.Filter.Eq(item => item.CandidateGeneration, job.CandidateGeneration);
        var now = DateTime.UtcNow;
        var update = Builders<CrawlJob>.Update
            .Set(item => item.ActiveGeneration, job.CandidateGeneration)
            .Set(item => item.CandidateGeneration, "")
            .Set(item => item.State, terminalState)
            .Set(item => item.Error, error ?? "")
            .Set(item => item.FinalizerOwner, "")
            .Set(item => item.FinalizerLeaseExpiresAtUtc, null)
            .Set(item => item.UpdatedAtUtc, now)
            .Set(item => item.FinishedAtUtc, now);
        var result = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs")
            .UpdateOneAsync(filter, update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    private Task MarkNotifiedAsync(CrawlJob job, CancellationToken cancellationToken) =>
        mongoDatabase.GetCollection<CrawlJob>("crawl_jobs").UpdateOneAsync(
            item => item.PlayerKey == job.PlayerKey && item.RunId == job.RunId,
            Builders<CrawlJob>.Update.Set(item => item.NotifiedRunId, job.RunId),
            cancellationToken: cancellationToken);

    private static FilterDefinition<CrawlJob> FinalizerOwnershipFilter(CrawlJob job) =>
        Builders<CrawlJob>.Filter.Eq(item => item.PlayerKey, job.PlayerKey)
        & Builders<CrawlJob>.Filter.Eq(item => item.RunId, job.RunId)
        & Builders<CrawlJob>.Filter.Eq(item => item.FinalizerOwner, job.FinalizerOwner)
        & Builders<CrawlJob>.Filter.Eq(item => item.FinalizerFence, job.FinalizerFence);

    private async Task PublishTerminalStatusAsync(CrawlJob job, string state, string? error)
    {
        var database = redis.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var key = CrawlerQueue.JobStatusKey(job.MembershipTypeId, job.MembershipId);
        const string script = """
            local currentRun = redis.call('HGET', KEYS[1], 'runId')
            local currentFence = tonumber(redis.call('HGET', KEYS[1], 'fence') or '-1')
            if currentRun and currentRun ~= ARGV[1] then return 0 end
            if currentFence > tonumber(ARGV[2]) then return 0 end
            redis.call('HSET', KEYS[1], 'runId', ARGV[1], 'fence', ARGV[2],
                'status', ARGV[3], 'error', ARGV[4], 'updatedAtUtc', ARGV[5])
            return 1
            """;
        var accepted = (long)await database.ScriptEvaluateAsync(
                script,
                [key],
                [job.RunId, job.Fence, state, error ?? "", now.ToString("O")])
            .ConfigureAwait(false);
        if (accepted != 1)
        {
            logger.LogInformation("Skipped stale terminal Redis event for crawler run {RunId} fence {Fence}.", job.RunId, job.Fence);
            return;
        }
        await database.KeyExpireAsync(key, CrawlerQueue.TerminalJobStatusTtl).ConfigureAwait(false);
        await database.PublishAsync(
            RedisChannel.Literal(CrawlerQueue.EventsChannelName),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                MembershipTypeId = job.MembershipTypeId,
                MembershipId = job.MembershipId,
                Status = state,
                StreamEntryId = job.StreamEntryId,
                Error = error,
                UpdatedAtUtc = now,
                Progress = (CrawlProgressSnapshot?)null
            })).ConfigureAwait(false);
    }
}
