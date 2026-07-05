using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;

namespace Destiny2Report.API.Features.Crawler;

public class CrawlerBackgroundJob : BackgroundService
{
    private const int ReadBatchSize = 1;
    private static readonly TimeSpan PendingMessageIdleTimeout = TimeSpan.FromMinutes(1);

    private readonly ILogger<CrawlerBackgroundJob> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly IMongoDatabase _mongoDatabase;
    private readonly string _consumerName = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    public CrawlerBackgroundJob(
        ILogger<CrawlerBackgroundJob> logger,
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        IMongoDatabase mongoDatabase)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _redis = redis;
        _mongoDatabase = mongoDatabase;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Crawler background job is starting.");

        var redisDatabase = _redis.GetDatabase();
        await EnsureConsumerGroupAsync(redisDatabase).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recoveredEntries = await ClaimStalePendingEntriesAsync(redisDatabase, stoppingToken).ConfigureAwait(false);
                if (recoveredEntries.Length > 0)
                {
                    foreach (var entry in recoveredEntries)
                    {
                        await ProcessEntryAsync(redisDatabase, entry, stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var entries = await redisDatabase.StreamReadGroupAsync(
                        CrawlerQueue.StreamName,
                        CrawlerQueue.ConsumerGroupName,
                        _consumerName,
                        ">",
                        count: ReadBatchSize)
                    .WaitAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (entries.Length == 0)
                {
                    var backgroundJob = await ClaimBackgroundJobAsync(stoppingToken).ConfigureAwait(false);
                    if (backgroundJob is not null)
                    {
                        await ProcessBackgroundJobAsync(backgroundJob, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var entry in entries)
                {
                    await ProcessEntryAsync(redisDatabase, entry, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while running the crawler.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Crawler background job is stopping.");
    }

    private async Task EnsureConsumerGroupAsync(IDatabase redisDatabase)
    {
        try
        {
            await redisDatabase.StreamCreateConsumerGroupAsync(
                    CrawlerQueue.StreamName,
                    CrawlerQueue.ConsumerGroupName,
                    "0-0",
                    createStream: true)
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Redis stream consumer group {ConsumerGroupName} already exists.", CrawlerQueue.ConsumerGroupName);
        }
    }

    private async Task<StreamEntry[]> ClaimStalePendingEntriesAsync(IDatabase redisDatabase, CancellationToken stoppingToken)
    {
        var result = await redisDatabase.StreamAutoClaimAsync(
                CrawlerQueue.StreamName,
                CrawlerQueue.ConsumerGroupName,
                _consumerName,
                (long)PendingMessageIdleTimeout.TotalMilliseconds,
                "0-0",
                ReadBatchSize)
            .WaitAsync(stoppingToken)
            .ConfigureAwait(false);

        if (result.ClaimedEntries.Length > 0)
        {
            _logger.LogInformation(
                "Claimed {EntryCount} stale pending crawler stream entries.",
                result.ClaimedEntries.Length);
        }

        return result.ClaimedEntries;
    }

    private async Task ProcessEntryAsync(IDatabase redisDatabase, StreamEntry entry, CancellationToken stoppingToken)
    {
        if (!TryReadCrawlerJob(entry, out var membershipTypeId, out var membershipId))
        {
            _logger.LogWarning("Acknowledging malformed crawler stream entry {EntryId}.", entry.Id);
            await redisDatabase.StreamAcknowledgeAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, entry.Id)
                .ConfigureAwait(false);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var crawlerService = scope.ServiceProvider.GetRequiredService<ICrawlerService>();
        using var activity = AppTelemetry.ActivitySource.StartActivity("crawler.player.process", ActivityKind.Consumer);

        activity?.SetTag("destiny.membership_type_id", membershipTypeId);
        activity?.SetTag("destiny.membership_id", membershipId);
        activity?.SetTag("messaging.system", "redis");
        activity?.SetTag("messaging.destination.name", CrawlerQueue.StreamName);
        activity?.SetTag("messaging.message.id", entry.Id.ToString());

        await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, DestinyReport.CrawlStateRunning, null)
            .ConfigureAwait(false);
        await UpdateReportCrawlStateAsync(membershipTypeId, membershipId, DestinyReport.CrawlStateRunning, queuedInRedis: true, null, stoppingToken)
            .ConfigureAwait(false);

        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await crawlerService.CrawlAsync(membershipTypeId, membershipId, stoppingToken).ConfigureAwait(false);
                await redisDatabase.StreamAcknowledgeAndDeleteAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, StreamTrimMode.DeleteReferences, entry.Id)
                    .ConfigureAwait(false);
                await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, DestinyReport.CrawlStateCompleted, null)
                    .ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogInformation("Completed crawler stream entry {EntryId}.", entry.Id);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Crawler stream entry {EntryId} failed on attempt {Attempt}; retrying once immediately.",
                    entry.Id,
                    attempt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, DestinyReport.CrawlStateFailed, ex.Message)
                    .ConfigureAwait(false);
                await redisDatabase.StreamAcknowledgeAndDeleteAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, StreamTrimMode.DeleteReferences, entry.Id)
                    .ConfigureAwait(false);
                await UpdateReportCrawlStateAsync(membershipTypeId, membershipId, DestinyReport.CrawlStateFailed, queuedInRedis: false, ex.Message, stoppingToken)
                    .ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                _logger.LogError(
                    ex,
                    "Crawler stream entry {EntryId} failed after {AttemptCount} attempts.",
                    entry.Id,
                    maxAttempts);
                return;
            }
        }

    }

    private async Task<DestinyReport?> ClaimBackgroundJobAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        var reports = _mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(report => report.CrawlState, DestinyReport.CrawlStateQueued)
            & Builders<DestinyReport>.Filter.Eq(report => report.QueuedInRedis, false);
        var update = Builders<DestinyReport>.Update
            .Set(report => report.CrawlState, DestinyReport.CrawlStateRunning)
            .Set(report => report.StartedAtUtc, now)
            .Set(report => report.CrawlError, "");
        var options = new FindOneAndUpdateOptions<DestinyReport>
        {
            Sort = Builders<DestinyReport>.Sort.Ascending(report => report.QueuedAtUtc),
            ReturnDocument = ReturnDocument.After
        };

        return await reports.FindOneAndUpdateAsync(filter, update, options, stoppingToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessBackgroundJobAsync(DestinyReport job, CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var crawlerService = scope.ServiceProvider.GetRequiredService<ICrawlerService>();
        using var activity = AppTelemetry.ActivitySource.StartActivity("crawler.player.background_process", ActivityKind.Consumer);

        activity?.SetTag("destiny.membership_type_id", job.PlatformId);
        activity?.SetTag("destiny.membership_id", job.PlayerMembershipId);
        activity?.SetTag("messaging.system", "mongodb");
        activity?.SetTag("messaging.destination.name", "destiny_reports");

        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await crawlerService.CrawlAsync(job.PlatformId, job.PlayerMembershipId, stoppingToken).ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogInformation(
                    "Completed background crawler job for membership {MembershipType}/{MembershipId}.",
                    job.PlatformId,
                    job.PlayerMembershipId);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Background crawler job for membership {MembershipType}/{MembershipId} failed on attempt {Attempt}; retrying once immediately.",
                    job.PlatformId,
                    job.PlayerMembershipId,
                    attempt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await UpdateReportCrawlStateAsync(job.PlatformId, job.PlayerMembershipId, DestinyReport.CrawlStateFailed, queuedInRedis: false, ex.Message, stoppingToken)
                    .ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                _logger.LogError(
                    ex,
                    "Background crawler job for membership {MembershipType}/{MembershipId} failed after {AttemptCount} attempts.",
                    job.PlatformId,
                    job.PlayerMembershipId,
                    maxAttempts);
                return;
            }
        }
    }

    private async Task UpdateReportCrawlStateAsync(
        int membershipTypeId,
        long membershipId,
        string crawlState,
        bool queuedInRedis,
        string? error,
        CancellationToken cancellationToken)
    {
        var reports = _mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(report => report.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(report => report.PlayerMembershipId, membershipId);
        var update = Builders<DestinyReport>.Update
            .Set(report => report.CrawlState, crawlState)
            .Set(report => report.QueuedInRedis, queuedInRedis)
            .Set(report => report.CrawlError, error ?? "");

        if (crawlState == DestinyReport.CrawlStateRunning)
        {
            update = update.Set(report => report.StartedAtUtc, DateTimeOffset.UtcNow);
        }

        await reports.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateJobStatusAsync(
        IDatabase redisDatabase,
        int membershipTypeId,
        long membershipId,
        RedisValue streamEntryId,
        string status,
        string? error)
    {
        var updatedAtUtc = DateTimeOffset.UtcNow;
        var statusKey = CrawlerQueue.JobStatusKey(membershipTypeId, membershipId);

        await redisDatabase.HashSetAsync(
                statusKey,
                [
                    new HashEntry("membershipTypeId", membershipTypeId),
                    new HashEntry("membershipId", membershipId),
                    new HashEntry("streamEntryId", streamEntryId),
                    new HashEntry("status", status),
                    new HashEntry("updatedAtUtc", updatedAtUtc.ToString("O")),
                    new HashEntry("error", error ?? "")
                ])
            .ConfigureAwait(false);

        if (status is "completed" or "failed")
        {
            await redisDatabase.KeyExpireAsync(statusKey, TimeSpan.FromHours(6)).ConfigureAwait(false);
        }

        var jobEvent = new
        {
            MembershipTypeId = membershipTypeId,
            MembershipId = membershipId,
            Status = status,
            StreamEntryId = streamEntryId.ToString(),
            Error = error,
            UpdatedAtUtc = updatedAtUtc
        };

        await redisDatabase.PublishAsync(RedisChannel.Literal(CrawlerQueue.EventsChannelName), JsonSerializer.Serialize(jobEvent))
            .ConfigureAwait(false);
    }

    private static bool TryReadCrawlerJob(StreamEntry entry, out int membershipTypeId, out long membershipId)
    {
        membershipTypeId = 0;
        membershipId = 0;

        var membershipTypeIdValue = entry.Values.FirstOrDefault(value => value.Name == "membershipTypeId").Value;
        var membershipIdValue = entry.Values.FirstOrDefault(value => value.Name == "membershipId").Value;

        return int.TryParse(membershipTypeIdValue.ToString(), out membershipTypeId)
            && long.TryParse(membershipIdValue.ToString(), out membershipId)
            && membershipTypeId > 0
            && membershipId > 0;
    }
}
